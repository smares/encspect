using Encspect.Core;

namespace Encspect.Formats.Flac;

// The names the checksums of a FLAC stream are declared with, which the decoder's results use too.
internal static class ChecksumNames
{
    public const string HeaderCrc8 = "frame header CRC-8";
    public const string FrameCrc16 = "frame CRC-16";
    public const string StreamInfoMd5 = "STREAMINFO MD5";
}

// The state of one analysis: the records the nodes are built from, the figures worked out as frames are read, and the session the frames
// are handed to. Shared by the raw format parser and the Ogg codec parser, which differ in how they find frames.
internal sealed class State
{
    // Frames past these are kept as 56-byte entries without their subframes.
    public const int MaxDetailedFrames = 4096;

    public State(AnalysisContext context, bool isContained)
    {
        Context = context;
        IsContained = isContained;
    }

    public AnalysisContext Context { get; }

    // The node of the stream, which holds the frames; set before the first frame is read.
    public Node Stream { get; set; } = null!;

    public bool IsContained { get; }

    public Records Records { get; } = new();

    public ChunkedList<Entry> Entries => Records.Entries;

    public FindingCollector Findings { get; } = new();

    // The nodes before the frames: the marker, the metadata blocks, then the encoder, the writer and the statistics.
    public List<Node> Leading { get; } = [];

    // Adds a node before the frames: to the stream node's leading nodes, or, in a stream a container holds, to the container's node.
    public void AddLeading(Node node)
    {
        if (IsContained)
        {
            Stream.Add(node);
        }
        else
        {
            Leading.Add(node);
        }
    }

    public StreamInfoBlock? Info { get; private set; }

    // The Vorbis comment the stream holds (the first), whose vendor string and ENCODER field name the encoder.
    public Node? CommentTag { get; set; }

    private readonly Dictionary<int, int> _seen = [];

    // Counts a metadata block of a type and returns how many of the type have been read.
    public int Seen(int type) => _seen[type] = _seen.GetValueOrDefault(type) + 1;

    public MappedBytes? InfoBytes { get; private set; }

    public Node? Md5Node { get; set; }

    public IDecodeSession? Session { get; private set; }

    public int MetadataBlocks { get; set; }

    public long ProbedBytes { get; set; }

    public bool IsProbeLimitReached { get; set; }

    // Counts over the frames.
    public long Frames { get; private set; }

    public long CutFrames { get; private set; }

    public long Samples { get; private set; }

    public long Crc8Failures { get; private set; }

    public long Crc16Failures { get; private set; }

    public long UnreadableFrames { get; private set; }

    public long SkippedBytes { get; set; }

    public bool HasGap { get; private set; }

    public long VariableFrames { get; private set; }

    public long[] AssignmentFrames { get; } = new long[4];

    public long[] SubframeTypes { get; } = new long[4];

    public long WastedSubframes { get; private set; }

    public long Subframes { get; private set; }

    public long LpcOrderTotal { get; private set; }

    public int LpcOrderMax { get; private set; }

    public int LpcPrecisionMin { get; private set; } = int.MaxValue;

    public int LpcPrecisionMax { get; private set; }

    public long[] FixedOrders { get; } = new long[5];

    public long[] PartitionOrders { get; } = new long[16];

    public long ResidualSubframes { get; private set; }

    public long Rice2Subframes { get; private set; }

    public long EscapedPartitions { get; private set; }

    public int MinBlockSize { get; private set; } = int.MaxValue;

    public int MaxBlockSize { get; private set; }

    public int FirstChannels { get; private set; }

    public int FirstBits { get; private set; }

    public int FirstRate { get; private set; }

    public int MaxFrameLength { get; private set; }

    public int MinFrameLength { get; private set; } = int.MaxValue;

    public bool IsFirstSampleKnown { get; private set; }

    public long FirstSample { get; private set; }

    public StreamFigures? Figures { get; private set; }

    public long NextSample { get; private set; }

    private bool _hasFrame;
    private bool _firstStrategy;
    private int _previousBlockSize;
    private int _previousNode = -1;

