using Encspect.Core;

namespace Encspect.Formats.Alac;

// The stream node, the checks over the whole stream, the statistics, the writer, the verification and the stream info.
internal static class Summary
{
    public const string CodecName = CodecNames.AppleLossless;

    // Ends the analysis of a stream: the checks, the nodes and the stream info. The nodes that cover the stream have the extent and the ranges of the
    // container's node for it, which the packets may not fill (MP4 samples in several chunks).
    public static void Finish(State s, CodecConfiguration? configuration)
    {
        Node stream = s.Stream;
        Checks(s);
        if (!HasStream(s))
        {
            // Without a usable cookie there is no stream to describe, but the frames are in the tree as the bytes they are.
            if (s.Listed > 0)
            {
                Records unread = s.Records;
                stream.Add(new Node("Frames", stream.Offset, stream.Length, NodeValue.Integer(unread.Frames.Count, "frames", "frame"), () => new BlockedChildren([], unread.Frames.Count, i => Nodes.Frame(unread, i)))
                {
                    Kind = NodeKind.Stream,
                    Ranges = stream.Ranges,
                    Description = Explain.ContainedFrames,
                    Topic = Topics.Alac,
                });
            }

            s.Findings.Flush(s.Context);
            return;
        }

        Node Cover(string name, NodeValue? value, string description, string? topic = null) =>
            new(name, stream.Offset, stream.Length, value) { Ranges = stream.Ranges, Description = description, Topic = topic };
        Node? writer = Writer(s, stream);
        if (writer is not null)
        {
            stream.Add(writer);
        }

        StreamVerification verification = Verify(s);
        StreamInfo? info = Statistics(s, Cover, stream, writer, verification, configuration);
        if (info is not null)
        {
            s.Context.AddStream(info);
        }

        foreach (Node leading in s.Leading)
        {
            stream.Add(leading);
        }

        Records records = s.Records;
        stream.Add(new Node("Frames", stream.Offset, stream.Length, NodeValue.Integer(records.Frames.Count, "frames", "frame"), () => new BlockedChildren([], records.Frames.Count, i => Nodes.Frame(records, i)))
        {
            Kind = NodeKind.Stream,
            Ranges = stream.Ranges,
            Description = Explain.ContainedFrames,
            Topic = Topics.Alac,
        });
        s.Findings.Flush(s.Context);
    }

    private static bool HasStream(State s) => s.Cookie is { IsComplete: true };

    private static void Checks(State s)
    {
        if (s.Cookie is { IsComplete: true, MaxFrameBytes: > 0 } cookie && s.Largest > cookie.MaxFrameBytes && s.CookieNode is { } cookieNode)
        {
            long largest = s.Largest;
            uint stated = cookie.MaxFrameBytes;
            s.Findings.AddNow(Severity.Warning, "alac.frame-larger-than-stated",
                $"The largest frame is {Counts.Of(largest, "byte", "bytes")}, more than the {Counts.Of(stated, "byte", "bytes")} the cookie states as the maximum size of a packet.", Field(cookieNode, "Specific Config", "Maximum Frame Size"));
        }

        if (s.Listed > Records.MaxEntries)
        {
            long listed = s.Listed;
            Node at = s.Stream;
            s.Findings.Add(Severity.Warning, "alac.entries-limit", at, _ =>
                $"The container holds {Counts.Of(listed, "frame", "frames")}, more than the {Records.MaxEntries} the report keeps; the rest are read and counted, but they have no nodes, and the graphs and the bitrate figures cover the frames before them.");
        }
    }

