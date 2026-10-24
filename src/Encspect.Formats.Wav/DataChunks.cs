using Encspect.Core;

namespace Encspect.Formats.Wav;

// The data chunk and what follows from it: the checks over the whole file, the hand-over of the audio to a codec parser, and the stream.
internal static class DataChunks
{
    public static void Read(State s, Fields f, Node chunk, long length)
    {
        s.DataChunks++;
        if (s.Data is not null && s.ListType == "wavl" && ReferenceEquals(s.WavlList, s.CurrentList))
        {
            s.WavlExtraBytes += length;
            return;
        }

        if (s.Data is not null)
        {
            s.Findings.Add(Severity.Warning, "wav.data-repeated", chunk, n => $"The file holds {n + 1} data chunks; the first one is the audio and the others are not counted.");
            return;
        }

        if (s.FormatChunks == 0)
        {
            s.IsDataBeforeFormat = true;
        }

        s.Data = new DataChunk(chunk, f.Reader.Start, length, s.ChunkStated, s.ChunkSizeIsUnknown);
        s.WavlList = s.ListType == "wavl" ? s.CurrentList : null;
        s.DataSize32 = s.ChunkSize32;
        s.FirstDataNode = chunk;
    }

    // The checks over the chunks, then the stream.
    public static void Finish(State s)
    {
        ParseContext context = s.Context;
        if (s.FormatChunks == 0)
        {
            s.Findings.Add(Severity.Error, "wav.no-format-chunk", s.Container, _ => "The file has no fmt chunk, so the format of the audio is not known.");
        }

        if (s.Data is null)
        {
            s.Findings.Add(Severity.Warning, "wav.no-data-chunk", s.Container, _ => "The file has no data chunk, so it holds no audio.");
        }
        else if (s.IsDataBeforeFormat && s.FormatChunks > 0)
        {
            s.Findings.Add(Severity.Warning, "wav.data-before-format", s.Data.Node, _ => "The data chunk comes before the fmt chunk, but the specification wants fmt first.");
        }

        if (s.Format is not { } format || s.Data is not { } data || format.Channels == 0 || format.SampleRate == 0)
        {
            return;
        }

        int coding = format.CodingTag;
        if (!FormatTags.IsPcm(coding) && !FormatTags.IsFloat(coding) && s.FactNode is null)
        {
            s.Findings.Add(Severity.Info, "wav.fact-missing", s.FormatChunkNode ?? s.Container, _ => "The format is not PCM, and the specification requires a fact chunk with the number of samples for such formats; the file has none.");
        }

        long stored = data.Available + s.WavlExtraBytes;
        long blockAlign = format.BlockAlign > 0 ? format.BlockAlign : (long)format.Channels * format.BytesPerSample;
        bool isLinear = FormatTags.IsLinear(coding);
        if (isLinear && blockAlign > 0 && stored % blockAlign != 0)
        {
            long partial = stored % blockAlign;
            s.Findings.Add(Severity.Warning, "wav.partial-sample-frame", data.Node, _ =>
                $"The data chunk's {Counts.Of(stored, "byte", "bytes")} are not a whole number of sample frames of {Counts.Of(blockAlign, "byte", "bytes")}; {Counts.Of(partial, "byte", "bytes")} are left over.");
        }

        long samples = isLinear && blockAlign > 0 ? (stored / blockAlign) + s.SilentSamples : s.FactNode is not null ? (long)Math.Min(s.FactSamples64, long.MaxValue) : 0;
        if (isLinear && s.FactNode is not null && s.FactSamples64 != (ulong)(stored / Math.Max(1, blockAlign)) && !data.IsSizeUnknown)
        {
            ulong fact = s.FactSamples64;
            long counted = stored / Math.Max(1, blockAlign);
            s.Findings.Add(Severity.Warning, "wav.fact-samples-mismatch", s.FactNode, _ =>
                $"The fact chunk states {fact} samples, but the data chunk holds {Counts.Of(counted, "sample frame", "sample frames")}.");
        }

        string? codec = FormatTags.CodecId(coding);
        int streamsBefore = context.Streams.Count;
        bool isRouted = false;
        if (codec is not null && data.Available > 0)
        {
            isRouted = context.ParseStream(codec, data.Node, context.Reader.Slice(data.Start, data.Available), Configuration(s, format, samples));
        }
        else if (codec is not null)
        {
            s.Findings.Add(Severity.Warning, "wav.no-audio", data.Node, _ => "The data chunk is empty, so there is no audio for the codec parser.");
        }

        if (isRouted && context.Streams.Count > streamsBefore)
        {
            CompareWithStream(s, format, context.Streams[streamsBefore]);
            return;
        }

        if (!isLinear && codec is null)
        {
            if (FormatTags.NameOf(coding) is { } name && name is not ("unknown" or "extensible"))
            {
                s.Findings.Add(Severity.Info, "wav.coding-not-analyzed", data.Node, _ => $"The coding is {name}, which the analyzer has no parser for; the audio is reported by the figures the file states.");
            }
            else if (format.IsExtensible && format.SubFormat is { } guid)
            {
                s.Findings.Add(Severity.Info, "wav.coding-unknown", s.FormatChunkNode ?? data.Node, _ =>
                    $"The sub-format {guid} is not a format tag in Microsoft's base GUID, so the coding is not known; the audio is reported by the figures the file states.");
            }
            else
            {
                int tag = format.Tag;
                s.Findings.Add(Severity.Info, "wav.coding-unknown", s.FormatChunkNode ?? data.Node, _ => $"The format tag 0x{tag:X4} is not one the analyzer lists, so the audio is reported by the figures the file states.");
            }
        }

        AddStream(s, format, data, samples, stored, blockAlign);
    }

