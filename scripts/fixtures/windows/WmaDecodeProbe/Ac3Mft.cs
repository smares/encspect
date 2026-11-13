// Rollout step 7 of docs/design/official-decoder-crosschecks.md: AC-3 decoded by "Microsoft Dolby Digital Plus Decoder MFT" (CLSID_CMSDDPlusDecMFT), run by hand on the
// lead's Windows notebook (the runner images have no Dolby decoder). The MFT is fed the syncframes of a unit list (unit_list.py --tsv), one per ProcessInput, and its
// output is written as raw interleaved 32-bit floats for scripts/fixtures/compare_pcm.py; one JSON line on standard output says what happened.
//
//   wma-decode-probe ac3-pcm <file.ac3> <units.tsv> <out.f32>
//   wma-decode-probe eac3-pcm <file.eac3> <units.tsv> <out.f32>
//
// The eac3-pcm mode feeds E-AC-3 the same way: its input type is a WAVEFORMATEXTENSIBLE with the subtype KSDATAFORMAT_SUBTYPE_DOLBY_DDPLUS
// ({A7FB87AF-2D02-42FB-A4D4-05CD93843BDD}), which MFInitMediaTypeFromWaveFormatEx turns into MFAudioFormat_Dolby_DDPlus; the rate, the channel count and the lfe flag are those of the
// first unit's independent frame (Annex E 2.2.2). Not run when written either; the first run's setInput and offeredOutputs lines say what the MFT accepts.
//
// Not run when written: the input type below (the AC-3 WAVE tag 0x2000 with the first frame's rate and channels) and the output types the MFT offers are the first run's
// findings. If SetInputType refuses, the line says so with the HRESULT and the types the MFT offers as input. The decoder is a black box; nothing of it is linked into Encspect.
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WmaDecodeProbe;

public static class Ac3Mft
{
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const uint ProvidesSamples = 0x100;
    private static readonly Guid Clsid = new("177c0afe-900b-48d4-9e4c-57add250b3d4");

    public static int Run(string path, string unitsPath, string outPath, bool isEac3 = false)
    {
        var record = new Dictionary<string, object?> { ["type"] = isEac3 ? "eac3-pcm" : "ac3-pcm", ["file"] = Path.GetFileName(path) };
        int code = Decode(path, unitsPath, outPath, record, isEac3);
        Console.WriteLine(JsonSerializer.Serialize(record));
        return code;
    }

    private static readonly byte[] DdPlusSubtype = new Guid("a7fb87af-2d02-42fb-a4d4-05cd93843bdd").ToByteArray();