    // Findings about what the cookie's fields say.
    public static void CookieFindings(State s, AlacCookie c, Node cookie)
    {
        if (!c.IsComplete)
        {
            int length = c.Length;
            s.Findings.AddNow(Severity.Warning, "alac.cookie-too-short",
                $"The magic cookie is {Counts.Of(length, "byte", "bytes")} long, fewer than the {AlacCookie.ConfigLength} of the ALACSpecificConfig, so the stream's figures are unknown and its frames are not read.", cookie);
            return;
        }

        string unread = c.CanWalk ? string.Empty : "; the frames are not read";
        if (c.CompatibleVersion != 0)
        {
            int version = c.CompatibleVersion;
            s.Findings.AddNow(Severity.Warning, "alac.compatible-version",
                $"The cookie states the compatible version {version}; the description says 0, and Apple's decoder refuses a cookie with a larger value.", Field(cookie, "Specific Config", "Compatible Version"));
        }

        if (!c.IsFrameLengthValid)
        {
            uint length = c.FrameLength;
            s.Findings.AddNow(Severity.Warning, "alac.frame-length-invalid",
                $"The cookie states a frame length of {length}, which is 0 or more than the {AlacCookie.MaxFrameLength} samples per channel a decoder is designed for.", Field(cookie, "Specific Config", "Frame Length"));
        }

        if (c.SampleRate == 0)
        {
            s.Findings.AddNow(Severity.Warning, "alac.sample-rate-zero",
                "The cookie states a sample rate of 0 Hz; Apple's ReadMe gives 1 to 384,000 Hz, so the stream's duration and bitrates are unknown.", Field(cookie, "Specific Config", "Sample Rate"));
        }

        if (!c.IsBitDepthValid)
        {
            int depth = c.BitDepth;
            s.Findings.AddNow(Severity.Warning, "alac.bit-depth-invalid",
                $"The cookie states a bit depth of {depth}; Apple's encoder writes 16, 20, 24 or 32{unread}.", Field(cookie, "Specific Config", "Bit Depth"));
        }

        if (!c.AreChannelsValid)
        {
            int channels = c.Channels;
            s.Findings.AddNow(Severity.Warning, "alac.channels-invalid",
                $"The cookie states {Counts.Of(channels, "channel", "channels")}; ALAC has 1 to {AlacCookie.MaxChannels}{unread}.", Field(cookie, "Specific Config", "Channels"));
        }

        if (c.HasLayout && CoreAudioLayouts.Channels(c.LayoutTag, c.LayoutBitmap, c.LayoutDescriptions) != c.Channels)
        {
            long layoutChannels = CoreAudioLayouts.Channels(c.LayoutTag, c.LayoutBitmap, c.LayoutDescriptions);
            int channels = c.Channels;
            s.Findings.AddNow(Severity.Warning, "alac.channel-layout-differs",
                $"The channel layout info describes {Counts.Of(layoutChannels, "channel", "channels")}, but the cookie states {channels}; the report gives the cookie's count and no layout.", Field(cookie, "Channel Layout Info", "Channel Layout Tag"));
        }

        if (c.ExtraLength > 0)
        {
            int extra = c.ExtraLength;
            s.Findings.AddNow(Severity.Info, "alac.cookie-extra",
                $"The cookie holds {Counts.Of(extra, "byte", "bytes")} after the fields the description defines; they are not read.", cookie.Children.FirstOrDefault(n => n.Name == "Extra Data") ?? cookie);
        }
    }

    private static Node Field(Node cookie, string group, string name)
    {
        Node? parent = cookie.Children.FirstOrDefault(n => n.Name == group);
        return parent?.Children.FirstOrDefault(n => n.Name == name) ?? parent ?? cookie;
    }

    private static StreamVerification Verify(State s)
    {
        // ALAC stores no checksum, and no decoder exists for it, so the session decodes nothing and its Finish says so.
        AlacCookie? cookie = s.Cookie;
        IDecodeSession session = s.Context.StartDecoding(AlacCodecParser.CodecId, new DecodeSetup
        {
            Stream = s.Stream,
            Channels = cookie?.Channels ?? 0,
            BitsPerSample = cookie?.BitDepth ?? 0,
            Samples = s.Samples,
            Configuration = s.CookieBytes,
        });
        return session.Finish([]);
    }

