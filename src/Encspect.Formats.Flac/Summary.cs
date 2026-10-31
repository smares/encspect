using System.Globalization;
using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.Flac;

// The stream node, the checks over the whole stream, the statistics, the encoder, the verification and the stream info.
internal static partial class Summary
{
    // libFLAC's vendor string: FLAC__VENDOR_STRING is "reference libFLAC " and its version and release date (format.c of libFLAC).
    [GeneratedRegex(@"^reference libFLAC (\d+(?:\.\d+){1,3}[0-9A-Za-z]*) (\d{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex LibFlacVendor();

    [GeneratedRegex(@"^reference libFLAC\b", RegexOptions.CultureInvariant)]
    private static partial Regex LibFlacWords();

    // ffmpeg's ENCODER comment when its native encoder writes the audio: "Lavc61.19.101 flac".
    [GeneratedRegex(@"^Lavc(\d\S*)? flac$", RegexOptions.CultureInvariant)]
    private static partial Regex FfmpegEncoder();

    public static Node StreamNode(long start, long length, List<Node> leading, Records records) =>
        new("FLAC", start, length, null, () => new BlockedChildren(leading, records.Entries.Count, i => Nodes.Entry(records, i)))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.Stream,
        };

    private static readonly Speaker[][] Layouts =
    [
        [Speaker.FrontCenter],
        [Speaker.FrontLeft, Speaker.FrontRight],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.BackLeft, Speaker.BackRight],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackCenter, Speaker.SideLeft, Speaker.SideRight],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight, Speaker.SideLeft, Speaker.SideRight],
    ];

    // The speakers of one to eight channels in FLAC's order (RFC 9639, section 9.1).
    public static IReadOnlyList<Speaker>? Layout(int channels) => channels is >= 1 and <= 8 ? Layouts[channels - 1] : null;

    // Ends the analysis of a stream: the checks, the nodes and the stream info. start and end are the stream node's extent.
    public static void Finish(State s, long start, long end)
    {
        Node stream = s.Stream;
        Checks(s);
        Node Cover(string name, NodeValue? value, string description, string? topic = null) =>
            new(name, start, end - start, value) { Description = description, Topic = topic };
        Node? encoder = Encoder(s, Cover);
        if (encoder is not null)
        {
            s.AddLeading(encoder);
        }

        Node? writer = Writer(s, start, end);
        if (writer is not null)
        {
            s.AddLeading(writer);
        }

        ClaimRoles.CompleteVersion(encoder, s.Context.EncoderClaims);
        StreamVerification verification = Verify(s);
        StreamInfo? info = Statistics(s, Cover, stream, encoder, writer, verification);
        if (info is not null)
        {
            s.Context.AddStream(info);
        }

        if (s.IsContained)
        {
            Records records = s.Records;
            stream.Add(new Node("Frames", start, end - start, NodeValue.Integer(records.Entries.Count, "frames"), () => new BlockedChildren([], records.Entries.Count, i => Nodes.Entry(records, i)))
            {
                Kind = NodeKind.Stream,
                Description = Explain.Stream,
                Topic = Topics.Stream,
            });
        }

        s.Findings.Flush(s.Context);
    }

    private static void Checks(State s)
    {
        Node stream = s.Stream;
        if (s.Frames == 0)
        {
            s.Findings.Add(Severity.Warning, "flac.no-frames", stream, _ => "The stream holds no frame, so it holds no audio.");
        }

        if (s.Info is not { } info)
        {
            return;
        }

        if (info.TotalSamples == 0)
        {
            if (s.Frames > 0)
            {
                s.Findings.Add(Severity.Info, "flac.total-samples-unknown", () => s.Md5Node ?? stream, _ =>
                    $"STREAMINFO does not say how many samples the stream holds (the count is 0, as an encoder writes it when it cannot seek back); the frames hold {s.Samples}.");
            }
        }
        else if (info.TotalSamples != s.Samples && s.Frames > 0)
        {
            long stated = info.TotalSamples;
            long counted = s.Samples;
            s.Findings.Add(Severity.Warning, "flac.total-samples-mismatch", s.Stream, _ =>
                $"STREAMINFO says the stream holds {Counts.Of(stated, "sample", "samples")}, but its frames hold {counted}{(counted < stated ? $", {stated - counted} fewer" : $", {counted - stated} more")}.");
        }

        if (StreamInfoBlock.IsMd5Unknown(s.InfoBytes!.Span))
        {
            s.Findings.Add(Severity.Info, "flac.md5-not-stored", () => s.Md5Node ?? stream, _ =>
                "The MD5 of the audio in STREAMINFO is 0, which means the encoder did not know it; the audio cannot be checked against it.");
        }
    }

    private static StreamVerification Verify(State s)
    {
        long md5 = s.Info is not null && !StreamInfoBlock.IsMd5Unknown(s.InfoBytes!.Span) ? 1 : 0;
        bool matches = s.Info is not { TotalSamples: > 0 } info || info.TotalSamples == s.Samples;
        bool coversAll = !s.HasGap && matches && s.CutFrames == 0 && s.UnreadableFrames == 0;
        IDecodeSession session = s.Session ?? s.Context.StartDecoding(FlacCodecParser.CodecId, new DecodeSetup { Stream = s.Stream, Channels = 1, BitsPerSample = 16 });
        return session.Finish(
        [
            new ChecksumTally { Name = ChecksumNames.HeaderCrc8, Covers = ChecksumCoverage.Header, Stored = s.Frames, Checked = s.Frames, Failed = s.Crc8Failures },
            new ChecksumTally { Name = ChecksumNames.FrameCrc16, Covers = ChecksumCoverage.CodedData, CoversAllAudio = coversAll, Stored = s.Frames, Checked = s.Frames - s.CutFrames, Failed = s.Crc16Failures },
            new ChecksumTally { Name = ChecksumNames.StreamInfoMd5, Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = true, Stored = md5 },
        ]);
    }

    // The encoder as far as the Vorbis comment names it: libFLAC writes its vendor string, ffmpeg's native encoder an ENCODER comment.
    private static Node? Encoder(State s, Func<string, NodeValue?, string, string?, Node> cover)
    {
        if (s.CommentTag is not { } tag)
        {
            return null;
        }

        List<EncoderClaim> claims = [.. ClaimRoles.Encoders(s.Context.EncoderClaims).Where(c => c.Source.Offset >= tag.Offset && c.Source.End <= tag.End)];
        EncoderClaim? vendor = claims.FirstOrDefault(c => c.Source.Name == "Vendor");
        EncoderClaim? field = claims.FirstOrDefault(c => c.Source.Name != "Vendor" && FfmpegEncoder().IsMatch(c.Text));
        string? name = null;
        Confidence confidence = Confidence.Medium;
        var evidence = new List<Node>();
        if (vendor is not null && LibFlacVendor().Match(vendor.Text) is { Success: true } release)
        {
            name = $"libFLAC {release.Groups[1].Value} ({release.Groups[2].Value})";
            confidence = Confidence.High;
            evidence.Add(Evidence(vendor.Source, $"Vorbis comment vendor string \"{vendor.Text}\", the form libFLAC writes"));
        }
        else if (vendor is not null && LibFlacWords().IsMatch(vendor.Text))
        {
            name = "libFLAC";
            evidence.Add(Evidence(vendor.Source, $"Vorbis comment vendor string \"{vendor.Text}\", libFLAC's form, of no release the analyzer knows"));
        }
        else if (field is not null)
        {
            name = $"FFmpeg ({field.Text})";
            evidence.Add(Evidence(field.Source, $"Vorbis comment ENCODER field \"{field.Text}\", which ffmpeg writes when it encodes"));
        }

        foreach (EncoderClaim claim in claims.Where(c => c.Source.Name != "Vendor"))
        {
            if (FfmpegEncoder().IsMatch(claim.Text))
            {
                if (!ReferenceEquals(claim, field) || !name!.StartsWith("FFmpeg", StringComparison.Ordinal))
                {
                    evidence.Add(Evidence(claim.Source, $"Vorbis comment ENCODER field \"{claim.Text}\", which ffmpeg writes when it encodes"));
                }

                continue;
            }

            string text = claim.Text;
            s.Findings.Add(Severity.Info, "flac.encoder-other-format", claim.Source, _ => $"The Vorbis comment ENCODER field names \"{text}\", which is no FLAC encoder the analyzer knows.");
            if (name is not null)
            {
                evidence.Add(Evidence(claim.Source, $"Vorbis comment ENCODER field \"{text}\", which does not count"));
            }
        }

        if (name is null)
        {
            return null;
        }

        long start = evidence.Min(e => e.Offset);
        long end = evidence.Max(e => e.End);
        var node = new Node("Encoder", start, end - start, name) { Description = Explain.Encoder, Topic = ClaimRoles.EncoderTopic };
        node.Add("Confidence", start, end - start, confidence.ToString(), Explain.Confidence);
        foreach (Node item in evidence)
        {
            node.Add(item);
        }

        return node;
    }

    private static Node Evidence(Node at, string text) => new("Evidence", at.Offset, at.Length, text) { Description = Explain.Evidence, Ranges = at.Ranges };

    private static Node? Writer(State s, long start, long end) =>
        WriterDetection.Detect(s.Context.EncoderClaims, (name, value, description) => new Node(name, start, end - start, value) { Description = description, Topic = ClaimRoles.TopicOf(name) },
            (claim, text, name) => s.Findings.Add(Severity.Info, "flac.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
            format: new WriterDetection.StreamFormat(KnownWriters.Flac, "FLAC", (claim, message, node) => s.Findings.Add(Severity.Info, "flac.writer-other-format", node, _ => message)));

    private static StreamInfo? Statistics(State s, Func<string, NodeValue?, string, string?, Node> cover, Node stream, Node? encoder, Node? writer, StreamVerification verification)
    {
        if (s.Info is null && s.Frames == 0)
        {
            return null;
        }

        Node Cover3(string name, NodeValue? value, string description) => cover(name, value, description, null);
        var statistics = new Node("Statistics", stream.Offset, stream.Length) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        StreamInfoBlock? info = s.Info;
        int rate = info?.SampleRate is > 0 ? info.Value.SampleRate : s.FirstRate;
        int channels = info?.Channels ?? s.FirstChannels;
        int bits = info?.BitsPerSample ?? s.FirstBits;
        long samples = s.Samples;
        double duration = rate > 0 ? samples / (double)rate : 0;
        IReadOnlyList<Speaker>? layout = Layout(channels);
        statistics.Add(cover("Frames", NodeValue.Integer(s.Frames, "frames"), Explain.StatFrames, Topics.Frames));
        statistics.Add(Cover3("Samples", NodeValue.Integer(samples, "samples"), Explain.StatSamples));
        statistics.Add(Cover3("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        if (rate > 0)
        {
            statistics.Add(Cover3("Sample Rate", NodeValue.Integer(rate, "Hz"), Explain.StatRate));
        }

        if (channels > 0)
        {
            statistics.Add(cover("Channels", NodeValue.Integer(channels, "channels"), Explain.StatChannels, ClaimsTopicChannels));
            if (layout is not null)
            {
                statistics.Add(cover("Channel Layout", ChannelLayouts.Describe(layout), Explain.StatLayout, ClaimsTopicChannels));
            }
        }

        if (bits > 0)
        {
            statistics.Add(Cover3("Bits Per Sample", NodeValue.Integer(bits, "bits"), Explain.StatBits));
        }

        if (s.Frames > 0)
        {
            string strategy = s.VariableFrames == 0 ? "fixed" : s.VariableFrames == s.Frames ? "variable" : "mixed";
            statistics.Add(cover("Blocking Strategy", strategy, Explain.StatStrategy, Topics.BlockingStrategy));
            statistics.Add(Cover3("Smallest Block Size", NodeValue.Integer(s.MinBlockSize, "samples"), Explain.StatSmallestBlock));
            statistics.Add(Cover3("Largest Block Size", NodeValue.Integer(s.MaxBlockSize, "samples"), Explain.StatLargestBlock));
        }

        long stereo = s.AssignmentFrames.Sum();
        if (channels == 2 && stereo > 0)
        {
            string[] names = ["Independent Frames", "Left/Side Frames", "Right/Side Frames", "Mid/Side Frames"];
            string[] descriptions = [Explain.StatIndependent, Explain.StatLeftSide, Explain.StatSideRight, Explain.StatMidSide];
            for (int i = 0; i < 4; i++)
            {
                if (i == 0 || s.AssignmentFrames[i] > 0)
                {
                    statistics.Add(cover(names[i], $"{StreamFigures.Counted(s.AssignmentFrames[i], "frame", "frames")} of {StreamFigures.Counted(stereo, "stereo frame", "stereo frames")} ({StreamFigures.Share(s.AssignmentFrames[i], stereo)})",
                        descriptions[i], i == 3 ? Topics.MidSideStereo : i == 1 ? "left-side-stereo" : Topics.ChannelAssignment));
                }
            }
        }

        if (s.Subframes > 0)
        {
            Node types = Cover3("Subframe Types", NodeValue.Integer(s.Subframes, "subframes", "subframe"), Explain.StatSubframeTypes);
            types.Add(cover("Constant", Share(s.SubframeTypes[(int)SubframeType.Constant], s.Subframes), Explain.StatConstant, Topics.Subframes));
            types.Add(cover("Verbatim", Share(s.SubframeTypes[(int)SubframeType.Verbatim], s.Subframes), Explain.StatVerbatim, Topics.Subframes));
            types.Add(cover("Fixed Predictor", Share(s.SubframeTypes[(int)SubframeType.Fixed], s.Subframes), Explain.StatFixed, Topics.LinearPrediction));
            types.Add(cover("Linear Predictor", Share(s.SubframeTypes[(int)SubframeType.Lpc], s.Subframes), Explain.StatLpc, Topics.LinearPrediction));
            statistics.Add(types);
            long lpc = s.SubframeTypes[(int)SubframeType.Lpc];
            if (lpc > 0)
            {
                statistics.Add(cover("Average Predictor Order", NodeValue.Decimal(s.LpcOrderTotal / (double)lpc, 1), Explain.StatAverageOrder, Topics.LinearPrediction));
                statistics.Add(cover("Highest Predictor Order", NodeValue.Integer(s.LpcOrderMax), Explain.StatHighestOrder, Topics.LinearPrediction));
                statistics.Add(cover("Coefficient Precision", s.LpcPrecisionMin == s.LpcPrecisionMax ? NodeValue.Integer(s.LpcPrecisionMin, "bits") : $"{s.LpcPrecisionMin} to {s.LpcPrecisionMax} bits", Explain.StatPrecision, Topics.LinearPrediction));
            }

            statistics.Add(cover("Subframes With Wasted Bits", Share(s.WastedSubframes, s.Subframes), Explain.StatWasted, Topics.WastedBits));
            if (s.ResidualSubframes > 0)
            {
                Node orders = Cover3("Partition Orders", NodeValue.Integer(s.ResidualSubframes, "residuals", "residual"), Explain.StatPartitionOrders);
                for (int order = 0; order < s.PartitionOrders.Length; order++)
                {
                    if (s.PartitionOrders[order] > 0)
                    {
                        orders.Add(cover($"Order {order}", Share(s.PartitionOrders[order], s.ResidualSubframes), Explain.StatPartitionOrder, Topics.RiceCoding));
                    }
                }

                statistics.Add(orders);
                statistics.Add(cover("Rice With 5-Bit Parameters", Share(s.Rice2Subframes, s.ResidualSubframes), Explain.StatRice2, Topics.RiceCoding));
                statistics.Add(cover("Escaped Partitions", NodeValue.Integer(s.EscapedPartitions, "partitions", "partition"), Explain.EscapedPartitionsStat, Topics.RiceCoding));
            }
        }

        statistics.Add(cover("Verified Header Checksums", NodeValue.Integer(s.Frames - s.Crc8Failures, "frames"), Explain.StatHeaderChecksums, Topics.Crc));
        if (s.Crc8Failures > 0)
        {
            statistics.Add(cover("Failed Header Checksums", NodeValue.Integer(s.Crc8Failures, "frames"), Explain.StatHeaderChecksumsFailed, Topics.Crc));
        }

        statistics.Add(cover("Verified Frame Checksums", NodeValue.Integer(s.Frames - s.CutFrames - s.Crc16Failures, "frames"), Explain.StatFrameChecksums, Topics.Crc));
        if (s.Crc16Failures > 0)
        {
            statistics.Add(cover("Failed Frame Checksums", NodeValue.Integer(s.Crc16Failures, "frames"), Explain.StatFrameChecksumsFailed, Topics.Crc));
        }

        if (s.Info is not null)
        {
            bool unknown = StreamInfoBlock.IsMd5Unknown(s.InfoBytes!.Span);
            statistics.Add(cover("MD5 Signature", unknown ? "not stored" : Convert.ToHexStringLower(StreamInfoBlock.Md5(s.InfoBytes!.Span)), Explain.StatMd5, Topics.Verification));
        }

        Node Covered(string name, NodeValue? value, string description) => cover(name, value, description, null);
        s.Figures?.AddTo(statistics, Covered, "Frame", "frames", true, out _);
        Node? anchor = s.Figures is null ? null : statistics.Children.FirstOrDefault(c => c.Name == "Average Bitrate");
        s.AddLeading(statistics);

        string? channelMode = channels switch
        {
            1 => "Mono",
            2 when stereo > 0 && s.AssignmentFrames[0] == stereo => "Stereo",
            2 when stereo > 0 && s.AssignmentFrames[0] == 0 => "Joint stereo",
            2 when stereo > 0 => "Stereo, joint in some frames",
            2 => "Stereo",
            > 2 => "Multichannel",
            _ => null,
        };
        return new StreamInfo
        {
            Nodes = [stream],
            Codec = "FLAC",
            SampleRate = rate,
            Channels = channels,
            ChannelMode = channelMode,
            ChannelLayout = layout,
            BitsPerSample = bits > 0 ? bits : null,
            Samples = samples,
            Duration = duration,
            AverageBitrate = s.Figures?.AverageBitrate,
            IsVbr = s.Figures is { Units: > 0 } figures && figures.MaximumBitrate > figures.MinimumBitrate,
            Encoder = encoder?.Value?.ToString(),
            EncoderConfidence = ClaimRoles.Resolved(encoder).Confidence,
            Writer = ClaimRoles.Resolved(writer).Name,
            WriterConfidence = ClaimRoles.Resolved(writer).Confidence,
            Verification = verification,
            Series = s.Figures?.Series(anchor, s.Context.CancellationToken) ?? [],
        };
    }

    private const string ClaimsTopicChannels = "channel-layouts";

    private static string Share(long count, long total) => $"{StreamFigures.Counted(count, "subframe", "subframes")} ({StreamFigures.Share(count, total)})";

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
