using Encspect.Core;

namespace Encspect.Formats.WavPack;

// The encoder, the statistics and the stream info of a legacy file.
internal static partial class Legacy
{
    // The speed mode the flags show, by the rule libwavpack's open applies (unpack3_open.c): without a bit limit a file with neither the fast nor the high flag is
    // CONFIG_FAST; the hybrid mode is "high" with the extreme decorrelator, else "normal".
    internal static string ModeOf(LegacyHeader h)
    {
        int flags = h.EffectiveFlags;
        bool isExtreme = (flags & LegacyHeader.ExtremeDecorrFlag) != 0;
        if (h.IsHybrid)
        {
            return isExtreme ? "high" : "normal";
        }

        if ((flags & LegacyHeader.FastFlag) != 0)
        {
            return "very fast";
        }

        return (flags & LegacyHeader.HighFlag) == 0 ? "fast" : isExtreme ? "high" : "normal";
    }

    // How the stream codes its two channels, from the flags and the branch of unpack3.c they select; null for one channel.
    internal static string? StereoOf(LegacyHeader h, bool isMono)
    {
        if (isMono)
        {
            return null;
        }

        if (!h.IsVersion3)
        {
            return "Mid/side stereo";
        }

        int flags = h.EffectiveFlags;
        bool isFast = (flags & LegacyHeader.FastFlag) != 0;
        bool isHigh = (flags & (LegacyHeader.HighFlag | LegacyHeader.NewHighFlag)) != 0;
        bool isJoint = (flags & LegacyHeader.JointStereoFlag) != 0;
        if (h.Bits != 0)
        {
            return !isFast && isHigh && isJoint ? "Mid/side stereo" : "Left/right stereo";
        }

        if (isFast)
        {
            return "Mid/side stereo";
        }

        if ((flags & LegacyHeader.HighFlag) == 0 || (flags & LegacyHeader.CrossDecorrFlag) == 0)
        {
            return "Adaptive stereo";
        }

        return isJoint ? "Mid/side stereo" : "Left/right stereo";
    }

