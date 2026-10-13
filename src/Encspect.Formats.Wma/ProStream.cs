using Encspect.Core;

namespace Encspect.Formats.Wma;

// A packet: one media object of the container, the unit its packets carry. 48 bytes.
internal struct ProPacketRecord
{
    public long Offset;
    public int Length;
    public int ObjectLength;
    public int RangeIndex;
    public int FirstFrame;
    public int FrameCount;

    // The bits the packet's header says belong to the frame before.
    public int PreviousBits;

    // Where the bits that begin a frame the packet does not finish start, and how many there are.
    public int TailStart;
    public int TailBits;
    public byte Sequence;
    public byte Flags;

    // The packet is too short to read.
    public const byte Skipped = 1;

    // Its sequence number does not follow the one before.
    public const byte SequenceBreak = 2;

    // A frame in it failed.
    public const byte Failed = 4;

    // The bits at its end begin a frame (the last frame's trailer bit says another follows), not padding.
    public const byte TailBeginsFrame = 8;

    public readonly bool Has(byte flag) => (Flags & flag) != 0;
}

// Figures counted while the packets are read.
internal sealed class ProTotals
{
    public long Packets { get; set; }

    public long Frames { get; set; }

    public long FailedFrames { get; set; }

    public long SkippedPackets { get; set; }

    public long SequenceBreaks { get; set; }

    public long UsedBytes { get; set; }

    public long FrameBits { get; set; }

    public long MinFrameBits { get; set; } = long.MaxValue;

    public long MaxFrameBits { get; set; }

    public long StraddlingFrames { get; set; }

    public long DroppedBits { get; set; }

    public long TailBitsAtEnd { get; set; }

    public long Subframes { get; set; }

    public long[] SubframesBySize { get; } = new long[ProParameters.MaxSubframes + 8];

    public long ChannelSubframes { get; set; }

    public long CodedChannelSubframes { get; set; }

    public long Groups { get; set; }

    public long TransformGroups { get; set; }

    public long BandsInTransformGroups { get; set; }

    public long BandsTransformed { get; set; }

    public long PostprocessFrames { get; set; }

    public long DrcFrames { get; set; }

    public long TrimmedFrames { get; set; }

    public long TrimmedSamples { get; set; }

    public long ExtendedHeaders { get; set; }

    public long VectorCountSubframes { get; set; }

    public long ScalesSent { get; set; }

    public long ScalesDifferential { get; set; }

    public long SecondCodebook { get; set; }

    public long NonZero { get; set; }

    public long Escapes { get; set; }

    public long CoefficientErrors { get; set; }

    public int MinQuantStep { get; set; } = int.MaxValue;

    public int MaxQuantStep { get; set; } = int.MinValue;

    public int MaxFramesInPacket { get; set; }

    public long FramesWithoutPrefixBytes { get; set; }
}

// Walks a stream's packets, joins the frames that run from one into the next and reads each frame's
// syntax, as FFmpeg's decode_packet in wmaprodec.c does for frames with a length prefix: a packet
// starts with its sequence number, two unused bits and the count of bits that finish the frame before,
// then holds whole frames, each prefixed with its length in bits, and the start of one that goes on.
internal sealed class ProStream
{
    // wmaprodec.c: MAX_FRAMESIZE, the most a frame can take.
    private const int MaxFrameBytes = 32768;

    private readonly ProDecoder _decoder;

    // The bits of the frame in progress: the packet's bytes from the one the frame begins in, as
    // wmaprodec.c's frame_data holds them, with the bits before the frame skipped.
    private byte[] _saved = new byte[64];
    private int _savedBits;
    private int _savedOffset;
    private readonly List<FramePart> _savedParts = [];
    private bool _packetLoss = true;
    private int _lastSequence;
    private bool _lastTailBeginsFrame;

    public ProStream(ProParameters parameters, FindingCollector findings, Func<int, Node> packetNode, Func<int, Node> frameNode)
    {
        Parameters = parameters;
        Findings = findings;
        PacketNode = packetNode;
        FrameNode = frameNode;
        _decoder = new ProDecoder(parameters, Subframes, Channels, Groups);
    }

    public ProParameters Parameters { get; }

    public FindingCollector Findings { get; }

    public Func<int, Node> PacketNode { get; }

    public Func<int, Node> FrameNode { get; }

    public ChunkedList<ProPacketRecord> Packets { get; } = new();

    public ChunkedList<ProFrameRecord> Frames { get; } = new();

    public ChunkedList<ProSubframeRecord> Subframes { get; } = new();

    public ChunkedList<ProChannelRecord> Channels { get; } = new();