    private static Node? Writer(State s, Node stream) =>
        WriterDetection.Detect(s.Context.EncoderClaims, (name, value, description) => new Node(name, stream.Offset, stream.Length, value) { Ranges = stream.Ranges, Description = description, Topic = ClaimRoles.TopicOf(name) },
            (claim, text, name) => s.Findings.Add(Severity.Info, "alac.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
            format: new WriterDetection.StreamFormat(KnownWriters.Alac, CodecName, (claim, message, node) => s.Findings.Add(Severity.Info, "alac.writer-other-format", node, _ => message)));

    private static StreamInfo? Statistics(State s, Func<string, NodeValue?, string, string?, Node> cover, Node stream, Node? writer, StreamVerification verification, CodecConfiguration? configuration)
    {
        AlacCookie? cookie = s.Cookie is { IsComplete: true } complete ? complete : null;
        var statistics = new Node("Statistics", stream.Offset, stream.Length) { Kind = NodeKind.Statistics, Ranges = stream.Ranges, Description = Explain.Statistics };
        StreamFigures? figures = s.Figures;
        long rate = cookie?.SampleRate ?? 0;
        int channels = cookie?.Channels ?? 0;
        long samples = s.Samples;
        double duration = rate > 0 ? samples / (double)rate : 0;
        if (samples == 0 && configuration?.Duration is { } stated && rate > 0)
        {
            duration = stated;
            samples = (long)Math.Round(stated * rate);
        }

        long frames = s.Listed;
        Node framesNode = statistics.Add(cover("Frames", NodeValue.Integer(frames, "frames", "frame"), Explain.StatFrames, Topics.Alac));
        if (s.Tally.Elements.Sum() > 0 || cookie is { CanWalk: true })
        {
            if (s.PartialFrames > 0)
            {
                framesNode.Add(cover("Partial Frames", Share(s.PartialFrames, frames, "frame", "frames"), Explain.StatPartialFrames, null));
            }

            if (s.UncompressedFrames > 0)
            {
                framesNode.Add(cover("Uncompressed Frames", Share(s.UncompressedFrames, frames, "frame", "frames"), Explain.StatUncompressedFrames, null));
            }

            if (s.UnreadableFrames > 0)
            {
                framesNode.Add(cover("Unreadable Frames", Share(s.UnreadableFrames, frames, "frame", "frames"), Explain.StatUnreadableFrames, null));
            }
        }

        if (cookie is not null)
        {
            statistics.Add(cover("Samples", NodeValue.Integer(samples, "samples", "sample"), Explain.StatSamples, null));
            statistics.Add(cover("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration, null));
            statistics.Add(cover("Sample Rate", NodeValue.Integer(rate, "Hz"), Explain.StatRate, null));
            statistics.Add(cover("Channels", NodeValue.Integer(channels, "channels", "channel"), Explain.StatChannels, null));
            statistics.Add(cover("Bit Depth", NodeValue.Integer(cookie.BitDepth, "bits", "bit"), Explain.StatBits, null));
            statistics.Add(cover("Frame Length", NodeValue.Integer(cookie.FrameLength, "samples", "sample"), Explain.StatFrameLength, Topics.Alac));
        }

        CodingNodes nodes = new(null, null);
        if (cookie is { CanWalk: true })
        {
            nodes = Coding(s, statistics, cover);
        }

        Node? anchor = null;
        if (figures is { Units: > 0 })
        {
            figures.AddTo(statistics, (name, value, description) => cover(name, value, description, null), "Frame", "frames", true, out _);
            anchor = statistics.Children.FirstOrDefault(c => c.Name == "Average Bitrate");
        }

        s.Leading.Add(statistics);
        var info = new StreamInfo
        {
            Nodes = [stream],
            Codec = CodecName,
            SampleRate = (int)Math.Min(rate, int.MaxValue),
            Channels = channels,
            ChannelMode = channels switch { 1 => "Mono", 2 => "Stereo", > 2 => "Multichannel", _ => null },
            ChannelLayout = cookie?.Layout,
            BitsPerSample = cookie is { BitDepth: > 0 } ? cookie.BitDepth : null,
            Samples = samples,
            Duration = duration,
            AverageBitrate = figures?.AverageBitrate,
            IsVbr = figures is { Units: > 0 } f && f.MaximumBitrate > f.MinimumBitrate,
            Writer = ClaimRoles.Resolved(writer).Name,
            WriterConfidence = ClaimRoles.Resolved(writer).Confidence,
            Verification = verification,
        };
        if (figures is null)
        {
            return info;
        }

        List<Series> series = figures.Series(anchor, s.Context.CancellationToken);
        IReadOnlyList<CategorySeries> categories = series.Count > 0
            ? s.Series.Build(series[0].X, series[0], figures.Time, nodes)
            : [];
        return info with { Series = series, CategorySeries = categories };
    }

    // The tallies of how the elements and channels are coded, and the nodes the coding series are anchored to.
    private static CodingNodes Coding(State s, Node statistics, Func<string, NodeValue?, string, string?, Node> cover)
    {
        CodingTally t = s.Tally;
        Node? stereo = null;
        Node? elementCoding = null;
        long audio = t.Compressed + t.Uncompressed;
        Node elements = statistics.Add(cover("Elements", NodeValue.Integer(t.Elements.Sum(), "elements", "element"), Explain.StatElements, Topics.Alac));
        for (int tag = 0; tag < 8; tag++)
        {
            if (t.Elements[tag] > 0)
            {
                elements.Add(cover(ElementTags.Name(tag), NodeValue.Integer(t.Elements[tag], "elements", "element"), Explain.StatElementCount, null));
            }
        }

        if (audio > 0)
        {
            elementCoding = statistics.Add(cover("Element Coding", NodeValue.Integer(audio, "elements", "element"), Explain.StatElementCoding, Topics.Alac));
            Entries(elementCoding, cover, CodingCategories.ElementCodings, [t.Compressed, t.Uncompressed], audio, "element", "elements");
        }

        if (t.Pairs > 0)
        {
            stereo = statistics.Add(cover("Stereo Coding", NodeValue.Integer(t.Pairs, "channel pairs", "channel pair"), Explain.StatStereoCoding, Topics.Prediction));
            Entries(stereo, cover, CodingCategories.StereoCodings, [t.Pairs - t.MixedPairs, t.MixedPairs], t.Pairs, "channel pair", "channel pairs");
        }

        if (t.Channels > 0)
        {
            Node compressed = statistics.Add(cover("Compressed Channels", NodeValue.Integer(t.Channels, "channels", "channel"), Explain.StatChannelsCompressed, Topics.Prediction));
            Distribution(compressed, cover, "Prediction Types", Explain.StatPredictionTypes, t.PredictionTypes, t.Channels, i => $"Type {i}", Topics.Prediction);
            Distribution(compressed, cover, "Predictor Orders", Explain.StatPredictorOrders, t.PredictorOrders, t.Channels, i => $"Order {i}", Topics.Prediction);
            Distribution(compressed, cover, "Predictor Shifts", Explain.StatPredictorShifts, t.PredictorShifts, t.Channels, i => $"Shift {i}", Topics.Prediction);
            Distribution(compressed, cover, "History Weight Factors", Explain.StatWeightFactors, t.WeightFactors, t.Channels, i => $"Factor {i}", Topics.AdaptiveGolomb);
            compressed.Add(cover("Zero Runs", NodeValue.Integer(t.ZeroRuns, "runs", "run"), Explain.StatZeroRuns, Topics.AdaptiveGolomb));
            compressed.Add(cover("Long Codes", NodeValue.Integer(t.LongCodes, "codes", "code"), Explain.StatLongCodes, Topics.AdaptiveGolomb));
        }

        if (t.Compressed > 0)
        {
            Distribution(statistics, cover, "Bytes Shifted", Explain.StatBytesShifted, t.BytesShifted, t.Compressed, i => $"{i} {(i == 1 ? "byte" : "bytes")}", Topics.WastedBits, "element", "elements");
        }

        if (t.FillBytes > 0)
        {
            statistics.Add(cover("Fill Bytes", NodeValue.Integer(t.FillBytes, "bytes", "byte"), Explain.StatFillBytes, null));
        }

        if (t.DataStreamBytes > 0)
        {
            statistics.Add(cover("Data Stream Bytes", NodeValue.Integer(t.DataStreamBytes, "bytes", "byte"), Explain.StatDataStreamBytes, null));
        }

        return new CodingNodes(stereo, elementCoding);
    }

    private static void Entries(Node parent, Func<string, NodeValue?, string, string?, Node> cover, Category[] categories, long[] counts, long total, string unit, string units)
    {
        for (int i = 0; i < categories.Length; i++)
        {
            if (counts[i] > 0)
            {
                parent.Add(cover(categories[i].Name, Share(counts[i], total, unit, units), categories[i].Description, null));
            }
        }
    }

    private static void Distribution(Node parent, Func<string, NodeValue?, string, string?, Node> cover, string name, string description, long[] counts, long total, Func<int, string> label, string topic,
        string unit = "channel", string units = "channels")
    {
        Node node = parent.Add(cover(name, NodeValue.Integer(total, units, unit), description, topic));
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                node.Add(cover(label(i), Share(counts[i], total, unit, units), Explain.StatCount, null));
            }
        }
    }

    private static string Share(long count, long total, string unit, string units) => $"{StreamFigures.Counted(count, unit, units)} ({StreamFigures.Share(count, total)})";
}