    // The block size that frame numbers count in when the stream has fixed block sizes: STREAMINFO's maximum, or the first frame's.
    private int _fixedBlockSize;

    public void SetStreamInfo(in StreamInfoBlock info, MappedBytes bytes)
    {
        Info = info;
        InfoBytes = bytes;
        Records.StreamRate = info.SampleRate;
        Records.StreamBits = info.BitsPerSample;
    }

    // Starts the decoding of the stream: with STREAMINFO's figures once known, or the first frame's where the stream has none, which the
    // decoder cannot take.
    public void StartSession(in FrameHeader first)
    {
        if (Session is not null)
        {
            return;
        }

        int channels = Info?.Channels ?? first.Channels;
        int bits = Info?.BitsPerSample ?? first.BitsPerSample;
        Session = Context.StartDecoding(FlacCodecParser.CodecId, new DecodeSetup
        {
            Stream = Stream,
            Channels = channels,
            BitsPerSample = bits,
            Samples = Info is { TotalSamples: > 0 } info ? info.TotalSamples : null,
            Configuration = InfoBytes,
        });
    }

    public void StartSession()
    {
        if (Info is { } info)
        {
            StartSession(new FrameHeader { ChannelCode = info.Channels - 1, BitsPerSample = info.BitsPerSample });
        }
    }

    public void AddSkipped(long offset, long length)
    {
        int index = Entries.Count;
        Entries.Add(new Entry { Kind = EntryKind.Skipped, Offset = offset, Length = length, Detail = -1 });
        SkippedBytes += length;
        Records records = Records;
        Findings.Add(Severity.Warning, "flac.data-skipped", () => Nodes.Skipped(records, index), count =>
            $"{Counts.Of(length, "byte", "bytes")} at 0x{offset:X} {Counts.Agree(length, "holds", "hold")} no frame{(count > 1 ? $", and {Counts.Of(count - 1, "more such stretch", "more such stretches")} {Counts.Agree(count - 1, "does", "do")}" : string.Empty)}; frames are found again after {(length == 1 ? "it" : "them")}.");
    }

    // Adds a frame, in stream order. bytes are the frame's bytes to its end, as far as it has one; for a cut frame, as far as the data goes.
    public void AddFrame(MappedBytes bytes, in FrameHeader header, in FrameWalk walk, ReadOnlySpan<SubframeFacts> facts, EntryStatus status, PartitionLog[]? logs, bool followsLoss = false)
    {
        int index = Entries.Count;
        long offset = bytes.FileStart;
        long length = bytes.FileEnd - bytes.FileStart;

        // The frame's own bytes: a frame that spans Ogg pages covers more of the file than it holds.
        int coded = bytes.Length;
        bool isCut = (status & EntryStatus.Cut) != 0;
        bool isWalked = walk.IsComplete && (status & (EntryStatus.Unreadable | EntryStatus.ExtentFound)) == 0;
        if (Session is null)
        {
            StartSession(header);
        }

        if (!_hasFrame)
        {
            _firstStrategy = header.IsVariable;
            _fixedBlockSize = Info is { MaxBlockSize: > 0 } info ? info.MaxBlockSize : header.BlockSize;
            FirstChannels = header.Channels;
            FirstBits = header.BitsPerSample;
            FirstRate = header.SampleRate;
        }

        long first = header.IsVariable ? header.Number : header.Number * _fixedBlockSize;
        bool followsGap = followsLoss;
        HasGap |= followsLoss;
        Records records = Records;
        if (!_hasFrame)
        {
            FirstSample = first;
            if (first != 0)
            {
                followsGap = true;
                HasGap = true;
                Findings.Add(Severity.Info, "flac.first-sample-not-zero", () => Nodes.Frame(records, index), _ =>
                    $"The first frame, at 0x{offset:X}, starts at sample {first} instead of 0{(followsLoss ? ": the container lost data before it, or the stream was cut out of a longer one" : ", as in a stream cut out of a longer one")}.");
            }
        }
        else if (first != NextSample)
        {
            followsGap = true;
            HasGap = true;
            long expected = NextSample;
            Findings.Add(Severity.Warning, "flac.sample-number-discontinuity", () => Nodes.Frame(records, index), count =>
                $"The frame at 0x{offset:X} starts at sample {first}, but the frame before it ends at {expected}: {(first > expected ? $"{Counts.Of(first - expected, "sample", "samples")} {Counts.Agree(first - expected, "is", "are")} missing" : "the frames overlap or are out of order")}{(count > 1 ? $", and {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, "breaks", "break")} the sequence" : string.Empty)}.");
        }

