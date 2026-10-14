using System.Globalization;
using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.WavPack;

// The stream node, the checks over the whole file, the statistics, the encoder and the stream info.
internal static partial class Summary
{
    [GeneratedRegex(@"wavpack|libwavpack", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WavPackWords();

    [GeneratedRegex(@"lavf|lavc|ffmpeg|libavcodec", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FfmpegWords();

    public static void Finish(State s, ParseContext context)
    {
        long start = s.Source.Start;
        long end = s.Source.End;
        Node? stream = null;
        Node Stream() => stream!;
        stream = new Node("WavPack", start, end - start, null, () => new BlockedChildren(s.Leading, s.Entries.Count, i => EntryNode(s, i)))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.Blocks,
        };
        context.Root.Add(stream);
        Checks(s, Stream);
        Node Cover(string name, NodeValue? value, string description, string? topic = null) =>
            new(name, start, end - start, value) { Description = description, Topic = topic };
        Node? encoder = s.Blocks > 0 ? Encoder(s, context.EncoderClaims, Cover) : null;
        if (encoder is not null)
        {
            s.Leading.Add(encoder);
        }

        StreamInfo? info = Statistics(s, Cover, stream, encoder);
        if (info is not null)
        {
            context.AddStream(info);
        }

        s.Findings.Flush(context);
    }

    // Audio blocks without a bitstream of their own that hold correction data or extra bits are a
    // correction file's.
    private static bool IsCorrection(State s) => s.AudioBlocks > 0 && s.WvBlocks == 0 && s.DsdBlocks == 0 && (s.WvcBlocks > 0 || s.WvxBlocks > 0);

    private static Node EntryNode(State s, int index) =>
        s.Entries[index].Kind == EntryKind.Skipped ? Nodes.Skipped(s, index) : Nodes.Block(s, index);

    private static void Checks(State s, Func<Node> stream)
    {
        if (s.Blocks == 0)
        {
            s.Findings.Add(Severity.Warning, "wavpack.no-blocks", stream, _ =>
                "No block header that passes libwavpack's checks and is followed by another one is in the data, so no block is read.");
            return;
        }

        if (s.AudioBlocks == 0)
        {
            s.Findings.Add(Severity.Warning, "wavpack.no-audio", stream, _ => $"None of the {s.Blocks} blocks counts a sample, so the file holds no audio.");
            return;
        }

        bool isCorrection = IsCorrection(s);
        if (s.ExtraOnly > 0 && !isCorrection)
        {
            int firstExtra = s.ExtraOnlyFirst;
            long at = s.Entries[firstExtra].Offset;
            s.Findings.Add(Severity.Warning, "wavpack.audio-without-bitstream", () => Nodes.Block(s, firstExtra), _ =>
                $"The block at 0x{at:X} counts samples, but holds only extra bits and no bitstream{(s.ExtraOnly > 1 ? $" ({s.ExtraOnly} blocks)" : string.Empty)}; it cannot be decoded.");
        }

        if (isCorrection)
        {
            s.Findings.Add(Severity.Info, "wavpack.correction-file", stream, _ =>
                "The audio blocks hold correction data only, as a .wvc file does: it adds to the hybrid .wv file with the same blocks, and cannot be decoded alone.");
        }
        else if (s.WvcBlocks > 0 && (s.WvBlocks > 0 || s.DsdBlocks > 0))
        {
            s.Findings.Add(Severity.Warning, "wavpack.mixed-streams", stream, _ =>
                $"{s.WvcBlocks} blocks hold correction data and {s.WvBlocks + s.DsdBlocks} hold audio of their own; a .wv file and a .wvc file are two files.");
        }

        long counted = s.CountedSamples;
        if (s.FirstIndex > 0)
        {
            long first = s.FirstIndex;
            s.Findings.Add(Severity.Info, "wavpack.first-block-index", () => Nodes.HeaderField(s, s.FirstAudio, "Block Index"), _ =>
                $"The first audio block begins at sample {first} instead of 0, as in a part cut from a longer stream; libwavpack takes the length from the last block then.");
        }

        if (s.DeclaredTotal is { } declared)
        {
            if (declared != counted)
            {
                s.Findings.Add(Severity.Warning, "wavpack.total-samples-mismatch", () => Nodes.HeaderField(s, s.DeclaredEntry, "Total Samples"), _ =>
                    $"The first block says the file holds {declared} samples, but its audio blocks hold {counted}.");
            }
        }
        else if (s.FirstIndex <= 0)
        {
            s.Findings.Add(Severity.Info, "wavpack.total-samples-unknown", () => Nodes.HeaderField(s, s.DeclaredEntry >= 0 ? s.DeclaredEntry : s.FirstAudio, "Total Samples"), _ =>
                "The first audio block does not say how many samples the file holds (the count is -1, as an encoder writes it when it cannot seek back); libwavpack finds the length from the last block.");
        }

        if (s.ChannelInfo is { } info && s.FirstSetChannels != 0 && info.Value.Channels != s.FirstSetChannels)
        {
            int stated = info.Value.Channels;
            s.Findings.Add(Severity.Warning, "wavpack.channel-count-mismatch", () => Nodes.SubBlock(s, info.Entry, info.SubBlock), _ =>
                $"The channel info says {stated} channels, but the first set of blocks holds {s.FirstSetChannels}.");
        }
    }

    // Whether the blocks hold what libwavpack 5.0 added, a new config block or a block checksum, which
    // makes it report encoder version 5.
    private static bool HasVersionFiveFeatures(State s) =>
        s.HasNewConfig || s.ChecksumValid + s.ChecksumInvalid > 0 || (s.Entries[FirstBlock(s)].Header.Flags & BlockHeader.HasChecksumFlag) != 0;

    // The encoder as far as the blocks tell it. libwavpack 5.0 and later write a new config block (even
    // an empty one, "to signal a 5.0 file", pack.c) and block checksums, which is evidence of that
    // library with medium confidence. Without them the blocks only show a WavPack 4 stream, which
    // libwavpack reports as encoder version 4 but which other encoders write as well: FFmpeg's (6.1.1,
    // libavcodec/wavpackenc.c) writes stream version 0x410 and neither, so that is low confidence.
    private static Node Encoder(State s, IReadOnlyList<EncoderClaim> claims, Func<string, NodeValue?, string, string?, Node> cover)
    {
        Entry first = s.Entries[FirstBlock(s)];
        bool isFive = HasVersionFiveFeatures(s);
        string name = isFive ? "WavPack 5.0 or later" : "WavPack 4 stream, no WavPack 5 features";
        string evidence = isFive
            ? $"The blocks hold {(s.HasNewConfig ? "a new config block" : "a block checksum")}, which libwavpack has written since 5.0; libwavpack reports encoder version 5 for a file with one"
            : $"The blocks have stream version 0x{first.Header.Version:X3} and neither a new config block nor a block checksum, which libwavpack has written since 5.0; libwavpack reports encoder version 4 for such a file, and another encoder, such as FFmpeg, may have written it";
        Node encoder = cover("Encoder", name, Explain.Encoder, Topics.EncoderDetection);
        encoder.Add(cover("Confidence", isFive ? "Medium" : "Low", isFive ? Explain.ConfidenceMedium : Explain.ConfidenceLow, null));
        encoder.Add(cover("Evidence", evidence, Explain.Evidence, null));
        foreach (EncoderClaim claim in claims)
        {
            string text = claim.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            string line = $"{claim.Description} at 0x{claim.Source.Offset:X} names \"{text}\"";
            if (WavPackWords().IsMatch(text))
            {
                encoder.Add(cover("Evidence", line, Explain.Evidence, null));
            }
            else if (!isFive && FfmpegWords().IsMatch(text))
            {
                encoder.Add(cover("Evidence", line + "; FFmpeg's WavPack encoder writes stream version 0x410 and neither a new config block nor a block checksum", Explain.Evidence, null));
            }
            else
            {
                s.Findings.Add(Severity.Info, "wavpack.encoder-claim-differs", claim.Source, _ =>
                    $"The {claim.Description} names \"{text}\", but the blocks point to {name}; the tag may have been written by other software, or kept when the audio was re-encoded.");
            }
        }

        return encoder;
    }

    // A target the encoder floored to its lowest is only a limit, which is text rather than a number.
    internal static NodeValue TargetValue(double value, int decimals, string unit, bool isAtMost) =>
        isAtMost ? NodeValue.Text(NodeValue.Decimal(value, decimals, unit) + " or less") : NodeValue.Decimal(value, decimals, unit);

    private static int FirstBlock(State s)
    {
        for (int i = 0; i < s.Entries.Count; i++)
        {
            if (s.Entries[i].Kind == EntryKind.Block)
            {
                return i;
            }
        }

        return 0;
    }

    private static StreamInfo? Statistics(State s, Func<string, NodeValue?, string, string?, Node> cover, Node stream, Node? encoder)
    {
        if (s.Blocks == 0)
        {
            return null;
        }

        Node Cover3(string name, NodeValue? value, string description) => cover(name, value, description, null);
        var statistics = new Node("Statistics", stream.Offset, stream.Length) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        statistics.Add(cover("Blocks", NodeValue.Integer(s.Blocks, "blocks", "block"), Explain.StatBlocks, Topics.Blocks));
        if (s.AudioBlocks == 0)
        {
            s.Leading.Add(statistics);
            return null;
        }

        bool isDsd = (s.FormatFlags & BlockHeader.DsdFlag) != 0;
        bool isCorrection = IsCorrection(s);
        BlockHeader first = s.FirstAudioHeader;
        bool isHybrid = first.IsHybrid || s.HybridBlocks > 0;
        bool isCorrectionCreated = s.Config is { } config && (config.Value.Flags & Decode.ConfigCreateWvc) != 0;
        long nativeRate = Reader.NativeRate(s);
        long samples = s.CountedSamples * s.FiguresScale;
        double duration = nativeRate > 0 ? samples / (double)nativeRate : 0;
        int channels = s.ChannelInfo?.Value.Channels ?? s.FirstSetChannels;
        List<Speaker>? layout = null;
        if (channels > 0)
        {
            uint mask = s.ChannelInfo?.Value.Mask ?? (channels == 1 ? 4u : channels == 2 ? 3u : 0u);
            layout = Decode.Layout(mask, channels);
        }

        int bits = isDsd ? 1 : first.IsFloat ? 32 : (first.BytesPerSample * 8) - first.LeftShift;
        bool isLossless = !isHybrid && !isCorrection && !s.HasLossyBlocks;
        statistics.Add(Cover3("Audio Blocks", NodeValue.Integer(s.AudioBlocks, "blocks", "block"), Explain.StatAudioBlocks));
        if (s.MetadataBlocks > 0)
        {
            statistics.Add(Cover3("Metadata Blocks", NodeValue.Integer(s.MetadataBlocks, "blocks", "block"), Explain.StatMetadataBlocks));
        }

        statistics.Add(Cover3("Block Sets", NodeValue.Integer(s.Sets, "sets", "set"), Explain.StatSets));
        statistics.Add(cover("Samples", NodeValue.Integer(samples, isDsd ? "bits" : "samples", isDsd ? "bit" : "sample"), isDsd ? Explain.StatSamplesDsd : Explain.StatSamples, null));
        statistics.Add(Cover3("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        statistics.Add(cover("Sample Rate", NodeValue.Integer(nativeRate, "Hz"), isDsd ? Explain.StatRateDsd : Explain.StatRate, null));
        if (channels > 0)
        {
            statistics.Add(cover("Channels", NodeValue.Integer(channels, "channels", "channel"), Explain.StatChannels, Topics.Channels));
            statistics.Add(cover("Channel Layout", ChannelLayouts.Describe(layout!), Explain.StatLayout, Topics.Channels));
        }

        statistics.Add(Cover3("Bits Per Sample", NodeValue.Integer(bits, "bits", "bit"), isDsd ? Explain.StatBitsDsd : Explain.StatBits));
        statistics.Add(cover("IsLossless", NodeValue.Boolean(isLossless), Explain.StatIsLossless, Topics.Hybrid));
        statistics.Add(cover("IsHybrid", NodeValue.Boolean(isHybrid), Explain.StatIsHybrid, Topics.Hybrid));
        statistics.Add(cover("IsCorrectionFileCreated", NodeValue.Boolean(isCorrection || isCorrectionCreated), Explain.StatIsCorrectionFileCreated, Topics.Hybrid));
        statistics.Add(cover("IsCorrectionFile", NodeValue.Boolean(isCorrection), Explain.StatIsCorrectionFile, Topics.Hybrid));
        statistics.Add(cover("IsFloatingPoint", NodeValue.Boolean(first.IsFloat), Explain.StatIsFloat, Topics.FloatAndLargeIntegers));
        statistics.Add(cover("IsDsd", NodeValue.Boolean(isDsd), Explain.StatIsDsd, Topics.Dsd));
        if (s.Config is { } located)
        {
            ConfigValue c = located.Value;
            string mode = (c.Flags & Decode.ConfigVeryHighMode) != 0 ? "very high" : (c.Flags & Decode.ConfigHighMode) != 0 ? "high" : (c.Flags & Decode.ConfigFastMode) != 0 ? "fast" : "normal";
            statistics.Add(Cover3("Compression Mode", mode, Explain.StatMode));
            if (c.ExtraLevel is { } level)
            {
                statistics.Add(cover("Extra Processing Level", NodeValue.Integer(level), Explain.StatExtraLevel, Topics.Decorrelation));
            }
            else
            {
                statistics.Add(cover("Extra Processing Level", "none", Explain.StatExtraLevel, Topics.Decorrelation));
            }
        }

        if (s.HybridTarget is { } target)
        {
            statistics.Add(cover("Hybrid Target Bitrate", TargetValue(target.Value.Bits, 2, "bits/sample", target.Value.IsAtMost), Explain.StatTargetBits, Topics.Hybrid));
            if (!isDsd && channels > 0)
            {
                statistics.Add(cover("Hybrid Target Rate", TargetValue(target.Value.Bits * nativeRate * channels / 1000, 1, "kbps", target.Value.IsAtMost), Explain.StatTargetRate, Topics.Hybrid));
            }
        }

        if (s.TermsBlocks > 0)
        {
            statistics.Add(cover("Decorrelation Passes", s.TermsMinimum == s.TermsMaximum ? NodeValue.Integer(s.TermsMinimum, "passes", "pass") : $"{s.TermsMinimum} to {s.TermsMaximum} passes", Explain.StatPasses, Topics.Decorrelation));
        }

        if (s.StereoBlocks > 0 && !isDsd)
        {
            statistics.Add(cover("Mid/Side Blocks", $"{StreamFigures.Counted(s.JointStereoBlocks, "block", "blocks")} of {StreamFigures.Counted(s.StereoBlocks, "stereo block", "stereo blocks")} ({StreamFigures.Share(s.JointStereoBlocks, s.StereoBlocks)})", Explain.StatMidSide, Topics.MidSideStereo));
        }

        statistics.Add(cover("Verified Block Checksums", NodeValue.Integer(s.ChecksumValid, "blocks", "block"), Explain.StatChecksums, Topics.Crc));
        if (s.ChecksumInvalid + s.ChecksumMissing > 0)
        {
            statistics.Add(cover("Failed Block Checksums", NodeValue.Integer(s.ChecksumInvalid + s.ChecksumMissing, "blocks", "block"), Explain.StatChecksumsFailed, Topics.Crc));
        }

        if (s.Md5 is { } md5)
        {
            statistics.Add(cover("MD5 Checksum", md5.Value, Explain.StatMd5, Topics.Wrapper));
        }

        if (s.HasNewConfig)
        {
            statistics.Add(cover("Source Format", Decode.FileFormat(s.NewConfig?.Value.FileFormat ?? 0), Explain.StatSourceFormat, Topics.Wrapper));
        }

        if (s.Extension is { Value.Length: > 0 } extension)
        {
            statistics.Add(cover("Source Extension", extension.Value, Explain.StatSourceExtension, Topics.Wrapper));
        }

        Node Covered(string name, NodeValue? value, string description) => cover(name, value, description, null);
        s.Figures?.AddTo(statistics, Covered, "Block Set", "block sets", true, out _);
        Node? anchor = s.Figures is null ? null : statistics.Children.FirstOrDefault(c => c.Name == "Average Bitrate");
        s.Leading.Add(statistics);

        string codec = isCorrection ? "WavPack correction data" : isHybrid ? "WavPack hybrid" : s.HasLossyBlocks ? "WavPack lossy" : "WavPack lossless";
        string? channelMode = channels switch
        {
            1 => "Mono",
            2 when s.JointStereoBlocks == s.StereoBlocks && s.StereoBlocks > 0 => "Joint stereo (mid/side)",
            2 when s.JointStereoBlocks > 0 => "Stereo, mid/side in some blocks",
            2 => "Stereo",
            > 2 => "Multichannel",
            _ => null,
        };
        return new StreamInfo
        {
            Nodes = [stream],
            Codec = codec,
            SampleRate = (int)Math.Min(int.MaxValue, nativeRate),
            Channels = channels,
            ChannelMode = channelMode,
            ChannelLayout = layout,
            BitsPerSample = bits,
            Samples = samples,
            Duration = duration,
            AverageBitrate = s.Figures?.AverageBitrate,
            IsVbr = s.Figures is { Units: > 0 } figures && figures.MaximumBitrate > figures.MinimumBitrate,
            Encoder = encoder?.Value?.ToString(),
            EncoderConfidence = HasVersionFiveFeatures(s) ? Confidence.Medium : Confidence.Low,
            Series = s.Figures?.Series(anchor, s.Cancellation) ?? [],
        };
    }

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