    private static CodecConfiguration Configuration(State s, WaveFormat format, long samples)
    {
        MappedBytes extra;
        if (format.CodecData is { } node && node.Length > 0)
        {
            extra = MappedBytes.Read(s.Reader, [new ByteRange(node.Offset, node.Length)]);
        }
        else
        {
            extra = new MappedBytes(ReadOnlyMemory<byte>.Empty, s.Data!.Start);
        }

        var audio = new AudioFormat(format.Tag, format.Channels, (int)Math.Min(format.SampleRate, int.MaxValue), (int)Math.Min(format.BytesPerSecond, int.MaxValue), format.BlockAlign, format.Bits)
        {
            ChannelMask = format.ChannelMask,
            ValidBitsPerSample = format.ValidBits,
        };
        return new CodecConfiguration(extra, s.FormatChunkNode ?? s.Data!.Node)
        {
            Format = audio,
            Duration = s.FactNode is not null && format.SampleRate > 0 ? s.FactSamples64 / (double)format.SampleRate : null,
        };
    }

    // What the container states against what the codec parser found in the audio: the channels and, where the stream has one rate, the rate.
    private static void CompareWithStream(State s, WaveFormat format, StreamInfo stream)
    {
        if (stream.Channels > 0 && format.Channels > 0 && stream.Channels != format.Channels && !format.IsExtensible)
        {
            int stated = format.Channels;
            int found = stream.Channels;
            s.Findings.Add(Severity.Warning, "wav.format-differs-from-stream", s.FormatChunkNode ?? s.Container, _ =>
                $"The fmt chunk states {Counts.Of(stated, "channel", "channels")}, but the {stream.Codec} stream has {found}.");
        }
        else if (stream.SampleRate > 0 && format.SampleRate > 0 && stream.SampleRate != format.SampleRate)
        {
            long stated = format.SampleRate;
            int found = stream.SampleRate;
            s.Findings.Add(Severity.Warning, "wav.format-differs-from-stream", s.FormatChunkNode ?? s.Container, _ =>
                $"The fmt chunk states a sample rate of {stated} Hz, but the {stream.Codec} stream has {found} Hz.");
        }
    }