    public ChunkedList<ProGroupRecord> Groups { get; } = new();

    public ChunkedList<FramePart> Parts { get; } = new();

    public Dictionary<int, ByteRange[]> Ranges { get; } = [];

    public ProTotals Totals { get; } = new();

    // The packet each frame began in, by frame.
    public List<int> StartPackets { get; } = [];

    public void Add(MappedBytes media)
    {
        ProParameters p = Parameters;
        int index = Packets.Count;
        int align = p.BlockAlign;
        var record = new ProPacketRecord
        {
            Offset = media.FileStart,
            ObjectLength = media.Length,
            Length = Math.Min(media.Length, align),
            RangeIndex = -1,
            FirstFrame = Frames.Count,
            TailStart = Math.Min(media.Length, align) * 8,
        };

        IReadOnlyList<ByteRange> ranges = media.Length >= align ? media.FileRanges(0, align) : media.FileRanges();
        if (ranges.Count > 1)
        {
            Ranges[index] = [.. ranges];
            record.RangeIndex = index;
        }

        if (ranges.Count > 0)
        {
            record.Offset = ranges[0].Offset;
        }

        Totals.Packets++;
        if (media.Length < align)
        {
            record.Flags |= ProPacketRecord.Skipped;
            Totals.SkippedPackets++;
            Packets.Add(record);
            Findings.Add(Severity.Warning, "wma.packet-short", () => PacketNode(index), count =>
                $"{(count == 1 ? "A media object holds" : $"{count} media objects hold")} fewer bytes than the block alignment of {align}, which a decoder rejects (wmaprodec.c); the first is at 0x{record.Offset:X}.");
            _packetLoss = true;
            return;
        }

        if (media.Length > align)
        {
            Findings.Add(Severity.Info, "wma.packet-long", () => PacketNode(index), count =>
                $"{(count == 1 ? "A media object holds" : $"{count} media objects hold")} more bytes than the block alignment of {align}; a decoder reads them as further packets of {align} bytes, which this parser does not.");
        }

        Totals.UsedBytes += align;
        byte[] data = media.Span[..align].ToArray();
        var bits = new Bits(data);
        int sequence = (int)bits.Read(4);
        bits.Skip(2);
        int previous = (int)bits.Read(p.Log2FrameSize);
        record.Sequence = (byte)sequence;
        if (!_packetLoss && ((_lastSequence + 1) & 0xF) != sequence)
        {
            _packetLoss = true;
            record.Flags |= ProPacketRecord.SequenceBreak;
            Totals.SequenceBreaks++;
            int expected = (_lastSequence + 1) & 0xF;
            Findings.Add(Severity.Warning, "wma.packet-sequence", () => PacketNode(index), count =>
                $"{(count == 1 ? "A packet's sequence number does not" : $"{count} packets' sequence numbers do not")} follow the packet before ({(count == 1 ? $"{sequence} for {expected}" : "the first is " + sequence + " for " + expected)}); a decoder takes packets to be lost and drops the frame that crosses into it.");
        }

        _lastSequence = sequence;
        if (!p.HasLengthPrefix)
        {
            record.Flags |= ProPacketRecord.Skipped;
            Packets.Add(record);
            return;
        }

        bool done = false;
        bool lastMore = false;
        record.PreviousBits = previous;
        if (previous > 0)
        {
            int remaining = data.Length * 8 - bits.Position;
            if (previous >= remaining)
            {
                previous = remaining;
                done = true;
            }

            record.PreviousBits = previous;
            Save(data, index, bits.Position, previous, append: true);
            bits.Skip(previous);
            if (!_packetLoss)
            {
                DecodeSaved(index, ref record, out lastMore);
            }
        }
        else if (_savedBits - _savedOffset > 0)
        {
            Totals.DroppedBits += _savedBits - _savedOffset;
        }

        if (_packetLoss)
        {
            _savedBits = 0;
            _packetLoss = false;
        }

        while (!done)
        {
            int remaining = data.Length * 8 - bits.Position;
            int frameSize;
            if (remaining > p.Log2FrameSize && (frameSize = (int)bits.Peek(p.Log2FrameSize)) != 0 && frameSize <= remaining)
            {
                Save(data, index, bits.Position, frameSize, append: false);
                bits.Skip(frameSize);
                if (_packetLoss)
                {
                    done = true;
                    break;
                }

                ProStatus status = DecodeSaved(index, ref record, out bool more);
                lastMore = more;
                done = status != ProStatus.Ok || !more;
            }
            else
            {
                done = true;
            }
        }

        int left = data.Length * 8 - bits.Position;
        if (!_packetLoss && left > 0)
        {
            record.TailStart = bits.Position;
            record.TailBits = left;
            if (lastMore)
            {
                record.Flags |= ProPacketRecord.TailBeginsFrame;
            }

            Save(data, index, bits.Position, left, append: false);
            bits.Skip(left);
        }

        _lastTailBeginsFrame = record.Has(ProPacketRecord.TailBeginsFrame);
        record.FrameCount = Frames.Count - record.FirstFrame;
        Totals.MaxFramesInPacket = Math.Max(Totals.MaxFramesInPacket, record.FrameCount);
        Packets.Add(record);
    }