        if (followsGap)
        {
            status |= EntryStatus.FollowsGap;
        }

        bool isVariable = header.IsVariable;
        bool firstIsVariable = _firstStrategy;
        if (_hasFrame && isVariable != firstIsVariable)
        {
            Findings.Add(Severity.Warning, "flac.blocking-strategy-changes", () => Nodes.Frame(records, index), _ =>
                $"The frame at 0x{offset:X} uses {(isVariable ? "variable" : "fixed")} block sizes, but the stream began with {(firstIsVariable ? "variable" : "fixed")} ones; a stream uses one blocking strategy.");
        }

        NextSample = first + header.BlockSize;
        _hasFrame = true;

        CheckAgainstStreamInfo(header, coded, offset, index, isCut);
        if (_previousBlockSize != 0 && Info is { MinBlockSize: > 0 } minimum && _previousBlockSize < minimum.MinBlockSize && !isCut)
        {
            int previousSize = _previousBlockSize;
            int previousIndex = _previousNode;
            Findings.Add(Severity.Warning, "flac.block-size-out-of-range", () => Nodes.Frame(records, previousIndex), _ =>
                $"A frame before the last has {Counts.Of(previousSize, "sample", "samples")}, fewer than the {minimum.MinBlockSize} STREAMINFO gives as the smallest block size; only the last frame may be shorter.");
        }

        _previousBlockSize = header.BlockSize;
        _previousNode = index;

        ushort stored = 0;
        ushort computed = 0;
        if (!isCut && coded >= header.Length + 2)
        {
            ReadOnlySpan<byte> data = bytes.Span;
            stored = (ushort)((data[^2] << 8) | data[^1]);
            computed = walk.IsComplete && walk.Length == data.Length ? walk.ComputedCrc16 : FlacCrc.Crc16(data[..^2]);
            if (stored != computed)
            {
                status |= EntryStatus.Crc16Fails;
            }
        }
        else if (!isCut)
        {
            status |= EntryStatus.Cut;
            isCut = true;
        }

