// Path (b): the WMA decoder DMO used directly, fed the media objects of the probe's own ASF reader one
// at a time. The decoder is found by the stream's format tag (DMOEnum over the audio decoder
// category), with the known CLSIDs of the WMA and WMA Voice decoders as a second way in.
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WmaDecodeProbe;

public static class DmoProbe
{
    private static readonly Guid AudioDecoderCategory = new("57f2db8b-e6bb-4513-9d43-dcd2a6593125");
    private static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid FormatWaveFormatEx = new("05589f81-c356-11ce-bf01-00aa0055595a");
    private const uint Incomplete = 0x01000000;

    private static Guid SubtypeOf(int tag) => new($"{tag:x8}-0000-0010-8000-00AA00389B71");

    public static void Run(string path, Dictionary<string, object?> record, bool withTimestamps = true)
    {
        var asf = AsfReader.Read(path);
        record["asf"] = asf;
        record["withTimestamps"] = withTimestamps;
        record["objects"] = asf.Objects;
        var objects = new List<Dictionary<string, object?>>();
        record["inputs"] = objects;
        if (asf.Error != null || asf.FormatBlock.Length == 0)
        {
            return;
        }

        var candidates = new List<Dictionary<string, object?>>();
        record["candidates"] = candidates;
        IMediaObject? dmo = null;
        foreach (var (clsid, name, how) in Candidates(asf.FormatTag))
        {
            var candidate = new Dictionary<string, object?> { ["clsid"] = clsid.ToString(), ["name"] = name, ["foundBy"] = how };
            candidates.Add(candidate);
            var dll = ServerOf(clsid);
            candidate["dll"] = dll;
            candidate["dllVersion"] = Versions.Of(dll);
            try
            {
                var type = Type.GetTypeFromCLSID(clsid, true)!;
                var instance = (IMediaObject)Activator.CreateInstance(type)!;
                var input = InputType(asf);
                int hr = instance.SetInputType(0, ref input, 0);
                candidate["setInputHresult"] = Format.Hr(hr);
                Marshal.FreeHGlobal(input.Format);
                if (hr >= 0)
                {
                    dmo = instance;
                    record["decoder"] = candidate;
                    break;
                }
            }
            catch (Exception e)
            {
                candidate["error"] = e.GetType().Name + ": " + e.Message;
            }
        }

        if (dmo == null)
        {
            return;
        }

        var offered = new List<Dictionary<string, object?>>();
        record["offeredOutputTypes"] = offered;
        DmoMediaType? chosen = null;
        for (uint i = 0; i < 64; i++)
        {
            if (dmo.GetOutputType(0, i, out var type) != 0)
            {
                break;
            }

            var d = Describe(type);
            offered.Add(d);
            bool pcm16 = type.SubType == SubtypeOf(1) && (int)d["bitsPerSample"]! == 16;
            if (chosen == null && pcm16)
            {
                chosen = type;
            }
        }

        if (chosen == null)
        {
            record["error"] = "the decoder offers no 16-bit PCM output type";
            return;
        }

        var output = chosen.Value;
        record["setOutputHresult"] = Format.Hr(dmo.SetOutputType(0, ref output, 0));
        var outFormat = Describe(output);
        record["outputFormat"] = outFormat;
        int outAlign = Math.Max((int)outFormat["blockAlign"]!, 1);
        dmo.GetOutputSizeInfo(0, out uint outSize, out _);
        dmo.GetInputSizeInfo(0, out uint inSize, out uint lookahead, out uint inAlignment);
        record["inputSizeInfo"] = new { size = inSize, lookahead, alignment = inAlignment };
        record["outputSize"] = outSize;
        record["allocateHresult"] = Format.Hr(dmo.AllocateStreamingResources());

        using var outBuffer = new NativeMediaBuffer(Math.Max(outSize * 4, 1u << 20));
        var buffers = new List<NativeMediaBuffer>();
        long total = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var obj in asf.Objects)
        {
            var entry = new Dictionary<string, object?>
            {
                ["index"] = obj.Index,
                ["number"] = obj.Number,
                ["size"] = obj.Size,
                ["bytes"] = obj.Data.Length,
                ["isComplete"] = obj.IsComplete,
                ["presentationTimeMs"] = obj.PresentationTimeMs,
            };
            lock (SourceReaderProbe.Gate)
            {
                objects.Add(entry);
                record["inObject"] = obj.Index;
            }
            var input = new NativeMediaBuffer((uint)obj.Data.Length, obj.Data);
            buffers.Add(input);
            uint flags = withTimestamps ? 0x1u | 0x2u : 0x1u;
            long time = withTimestamps ? obj.PresentationTimeMs * 10000 : 0;
            int hr = dmo.ProcessInput(0, input, flags, time, 0);
            entry["processInputHresult"] = Format.Hr(hr);
            var outputs = new List<Dictionary<string, object?>>();
            entry["outputs"] = outputs;
            long frames = Drain(dmo, outBuffer, outAlign, outputs);
            if (hr == unchecked((int)0x80040204)) // DMO_E_NOTACCEPTING: the output had to be drained first
            {
                int retry = dmo.ProcessInput(0, input, flags, time, 0);
                entry["retryHresult"] = Format.Hr(retry);
                frames += Drain(dmo, outBuffer, outAlign, outputs);
            }

            entry["frames"] = frames;
            total += frames;
        }

