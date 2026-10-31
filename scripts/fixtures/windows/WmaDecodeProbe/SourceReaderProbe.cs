// Path (a): Media Foundation's Source Reader with PCM output. Records every ReadSample call.
using System.Runtime.InteropServices;

namespace WmaDecodeProbe;

public static class SourceReaderProbe
{
    private const uint ErrorFlag = 0x1, EndOfStreamFlag = 0x2;
    private const uint NativeChanged = 0x10, CurrentChanged = 0x20;

    public static Dictionary<string, object?> Run(string path)
    {
        var record = new Dictionary<string, object?>();
        var reads = new List<Dictionary<string, object?>>();
        record["reads"] = reads;
        int hr = Native.MFStartup(Native.MfVersion, 0);
        if (hr < 0)
        {
            record["startupHresult"] = Format.Hr(hr);
            return record;
        }

        hr = Native.MFCreateSourceReaderFromURL(Path.GetFullPath(path), IntPtr.Zero, out var reader);
        record["openHresult"] = Format.Hr(hr);
        if (hr < 0 || reader == null)
        {
            record["opened"] = false;
            return record;
        }

        record["opened"] = true;
        reader.SetStreamSelection(Native.AllStreams, 0);
        record["selectHresult"] = Format.Hr(reader.SetStreamSelection(Native.FirstAudioStream, 1));
        hr = reader.GetNativeMediaType(Native.FirstAudioStream, 0, out var native);
        record["nativeTypeHresult"] = Format.Hr(hr);
        if (hr >= 0)
        {
            record["nativeType"] = Describe(native);
        }

        var attempts = new List<Dictionary<string, object?>>();
        record["outputAttempts"] = attempts;
        var nativeFormat = hr >= 0 ? Describe(native) : null;
        int channels = nativeFormat != null && nativeFormat.TryGetValue("channels", out var c) ? (int)c! : 2;
        int rate = nativeFormat != null && nativeFormat.TryGetValue("sampleRate", out var r) ? (int)r! : 44100;
        bool set = false;
        foreach (var (tag, bits) in new[] { (1, 16), (3, 32), (1, 24) })
        {
            var attempt = new Dictionary<string, object?> { ["tag"] = tag, ["bits"] = bits };
            attempts.Add(attempt);
            Native.MFCreateMediaType(out var type);
            var wfx = Format.Wfx(tag, channels, rate, bits);
            attempt["initHresult"] = Format.Hr(Native.MFInitMediaTypeFromWaveFormatEx(type, wfx, (uint)wfx.Length));
            int setHr = reader.SetCurrentMediaType(Native.FirstAudioStream, IntPtr.Zero, type);
            attempt["setHresult"] = Format.Hr(setHr);
            if (setHr >= 0)
            {
                set = true;
                break;
            }
        }

        record["outputSet"] = set;
        if (!set)
        {
            return record;
        }

        int blockAlign = 0;
        void ReadOutputType()
        {
            if (reader.GetCurrentMediaType(Native.FirstAudioStream, out var current) >= 0)
            {
                var d = Describe(current);
                if (d != null)
                {
                    blockAlign = (int)d["blockAlign"]!;
                    record["outputFormat"] = d;
                }
            }
        }

        ReadOutputType();
        long totalFrames = 0, totalBytes = 0;
        int errors = 0, samples = 0, calls = 0;
        bool ended = false;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (calls < 2_000_000)
        {
            calls++;
            hr = reader.ReadSample(Native.FirstAudioStream, 0, out uint actual, out uint flags, out long timestamp, out var sample);
            var read = new Dictionary<string, object?>
            {
                ["hresult"] = Format.Hr(hr),
                ["flags"] = flags,
                ["flagNames"] = FlagNames(flags),
                ["timestamp"] = timestamp,
            };
            if (sample != null)
            {
                sample.GetSampleDuration(out long duration);
                sample.GetTotalLength(out uint length);
                read["duration"] = duration;
                read["bytes"] = length;
                long frames = blockAlign > 0 ? length / blockAlign : 0;
                read["frames"] = frames;
                totalFrames += frames;
                totalBytes += length;
                samples++;
                sample.GetSampleFlags(out uint sampleFlags);
                if (sampleFlags != 0)
                {
                    read["sampleFlags"] = sampleFlags;
                }

                var attributes = Attributes(sample);
                if (attributes.Count > 0)
                {
                    read["attributes"] = attributes;
                }

                Marshal.ReleaseComObject(sample);
            }

            reads.Add(read);
            if ((flags & (NativeChanged | CurrentChanged)) != 0)
            {
                ReadOutputType();
            }

            if (hr < 0 || (flags & ErrorFlag) != 0)
            {
                if (++errors >= 5)
                {
                    break;
                }
            }

            if ((flags & EndOfStreamFlag) != 0)
            {
                ended = true;
                break;
            }
        }

        record["calls"] = calls;
        record["sampleCount"] = samples;
        record["errorCount"] = errors;
        record["endOfStream"] = ended;
        record["totalBytes"] = totalBytes;
        record["totalSamples"] = totalFrames;
        record["elapsedMs"] = timer.ElapsedMilliseconds;
        return record;
    }

    private static string[] FlagNames(uint flags)
    {
        var names = new List<string>();
        void Add(uint bit, string name)
        {
            if ((flags & bit) != 0)
            {
                names.Add(name);
            }
        }

        Add(0x1, "error");
        Add(0x2, "endOfStream");
        Add(0x4, "newStream");
        Add(0x10, "nativeMediaTypeChanged");
        Add(0x20, "currentMediaTypeChanged");
        Add(0x100, "streamTick");
        Add(0x200, "allEffectsRemoved");
        return [.. names];
    }

    // All attributes of a sample as "guid=value" for 32- and 64-bit integers, so the discontinuity
    // flag and whatever else the reader sets show up without relying on a remembered GUID.
    private static List<string> Attributes(IMFSample sample)
    {
        var list = new List<string>();
        if (sample.GetCount(out uint count) < 0)
        {
            return list;
        }

        var variant = Marshal.AllocHGlobal(32);
        try
        {
            for (uint i = 0; i < count && i < 64; i++)
            {
                for (int k = 0; k < 32; k += 8)
                {
                    Marshal.WriteInt64(variant, k, 0);
                }

                if (sample.GetItemByIndex(i, out Guid key, variant) < 0)
                {
                    continue;
                }

                int vt = Marshal.ReadInt16(variant);
                string value = vt switch
                {
                    19 => Marshal.ReadInt32(variant, 8).ToString(),
                    21 => Marshal.ReadInt64(variant, 8).ToString(),
                    _ => "vt" + vt,
                };
                list.Add($"{key}={value}");
                Native.PropVariantClear(variant);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
        }

        return list;
    }

    private static Dictionary<string, object?>? Describe(IMFMediaType type)
    {
        if (Native.MFCreateWaveFormatExFromMFMediaType(type, out var pointer, out uint size, 0) < 0 || pointer == IntPtr.Zero)
        {
            return null;
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