    private static int Decode(string path, string unitsPath, string outPath, Dictionary<string, object?> record, bool isEac3)
    {
        var list = UnitFile.Read(unitsPath);
        if (list.Units.Count == 0)
        {
            record["error"] = "no units";
            return 1;
        }

        var data = File.ReadAllBytes(path);
        long first = list.Units[0].Offset;
        int acmod, lfe, rate;
        if (isEac3)
        {
            // E-AC-3 (Annex E 2.2.2): after the sync word strmtyp, substreamid and frmsiz fill two bytes, then fscod (2 bits), numblkscod (2), acmod (3) and lfeon (1).
            acmod = (data[first + 4] >> 1) & 7;
            lfe = data[first + 4] & 1;
            rate = new[] { 48000, 44100, 32000, 0 }[data[first + 4] >> 6];
        }
        else
        {
            acmod = (data[first + 6] >> 5) & 7;
            int bits = (data[first + 6] << 8) | data[first + 7];
            int at = 15 - 3;
            if ((acmod & 1) != 0 && acmod != 1) at -= 2;
            if ((acmod & 4) != 0) at -= 2;
            if (acmod == 2) at -= 2;
            lfe = (bits >> at) & 1;
            rate = new[] { 48000, 44100, 32000 }[data[first + 4] >> 6];
        }

        int[] counts = [2, 1, 2, 3, 3, 4, 4, 5];
        int channels = counts[acmod] + lfe;
        record["input"] = new { rate, channels, acmod, lfe };
        if (Native.MFStartup(Native.MfVersion, 0) < 0)
        {
            record["error"] = "MFStartup";
            return 1;
        }

        try
        {
            var mft = (IMFTransform)Activator.CreateInstance(Type.GetTypeFromCLSID(Clsid, true)!)!;
            byte[] wfx;
            if (isEac3)
            {
                // WAVEFORMATEXTENSIBLE: tag 0xFFFE, cbSize 22, valid bits 16, the channel mask 0 and the subtype of Dolby Digital Plus.
                wfx = new byte[40];
                BitConverter.GetBytes((ushort)0xFFFE).CopyTo(wfx, 0);
                BitConverter.GetBytes((ushort)channels).CopyTo(wfx, 2);
                BitConverter.GetBytes((uint)rate).CopyTo(wfx, 4);
                BitConverter.GetBytes((uint)(list.Units[0].Received * rate / 1536)).CopyTo(wfx, 8);
                BitConverter.GetBytes((ushort)1).CopyTo(wfx, 12);
                BitConverter.GetBytes((ushort)16).CopyTo(wfx, 14);
                BitConverter.GetBytes((ushort)22).CopyTo(wfx, 16);
                BitConverter.GetBytes((ushort)16).CopyTo(wfx, 18);
                DdPlusSubtype.CopyTo(wfx, 24);
            }
            else
            {
                wfx = new byte[18];
                BitConverter.GetBytes((ushort)0x2000).CopyTo(wfx, 0);
                BitConverter.GetBytes((ushort)channels).CopyTo(wfx, 2);
                BitConverter.GetBytes((uint)rate).CopyTo(wfx, 4);
                BitConverter.GetBytes((uint)(list.Units[0].Received * rate / 1536)).CopyTo(wfx, 8);
                BitConverter.GetBytes((ushort)1).CopyTo(wfx, 12);
            }

            Native.MFCreateMediaType(out var type);
            Native.MFInitMediaTypeFromWaveFormatEx(type, wfx, (uint)wfx.Length);
            int hr = mft.SetInputType(0, type, 0);
            record["setInput"] = Format.Hr(hr);
            if (hr < 0)
            {
                return 1;
            }

            // Prefer 32-bit floats, then 16-bit integers.
            int bytesPerSample = 0;
            bool isFloat = false;
            var offered = new List<object>();
            for (int pass = 0; pass < 2 && bytesPerSample == 0; pass++)
            {
                for (uint i = 0; i < 64 && mft.GetOutputAvailableType(0, i, out var t) == 0 && t != null; i++)
                {
                    if (Native.MFCreateWaveFormatExFromMFMediaType(t, out var pointer, out uint size, 0) < 0)
                    {
                        continue;
                    }

                    var bytes = new byte[size];
                    Marshal.Copy(pointer, bytes, 0, (int)size);
                    Native.CoTaskMemFree(pointer);
                    int tag = BitConverter.ToUInt16(bytes, 0), bps = BitConverter.ToUInt16(bytes, 14);
                    if (pass == 0)
                    {
                        offered.Add(new { tag, channels = BitConverter.ToUInt16(bytes, 2), rate = BitConverter.ToInt32(bytes, 4), bps });
                    }

                    bool wanted = pass == 0 ? tag == 3 && bps == 32 : tag == 1 && bps == 16;
                    if (wanted && mft.SetOutputType(0, t, 0) >= 0)
                    {
                        bytesPerSample = bps / 8;
                        isFloat = pass == 0;
                        record["output"] = new { tag, bps, channels = BitConverter.ToUInt16(bytes, 2), rate = BitConverter.ToInt32(bytes, 4) };
                        break;
                    }
                }
            }

            record["offeredOutputs"] = offered;
            if (bytesPerSample == 0)
            {
                record["error"] = "no float or 16-bit output type accepted";
                return 1;
            }

            mft.GetOutputStreamInfo(0, out var info);
            bool provides = (info.Flags & ProvidesSamples) != 0;
            mft.ProcessMessage(0x10000000, IntPtr.Zero);
            mft.ProcessMessage(0x10000003, IntPtr.Zero);
            using var output = File.Create(outPath);
            long frames = 0;

            void Pull()
            {
                for (int n = 0; n < 64; n++)
                {
                    var buffer = new MftOutputDataBuffer { StreamId = 0 };
                    if (!provides)
                    {
                        Native.MFCreateSample(out var s);
                        Native.MFCreateMemoryBuffer(Math.Max(info.Size, 65536u), out var b);
                        s.AddBuffer(b);
                        buffer.Sample = Marshal.GetIUnknownForObject(s);
                    }

                    int result = mft.ProcessOutput(0, 1, ref buffer, out _);
                    if (result == NeedMoreInput || result < 0 || buffer.Sample == IntPtr.Zero)
                    {
                        return;
                    }

                    var sample = (IMFSample)Marshal.GetObjectForIUnknown(buffer.Sample);
                    sample.ConvertToContiguousBuffer(out var mb);
                    mb.Lock(out var pointer, out _, out uint length);
                    var raw = new byte[length];
                    Marshal.Copy(pointer, raw, 0, (int)length);
                    mb.Unlock();
                    if (isFloat)
                    {
                        output.Write(raw);
                    }
                    else
                    {
                        for (int i = 0; i + 1 < raw.Length; i += 2)
                        {
                            output.Write(BitConverter.GetBytes(BitConverter.ToInt16(raw, i) / 32768f));
                        }
                    }

                    frames += length / (bytesPerSample * channels);
                }
            }

            foreach (var (offset, _, received) in list.Units)
            {
                if (received <= 0)
                {
                    continue;
                }

                Native.MFCreateSample(out var sample);
                Native.MFCreateMemoryBuffer((uint)received, out var mb);
                mb.Lock(out var pointer, out _, out _);
                Marshal.Copy(data, (int)offset, pointer, (int)received);
                mb.Unlock();
                mb.SetCurrentLength((uint)received);
                sample.AddBuffer(mb);
                mft.ProcessInput(0, sample, 0);
                Pull();
            }

            mft.ProcessMessage(0x10000002, IntPtr.Zero);
            mft.ProcessMessage(1, IntPtr.Zero);
            Pull();
            record["frames"] = frames;
            record["channels"] = channels;
            return 0;
        }
        catch (Exception e)
        {
            record["error"] = e.GetType().Name + ": " + e.Message;
            return 1;
        }
        finally
        {
            Native.MFShutdown();
        }
    }
}
