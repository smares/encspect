// The direct path for AAC on Windows (docs/design/official-decoder-crosschecks.md): the AAC decoder MFT fed one unit of the
// unit list (unit_list.py --tsv) per ProcessInput, in file order. A cut unit is fed from its first byte to the last byte the
// file holds and never padded. The decoder is a black box; nothing of it is linked into Encspect.
using System.Runtime.InteropServices;

namespace WmaDecodeProbe;

public sealed class UnitFile
{
    public string Kind = "unknown";
    public string Codec = "";
    public byte[] Config = [];
    public int AdtsProfile = -1, AdtsSfi, AdtsChannels;
    public List<(long Offset, long Declared, long Received)> Units = [];
    public List<(long Offset, long Length)> Skipped = [];
    public string? Note;

    public static UnitFile Read(string path)
    {
        var file = new UnitFile();
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.TrimEnd().Split(' ');
            switch (parts[0])
            {
                case "F" when parts.Length >= 3:
                    file.Kind = parts[2];
                    break;
                case "T" when parts.Length >= 2:
                    file.Codec = parts[1];
                    break;
                case "C" when parts.Length >= 2:
                    file.Config = Convert.FromHexString(parts[1]);
                    break;
                case "H" when parts.Length >= 4:
                    file.AdtsProfile = int.Parse(parts[1]);
                    file.AdtsSfi = int.Parse(parts[2]);
                    file.AdtsChannels = int.Parse(parts[3]);
                    break;
                case "U" when parts.Length >= 4:
                    file.Units.Add((long.Parse(parts[1]), long.Parse(parts[2]), long.Parse(parts[3])));
                    break;
                case "S" when parts.Length >= 3:
                    file.Skipped.Add((long.Parse(parts[1]), long.Parse(parts[2])));
                    break;
                case "N":
                    file.Note = line.Length > 2 ? line[2..] : "";
                    break;
            }
        }

        return file;
    }
}

public static class MftProbe
{
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int NotAccepting = unchecked((int)0xC00D36B5);
    private const uint ProvidesSamples = 0x100;
    private static readonly int[] Rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    // aot, sampling frequency index, channel configuration, extension aot and its sampling frequency index, as an AudioSpecificConfig states them.
    public static (int Aot, int Sfi, int Channels, int ExtAot, int ExtSfi, bool Ok) ParseAsc(byte[] asc)
    {
        int pos = 0;
        int Bits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++, pos++)
            {
                int bit = pos / 8 < asc.Length ? (asc[pos / 8] >> (7 - pos % 8)) & 1 : 0;
                v = (v << 1) | bit;
            }

