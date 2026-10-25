using Encspect.Core;

namespace Encspect.Formats.Aiff;

// The checks over the whole file, and the stream.
internal static class Summary
{
    public static void Finish(State s)
    {
        if (s.CommonChunks == 0)
        {
            s.Findings.Add(Severity.Warning, "aiff.no-common-chunk", s.Container, _ => "The file has no Common Chunk, so the parameters of the sound are not known.");
        }

        if (s.IsAifc)
        {
            if (s.VersionChunks == 0)
            {
                s.Findings.Add(Severity.Warning, "aiff.no-format-version", s.Container, _ => "The FORM is of type AIFC, which requires a Format Version Chunk; the file has none.");
            }
            else if (s.Version is { } version && version != Chunks.AifcVersion1)
            {
                s.Findings.Add(Severity.Warning, "aiff.format-version-unknown", s.VersionNode ?? s.Container, _ =>
                    $"The format version {version} is not the one the analyzer knows ({Chunks.AifcVersion1}, version 1 of AIFF-C), and the specification wants an exact comparison.");
            }
        }

        foreach ((int marker, Node reference) in s.MarkerReferences)
        {
            if (marker > 0 && s.MarkerIds?.Contains(marker) != true)
            {
                s.Findings.Add(Severity.Warning, "aiff.marker-missing", reference, n => $"{Counts.Of(n, "reference", "references")} to a marker ID the Marker Chunk does not hold.");
            }
        }

        if (s.Common is not { } common)
        {
            return;
        }

        if (s.Sound is null && common.Frames > 0)
        {
            s.Findings.Add(Severity.Warning, "aiff.no-sound-data", s.Container, _ => $"The Common Chunk states {Counts.Of(common.Frames, "sample frame", "sample frames")}, but the file has no Sound Data Chunk.");
        }

        if (common.Channels > 0)
        {
            AddStream(s, common);
        }
    }

