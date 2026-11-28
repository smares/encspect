// Windows Media Audio decoded by Microsoft's decoder to samples, for scripts/fixtures/crosscheck_wma_pcm.py (docs/design/lossy-decoders.md, "The PCM cross-checks"; Microsoft's
// decoder is the reference of the decoder of versions 1 and 2, docs/design/wma-standard-decoding.md). The Source Reader of Media Foundation reads each file with 32-bit float output
// (as the Source Reader probe asks for it first, after 16 bits: here the float type is asked for alone), and every sample's bytes are written to <out>/<name>.f32, the samples as
// interleaved little-endian floats at full scale 1.0, with <out>/<name>.json saying the format, the samples per channel and the timestamps of the reads. One JSON line on
// standard output says what happened. The decoder is a black box; nothing of it is linked into Encspect.
//
//   wma-decode-probe wma-pcm <out folder> <file.wma or file.asf>...
//
// Runs only on Windows (the workflow `windows-wma-decode.yml` runs it after the probe); a file whose reader fails keeps the samples it gave and the record says where it stopped.
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WmaDecodeProbe;

public static class WmaPcm
{
    private const uint ErrorFlag = 0x1, EndOfStreamFlag = 0x2;

    public static int Run(string outDir, IEnumerable<string> files)
    {
        Directory.CreateDirectory(outDir);
        int failures = 0;
        foreach (string file in files)
        {
            var record = new Dictionary<string, object?> { ["type"] = "wma-pcm", ["file"] = Path.GetFileName(file) };
            try
            {
                failures += Decode(file, Path.Combine(outDir, Path.GetFileName(file)), record) ? 0 : 1;
            }
            catch (Exception ex) when (ex is IOException or COMException or InvalidCastException)
            {
                record["error"] = ex.Message;
                failures++;
            }

            Console.WriteLine(JsonSerializer.Serialize(record));
        }

        return failures == 0 ? 0 : 1;
    }

    private static bool Decode(string path, string stem, Dictionary<string, object?> record)
    {
        var com = new List<object>();
        try
        {
            return DecodeCore(path, stem, record, com);
        }
        finally
        {
            foreach (object o in com)
            {
                Marshal.ReleaseComObject(o);
            }

            Native.MFShutdown();
        }
    }

    private static bool DecodeCore(string path, string stem, Dictionary<string, object?> record, List<object> com)
    {
        int hr = Native.MFStartup(Native.MfVersion, 0);
        if (hr < 0)
        {
            record["error"] = "MFStartup " + Format.Hr(hr);
            return false;
        }

        hr = Native.MFCreateSourceReaderFromURL(Path.GetFullPath(path), IntPtr.Zero, out var reader);
        if (hr < 0 || reader == null)
        {
            record["error"] = "MFCreateSourceReaderFromURL " + Format.Hr(hr);
            return false;
        }

        com.Add(reader);
        reader.SetStreamSelection(Native.AllStreams, 0);
        reader.SetStreamSelection(Native.FirstAudioStream, 1);
        hr = reader.GetNativeMediaType(Native.FirstAudioStream, 0, out var native);
        if (hr < 0 || native == null)
        {
            record["error"] = "GetNativeMediaType " + Format.Hr(hr);
            return false;
        }

        com.Add(native);
        var format = Describe(native);
        int channels = format != null ? (int)format["channels"]! : 2;
        int rate = format != null ? (int)format["sampleRate"]! : 44100;
        Native.MFCreateMediaType(out var type);
        com.Add(type);
        byte[] wfx = Format.Wfx(3, channels, rate, 32);
        Native.MFInitMediaTypeFromWaveFormatEx(type, wfx, (uint)wfx.Length);
        hr = reader.SetCurrentMediaType(Native.FirstAudioStream, IntPtr.Zero, type);
        if (hr < 0)
        {
            record["error"] = "SetCurrentMediaType (32-bit float) " + Format.Hr(hr);
            return false;
        }

        record["channels"] = channels;
        record["sampleRate"] = rate;
        var reads = new List<long[]>();
        long frames = 0;
        bool ended = false;
        using (var output = File.Create(stem + ".f32"))
        {
            for (int calls = 0; calls < 200_000; calls++)
            {
                hr = reader.ReadSample(Native.FirstAudioStream, 0, out _, out uint flags, out long timestamp, out var sample);
                if (sample != null)
                {
                    if (sample.ConvertToContiguousBuffer(out var buffer) >= 0 && buffer.Lock(out IntPtr data, out _, out uint length) >= 0)
                    {
                        byte[] bytes = new byte[length];
                        Marshal.Copy(data, bytes, 0, (int)length);
                        buffer.Unlock();
                        output.Write(bytes);
                        long count = length / (4L * channels);
                        reads.Add([timestamp, frames, count]);
                        frames += count;
                        Marshal.ReleaseComObject(buffer);
                    }

                    Marshal.ReleaseComObject(sample);
                }

                if (hr < 0 || (flags & ErrorFlag) != 0)
                {
                    record["stoppedBy"] = "a failed read, " + Format.Hr(hr);
                    break;
                }

                if ((flags & EndOfStreamFlag) != 0)
                {
                    ended = true;
                    break;
                }
            }
        }

        record["samples"] = frames;
        record["endOfStream"] = ended;
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(new { file = Path.GetFileName(path), channels, sampleRate = rate, samples = frames, endOfStream = ended, reads }));
        return ended;
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