            return v;
        }

        if (asc.Length < 2)
        {
            return (0, 0, 0, 0, 0, false);
        }

        int aot = Bits(5);
        if (aot == 31)
        {
            aot = 32 + Bits(6);
        }

        int sfi = Bits(4);
        if (sfi == 15)
        {
            Bits(24);
        }

        int channels = Bits(4);
        int extAot = 0, extSfi = 0;
        if (aot is 5 or 29)
        {
            extAot = 5;
            extSfi = Bits(4);
        }

        return (aot, sfi, channels, extAot, extSfi, true);
    }

    // The backward-compatible form of an explicitly hierarchical configuration (aot 5 or 29 first): aot 2, the core
    // frequency, the channels, the three GASpecificConfig flags, then the sync extension 0x2B7 (0x548 for parametric stereo).
    public static byte[]? Rewrite(byte[] asc)
    {
        var (aot, sfi, channels, _, extSfi, ok) = ParseAsc(asc);
        if (!ok || aot is not (5 or 29) || sfi == 15 || extSfi == 15)
        {
            return null;
        }

        var bits = new List<int>();
        void Put(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add((value >> i) & 1);
            }
        }

        Put(2, 5);
        Put(sfi, 4);
        Put(aot == 29 ? 1 : channels, 4);
        Put(0, 3);
        Put(0x2B7, 11);
        Put(5, 5);
        Put(1, 1);
        Put(extSfi, 4);
        if (aot == 29)
        {
            Put(0x548, 11);
            Put(1, 1);
        }

        while (bits.Count % 8 != 0)
        {
            bits.Add(0);
        }

        var bytes = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            bytes[i / 8] |= (byte)(bits[i] << (7 - i % 8));
        }

        return bytes;
    }

    private static byte[] AscFromAdts(UnitFile units)
    {
        int value = ((units.AdtsProfile + 1) << 11) | (units.AdtsSfi << 7) | (units.AdtsChannels << 3);
        return [(byte)(value >> 8), (byte)value];
    }

    private static IMFMediaType? InputType(UnitFile units, byte[] asc, int channels, int rate, bool adts)
    {
        // WAVEFORMATEX with the HEAACWAVEINFO members after it (wPayloadType, wAudioProfileLevelIndication, wStructType, wReserved1, dwReserved2),
        // then the AudioSpecificConfig for raw access units.
        var tail = adts ? [] : asc;
        var wfx = new byte[18 + 12 + tail.Length];
        BitConverter.GetBytes((ushort)0x1610).CopyTo(wfx, 0);
        BitConverter.GetBytes((ushort)channels).CopyTo(wfx, 2);
        BitConverter.GetBytes((uint)rate).CopyTo(wfx, 4);
        BitConverter.GetBytes((ushort)1).CopyTo(wfx, 12);
        BitConverter.GetBytes((ushort)16).CopyTo(wfx, 14);
        BitConverter.GetBytes((ushort)(12 + tail.Length)).CopyTo(wfx, 16);
        BitConverter.GetBytes((ushort)(adts ? 1 : 0)).CopyTo(wfx, 18);
        tail.CopyTo(wfx, 30);
        if (Native.MFCreateMediaType(out var type) < 0)
        {
            return null;
        }

        return Native.MFInitMediaTypeFromWaveFormatEx(type, wfx, (uint)wfx.Length) < 0 ? null : type;
    }

    public static void Run(string path, string unitsPath, Dictionary<string, object?> record, bool withTimestamps)
    {
        void Set(string key, object? value)
        {
            lock (SourceReaderProbe.Gate)
            {
                record[key] = value;
            }
        }

        var list = UnitFile.Read(unitsPath);
        Set("unitList", new { list.Kind, list.Codec, units = list.Units.Count, skippedRanges = list.Skipped.Count, list.Note, config = Convert.ToHexString(list.Config) });
        Set("withTimestamps", withTimestamps);
        if (list.Codec != "aac" || list.Units.Count == 0)
        {
            Set("skipped", list.Note ?? ("no AAC units (" + list.Kind + ")"));
            return;
        }

        bool adts = list.Kind == "adts";
        byte[] asc = adts ? AscFromAdts(list) : list.Config;
        var parsed = ParseAsc(asc);
        Set("asc", new { stored = Convert.ToHexString(asc), parsed.Aot, parsed.Sfi, parsed.Channels, parsed.ExtAot, parsed.ExtSfi });
        if (!parsed.Ok || parsed.Sfi >= Rates.Length)
        {
            Set("skipped", "the configuration could not be read");
            return;
        }

        int rate = Rates[parsed.Sfi];
        int channels = parsed.Channels == 0 ? 2 : parsed.Channels == 7 ? 8 : parsed.Channels;
        int hr = Native.MFStartup(Native.MfVersion, 0);
        if (hr < 0)
        {
            Set("startupHresult", Format.Hr(hr));
            return;
        }

        var com = new List<object>();
        try
        {
            RunCore(path, list, asc, adts, rate, channels, withTimestamps, Set, record, com);
        }
        finally
        {
            foreach (var o in com)
            {
                Marshal.ReleaseComObject(o);
            }

            Native.MFShutdown();
        }
    }

    private static void RunCore(string path, UnitFile list, byte[] asc, bool adts, int rate, int channels, bool withTimestamps, Action<string, object?> set,
        Dictionary<string, object?> record, List<object> com)
    {
        var attempts = new List<Dictionary<string, object?>>();
        set("inputAttempts", attempts);
        IMFTransform? mft = null;
        var candidates = new List<(string Name, byte[] Asc)> { ("asStored", asc) };
        if (!adts && Rewrite(asc) is { } rewritten)
        {
            candidates.Add(("rewrittenBackwardCompatible", rewritten));
        }

        foreach (var (name, candidate) in candidates)
        {
            var attempt = new Dictionary<string, object?> { ["config"] = name, ["asc"] = Convert.ToHexString(candidate) };
            try
            {
                var instance = (IMFTransform)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("32d186a7-218f-4c75-8876-dd77273a8999"), true)!)!;
                var type = InputType(list, candidate, channels, rate, adts);
                if (type == null)
                {
                    attempt["typeBuilt"] = false;
                    Marshal.ReleaseComObject(instance);
                    continue;
                }

                com.Add(type);
                int result = instance.SetInputType(0, type, 0);
                attempt["setInputHresult"] = Format.Hr(result);
                if (result >= 0)
                {
                    mft = instance;
                    com.Add(instance);
                    set("inputConfigUsed", name);
                    var dll = Inventory.ServerOf(new Guid("32d186a7-218f-4c75-8876-dd77273a8999"));
                    set("decoder", new Dictionary<string, object?> { ["clsid"] = "32d186a7-218f-4c75-8876-dd77273a8999", ["dll"] = dll, ["dllVersion"] = Versions.Of(dll) });
                    break;
                }

                Marshal.ReleaseComObject(instance);
            }
            catch (Exception e)
            {
                attempt["error"] = e.GetType().Name + ": " + e.Message;
            }

            // The attempt is complete before the serializer can see it.
            lock (SourceReaderProbe.Gate)
            {
                attempts.Add(attempt);
            }
        }

        if (mft == null)
        {
            set("refusedMediaType", true);
            return;
        }

        int blockAlign = 4;
        string formatKey = "";
        var byFormat = new Dictionary<string, long>();
        var offered = new List<Dictionary<string, object?>>();
        set("offeredOutputTypes", offered);

        bool ChooseOutput()
        {
            for (uint i = 0; i < 64; i++)
            {
                if (mft.GetOutputAvailableType(0, i, out var type) != 0 || type == null)
                {
                    break;
                }

                var d = Describe(type);
                lock (SourceReaderProbe.Gate)
                {
                    offered.Add(d);
                }

                if (d.TryGetValue("tag", out var tag) && tag is 1 && d.TryGetValue("bitsPerSample", out var bits) && bits is 16)
                {
                    int hrSet = mft.SetOutputType(0, type, 0);
                    set("outputSetHresult", Format.Hr(hrSet));
                    if (hrSet >= 0)
                    {
                        blockAlign = Math.Max(1, (int)d["blockAlign"]!);
                        formatKey = $"{d.GetValueOrDefault("sampleRate")} Hz, {d.GetValueOrDefault("channels")} channels";
                        set("outputFormat", d);
                        Marshal.ReleaseComObject(type);
                        return true;
                    }
                }

                Marshal.ReleaseComObject(type);
            }

            return false;
        }

        if (!ChooseOutput())
        {
            set("outputSet", false);
            return;
        }

        set("outputSet", true);
        mft.GetOutputStreamInfo(0, out var outInfo);
        set("outputStreamInfo", new { outInfo.Flags, outInfo.Size, outInfo.Alignment });
        bool provides = (outInfo.Flags & ProvidesSamples) != 0;
        set("beginStreaming", Format.Hr(mft.ProcessMessage(0x10000000, IntPtr.Zero)));
        set("startOfStream", Format.Hr(mft.ProcessMessage(0x10000003, IntPtr.Zero)));

        long total = 0;
        int errors = 0;
        var units = new List<Dictionary<string, object?>>();
        set("units", units);
        var data = File.ReadAllBytes(path);

        // Reads everything the decoder has for the input it was given; returns the frames and the results.
        (long Frames, string Results) Pull()
        {
            long frames = 0;
            var results = new List<string>();
            for (int n = 0; n < 64; n++)
            {
                var buffer = new MftOutputDataBuffer { StreamId = 0 };
                IntPtr own = IntPtr.Zero;
                if (!provides)
                {
                    Native.MFCreateSample(out var sample);
                    Native.MFCreateMemoryBuffer(Math.Max(outInfo.Size, 65536u), out var mb);
                    sample.AddBuffer(mb);
                    own = Marshal.GetIUnknownForObject(sample);
                    buffer.Sample = own;
                    Marshal.ReleaseComObject(mb);
                    Marshal.ReleaseComObject(sample);
                }

                int result = mft.ProcessOutput(0, 1, ref buffer, out _);
                try
                {
                    if (result == NeedMoreInput)
                    {
                        break;
                    }

                    if (result == StreamChange)
                    {
                        results.Add("streamChange");
                        ChooseOutput();
                        continue;
                    }

                    if (result < 0)
                    {
                        results.Add(Format.Hr(result));
                        errors++;
                        break;
                    }

                    if (buffer.Sample != IntPtr.Zero)
                    {
                        var sample = (IMFSample)Marshal.GetObjectForIUnknown(buffer.Sample);
                        sample.GetTotalLength(out uint length);
                        frames += length / blockAlign;
                        byFormat[formatKey] = byFormat.GetValueOrDefault(formatKey) + length / blockAlign;
                        Marshal.ReleaseComObject(sample);
                        results.Add("ok");
                    }
                    else
                    {
                        results.Add("noSample");
                    }
                }
                finally
                {
                    if (buffer.Events != IntPtr.Zero)
                    {
                        Marshal.Release(buffer.Events);
                    }

                    if (own != IntPtr.Zero)
                    {
                        Marshal.Release(own);
                    }
                    else if (buffer.Sample != IntPtr.Zero)
                    {
                        Marshal.Release(buffer.Sample);
                    }
                }
            }

            return (frames, string.Join(",", results));
        }

        int consecutiveFailures = 0;
        string stopped = "end";
        for (int i = 0; i < list.Units.Count; i++)
        {
            var (offset, declared, received) = list.Units[i];
            var entry = new Dictionary<string, object?> { ["index"] = i, ["offset"] = offset, ["declared"] = declared, ["received"] = received };
            if (received <= 0)
            {
                entry["fed"] = false;
                lock (SourceReaderProbe.Gate)
                {
                    units.Add(entry);
                }

                continue;
            }

            Native.MFCreateSample(out var sample);
            Native.MFCreateMemoryBuffer((uint)received, out var mb);
            if (mb.Lock(out var pointer, out _, out _) < 0)
            {
                entry["lockFailed"] = true;
                lock (SourceReaderProbe.Gate)
                {
                    units.Add(entry);
                }

                Marshal.ReleaseComObject(mb);
                Marshal.ReleaseComObject(sample);
                continue;
            }

            Marshal.Copy(data, (int)offset, pointer, (int)received);
            mb.Unlock();
            mb.SetCurrentLength((uint)received);
            sample.AddBuffer(mb);
            if (withTimestamps)
            {
                sample.SetSampleTime(i * 10_000_000L * 1024 / rate);
                sample.SetSampleDuration(10_000_000L * 1024 / rate);
            }

            long frames = 0;
            int result = mft.ProcessInput(0, sample, 0);
            entry["inputHresult"] = Format.Hr(result);
            if (result == NotAccepting)
            {
                // A refused first call is not a failing unit: read the output and offer the same unit once more.
                var (more, text) = Pull();
                frames += more;
                entry["drainedBeforeRetry"] = text;
                result = mft.ProcessInput(0, sample, 0);
                entry["retryHresult"] = Format.Hr(result);
            }

            if (result >= 0)
            {
                var (more, text) = Pull();
                frames += more;
                entry["output"] = text;
                consecutiveFailures = 0;
            }
            else
            {
                errors++;
                consecutiveFailures++;
            }

            entry["frames"] = frames;
            total += frames;
            Marshal.ReleaseComObject(mb);
            Marshal.ReleaseComObject(sample);
            lock (SourceReaderProbe.Gate)
            {
                units.Add(entry);
                record["partialSamples"] = total;
                record["unitsFed"] = i + 1;
            }

            if (consecutiveFailures >= 1000)
            {
                stopped = "1000 refused units in a row";
                break;
            }
        }

        set("stoppedBy", stopped);
        set("endOfStreamMessage", Format.Hr(mft.ProcessMessage(0x10000002, IntPtr.Zero)));
        set("drainMessage", Format.Hr(mft.ProcessMessage(1, IntPtr.Zero)));
        var (drained, drainText) = Pull();
        set("drain", new { frames = drained, results = drainText });
        set("errorCount", errors);
        set("totalsByOutputFormat", byFormat);
        // The complete total only for a stream fed to its end and drained; a loop that stopped early leaves partialSamples.
        if (stopped == "end")
        {
            set("codedSamples", total);
            set("totalSamples", total + drained);
        }
    }

    private static Dictionary<string, object?> Describe(IMFMediaType type)
    {
        if (Native.MFCreateWaveFormatExFromMFMediaType(type, out var pointer, out uint size, 0) < 0 || pointer == IntPtr.Zero)
        {
            return new Dictionary<string, object?> { ["error"] = "no WAVEFORMATEX", ["blockAlign"] = 4 };
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, (int)size);
            return Format.DescribeWfx(bytes);
        }
        finally
        {
            Native.CoTaskMemFree(pointer);
        }
    }
}
