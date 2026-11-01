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
        var asf = AsfReader.ReadHeader(path);
        var objects = new List<MediaObject>();
        var objectInputs = new List<Dictionary<string, object?>>();
        lock (SourceReaderProbe.Gate)
        {
            record["asf"] = asf;
            record["withTimestamps"] = withTimestamps;
            record["objects"] = objects;
            record["inputs"] = objectInputs;
        }

        if (asf.Error != null || asf.FormatBlock.Length == 0)
        {
            return;
        }

        var candidates = new List<Dictionary<string, object?>>();
        lock (SourceReaderProbe.Gate)
        {
            record["candidates"] = candidates;
        }

        IMediaObject? dmo = null;
        foreach (var (clsid, name, how) in Candidates(asf.FormatTag))
        {
            var candidate = new Dictionary<string, object?> { ["clsid"] = clsid.ToString(), ["name"] = name, ["foundBy"] = how };
            var dll = ServerOf(clsid);
            candidate["dll"] = dll;
            candidate["dllVersion"] = Versions.Of(dll);
            IMediaObject? instance = null;
            try
            {
                var type = Type.GetTypeFromCLSID(clsid, true)!;
                instance = (IMediaObject)Activator.CreateInstance(type)!;
                var input = InputType(asf);
                int hr;
                try
                {
                    hr = instance.SetInputType(0, ref input, 0);
                }
                finally
                {
                    Marshal.FreeHGlobal(input.Format);
                }

                candidate["setInputHresult"] = Format.Hr(hr);
                if (hr >= 0)
                {
                    dmo = instance;
                    lock (SourceReaderProbe.Gate)
                    {
                        candidates.Add(candidate);
                        record["decoder"] = candidate;
                    }

                    break;
                }
            }
            catch (Exception e)
            {
                candidate["error"] = e.GetType().Name + ": " + e.Message;
            }

            if (instance != null)
            {
                Marshal.ReleaseComObject(instance);
            }

            lock (SourceReaderProbe.Gate)
            {
                candidates.Add(candidate);
            }
        }

        if (dmo == null)
        {
            return;
        }

        try
        {
            Decode(dmo, asf, record, objects, objectInputs, withTimestamps);
        }
        finally
        {
            Marshal.ReleaseComObject(dmo);
        }
    }

    private static void Decode(IMediaObject dmo, AsfAudio asf, Dictionary<string, object?> record, List<MediaObject> objects,
        List<Dictionary<string, object?>> objectInputs, bool withTimestamps)
    {
        var offered = new List<Dictionary<string, object?>>();
        DmoMediaType? chosen = null;
        for (uint i = 0; i < 64; i++)
        {
            if (dmo.GetOutputType(0, i, out var type) != 0)
            {
                break;
            }

            var d = Describe(type);
            offered.Add(d);
            bool pcm16 = type.SubType == SubtypeOf(1) && d.TryGetValue("bitsPerSample", out var bits) && bits is 16;
            if (chosen == null && pcm16)
            {
                chosen = type;
            }
            else if (type.Format != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(type.Format);
            }
        }

        lock (SourceReaderProbe.Gate)
        {
            record["offeredOutputTypes"] = offered;
        }

        if (chosen == null)
        {
            lock (SourceReaderProbe.Gate)
            {
                record["error"] = "the decoder offers no 16-bit PCM output type";
            }

            return;
        }

        var output = chosen.Value;
        int setOutput = dmo.SetOutputType(0, ref output, 0);
        var outFormat = Describe(output);
        Marshal.FreeCoTaskMem(output.Format);
        int outAlign = Math.Max(outFormat.TryGetValue("blockAlign", out var align) && align is int a ? a : 1, 1);
        dmo.GetOutputSizeInfo(0, out uint outSize, out _);
        dmo.GetInputSizeInfo(0, out uint inSize, out uint lookahead, out uint inAlignment);
        int allocate = dmo.AllocateStreamingResources();
        lock (SourceReaderProbe.Gate)
        {
            record["setOutputHresult"] = Format.Hr(setOutput);
            record["outputFormat"] = outFormat;
            record["inputSizeInfo"] = new { size = inSize, lookahead, alignment = inAlignment };
            record["outputSize"] = outSize;
            record["allocateHresult"] = Format.Hr(allocate);
        }

        using var outBuffer = new NativeMediaBuffer((uint)Math.Clamp((long)outSize * 4, 1L << 20, 64L << 20));
        long total = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        // The DMO may keep an input buffer past the call that handed it over, so a buffer is freed only
        // after the decoder is done with the stream.
        var buffers = new List<NativeMediaBuffer>();
        try
        {
            AsfReader.WalkObjects(asf, obj =>
            {
                var entry = new Dictionary<string, object?>
                {
                    ["index"] = obj.Index,
                    ["number"] = obj.Number,
                    ["size"] = obj.Size,
                    ["bytes"] = obj.FedBytes,
                    ["isComplete"] = obj.IsComplete,
                    ["presentationTimeMs"] = obj.PresentationTimeMs,
                };
                var outputs = new List<Dictionary<string, object?>>();
                entry["outputs"] = outputs;
                long frames = 0;
                if (obj.FedBytes == 0)
                {
                    entry["processInputHresult"] = null;
                    entry["notFed"] = "no bytes from offset 0";
                }
                else
                {
                    var input = new NativeMediaBuffer((uint)obj.Data.Length, obj.Data);
                    buffers.Add(input);
                    uint flags = withTimestamps ? 0x1u | 0x2u : 0x1u;
                    long time = withTimestamps ? obj.PresentationTimeMs * 10000 : 0;
                    int hr = dmo.ProcessInput(0, input, flags, time, 0);
                    entry["processInputHresult"] = Format.Hr(hr);
                    frames = Drain(dmo, outBuffer, outAlign, outputs);
                    if (hr == unchecked((int)0x80040204)) // DMO_E_NOTACCEPTING: the output had to be drained first
                    {
                        int retry = dmo.ProcessInput(0, input, flags, time, 0);
                        entry["retryHresult"] = Format.Hr(retry);
                        frames += Drain(dmo, outBuffer, outAlign, outputs);
                    }
                }

                entry["frames"] = frames;
                total += frames;
                lock (SourceReaderProbe.Gate)
                {
                    objects.Add(obj);
                    objectInputs.Add(entry);
                    record["partialSamples"] = total;
                    record["objectCount"] = objects.Count;
                }
            });

            var tail = new Dictionary<string, object?>();
            var tailOutputs = new List<Dictionary<string, object?>>();
            tail["discontinuityHresult"] = Format.Hr(dmo.Discontinuity(0));
            long tailFrames = Drain(dmo, outBuffer, outAlign, tailOutputs);
            tail["outputs"] = tailOutputs;
            tail["frames"] = tailFrames;
            total += tailFrames;
            lock (SourceReaderProbe.Gate)
            {
                // totalSamples exists only for a run that fed every object and drained; a stalled or
                // failed run leaves partialSamples.
                record["drain"] = tail;
                record["totalSamples"] = total;
                record["partialSamples"] = total;
                record["objectCount"] = objects.Count;
                record["elapsedMs"] = timer.ElapsedMilliseconds;
            }
        }
        finally
        {
            dmo.FreeStreamingResources();
            foreach (var b in buffers)
            {
                b.Dispose();
            }
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