    private static void AddStream(State s, CommonChunk common)
    {
        ParseContext context = s.Context;
        AifcType? type = AifcType.Of(common.Type);
        string codec = type?.Codec ?? CodecNames.Other;
        if (type is null)
        {
            string unknown = common.Type;
            s.Findings.Add(Severity.Info, "aiff.compression-unknown", common.Node, _ => $"The compression type {unknown} is not one the analyzer lists, so the sound data is not interpreted; the audio is reported by the figures the Common Chunk states.");
        }

        int? frameBytes = type?.FrameBytes(common.Channels, common.SampleSize);
        long frames = common.Frames;
        long dataBytes = 0;
        bool isLengthKnown = true;
        if (s.Sound is { } sound)
        {
            dataBytes = sound.Length - 8 - sound.Offset;
            if (dataBytes < 0)
            {
                dataBytes = 0;
                s.Findings.Add(Severity.Warning, "aiff.sound-offset-past-end", sound.Node, _ =>
                    $"The sound data's offset of {sound.Offset} bytes is more than the {Counts.Of(sound.Length - 8, "byte", "bytes")} that follow the offset and block size fields.");
            }

            if (frameBytes is > 0)
            {
                long held = dataBytes / frameBytes.Value;
                long needed = frames * frameBytes.Value;
                if (frames == 0 && held > 0)
                {
                    frames = held;
                    s.Findings.Add(Severity.Warning, "aiff.sample-frames-zero", common.Node, _ =>
                        $"The Common Chunk states 0 sample frames, but the sound data holds {Counts.Of(held, "frame", "frames")}; they are counted.");
                }
                else if (held < frames)
                {
                    long stated = frames;
                    frames = held;
                    s.Findings.Add(Severity.Warning, "aiff.sound-data-short", sound.Node, _ =>
                        $"The Common Chunk states {Counts.Of(stated, "sample frame", "sample frames")} of {Counts.Of(frameBytes.Value, "byte", "bytes")}, but the sound data holds room for {held}; the frames it holds are counted.");
                }
                else if (!sound.IsSizeUnknown && sound.BlockSize == 0 && dataBytes - needed >= frameBytes.Value)
                {
                    long extra = dataBytes - needed;
                    s.Findings.Add(Severity.Info, "aiff.sound-data-extra", sound.Node, _ =>
                        $"The sound data holds {Counts.Of(extra, "byte", "bytes")} after the last of the {Counts.Of(frames, "sample frame", "sample frames")} the Common Chunk states, and no block size explains them.");
                }
            }
        }

        // A coding that packs samples in blocks counts its compressed sample frames in the Common Chunk: "the number of sample frames in the Sound Data
        // Chunk" (AIFF-C, section 4.0), where a compressed sample frame is the unit of appendix C. The packed codings whose frame is known
        // (ima4, ACE and MACE, AifcType) give their length as frames times the samples of one; the others state none.
        if (frameBytes is null)
        {
            if (type is { PacketSamples: > 0 } packed)
            {
                long packets = Math.Min(frames, s.Sound is null ? frames : dataBytes / ((long)packed.PacketBytesPerChannel * common.Channels));
                frames = packets * packed.PacketSamples;
            }
            else
            {
                frames = 0;
                isLengthKnown = false;
                if (type is not null)
                {
                    string coding = type.Codec;
                    s.Findings.Add(Severity.Info, "aiff.length-unknown", common.Node, _ =>
                        $"The coding is {coding}, which packs samples in blocks of a size no source read gives; the Common Chunk's count of frames is not taken for a number of sample frames, so the stream has no length.");
                }
            }
        }

        double rate = common.SampleRate;
        double duration = rate > 0 ? frames / rate : 0;
        double? bitrate = duration > 0 && s.Sound is not null && isLengthKnown ? (frameBytes is > 0 ? frames * frameBytes.Value * 8 : dataBytes * 8) / duration / 1000 : null;
        IReadOnlyList<Speaker>? layout = common.Channels is >= 1 and <= 3 ? AiffLayouts.Of(common.Channels) : null;
        if (s.Channel is { } chan)
        {
            if (chan.Channels != common.Channels)
            {
                long stated = chan.Channels;
                int channels = common.Channels;
                string field = chan.Field;
                s.Findings.Add(Severity.Warning, "aiff.channel-layout-differs", chan.Node, _ =>
                    $"The CHAN chunk's {field} states {Counts.Of(stated, "channel", "channels")}, but the Common Chunk states {channels}; the layout of the CHAN chunk is not used.");
            }
            else if (chan.Layout is not null)
            {
                layout = chan.Layout;
            }
        }

        int? bits = type is { IsLinear: true } ? (type.FixedBits ?? (common.SampleSize is >= 1 and <= 32 ? common.SampleSize : null)) : null;
        Node node = s.Sound?.Node ?? s.Container;
        Node Cover(string name, NodeValue? value, string description, string? topic = null) =>
            new(name, node.Offset, node.Length, value) { Description = description, Topic = topic };

        var statistics = new Node("Statistics", node.Offset, node.Length) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        statistics.Add(Cover("Codec", codec, Explain.StatCodec, Topics.Compression));
        if (s.IsAifc)
        {
            statistics.Add(Cover("Compression Type", common.Type, Explain.StatCompressionType, Topics.Compression));
        }

        statistics.Add(Cover("Sample Frames", NodeValue.Integer(frames, "sample frames", "sample frame"), Explain.StatSampleFrames, Topics.PcmSamples));
        statistics.Add(Cover("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        if (common.IsRateValid)
        {
            statistics.Add(Cover("Sample Rate", common.SampleRate == Math.Floor(common.SampleRate) ? NodeValue.Integer((long)common.SampleRate, "Hz") : NodeValue.Decimal(common.SampleRate, 4, "Hz"), Explain.StatRate, Topics.ExtendedRate));
        }

        statistics.Add(Cover("Channels", NodeValue.Integer(common.Channels, "channels", "channel"), Explain.StatChannels, Topics.ChannelLayouts));
        if (layout is not null)
        {
            statistics.Add(Cover("Channel Layout", ChannelLayouts.Describe(layout), Explain.StatLayout, Topics.ChannelLayouts));
        }

        if (bits is not null)
        {
            statistics.Add(Cover("Bits Per Sample", NodeValue.Integer(bits.Value, "bits", "bit"), Explain.StatBits, Topics.PcmSamples));
        }

        if (type is { IsLinear: true })
        {
            statistics.Add(Cover("Sample Format", type.IsFloat ? "IEEE 754 floating point" : type.IsUnsigned ? "unsigned integer" : "signed integer", Explain.StatSampleFormat, Topics.PcmSamples));
            statistics.Add(Cover("Byte Order", type.IsLittleEndian ? "little-endian" : "big-endian", Explain.StatByteOrder, Topics.PcmSamples));
        }

        if (bitrate is not null)
        {
            statistics.Add(Cover("Average Bitrate", NodeValue.Decimal(bitrate.Value, 1, "kbps"), Explain.StatBitrate));
        }

        node.Add(statistics);
        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Codec = codec,
            SampleRate = common.IsRateValid ? (int)Math.Min(Math.Round(common.SampleRate), int.MaxValue) : 0,
            Channels = common.Channels,
            ChannelMode = common.Channels switch { 1 => "Mono", 2 => "Stereo", > 2 => "Multichannel", _ => null },
            ChannelLayout = layout,
            BitsPerSample = bits,
            Samples = frames,
            Duration = duration,
            AverageBitrate = bitrate,
        });
    }
}
