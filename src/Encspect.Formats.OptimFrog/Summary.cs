using Encspect.Core;

namespace Encspect.Formats.OptimFrog;

// The stream node, the checks over the whole file, the statistics, the verification and the stream info.
internal static class Summary
{
    public const string CodecName = "OptimFROG";

    // The codec ID a decoder would register under; none exists, so the host reports that no decoder handles the coding.
    public const string CodecId = "optimfrog";

    // The builders capture the records, never the state, which holds the context and so the input.
    public static Node StreamNode(State s, Source source)
    {
        Records r = s.Records;
        List<Node> leading = s.Leading;
        int first = r.MainIndex == 0 && r.Blocks.Count > 0 ? 1 : 0;
        return new Node(CodecName, r.Offset, Math.Max(0, r.End - r.Offset), null, () => new BlockedChildren(leading, r.Blocks.Count - first, i => Nodes.Block(source, first + i)))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.Blocks,
        };
    }

    public static void Finish(State s, Source source)
    {
        foreach (HeaderInfo header in source.Headers.Values)
        {
            HeaderData.Findings(s, source, header, header.Name, header.At, header.Held);
        }

        Records r = s.Records;
        Node stream = s.Stream;
        MainBlock? main = r.Main;
        if (main is not null)
        {
            s.Leading.Insert(0, Nodes.Block(source, 0));
        }

        Checks(s, source);
        if (main is not { } m)
        {
            s.Findings.Flush(s.Context);
            return;
        }

        Node Cover(string name, NodeValue? value, string description, string? topic = null) => new(name, r.Offset, r.End - r.Offset, value) { Description = description, Topic = topic };
        Node? encoder = Encoder(s, m, Cover);
        if (encoder is not null)
        {
            s.AddLeading(encoder);
        }

        Node? writer = WriterDetection.Detect(s.Context.EncoderClaims, (name, value, description) => Cover(name, value, description, ClaimRoles.TopicOf(name)),
            (claim, text, name) => s.Findings.Add(Severity.Info, "optimfrog.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)));
        if (writer is not null)
        {
            s.AddLeading(writer);
        }

        Node? processor = ProcessorDetection.Detect(s.Context.EncoderClaims, (name, value, description) => Cover(name, value, description, ClaimRoles.TopicOf(name)));
        if (processor is not null)
        {
            s.AddLeading(processor);
        }

        ClaimRoles.CompleteVersion(encoder, s.Context.EncoderClaims);
        StreamVerification verification = Verify(s, m);
        StreamInfo info = Statistics(s, source, m, Cover, encoder, writer, processor, verification);
        s.Context.AddStream(info);
        s.Findings.Flush(s.Context);
    }

    private static void Checks(State s, Source source)
    {
        Records r = s.Records;
        Node stream = s.Stream;

        // The node of the block at an index, or the stream's where the records stopped before it (Records.MaxBlocks).
        Func<Node> At(int index) => index is >= 0 and < Records.MaxBlocks && index < r.Blocks.Count ? () => Nodes.Block(source, index) : () => stream;

        if (r.Counted == 0 || (r.Main is null && r.MainIndex < 0 && (r.Blocks.Count == 0 || !Layout.IsMain(r.Blocks[0].Id))))
        {
            Node at = r.Blocks.Count > 0 ? Nodes.Block(source, 0) : stream;
            long length = r.Counted;
            s.Findings.AddNow(Severity.Warning, "optimfrog.main-block-missing", length == 0
                ? "The data holds no block: a block is a 4-character ID and a length."
                : "The first block is not the main block \"OFR \" that every OptimFROG file starts with, so the file's figures are not read.", at);
        }

        if (r.Blocks.Count > 0 && r.Blocks[0].Id == Layout.MainFloat)
        {
            s.Findings.Add(Severity.Info, "optimfrog.main-block-variant", At(0), _ =>
                "The main block's ID is \"OFRX\", which format.txt does not define; the files the float encoder `off` 5.100 wrote in Encspect's tests start with it, and its fields are read as those of \"OFR \".");
        }

        if (s.SizeInvalidBlocks > 0)
        {
            int index = s.FirstSizeInvalid;
            BlockRecord b = index < Records.MaxBlocks && index < r.Blocks.Count ? r.Blocks[index] : default;
            string id = Layout.Text(b.Id).Trim();
            uint length = b.Length;
            string rule = b.Id switch
            {
                Layout.Main or Layout.MainFloat => $"{Layout.MainFields} bytes for its fields",
                Layout.Comp => $"{Layout.CompFields} bytes for its fields",
                _ => $"{Layout.Md5Length} bytes",
            };
            long others = s.SizeInvalidBlocks - 1;
            s.Findings.Add(Severity.Warning, "optimfrog.block-size-invalid", At(index), count =>
                $"The {id} block states {Counts.Of(length, "byte", "bytes")}, which breaks the specification: it needs {rule}{(count > 1 ? $"; {Counts.Of(others, "more block", "more blocks")} {Counts.Agree(others, "does", "do")} too" : string.Empty)}; it is read as far as it holds fields.", s.SizeInvalidBlocks);
        }

        if (s.CutBlocks > 0)
        {
            int index = s.FirstCut;
            BlockRecord b = r.Blocks[Math.Min(index, r.Blocks.Count - 1)];
            string id = Layout.Text(b.Id).Trim();
            long stated = b.Length;
            long present = Math.Max(0, r.DataEnd - b.Offset - Layout.BlockHeaderLength);
            s.Findings.Add(Severity.Warning, "optimfrog.block-cut", At(index), _ =>
                $"The data ends inside the {id} block at 0x{b.Offset:X}, which states {Counts.Of(stated, "byte", "bytes")}; {present} of them {Counts.Agree(present, "is", "are")} there. The block is read as far as it goes, and a cut data block's CRC-32 is not compared.");
        }

        if (s.CrcFailures > 0)
        {
            int index = s.FirstCrcFailure;
            BlockRecord b = r.Blocks[Math.Min(index, r.Blocks.Count - 1)];
            uint stored = b.StoredCrc;
            uint computed = b.ComputedCrc;
            Func<Node> node = index < Records.MaxBlocks ? () => Nodes.CrcField(source, index) : () => stream;
            s.Findings.Add(Severity.Warning, "optimfrog.block-crc-mismatch", node, count =>
                $"The CRC-32 of data block {index} is {stored:x8}, but its data gives {computed:x8}{(count > 1 ? $"; {Counts.Of(count - 1, "more block", "more blocks")} {Counts.Agree(count - 1, "differs", "differ")}" : string.Empty)}.", s.CrcFailures);
        }

        if (s.UnknownBlocks > 0)
        {
            int index = s.FirstUnknown;
            BlockRecord b = r.Blocks[Math.Min(index, r.Blocks.Count - 1)];
            string id = Layout.Text(b.Id);
            long stated = b.Length;
            s.Findings.Add(Severity.Info, "optimfrog.block-unknown", At(index), count =>
                $"The block \"{id}\" at 0x{b.Offset:X} has an ID the specification does not define, so it is skipped by its length of {Counts.Of(stated, "byte", "bytes")}{(count > 1 ? $"; {Counts.Of(count - 1, "more block", "more blocks")} {Counts.Agree(count - 1, "is", "are")} unknown" : string.Empty)}.", s.UnknownBlocks);
        }

        if (s.OutOfOrderBlocks > 0)
        {
            int index = s.FirstOutOfOrder;
            BlockRecord b = r.Blocks[Math.Min(index, r.Blocks.Count - 1)];
            string id = Layout.Text(b.Id).Trim();
            s.Findings.Add(Severity.Warning, "optimfrog.block-out-of-order", At(index), count =>
                $"The {id} block at 0x{b.Offset:X} comes out of the order the specification gives, 'OFR ' [...] 'HEAD' 'COMP'* 'TAIL' [...] ['MD5 '] ['RECV']{(count > 1 ? $"; {Counts.Of(count - 1, "more block", "more blocks")} {Counts.Agree(count - 1, "does", "do")} too" : string.Empty)}.", s.OutOfOrderBlocks);
        }

        if (r.Blocks.Count < r.Counted)
        {
            long counted = r.Counted;
            s.Findings.Add(Severity.Warning, "optimfrog.blocks-limit", stream, _ =>
                $"The file holds {counted} blocks, more than the {Records.MaxBlocks} the report keeps; the rest are counted and their CRC-32s compared, but they have no nodes, and the graphs and the bitrate figures cover the blocks before them.");
        }

        if (r.Main is not { } main)
        {
            return;
        }

        Node MainNode(string child) => Nodes.Block(source, 0).Children.FirstOrDefault(c => c.Name == child) ?? Nodes.Block(source, 0);
        if (main.SampleType > 10)
        {
            int value = main.SampleType;
            s.Findings.Add(Severity.Info, "optimfrog.value-undefined", () => MainNode("Sample Type"), _ => $"The sample type {value} is not defined (format.txt, appendix A defines 0 to 10).", "sample-type");
        }

        if (main.ChannelConfiguration > 1)
        {
            int value = main.ChannelConfiguration;
            s.Findings.Add(Severity.Info, "optimfrog.value-undefined", () => MainNode("Channel Configuration"), _ => $"The channel configuration {value} is not defined (format.txt, appendix B defines 0 and 1); it is read as two channels.", "channel-configuration");
        }

        if (Layout.BuildSystem(main.BuildSystem) is null)
        {
            int value = main.BuildSystem;
            s.Findings.Add(Severity.Info, "optimfrog.value-undefined", () => MainNode("Encoder ID"), _ => $"The build system {value} in the encoder ID is not defined (format.txt, appendix C defines 0 to 8 and 15 as unknown).", "build-system");
        }

        if (Layout.Mode(main.Mode) is null)
        {
            int value = main.Mode;
            s.Findings.Add(Severity.Info, "optimfrog.value-undefined", () => MainNode("Compression"), _ => $"The compression mode {value} is not defined (format.txt, appendix D defines 0 to 14).", "mode");
        }

        if (Layout.Speedup(main.Speedup) is null)
        {
            int value = main.Speedup;
            s.Findings.Add(Severity.Info, "optimfrog.value-undefined", () => MainNode("Compression"), _ => $"The speedup {value} is not defined (format.txt, appendix D defines 0 to 2).", "speedup");
        }

        if (main.SampleRate == 0)
        {
            s.Findings.Add(Severity.Warning, "optimfrog.sample-rate-zero", () => MainNode("Sample Rate"), _ => "The main block gives a sample rate of 0, so no duration or bitrate is worked out.");
        }

        if (main.Samples % main.Channels != 0)
        {
            long total = main.Samples;
            int channels = main.Channels;
            s.Findings.Add(Severity.Warning, "optimfrog.samples-channels-mismatch", () => MainNode("Total Samples"), _ =>
                $"The main block states {Counts.Of(total, "sample", "samples")} over all channels, which {Counts.Of(channels, "channel", "channels")} do not divide.");
        }

        if (s.DifferingBlocks > 0)
        {
            int index = s.FirstDiffering;
            BlockRecord b = r.Blocks[Math.Min(index, r.Blocks.Count - 1)];
            s.Findings.Add(Severity.Warning, "optimfrog.block-differs", At(index), count =>
                $"Data block {index} gives a sample type, channel configuration or encoder ID other than the main block's{(count > 1 ? $"; {Counts.Of(count - 1, "more block", "more blocks")} {Counts.Agree(count - 1, "differs", "differ")} too" : string.Empty)}.", s.DifferingBlocks);
        }

        if (s.CutBlocks == 0 && s.CompSamples != main.Samples && r.Blocks.Count == r.Counted)
        {
            long held = s.CompSamples;
            long stated = main.Samples;
            s.Findings.Add(Severity.Warning, "optimfrog.samples-mismatch", () => MainNode("Total Samples"), _ =>
                $"The data blocks hold {Counts.Of(held, "sample", "samples")} over all channels, but the main block states {stated}; a block is missing, or the file ends at a block boundary.");
        }

        if (!s.HasHead)
        {
            s.Findings.Add(Severity.Warning, "optimfrog.head-missing", stream, _ => "The file holds no header block (HEAD), which the order in the specification does not bracket as optional.");
        }

        if (!s.HasTail && s.CutBlocks == 0)
        {
            s.Findings.Add(Severity.Warning, "optimfrog.tail-missing", stream, _ => "The file holds no footer block (TAIL), which the order in the specification does not bracket as optional.");
        }
    }

    private static Node? Encoder(State s, MainBlock m, Func<string, NodeValue?, string, string?, Node> cover)
    {
        Node encoder = cover("Encoder", $"OptimFROG {Layout.VersionText(m.Version)}", Explain.Encoder, ClaimRoles.EncoderTopic);
        encoder.Add(cover("Confidence", "High", Explain.ConfidenceHigh, null));
        encoder.Add(cover("Evidence", $"The main block's encoder ID states version {Layout.VersionText(m.Version)}", Explain.Evidence, null));
        return encoder;
    }

    private static StreamVerification Verify(State s, MainBlock m)
    {
        Records r = s.Records;
        bool coversAll = s.CompBlocks > 0 && s.CompSamples == m.Samples && s.CutBlocks == 0 && s.SizeInvalidBlocks == 0 && s.DifferingBlocks == 0 && s.OutOfOrderBlocks == 0;
        long wholeStored = s.CompBlocks + s.CutBlocks;
        IDecodeSession session = s.Context.StartDecoding(CodecId, new DecodeSetup { Stream = s.Stream, Channels = m.Channels, BitsPerSample = Math.Max(m.BitsPerSample, 8), Samples = m.SamplesPerChannel });
        return session.Finish(
        [
            new ChecksumTally { Name = ChecksumNames.BlockCrc32, Covers = ChecksumCoverage.CodedData, CoversAllAudio = coversAll, Stored = wholeStored, Checked = s.CompBlocks, Failed = s.CrcFailures },
            new ChecksumTally { Name = ChecksumNames.Md5, Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = true, Stored = r.Md5 is not null ? 1 : 0 },
        ]);
    }

    private static IReadOnlyList<Speaker>? LayoutOf(MainBlock m, uint? mask) =>
        mask is { } value and not 0 ? ChannelLayouts.FromWaveMask(value, m.Channels) : m.Channels == 1 ? ChannelLayouts.Mono : ChannelLayouts.Stereo;

    private static StreamInfo Statistics(State s, Source source, MainBlock m, Func<string, NodeValue?, string, string?, Node> cover, Node? encoder, Node? writer, Node? processor, StreamVerification verification)
    {
        Records r = s.Records;
        Node stream = s.Stream;
        var statistics = new Node("Statistics", r.Offset, r.End - r.Offset) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        long rate = m.SampleRate;
        long samples = m.SamplesPerChannel;
        double duration = rate > 0 ? samples / (double)rate : 0;
        uint? mask = source.Headers.Values.Select(h => h.Format?.Mask).FirstOrDefault(v => v is not null);
        IReadOnlyList<Speaker>? layout = LayoutOf(m, mask);
        Node Cover3(string name, NodeValue? value, string description) => cover(name, value, description, null);
        statistics.Add(cover("Blocks", NodeValue.Integer(s.CompBlocks, "blocks", "block"), Explain.StatBlocks, Topics.Blocks));
        statistics.Add(Cover3("Samples", NodeValue.Integer(samples, "samples", "sample"), Explain.StatSamples));
        statistics.Add(Cover3("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        if (rate > 0)
        {
            statistics.Add(Cover3("Sample Rate", NodeValue.Integer(rate, "Hz"), Explain.StatRate));
        }

        statistics.Add(Cover3("Channels", NodeValue.Integer(m.Channels, "channels", "channel"), Explain.StatChannels));
        if (layout is not null)
        {
            statistics.Add(cover("Channel Layout", ChannelLayouts.Describe(layout), Explain.StatLayout, Topics.Channels));
        }

        if (m.BitsPerSample > 0)
        {
            statistics.Add(Cover3("Bits Per Sample", NodeValue.Integer(m.BitsPerSample, "bits", "bit"), Explain.StatBits));
        }

        statistics.Add(cover("Mode", Layout.Mode(m.Mode) ?? $"undefined ({m.Mode})", Explain.StatMode, Topics.Modes));
        statistics.Add(cover("Speedup", Layout.Speedup(m.Speedup) ?? $"undefined ({m.Speedup})", Explain.StatSpeedup, Topics.Modes));
        statistics.Add(cover("Sample Type", Layout.SampleType(m.SampleType) ?? $"undefined ({m.SampleType})", Explain.StatSampleType, Topics.Blocks));
        statistics.Add(cover("Build System", Layout.BuildSystem(m.BuildSystem) ?? $"undefined ({m.BuildSystem})", Explain.StatBuildSystem, Topics.Blocks));
        statistics.Add(cover("Verified Block Checksums", NodeValue.Integer(s.CompBlocks - s.CrcFailures, "blocks", "block"), Explain.StatBlockChecksums, Topics.Crc));
        if (s.CrcFailures > 0)
        {
            statistics.Add(cover("Failed Block Checksums", NodeValue.Integer(s.CrcFailures, "blocks", "block"), Explain.StatBlockChecksumsFailed, Topics.Crc));
        }

        StreamFigures figures = s.Figures ?? new StreamFigures(rate);
        Node Covered(string name, NodeValue? value, string description) => cover(name, value, description, null);
        figures.AddTo(statistics, Covered, "Block", "blocks", true, out _);
        Node? anchor = statistics.Children.FirstOrDefault(c => c.Name == "Average Bitrate");
        s.AddLeading(statistics);

        return new StreamInfo
        {
            Nodes = [stream],
            Codec = CodecName,
            SampleRate = (int)Math.Min(rate, int.MaxValue),
            Channels = m.Channels,
            ChannelMode = m.Channels == 1 ? "Mono" : "Stereo",
            ChannelLayout = layout,
            BitsPerSample = m.BitsPerSample > 0 ? m.BitsPerSample : null,
            Samples = samples,
            Duration = duration,
            AverageBitrate = figures.AverageBitrate,
            IsVbr = figures is { Units: > 0 } f && f.MaximumBitrate > f.MinimumBitrate,
            Encoder = ClaimRoles.Resolved(encoder).Name,
            EncoderConfidence = ClaimRoles.Resolved(encoder).Confidence,
            Writer = ClaimRoles.Resolved(writer).Name,
            WriterConfidence = ClaimRoles.Resolved(writer).Confidence,
            Processor = ClaimRoles.Resolved(processor).Name,
            ProcessorConfidence = ClaimRoles.Resolved(processor).Confidence,
            Verification = verification,
            Series = figures.Units > 0 ? figures.Series(anchor, s.Context.CancellationToken) : [],
        };
    }
}