    // Called when the last packet is in: the frame in progress never ends.
    public void Finish()
    {
        Totals.TailBitsAtEnd = _packetLoss || !_lastTailBeginsFrame ? 0 : Math.Max(0, _savedBits - _savedOffset);
    }

    // wmaprodec.c save_bits: the bits go in the buffer of the frame in progress, which starts at the byte
    // that holds the first (append is false), or follow the bits already there.
    private void Save(byte[] data, int packet, int position, int count, bool append)
    {
        int bufferBits;
        if (!append)
        {
            _savedOffset = position & 7;
            bufferBits = _savedOffset + count;
            _savedParts.Clear();
            _savedBits = 0;
            if ((bufferBits + 7) / 8 > MaxFrameBytes || count <= 0)
            {
                _packetLoss = true;
                return;
            }

            EnsureCapacity((bufferBits + 7) / 8);
            Array.Clear(_saved, 0, (bufferBits + 7) / 8);
            CopyBits(data, position & ~7, _saved, 0, bufferBits);
            _savedBits = bufferBits;
            _savedParts.Add(new FramePart(packet, position & ~7, bufferBits));
            return;
        }

        bufferBits = _savedBits + count;
        if (count <= 0 || (bufferBits + 7) / 8 > MaxFrameBytes)
        {
            _packetLoss = true;
            return;
        }

        EnsureCapacity((bufferBits + 7) / 8);
        for (int i = (_savedBits + 7) / 8; i < (bufferBits + 7) / 8; i++)
        {
            _saved[i] = 0;
        }

        CopyBits(data, position, _saved, _savedBits, count);
        _savedBits = bufferBits;
        _savedParts.Add(new FramePart(packet, position, count));
    }

    private void EnsureCapacity(int bytes)
    {
        if (_saved.Length < bytes)
        {
            Array.Resize(ref _saved, Math.Max(bytes, _saved.Length * 2));
        }
    }

