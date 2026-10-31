using Encspect.Core;

namespace Encspect.Formats.Tta;

// The stream node, the checks over the whole stream, the statistics, the writer, the verification and the stream info.
internal static class Summary
{
    public const string CodecName = "TTA";

    // The builders capture the leading nodes and the records, never the state, which holds the context and so the input.
    public static Node StreamNode(long start, long length, List<Node> leading, Records records) =>
        new(CodecName, start, length, null, () => new BlockedChildren(leading, records.FrameNodes, i => Nodes.Frame(records, i)))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.Stream,
        };

    // Ends the analysis of a stream: the checks, the nodes and the stream info. start and end are the stream node's extent.
    public static void Finish(State s, long start, long end)
    {
        Node stream = s.Stream;
        Checks(s);
        if (!HasStream(s))
        {
            s.Findings.Flush(s.Context);
            return;
        }

        Node Cover(string name, NodeValue? value, string description, string? topic = null) =>
            new(name, start, end - start, value) { Description = description, Topic = topic };
        Node? writer = Writer(s, start, end);
        if (writer is not null)
        {
            s.AddLeading(writer);
        }

        StreamVerification verification = Verify(s);
        StreamInfo? info = Statistics(s, Cover, stream, writer, verification);
        if (info is not null)
        {
            s.Context.AddStream(info);
        }

        if (s.IsContained)
        {
            Records records = s.Records;
            stream.Add(new Node("Frames", start, end - start, NodeValue.Integer(records.FrameNodes, "frames", "frame"), () => new BlockedChildren([], records.FrameNodes, i => Nodes.Frame(records, i)))
            {
                Kind = NodeKind.Stream,
                Description = Explain.ContainedFrames,
                Topic = Topics.Stream,
            });
        }

        s.Findings.Flush(s.Context);
    }

    private static bool HasStream(State s) => s.Header is not null || s.Frames > 0 || s.SampleRate > 0;

    private static void Checks(State s)
    {
        Records records = s.Records;
        if (s.Header is { } header)
        {
            if (!header.IsCrcValid)
            {
                uint stored = header.StoredCrc;
                uint computed = header.ComputedCrc;
                s.Findings.Add(Severity.Warning, "tta.header-crc-mismatch", s.HeaderNode!, _ =>
                    $"The header stores the CRC-32 {stored:x8}, but its 18 bytes before it give {computed:x8}, so the header is damaged and the fields in it cannot be trusted.");
            }

            if (records.TableStoredCrc is { } tableStored && tableStored != records.TableComputedCrc)
            {
                uint computed = records.TableComputedCrc;
                s.Findings.Add(Severity.Warning, "tta.seek-table-crc-mismatch", s.TableNode!, _ =>
                    $"The seek table stores the CRC-32 {tableStored:x8}, but its entries give {computed:x8}, so the table is damaged and the frame lengths in it cannot be trusted.");
            }
        }

        // The node of the entry at an index, or the stream's where the records stopped before it (Records.MaxEntries).
        Node stream = s.Stream;
        Func<Node> At(long index, Func<int, Node> node) => index < Records.MaxEntries ? () => node((int)index) : () => stream;

        if (s.CrcFailures > 0)
        {
            (long first, Entry entry) = s.FirstCrcFailure;
            uint stored = entry.StoredCrc;
            uint computed = entry.ComputedCrc;
            s.Findings.Add(Severity.Warning, "tta.frame-crc-mismatch", At(first, i => Nodes.CrcField(records, i)), count =>
                $"The CRC-32 of frame {first} is {stored:x8}, but its bytes give {computed:x8}{(count > 1 ? $"; {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, "differs", "differ")}" : string.Empty)}.", s.CrcFailures);
        }

        if (s.CutFrames > 0)
        {
            (long cut, Entry entry) = s.FirstCut;
            long present = Math.Max(0, records.DataEnd - entry.Offset);
            uint declared = entry.Size;
            s.Findings.Add(Severity.Warning, "tta.frame-cut", At(cut, i => Nodes.Frame(records, i)), _ =>
                $"The data ends inside frame {cut}, which the seek table gives {Counts.Of(declared, "byte", "bytes")}; {present} of them {Counts.Agree(present, "is", "are")} there.");
        }

        if (s.MissingFrames > 0)
        {
            long missing = s.MissingFrames;
            s.Findings.Add(Severity.Warning, "tta.frames-missing", s.TableNode ?? s.Stream, _ =>
                $"{Counts.Of(missing, "frame", "frames")} the seek table lists {Counts.Agree(missing, "lies", "lie")} past the end of the data.");
        }

        if (s.TooSmallFrames > 0)
        {
            (long small, Entry entry) = s.FirstTooSmall;
            uint declared = entry.Size;
            s.Findings.Add(Severity.Warning, "tta.frame-too-small", At(small, i => Nodes.Frame(records, i)), count =>
                $"The seek table gives frame {small} {Counts.Of(declared, "byte", "bytes")}, fewer than the 4 of the CRC-32 every frame ends with{(count > 1 ? $"; {count - 1} more {Counts.Agree(count - 1, "entry is", "entries are")} as short" : string.Empty)}.", s.TooSmallFrames);
        }

        if (s.Listed > Records.MaxEntries)
        {
            long listed = s.Listed;
            bool isContained = s.IsContained;
            Node at = s.TableNode ?? stream;
            s.Findings.Add(Severity.Warning, "tta.entries-limit", at, _ =>
                $"{(isContained ? "The container holds" : "The seek table lists")} {Counts.Of(listed, isContained ? "frame" : "entry", isContained ? "frames" : "entries")}, more than the {Records.MaxEntries} the report keeps; the rest are counted and their CRC-32s compared, "
                + "but they have no nodes, and the graphs and the bitrate figures cover the frames before them.");
        }
    }

    private static StreamVerification Verify(State s)
    {
        Records records = s.Records;
        var tallies = new List<ChecksumTally>();
        if (s.Header is { } header)
        {
            tallies.Add(new ChecksumTally { Name = ChecksumNames.HeaderCrc32, Covers = ChecksumCoverage.Header, Stored = 1, Checked = 1, Failed = header.IsCrcValid ? 0 : 1 });
        }

        if (records.TableStoredCrc is { } tableStored)
        {
            tallies.Add(new ChecksumTally { Name = ChecksumNames.SeekTableCrc32, Covers = ChecksumCoverage.Header, Stored = 1, Checked = 1, Failed = tableStored == records.TableComputedCrc ? 0 : 1 });
        }

        // The frame CRC-32 of every frame holds all the audio the header counts only when the header can be trusted and every frame it calls for
        // is there, whole.
        bool coversAll = s.Header is { IsCrcValid: true, Samples: > 0, SampleRate: > 0 } counted && s.Frames == counted.FrameCount
            && s.CutFrames == 0 && s.TooSmallFrames == 0 && s.MissingFrames == 0 && !s.HasGap;
        tallies.Add(new ChecksumTally
        {
            Name = ChecksumNames.FrameCrc32,
            Covers = ChecksumCoverage.CodedData,
            CoversAllAudio = coversAll,
            // A frame shorter than its CRC-32 stores none: it is neither stored nor compared (tta.frame-too-small), and CoversAllAudio stays false.
            Stored = s.Frames + s.CutFrames,
            Checked = s.Frames,
            Failed = s.CrcFailures,
        });

        // No decoder exists for TTA, so the session decodes nothing and its Finish says so.
        IDecodeSession session = s.Context.StartDecoding(TtaCodecParser.CodecId, new DecodeSetup
        {
            Stream = s.Stream,
            Channels = s.Channels,
            BitsPerSample = s.BitsPerSample,
            Samples = s.Header?.Samples,
            Configuration = s.HeaderBytes,
        });
        return session.Finish(tallies);
    }

    private static Node? Writer(State s, long start, long end) =>
        WriterDetection.Detect(s.Context.EncoderClaims, (name, value, description) => new Node(name, start, end - start, value) { Description = description, Topic = ClaimRoles.TopicOf(name) },
            (claim, text, name) => s.Findings.Add(Severity.Info, "tta.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
            format: new WriterDetection.StreamFormat(KnownWriters.Tta, "TTA", (claim, message, node) => s.Findings.Add(Severity.Info, "tta.writer-other-format", node, _ => message)));

    private static StreamInfo? Statistics(State s, Func<string, NodeValue?, string, string?, Node> cover, Node stream, Node? writer, StreamVerification verification)
    {
        Node Cover3(string name, NodeValue? value, string description) => cover(name, value, description, null);
        var statistics = new Node("Statistics", stream.Offset, stream.Length) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        long rate = s.SampleRate;
        int channels = s.Channels;
        int bits = s.BitsPerSample;
        bool hasSamples = s.Header is { SampleRate: > 0 };
        long samples = hasSamples ? s.Samples : 0;
        double duration = hasSamples && rate > 0 ? samples / (double)rate : 0;
        StreamFigures? figures = hasSamples ? Figures(s) : null;
        statistics.Add(cover("Frames", NodeValue.Integer(s.Frames, "frames", "frame"), Explain.StatFrames, Topics.Stream));
        if (hasSamples)
        {
            statistics.Add(Cover3("Samples", NodeValue.Integer(samples, "samples", "sample"), Explain.StatSamples));
            statistics.Add(Cover3("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        }

        if (rate > 0)
        {
            statistics.Add(Cover3("Sample Rate", NodeValue.Integer(rate, "Hz"), Explain.StatRate));
            statistics.Add(cover("Frame Length", NodeValue.Integer(TtaHeader.FrameLengthOf(rate), "samples", "sample"), Explain.StatFrameLength, Topics.Stream));
        }

        if (channels > 0)
        {
            statistics.Add(Cover3("Channels", NodeValue.Integer(channels, "channels", "channel"), Explain.StatChannels));
        }

        if (bits > 0)
        {
            statistics.Add(Cover3("Bits Per Sample", NodeValue.Integer(bits, "bits", "bit"), Explain.StatBits));
        }

        statistics.Add(cover("Verified Frame Checksums", NodeValue.Integer(s.Frames - s.CrcFailures, "frames", "frame"), Explain.StatFrameChecksums, Topics.Crc));
        if (s.CrcFailures > 0)
        {
            statistics.Add(cover("Failed Frame Checksums", NodeValue.Integer(s.CrcFailures, "frames", "frame"), Explain.StatFrameChecksumsFailed, Topics.Crc));
        }

        Node Covered(string name, NodeValue? value, string description) => cover(name, value, description, null);
        figures?.AddTo(statistics, Covered, "Frame", "frames", true, out _);
        Node? anchor = figures is null ? null : statistics.Children.FirstOrDefault(c => c.Name == "Average Bitrate");
        s.AddLeading(statistics);

        return new StreamInfo
        {
            Nodes = [stream],
            Codec = CodecName,
            SampleRate = (int)Math.Min(rate, int.MaxValue),
            Channels = channels,
            ChannelMode = channels switch { 1 => "Mono", 2 => "Stereo", > 2 => "Multichannel", _ => null },
            BitsPerSample = bits > 0 ? bits : null,
            Samples = samples,
            Duration = duration,
            AverageBitrate = figures?.AverageBitrate,
            IsVbr = figures is { Units: > 0 } f && f.MaximumBitrate > f.MinimumBitrate,
            Writer = ClaimRoles.Resolved(writer).Name,
            WriterConfidence = ClaimRoles.Resolved(writer).Confidence,
            Verification = verification,
            Series = figures?.Series(anchor, s.Context.CancellationToken) ?? [],
        };
    }

    // The bitrate of every frame the data holds whole.
    private static StreamFigures Figures(State s)
    {
        var figures = new StreamFigures(s.SampleRate);
        Records records = s.Records;
        for (int i = 0; i < records.Entries.Count; i++)
        {
            if (i % 4096 == 0)
            {
                s.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            Entry e = records.Entries[i];
            if (e.IsComplete)
            {
                figures.Add(e.Offset, e.Size * 8L, records.SamplesOf(i));
            }
        }

        return figures;
    }

    // Findings about what the header's fields say; the CRC-32 is compared in Summary.
    public static void HeaderFindings(State s, TtaHeader header)
    {
        Node at = s.HeaderNode!;
        if (header.AudioFormat == TtaHeader.FormatEncrypted)
        {
            s.Findings.AddNow(Severity.Info, "tta.audio-format-encrypted", "The audio format is 2: libtta-c 2.3 writes it for a stream encoded with a password in Encspect's test file, and FFmpeg 6.1.1 refuses to decode such a file without one. The format description does not define the value.", at);
        }
        else if (header.AudioFormat != TtaHeader.FormatSimple)
        {
            int format = header.AudioFormat;
            s.Findings.AddNow(Severity.Warning, "tta.audio-format-unknown", $"The audio format is {format}; the format description does not define it, and Encspect's test files hold 1 and 2.", at);
        }

        if (header.Channels == 0)
        {
            s.Findings.AddNow(Severity.Warning, "tta.channels-zero", "The header gives 0 channels.", at);
        }

        if (header.BitsPerSample is not (8 or 16 or 24))
        {
            int bits = header.BitsPerSample;
            s.Findings.AddNow(Severity.Info, "tta.bits-per-sample-unusual", $"The header gives {Counts.Of(bits, "bit", "bits")} per sample; the compression theory page of the TTA authors names 8, 16 and 24.", at);
        }

        if (header.SampleRate == 0)
        {
            s.Findings.AddNow(Severity.Warning, "tta.sample-rate-zero", "The header gives a sample rate of 0, which leaves the number of samples in a frame, and with it the number of entries of the seek table, undefined.", at);
        }
        else if (header.Samples == 0)
        {
            s.Findings.AddNow(Severity.Warning, "tta.no-samples", "The header counts no samples, so it calls for no frame.", at);
        }
    }
}