    private static void AddStream(State s, WaveFormat format, DataChunk data, long samples, long stored, long blockAlign)
    {
        ParseContext context = s.Context;
        int coding = format.CodingTag;
        int channels = format.Channels;
        long rate = format.SampleRate;
        IReadOnlyList<Speaker>? layout = StreamLayout(format);
        double duration = rate > 0 ? samples / (double)rate : 0;
        double? bitrate = duration > 0 ? stored * 8 / duration / 1000 : format.BytesPerSecond > 0 ? format.BytesPerSecond * 8 / 1000.0 : null;
        bool isLinear = FormatTags.IsLinear(coding);
        int? bits = isLinear && format.AudioBits > 0 ? format.AudioBits : null;
        Node node = data.Node;
        Node Cover(string name, NodeValue? value, string description, string? topic = null) =>
            new(name, node.Offset, node.Length, value) { Description = description, Topic = topic };

        var statistics = new Node("Statistics", node.Offset, node.Length) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        statistics.Add(Cover("Codec", FormatTags.CodecName(coding), Explain.StatCodec, Topics.WaveFormat));
        if (isLinear)
        {
            statistics.Add(Cover("Sample Frames", NodeValue.Integer(samples, "sample frames", "sample frame"), Explain.StatSampleFrames, Topics.PcmSamples));
        }
        else if (s.FactNode is not null)
        {
            statistics.Add(Cover("Samples", NodeValue.Integer(samples, "samples"), Explain.StatSamples, Topics.WaveCompressed));
        }

        statistics.Add(Cover("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        statistics.Add(Cover("Sample Rate", NodeValue.Integer(rate, "Hz"), Explain.StatRate));
        statistics.Add(Cover("Channels", NodeValue.Integer(channels, "channels", "channel"), Explain.StatChannels, Topics.ChannelLayouts));
        if (layout is not null)
        {
            statistics.Add(Cover("Channel Layout", ChannelLayouts.Describe(layout), Explain.StatLayout, Topics.ChannelLayouts));
        }

        if (isLinear)
        {
            if (bits is not null)
            {
                statistics.Add(Cover("Bits Per Sample", NodeValue.Integer(bits.Value, "bits", "bit"), Explain.StatBits, Topics.PcmSamples));
            }

            if (format.Bits != bits && format.Bits > 0)
            {
                statistics.Add(Cover("Container Bits Per Sample", NodeValue.Integer(format.Bits, "bits", "bit"), Explain.StatContainerBits, Topics.Extensible));
            }

            statistics.Add(Cover("Sample Format", SampleFormat(coding, format.Bits), Explain.StatSampleFormat, Topics.PcmSamples));
            statistics.Add(Cover("Byte Order", s.IsBigEndian ? "big-endian" : "little-endian", Explain.StatByteOrder, Topics.PcmSamples));
        }

        if (bitrate is not null)
        {
            statistics.Add(Cover("Average Bitrate", NodeValue.Decimal(bitrate.Value, 1, "kbps"), Explain.StatBitrate));
        }

        node.Add(statistics);

        Node? writer = WriterDetection.Detect(context.EncoderClaims, (name, value, description) => Cover(name, value, description, ClaimRoles.TopicOf(name)),
            (claim, text, name) => s.Findings.Add(Severity.Info, "wav.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)));
        if (writer is not null)
        {
            node.Add(writer);
        }

        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Codec = FormatTags.CodecName(coding),
            SampleRate = (int)Math.Min(rate, int.MaxValue),
            Channels = channels,
            ChannelMode = channels switch { 1 => "Mono", 2 => "Stereo", > 2 => "Multichannel", _ => null },
            ChannelLayout = layout,
            BitsPerSample = bits,
            Samples = samples,
            Duration = duration,
            AverageBitrate = bitrate,
            Writer = ClaimRoles.Resolved(writer).Name,
            WriterConfidence = ClaimRoles.Resolved(writer).Confidence,
        });
    }

    private static string SampleFormat(int coding, int bits) => coding switch
    {
        FormatTags.IeeeFloat => "IEEE 754 floating point",
        FormatTags.ALaw or FormatTags.MuLaw => "companded",
        _ => bits <= 8 ? "unsigned integer" : "signed integer",
    };

    // The speakers of a stream's channels: the assignment of the channel mask where it names as many speakers as there are channels, else, for
    // the plain WAVEFORMATEX, front left and right for two channels and the front center for one (ksmedia.h, KSAUDIO_SPEAKER_MONO).
    public static IReadOnlyList<Speaker>? StreamLayout(WaveFormat format)
    {
        if (format.Channels is < 1 or > 64)
        {
            return null;
        }

        if (format.IsExtensible && format.ChannelMask is { } mask && mask != 0)
        {
            IReadOnlyList<Speaker> speakers = FormatChunk.Layout(mask, format.Channels);
            return speakers.Count == format.Channels ? speakers : null;
        }

        return format.Channels switch { 1 => ChannelLayouts.Mono, 2 => ChannelLayouts.Stereo, _ => null };
    }
}