    private static void CopyBits(byte[] source, int sourceBit, byte[] target, int targetBit, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int s = sourceBit + i;
            int t = targetBit + i;
            if (((source[s >> 3] >> (7 - (s & 7))) & 1) != 0)
            {
                target[t >> 3] |= (byte)(1 << (7 - (t & 7)));
            }
            else
            {
                target[t >> 3] &= (byte)~(1 << (7 - (t & 7)));
            }
        }
    }

    // Reads the frame in the buffer of the frame in progress and keeps its records.
    private ProStatus DecodeSaved(int packet, ref ProPacketRecord packetRecord, out bool more)
    {
        var bits = new Bits(_saved, _savedBits) { Position = _savedOffset };
        int firstPart = Parts.Count;
        foreach (FramePart part in _savedParts)
        {
            Parts.Add(part);
        }

        var frame = new ProFrameRecord { FirstPart = firstPart, PartCount = (byte)Math.Min(_savedParts.Count, byte.MaxValue) };
        int firstSubframe = Subframes.Count;
        ProStatus status = _decoder.DecodeFrame(bits, ref frame, out more);
        frame.Status = (byte)status;
        for (int i = firstSubframe; i < Subframes.Count; i++)
        {
            Count(Subframes[i]);
        }

        Frames.Add(frame);
        StartPackets.Add(_savedParts[0].Superframe);
        Totals.Frames++;
        if (_savedParts[0].Superframe != packet)
        {
            Totals.StraddlingFrames++;
        }

        long frameBits = frame.EndBit - frame.StartBit;
        if (status == ProStatus.Ok)
        {
            Totals.FrameBits += frameBits;
            Totals.MinFrameBits = Math.Min(Totals.MinFrameBits, frameBits);
            Totals.MaxFrameBits = Math.Max(Totals.MaxFrameBits, frameBits);
            CountFrame(frame);
        }
        else
        {
            Totals.FailedFrames++;
            packetRecord.Flags |= ProPacketRecord.Failed;
            _packetLoss = true;
            int frameIndex = Frames.Count - 1;
            ProStatus reported = status;
            Findings.Add(Severity.Warning, "wma.frame-invalid", () => FrameNode(frameIndex), count =>
                $"{(count == 1 ? "A frame is" : $"{count} frames are")} invalid: {StatusText(reported)}; a decoder drops it and the frame that crosses into the next packet. The first is at 0x{FrameOffset(frameIndex):X}.");
        }

        return status;
    }

    private long FrameOffset(int frame)
    {
        ProFrameRecord record = Frames[frame];
        FramePart part = Parts[record.FirstPart];
        ProPacketRecord packet = Packets.Count > part.Superframe ? Packets[part.Superframe] : default;
        return packet.Offset + ((part.StartBit + record.StartBit) / 8);
    }

    private void CountFrame(in ProFrameRecord frame)
    {
        if (frame.Has(ProFrameRecord.HasPostprocess))
        {
            Totals.PostprocessFrames++;
        }

        if (frame.Has(ProFrameRecord.HasDrc))
        {
            Totals.DrcFrames++;
        }

        if (frame.TrimStart != 0 || frame.TrimEnd != 0)
        {
            Totals.TrimmedFrames++;
            Totals.TrimmedSamples += frame.TrimStart + frame.TrimEnd;
        }
    }

    private void Count(in ProSubframeRecord subframe)
    {
        Totals.Subframes++;
        int size = ProParameters.Log2(Parameters.SamplesPerFrame / Math.Max(1, (int)subframe.Length));
        if (size >= 0 && size < Totals.SubframesBySize.Length)
        {
            Totals.SubframesBySize[size]++;
        }

        Totals.ChannelSubframes += subframe.ChannelCount;
        if (subframe.Has(ProSubframeRecord.HasExtendedHeader))
        {
            Totals.ExtendedHeaders++;
        }

        if (subframe.Has(ProSubframeRecord.SendsVectorCounts))
        {
            Totals.VectorCountSubframes++;
        }

        for (int g = 0; g < subframe.GroupCount; g++)
        {
            ProGroupRecord group = Groups[subframe.FirstGroup + g];
            Totals.Groups++;
            if (group.Transform != 0)
            {
                Totals.TransformGroups++;
                int table = ProParameters.Log2(Parameters.SamplesPerFrame / Math.Max(1, (int)subframe.Length));
                Totals.BandsInTransformGroups += Parameters.BandCounts[Math.Min(table, Parameters.BandCounts.Length - 1)];
                Totals.BandsTransformed += group.BandsOn;
            }
        }

        if (subframe.Has(ProSubframeRecord.HasCoefficients))
        {
            Totals.MinQuantStep = Math.Min(Totals.MinQuantStep, subframe.QuantStep);
            Totals.MaxQuantStep = Math.Max(Totals.MaxQuantStep, subframe.QuantStep);
        }

        for (int i = 0; i < subframe.ChannelCount; i++)
        {
            ProChannelRecord channel = Channels[subframe.FirstChannel + i];
            if (!channel.Has(ProChannelRecord.Transmits))
            {
                continue;
            }

            Totals.CodedChannelSubframes++;
            Totals.NonZero += channel.NonZero;
            Totals.Escapes += channel.Escapes;
            if (channel.Has(ProChannelRecord.ScalesSent))
            {
                Totals.ScalesSent++;
            }

            if (channel.Has(ProChannelRecord.ScalesDifferential))
            {
                Totals.ScalesDifferential++;
            }

            if (channel.Has(ProChannelRecord.SecondCodebook))
            {
                Totals.SecondCodebook++;
            }

            if (channel.Has(ProChannelRecord.CoefficientError))
            {
                Totals.CoefficientErrors++;
            }
        }
    }

    internal static string StatusText(ProStatus status) => status switch
    {
        ProStatus.TileHeader => "its tile header lays out subframes the stream cannot have",
        ProStatus.ChannelTransform => "a channel transform that FFmpeg's decoder does not support",
        ProStatus.ReservedBit => "a subframe sets a reserved bit",
        ProStatus.FillBits => "an extended header skips more bits than the frame has",
        ProStatus.VectorCount => "a subframe states more vector-coded coefficients than it has coefficients",
        ProStatus.ScaleFactors => "a scale factor run goes past the last band",
        ProStatus.BrokenSubframe => "a channel has more subframes than its tile header lays out",
        ProStatus.LengthPrefix => "its length prefix is not where its data ends",
        ProStatus.Overrun => "it needs more bits than it has",
        _ => "an error",
    };
}