    private static void Summarize(ParseContext context, LegacyHeader h, LegacyLayout layout, Format? format, long fromData, bool isMono, long bitstreamBytes, Node stream, List<Node> leading, FindingCollector findings,
        FactText? fact, long factAt, int factLength)
    {
        long start = stream.Offset;
        long end = stream.End;
        Node Cover(string name, NodeValue? value, string description, string? topic = LegacyExplain.Topic) => new(name, start, end - start, value) { Description = description, Topic = topic };

        int channels = layout.IsRaw ? (isMono ? 1 : 2) : format?.Channels ?? 0;
        long rate = layout.IsRaw ? RawRate : format?.Rate ?? 0;
        int bits = layout.IsRaw ? ((h.Flags & LegacyHeader.Bytes3Flag) != 0 ? 24 : 16) : format?.Bits ?? 0;
        long samples = h.IsVersion3 ? h.TotalSamples : Math.Max(fromData, 0);
        bool isUsable = rate > 0 && channels is 1 or 2;
        double duration = rate > 0 ? samples / (double)rate : 0;
        double? bitrate = samples > 0 && rate > 0 ? bitstreamBytes * 8.0 * rate / samples / 1000 : null;
        uint mask = channels == 1 ? 4u : 3u;
        List<Speaker>? speakers = channels is 1 or 2 ? ChannelLayouts.FromWaveMask(mask, channels) : null;
        string codec = h.IsHybrid ? "WavPack hybrid" : h.IsLossy ? "WavPack lossy" : "WavPack lossless";

        string evidence = $"The version field of the WavPack header is {h.Version}; libwavpack reports it as the major version of the program that wrote the file. The Cool Edit filter of the 3.97 sources writes version 3 as well, from a header it builds (cool_wv.c)";
        Node encoder = Summary.EncoderNode($"WavPack {h.Version}", true, evidence, ClaimRoles.Encoders(context.EncoderClaims), (name, value, description, topic) => Cover(name, value, description, topic), findings,
            LegacyExplain.ConfidenceMedium, "the version field points to");
        leading.Add(encoder);
        Node? writer = Summary.Writer(findings, context.EncoderClaims, start, end);
        if (writer is not null)
        {
            leading.Add(writer);
        }

        if (fact?.ToClaim(new Node("Text", factAt, factLength), "fact chunk in the stored WAV header") is { } claim)
        {
            context.AddEncoderClaim(claim);
        }

        Node? processor = ProcessorDetection.Detect(context.EncoderClaims, (name, value, description) => Cover(name, value, description, ClaimRoles.TopicOf(name)));
        if (processor is not null)
        {
            leading.Add(processor);
        }

        ClaimRoles.CompleteVersion(encoder, context.EncoderClaims);

        var statistics = new Node("Statistics", start, end - start) { Kind = NodeKind.Statistics, Description = LegacyExplain.Statistics };
        statistics.Add(Cover("Samples", NodeValue.Integer(samples, "samples", "sample"), LegacyExplain.StatSamples, null));
        statistics.Add(Cover("Duration", NodeValue.Decimal(duration, 3, "s"), LegacyExplain.StatDuration, null));
        if (rate > 0)
        {
            statistics.Add(Cover("Sample Rate", NodeValue.Integer(rate, "Hz"), LegacyExplain.StatRate, null));
        }

        if (channels > 0)
        {
            statistics.Add(Cover("Channels", NodeValue.Integer(channels, "channels", "channel"), LegacyExplain.StatChannels, Topics.Channels));
        }

        if (speakers is not null)
        {
            statistics.Add(Cover("Channel Layout", ChannelLayouts.Describe(speakers), LegacyExplain.StatLayout, Topics.Channels));
        }

        if (bits > 0)
        {
            statistics.Add(Cover("Bits Per Sample", NodeValue.Integer(bits, "bits", "bit"), LegacyExplain.StatBits, null));
        }

        statistics.Add(Cover("IsLossless", NodeValue.Boolean(h.Bits == 0), LegacyExplain.StatIsLossless, Topics.Hybrid));
        statistics.Add(Cover("IsHybrid", NodeValue.Boolean(h.IsHybrid), LegacyExplain.StatIsHybrid, Topics.Hybrid));
        statistics.Add(Cover("IsRaw", NodeValue.Boolean(layout.IsRaw), LegacyExplain.StatIsRaw));
        if (h.IsVersion3)
        {
            statistics.Add(Cover("IsNoiseShaped", NodeValue.Boolean((h.Flags & LegacyHeader.LossyShapeFlag) != 0), LegacyExplain.StatIsNoiseShaped, Topics.Hybrid));
        }

        statistics.Add(Cover("Compression Mode", ModeOf(h), LegacyExplain.StatMode));
        if (h.IsHybrid && channels > 0 && rate > 0)
        {
            statistics.Add(Cover("Hybrid Target Bitrate", NodeValue.Decimal(h.Bits / 256.0 / channels, 2, "bits/sample"), LegacyExplain.StatTargetBits, Topics.Hybrid));
            statistics.Add(Cover("Hybrid Target Rate", NodeValue.Decimal(h.Bits * (double)rate / 256000, 1, "kbps"), LegacyExplain.StatTargetRate, Topics.Hybrid));
        }
        else if (h.IsLossy)
        {
            statistics.Add(Cover("Lossy Bit Limit", NodeValue.Integer(h.Bits, "bits", "bit"), LegacyExplain.StatLossyBitLimit));
        }

        if (StereoOf(h, isMono) is { } stereo)
        {
            statistics.Add(Cover("Stereo Coding", stereo, LegacyExplain.StatStereoCoding, Topics.MidSideStereo));
        }

        if (h.IsVersion3 && h.Extension.Length > 0)
        {
            statistics.Add(Cover("Source Extension", h.Extension, LegacyExplain.StatSourceExtension, Topics.Wrapper));
        }

        statistics.Add(Cover("Average Bitrate", bitrate is { } kbps ? NodeValue.Decimal(kbps, 1, "kbps") : "none", LegacyExplain.StatAverageBitrate));
        leading.Add(statistics);

        if (!isUsable)
        {
            return;
        }

        string channelMode = channels == 1 ? "Mono" : StereoOf(h, false) switch
        {
            "Mid/side stereo" => "Joint stereo (mid/side)",
            "Adaptive stereo" => "Stereo, mid/side in some samples",
            _ => "Stereo",
        };
        context.AddStream(new StreamInfo
        {
            Nodes = [stream],
            Codec = codec,
            SampleRate = (int)Math.Min(int.MaxValue, rate),
            Channels = channels,
            ChannelMode = channelMode,
            ChannelLayout = speakers,
            BitsPerSample = bits > 0 ? bits : null,
            Samples = samples,
            Duration = duration,
            AverageBitrate = bitrate,
            Encoder = encoder.Value?.ToString(),
            EncoderConfidence = Confidence.Medium,
            Writer = ClaimRoles.Resolved(writer).Name,
            WriterConfidence = ClaimRoles.Resolved(writer).Confidence,
            Processor = ClaimRoles.Resolved(processor).Name,
            ProcessorConfidence = ClaimRoles.Resolved(processor).Confidence,
            Verification = new StreamVerification
            {
                Decoding = DecodeStatus.NoDecoder,
                IsLossy = h.Bits != 0,
                Checksums = h.IsVersion3 ? [new ChecksumTally { Name = ChecksumNames.LegacyCrc, Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = true, Stored = 1 }] : [],
            },
        });
    }
}
