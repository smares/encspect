using Encspect.Core;

namespace Encspect.Formats.Wav;

// The data chunk and what follows from it: the checks over the whole file, the hand-over of the audio to a codec parser, and the stream.
internal static class DataChunks
{
    public static void Read(State s, Fields f, Node chunk, long length)
    {
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
            if (!s.IsNodeLimitReached)
            {
                s.Findings.Add(Severity.Warning, "wav.no-format-chunk", s.Container, _ => "The file has no fmt chunk, so the format of the audio is not known.");
            }
        }

        if (s.Data is null)
        {
            if (!s.IsNodeLimitReached)
            {
                s.Findings.Add(Severity.Warning, "wav.no-data-chunk", s.Container, _ => "The file has no data chunk, so it holds no audio.");
            }
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
        if (s.FactNode is null && ((!FormatTags.IsPcm(coding) && !FormatTags.IsFloat(coding)) || s.WavlList is not null))
        {
            // RIFF 1.0, "FACT Chunk": required if the waveform data is in a wavl list and for all compressed audio formats.
            s.Findings.Add(Severity.Info, "wav.fact-missing", s.FormatChunkNode ?? s.Container, _ => "The data is in a wavl list or the format is neither PCM nor floating point, and the specification requires a fact chunk with the number of samples for such files; the file has none.");
        }

        long stored = data.Available + s.WavlExtraBytes;
        long blockAlign = format.BlockAlign > 0 ? format.BlockAlign : (long)format.Channels * format.BytesPerSample;
        bool isLinear = FormatTags.IsLinear(coding);
        if (isLinear && blockAlign > 0 && stored % blockAlign != 0)
        {
            long partial = stored % blockAlign;
            s.Findings.Add(Severity.Warning, "wav.partial-sample-frame", data.Node, _ =>
                $"The data chunk's {Counts.Of(stored, "byte", "bytes")} are not a whole number of sample frames of {Counts.Of(blockAlign, "byte", "bytes")}; {Counts.Of(partial, "byte", "bytes")} {(partial == 1 ? "is" : "are")} left over.");
        }

        long samples = isLinear && blockAlign > 0 ? (stored / blockAlign) + s.SilentSamples : s.FactNode is not null ? (long)Math.Min(s.FactSamples64, long.MaxValue) : 0;
        if (isLinear && s.FactNode is not null && s.FactSamples64 != (ulong)samples && !data.IsSizeUnknown)
        {
            ulong fact = s.FactSamples64;
            long counted = samples;
            s.Findings.Add(Severity.Warning, "wav.fact-samples-mismatch", s.FactNode, _ =>
                $"The fact chunk states {fact} samples, but the audio holds {Counts.Of(counted, "sample frame", "sample frames")}.");
        }

        if (s.Burst is { Type.Codec: null } unread)
        {
            Iec61937Route.AddUnanalyzed(s, format, data, unread);
            return;
        }

        // None of the routed codec parsers takes anything from the fmt chunk: AC-3 would read the extra bytes of the WAVEFORMATEX as a dac3 or dec3 box.
        string? codec = s.Burst is not null ? s.CarriedCodec : format.CodecId ?? s.CarriedCodec;
        bool isCarried = s.Burst is null && format.CodecId is null && s.CarriedCodec is not null;
        int streamsBefore = context.Streams.Count;
        bool isRouted = false;
        if (codec is not null && data.Available > 0)
        {
            isRouted = s.Burst is { } chain
                ? Iec61937Route.ParseBursts(s, format, data, chain, codec)
                : context.ParseStream(codec, data.Node, context.Reader.Slice(data.Start, data.Available));
        }
        else if (codec is not null)
        {
            s.Findings.Add(Severity.Warning, "wav.no-audio", data.Node, _ => "The data chunk is empty, so there is no audio for the codec parser.");
        }

        if (isRouted && context.Streams.Count > streamsBefore)
        {
            if (s.Burst is not null)
            {
                Iec61937Route.Compare(s, format, data, context.Streams[streamsBefore]);
            }
            else if (isCarried)
            {
                int bits = format.Bits;
                long rate = format.SampleRate;
                s.Findings.Add(Severity.Info, "wav.dts-in-pcm", data.Node, _ =>
                    $"The samples of the {bits}-bit stereo PCM data at {rate} Hz hold a DTS stream, as on a DTS music CD; the fmt chunk describes the carrier, and the stream is read as DTS.");
            }
            else
            {
                CompareWithStream(s, format, context.Streams[streamsBefore]);
            }

            return;
        }

        if (!isLinear && codec is null)
        {
            if (format.CodingName is { } name && name is not ("unknown" or "extensible"))
            {
                s.Findings.Add(Severity.Info, "wav.coding-not-analyzed", data.Node, _ => $"The coding is {name}, which the analyzer has no parser for; the audio is reported by the figures the file states.");
            }
            else if (format.IsExtensible && format.SubFormat is null)
            {
                s.Findings.Add(Severity.Info, "wav.coding-unknown", s.FormatChunkNode ?? data.Node, _ => "The extensible format tag 0xFFFE has no sub-format, because its header is incomplete, so the coding is not known; the audio is reported by the figures the file states.");
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

    // The codec parser whose stream the samples of a PCM file carry, or null: IEC 61937 bursts of AC-3, E-AC-3 or DTS in the 16-bit stereo samples
    // (S/PDIF passthrough, also under the format tags that stand for it), or a DTS stream written straight into them, as on a DTS music CD, where the format tag says PCM. A chain of bursts, or
    // for the codec parser a chain of consistent frames, at the start of the data, after any silence, decides.
    public static string? CarriedCodec(State s, WaveFormat format, Node parent, ByteReader data)
    {
        if (data.Length == 0 || s.IsBigEndian)
        {
            return null;
        }

        // IEC 61937 bursts in the samples of 16-bit stereo PCM or of the DTS tag (Microsoft's sub-format of DTS over IEC 61937 is that tag's), or in a
        // file whose format tag is one of those for S/PDIF data: a chain of bursts decides.
        if ((format.CodingTag is FormatTags.Pcm or FormatTags.DtsMicrosoft && format.Bits == 16 && format.Channels == 2) || FormatTags.IsIec61937Carrier(format.CodingTag))
        {
            if (Iec61937.Recognize(data) is { } chain)
            {
                s.Burst = chain;
                return chain.Type.Codec ?? Iec61937Route.Unanalyzed;
            }
        }

        if (format.CodingTag != FormatTags.Pcm || format.Bits != 16 || format.Channels != 2)
        {
            return null;
        }

        return s.Context.Recognizes("dts", parent, data) ? "dts" : null;
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
        statistics.Add(Cover("Codec", CodecName(format), Explain.StatCodec, Topics.WaveFormat));
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

        StreamVerification? verification = PcmRoute.Run(s, format, data);
        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Verification = verification,
            Codec = CodecName(format),
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

    // The name the stream reports for a coding no codec parser read: the coding's, or "Other" for one that has none.
    private static string CodecName(WaveFormat format) => format.CodingName is { } name && name is not ("unknown" or "extensible") ? name : CodecNames.Other;

    private static string SampleFormat(int coding, int bits) => coding switch
    {
        FormatTags.IeeeFloat => "IEEE 754 floating point",
        FormatTags.ALaw or FormatTags.MuLaw => "companded",
        _ => bits <= 8 ? "unsigned integer" : "signed integer",
    };

    // The speakers of a stream's channels: the assignment of the channel mask (a mask that names fewer speakers than there are channels leaves the
    // rest without a position, one that names more has its extra bits ignored), else, for the plain WAVEFORMATEX, front left and right for two
    // channels and the front center for one (ksmedia.h, KSAUDIO_SPEAKER_MONO).
    public static IReadOnlyList<Speaker>? StreamLayout(WaveFormat format)
    {
        if (format.Channels is < 1 or > 64)
        {
            return null;
        }

        if (format.IsExtensible && format.ChannelMask is { } mask && mask != 0)
        {
            return FormatChunk.Layout(mask, format.Channels);
        }

        return format.Channels switch { 1 => ChannelLayouts.Mono, 2 => ChannelLayouts.Stereo, _ => null };
    }
}