        var tail = new Dictionary<string, object?>();
        var tailOutputs = new List<Dictionary<string, object?>>();
        tail["discontinuityHresult"] = Format.Hr(dmo.Discontinuity(0));
        long tailFrames = Drain(dmo, outBuffer, outAlign, tailOutputs);
        tail["outputs"] = tailOutputs;
        tail["frames"] = tailFrames;
        record["drain"] = tail;
        total += tailFrames;
        record["totalSamples"] = total;
        record["objectCount"] = asf.Objects.Count;
        record["elapsedMs"] = timer.ElapsedMilliseconds;
        foreach (var b in buffers)
        {
            b.Dispose();
        }

    }

    // Calls ProcessOutput until the decoder has nothing more for what it was given.
    private static long Drain(IMediaObject dmo, NativeMediaBuffer buffer, int align, List<Dictionary<string, object?>> outputs)
    {
        long frames = 0;
        IntPtr pointer = Marshal.GetComInterfaceForObject(buffer, typeof(IMediaBuffer));
        try
        {
            for (int i = 0; i < 100000; i++)
            {
                buffer.Reset();
                var data = new DmoOutputBuffer { Buffer = pointer };
                int hr = dmo.ProcessOutput(0, 1, ref data, out _);
                long n = buffer.Length / align;
                if (hr != 1 || buffer.Length > 0 || data.Status != 0)
                {
                    outputs.Add(new Dictionary<string, object?>
                    {
                        ["hresult"] = Format.Hr(hr),
                        ["bytes"] = buffer.Length,
                        ["frames"] = n,
                        ["status"] = data.Status,
                    });
                }

                frames += n;
                // More output for the same input is signaled by the incomplete flag; S_FALSE or an empty buffer ends it.
                if (hr != 0 || buffer.Length == 0 || (data.Status & Incomplete) == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            Marshal.Release(pointer);
        }

        return frames;
    }

    private static DmoMediaType InputType(AsfAudio asf)
    {
        var format = Marshal.AllocHGlobal(asf.FormatBlock.Length);
        Marshal.Copy(asf.FormatBlock, 0, format, asf.FormatBlock.Length);
        return new DmoMediaType
        {
            MajorType = MediaTypeAudio,
            SubType = SubtypeOf(asf.FormatTag),
            FixedSizeSamples = 0,
            TemporalCompression = 0,
            SampleSize = 0,
            FormatType = FormatWaveFormatEx,
            FormatSize = (uint)asf.FormatBlock.Length,
            Format = format,
        };
    }

    private static Dictionary<string, object?> Describe(DmoMediaType type)
    {
        var bytes = new byte[type.FormatSize];
        if (type.Format != IntPtr.Zero && type.FormatSize > 0)
        {
            Marshal.Copy(type.Format, bytes, 0, bytes.Length);
        }

        var d = Format.DescribeWfx(bytes);
        d["subtype"] = type.SubType.ToString();
        return d;
    }

    private static IEnumerable<(Guid, string, string)> Candidates(int tag)
    {
        var seen = new HashSet<Guid>();
        var found = new List<(Guid, string, string)>();
        var category = AudioDecoderCategory;
        var inTypes = new[] { new DmoPartialType { Type = MediaTypeAudio, SubType = SubtypeOf(tag) } };
        IEnumDMO? enumerator = null;
        try
        {
            if (Native.DMOEnum(ref category, 0, 1, inTypes, 0, IntPtr.Zero, out enumerator) < 0)
            {
                enumerator = null;
            }
        }
        catch (Exception)
        {
        }

        if (enumerator != null)
        {
            var clsids = new Guid[16];
            var names = new IntPtr[16];
            while (enumerator.Next(16, clsids, names, out uint fetched) >= 0 && fetched > 0)
            {
                for (int i = 0; i < fetched; i++)
                {
                    string name = names[i] != IntPtr.Zero ? Marshal.PtrToStringUni(names[i]) ?? "" : "";
                    if (names[i] != IntPtr.Zero)
                    {
                        Native.CoTaskMemFree(names[i]);
                    }

                    if (seen.Add(clsids[i]))
                    {
                        found.Add((clsids[i], name, "DMOEnum"));
                    }
                }
            }
        }

        // CWMADecMediaObject (wmadmod.dll: WMA, WMA Professional, WMA Lossless) and CWMSPDecMediaObject (wmspdmod.dll: WMA Voice).
        var known = tag == 0x000A
            ? new[] { (new Guid("874131cb-4ecc-443b-8948-746b89595d20"), "WMSpeech Decoder DMO") }
            : new[] { (new Guid("2eeb4adf-4578-4d10-bca7-bb955f56320a"), "WMAudio Decoder DMO") };
        foreach (var (clsid, name) in known)
        {
            if (seen.Add(clsid))
            {
                found.Add((clsid, name, "known CLSID"));
            }
        }

        return found;
    }

    private static string? ServerOf(Guid clsid)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid}}}\InprocServer32");
            return key?.GetValue(null) is string s ? Environment.ExpandEnvironmentVariables(s) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