        if (walk.HasNonZeroPadding)
        {
            status |= EntryStatus.PaddingSet;
            Findings.Add(Severity.Info, "flac.padding-bits-set", () => Nodes.Frame(records, index), count =>
                $"The padding bits that bring the frame at 0x{offset:X} to a byte boundary are not all 0{(count > 1 ? $", and {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, "has", "have")} the same fault" : string.Empty)}.");
        }

        Frames++;
        if (header.SampleRateBytes > 0 && header.SampleRate == 0)
        {
            Findings.Add(Severity.Info, "flac.sample-rate-zero", () => Nodes.Frame(records, index), count =>
                $"The header of the frame at 0x{offset:X} states the sample rate 0, which the specification allows for content that is not audio{(count > 1 ? $"; {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, "does", "do")}" : string.Empty)}.");
        }

        if (header.StoredCrc8 != header.ComputedCrc8)
        {
            Crc8Failures++;
            status |= EntryStatus.Crc8Fails;
            byte storedCrc8 = header.StoredCrc8;
            byte computedCrc8 = header.ComputedCrc8;
            bool isCrc16Right = !isCut && (status & EntryStatus.Crc16Fails) == 0;
            Findings.Add(Severity.Warning, "flac.header-crc-mismatch", () => Nodes.Frame(records, index), count =>
                $"The header of the frame at 0x{offset:X} has the CRC-8 0x{storedCrc8:X2}, but 0x{computedCrc8:X2} is computed over it; {(isCrc16Right ? "the rest of the frame reads and holds its CRC-16" : "the frame has no CRC-16 that holds either")}{(count > 1 ? $"; {Counts.Of(count - 1, "more header", "more headers")} {Counts.Agree(count - 1, "differs", "differ")}" : string.Empty)}.");
        }

        if (isCut)
        {
            CutFrames++;
            long cutLength = length;
            int blockSize = header.BlockSize;
            Findings.Add(Severity.Warning, "flac.frame-cut", () => Nodes.Frame(records, index), _ =>
                $"The data ends inside the frame at 0x{offset:X}, after {Counts.Of(cutLength, "byte", "bytes")} of its {Counts.Of(blockSize, "sample", "samples")}.");
        }
        else
        {
            Samples += header.BlockSize;
            if ((status & EntryStatus.Crc16Fails) != 0)
            {
                Crc16Failures++;
                ushort storedCrc = stored;
                ushort computedCrc = computed;
                Findings.Add(Severity.Warning, "flac.crc16-mismatch", () => Nodes.Frame(records, index), count =>
                    $"The frame at 0x{offset:X} has the CRC-16 0x{storedCrc:X4}, but 0x{computedCrc:X4} is computed over it{(count > 1 ? $"; {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, "differs", "differ")}" : string.Empty)}.");
            }

            MaxFrameLength = Math.Max(MaxFrameLength, coded);
            MinFrameLength = Math.Min(MinFrameLength, coded);
            Figures ??= new StreamFigures(Info is { SampleRate: > 0 } rate ? rate.SampleRate : header.SampleRate);
            Figures.Add(offset, coded * 8L, header.BlockSize);
        }

        if (header.IsVariable)
        {
            VariableFrames++;
        }

        AssignmentFrames[(int)header.Assignment]++;
        MinBlockSize = Math.Min(MinBlockSize, header.BlockSize);
        MaxBlockSize = Math.Max(MaxBlockSize, header.BlockSize);
        int detail = -1;
        if (isWalked)
        {
            Count(facts[..header.Channels]);
            ReportResidualLimits(facts[..header.Channels], offset, index);
            if (index < MaxDetailedFrames && Records.Details.Count < MaxDetailedFrames)
            {
                detail = Records.Details.Count;
                Records.Details.Add(DetailOf(header, facts, logs));
            }
        }

        if ((status & EntryStatus.Unreadable) != 0)
        {
            UnreadableFrames++;
            SubframeFault fault = walk.Fault;
            int channel = walk.FaultChannel;
            Findings.Add(Severity.Warning, "flac.subframe-invalid", () => Nodes.Frame(records, index), count =>
                $"The frame at 0x{offset:X} cannot be read: {Describe(fault, channel)}{(count > 1 ? $"; {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, "fails", "fail")} the same way" : string.Empty)}.", fault.ToString());
        }

        if (bytes.FileRanges() is { Count: > 1 } ranges)
        {
            status |= EntryStatus.HasRanges;
            Records.Ranges[index] = [.. ranges];
        }

        Entries.Add(new Entry
        {
            Kind = EntryKind.Frame,
            Status = status,
            Offset = offset,
            Length = length,
            Number = header.Number,
            BlockSize = header.BlockSize,
            StatedRate = header.SampleRateBytes > 0 ? header.SampleRate : 0,
            BlockSizeCode = (byte)header.BlockSizeCode,
            RateCode = (byte)header.SampleRateCode,
            ChannelCode = (byte)header.ChannelCode,
            SizeCode = (byte)header.SampleSizeCode,
            NumberLength = (byte)header.NumberLength,
            Crc8 = header.StoredCrc8,
            ComputedCrc8 = header.ComputedCrc8,
            IsVariable = header.IsVariable,
            Crc16 = stored,
            ComputedCrc16 = computed,
            Detail = detail,
        });

        if (Session is { IsDecoding: true } session)
        {
            var codedFrame = new CodedFrame(bytes, "Frame", first, header.BlockSize)
            {
                IsDamaged = (status & (EntryStatus.Crc16Fails | EntryStatus.Cut | EntryStatus.Unreadable | EntryStatus.ExtentFound | EntryStatus.Crc8Fails)) != 0,
                FollowsGap = followsGap,
                IsWalked = walk.IsStored && isWalked,
                IsUnreadable = (status & EntryStatus.Unreadable) != 0,
            };
            session.Decode(codedFrame);
        }
    }

    public static string Describe(SubframeFault fault, int channel) => $"the subframe of channel {channel + 1} {fault switch
    {
        SubframeFault.Overrun => "runs past the data",
        SubframeFault.ReservedBit => "starts with a bit the specification reserves as 0",
        SubframeFault.ReservedType => "has a type the specification reserves",
        SubframeFault.WastedBits => "states as many wasted bits as the sample has bits",
        SubframeFault.Order => "has a predictor order above the block size",
        SubframeFault.Precision => "states the forbidden coefficient precision",
        SubframeFault.Shift => "states a negative coefficient shift",
        SubframeFault.ResidualMethod => "has a residual coding method the specification reserves",
        SubframeFault.PartitionOrder => "has a partition order that the block size does not divide into",
        SubframeFault.ResidualOverflow => "has a Rice code whose value does not fit 32 bits",
        SubframeFault.TooLong => "is not over after 16 MiB, the most STREAMINFO can state for a frame",
        _ => "is damaged",
    }}";

    private void CheckAgainstStreamInfo(in FrameHeader header, long length, long offset, int index, bool isCut)
    {
        if (Info is not { } info)
        {
            return;
        }

        Records records = Records;
        if (header.BlockSize > info.MaxBlockSize && info.MaxBlockSize >= 16)
        {
            int size = header.BlockSize;
            Findings.Add(Severity.Warning, "flac.block-size-out-of-range", () => Nodes.Frame(records, index), _ =>
                $"The frame at 0x{offset:X} has {Counts.Of(size, "sample", "samples")}, more than the {info.MaxBlockSize} STREAMINFO gives as the largest block size.");
        }

        if (!isCut && ((info.MaxFrameSize != 0 && length > info.MaxFrameSize) || (info.MinFrameSize != 0 && length < info.MinFrameSize)))
        {
            Findings.Add(Severity.Warning, "flac.frame-size-out-of-range", () => Nodes.Frame(records, index), count =>
                $"The frame at 0x{offset:X} is {Counts.Of(length, "byte", "bytes")} long, outside the {info.MinFrameSize} to {info.MaxFrameSize} bytes STREAMINFO states{(count > 1 ? $"; {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, "is", "are")} outside them" : string.Empty)}.");
        }

        void Differs(string problem, string text) =>
            Findings.Add(Severity.Warning, "flac.frame-differs-from-streaminfo", () => Nodes.Frame(records, index), count =>
                $"The frame at 0x{offset:X} {text}, but STREAMINFO says otherwise{(count > 1 ? $"; {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, "differs", "differ")} in the same way" : string.Empty)}.", problem);
        if (header.SampleRateCode != 0 && header.SampleRate != 0 && header.SampleRate != info.SampleRate)
        {
            Differs("sample rate", $"has the sample rate {header.SampleRate} Hz");
        }

        if (header.SampleSizeCode != 0 && header.BitsPerSample != info.BitsPerSample)
        {
            Differs("bits per sample", $"has {Counts.Of(header.BitsPerSample, "bit", "bits")} per sample");
        }

        if (header.Channels != info.Channels)
        {
            Differs("channels", $"has {Counts.Of(header.Channels, "channel", "channels")}");
        }
    }

    private void ReportResidualLimits(ReadOnlySpan<SubframeFacts> facts, long offset, int index)
    {
        foreach (ref readonly SubframeFacts f in facts)
        {
            if (f.IsPartitionOrderTooHigh && f.ResidualMethod >= 0)
            {
                int order = f.PartitionOrder;
                int predictor = f.Order;
                Findings.Add(Severity.Warning, "flac.partition-order-too-high", () => Nodes.Frame(Records, index), count =>
                    $"The residual of a subframe in the frame at 0x{offset:X} has partition order {order}, which leaves as many samples in each partition as the predictor order {predictor}; the specification requires more{(count > 1 ? $" ({Counts.Of(count - 1, "more frame", "more frames")})" : string.Empty)}.");
            }

            if (f.HasMinimumResidual)
            {
                Findings.Add(Severity.Warning, "flac.residual-out-of-range", () => Nodes.Frame(Records, index), count =>
                    $"A subframe in the frame at 0x{offset:X} has the residual sample -2^31, which the specification excludes{(count > 1 ? $" ({Counts.Of(count - 1, "more frame", "more frames")})" : string.Empty)}.");
            }
        }
    }

    private void Count(ReadOnlySpan<SubframeFacts> facts)
    {
        foreach (ref readonly SubframeFacts f in facts)
        {
            Subframes++;
            SubframeTypes[(int)f.Type]++;
            if (f.WastedBits > 0)
            {
                WastedSubframes++;
            }

            if (f.Type == SubframeType.Lpc)
            {
                LpcOrderTotal += f.Order;
                LpcOrderMax = Math.Max(LpcOrderMax, f.Order);
                LpcPrecisionMin = Math.Min(LpcPrecisionMin, f.Precision);
                LpcPrecisionMax = Math.Max(LpcPrecisionMax, f.Precision);
            }
            else if (f.Type == SubframeType.Fixed)
            {
                FixedOrders[f.Order]++;
            }

            if (f.ResidualMethod >= 0)
            {
                ResidualSubframes++;
                PartitionOrders[f.PartitionOrder]++;
                EscapedPartitions += f.EscapedPartitions;
                if (f.ResidualMethod == 1)
                {
                    Rice2Subframes++;
                }
            }
        }
    }

    private FrameDetail DetailOf(in FrameHeader header, ReadOnlySpan<SubframeFacts> facts, PartitionLog[]? logs)
    {
        var records = new SubframeRecord[header.Channels];
        Span<byte> stored = stackalloc byte[(SubframeLayout.MaxOrder * 2) + (PartitionLog.Capacity * 6) + 4];
        for (int c = 0; c < records.Length; c++)
        {
            ref readonly SubframeFacts f = ref facts[c];
            int count = f.Type is SubframeType.Fixed or SubframeType.Lpc && logs is not null ? Math.Min(logs[c].Count, PartitionLog.Capacity) : 0;
            int coefficients = f.Type == SubframeType.Lpc ? f.Order : 0;
            bool hasMore = count > 0 && (1 << f.PartitionOrder) > count;
            int size = (coefficients * 2) + (count * 6) + (hasMore ? 4 : 0);
            for (int i = 0; i < coefficients; i++)
            {
                stored[i * 2] = (byte)(f.Coefficients[i] >> 8);
                stored[(i * 2) + 1] = (byte)f.Coefficients[i];
            }

            for (int i = 0; i < count; i++)
            {
                int at = (coefficients * 2) + (i * 6);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(stored[at..], logs![c].StartBits[i]);
                stored[at + 4] = logs[c].Parameters[i];
                stored[at + 5] = logs[c].IsEscaped[i] ? (byte)1 : (byte)0;
            }

            if (hasMore)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(stored[((coefficients * 2) + (count * 6))..], logs![c].StartBits[count]);
            }

            records[c] = new SubframeRecord
            {
                Type = f.Type,
                StartBit = f.StartBit,
                EndBit = f.EndBit,
                ResidualStartBit = f.ResidualStartBit,
                Order = f.Order,
                WastedBits = f.WastedBits,
                SampleBits = f.SampleBits,
                Precision = f.Precision,
                Shift = f.Shift,
                ResidualMethod = f.ResidualMethod,
                PartitionOrder = f.PartitionOrder,
                EscapedPartitions = f.EscapedPartitions,
                MinParameter = f.MinParameter,
                MaxParameter = f.MaxParameter,
                StoredAt = size == 0 ? 0 : Records.Store.Add(stored[..size]),
                Partitions = count,
                Constant = f.Constant,
            };
        }

        return new FrameDetail(records);
    }
}
