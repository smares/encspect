using Encspect.Core;

namespace Encspect.Formats.Wma;

// Reads the packets of a Professional stream and the frames in them (docs/design/wma-pro-format.md, "Packets" and "Sequence numbers, errors
// and loss"), and keeps what the statistics, the series and the nodes need.
internal sealed class ProStreamReader
{
    public const int MaxPackets = 262_144;

    // A frame's buffer holds this many bytes, counted from the byte that holds its first bit.
    private const int BufferBytes = 32_768;

    private readonly ProParameters _p;
    private readonly CodecContext _context;
    private readonly FindingCollector _findings;
    private readonly ProFrameReader _reader;
    private readonly TraceWriter _trace = new();
    private readonly byte[] _packetBuffer;
    private byte[] _savedBuffer = new byte[64];
    private readonly List<FramePart> _saved = [];
    private int _savedBits;
    private int _previousSequence;
    private bool _hasSequence;
    private bool _isLoss = true;
    private bool _isFirstSeen;
    private int _currentPacket;
    private long _framesInCall;
    private readonly List<long> _secondBits = [];
    private readonly List<int> _secondFrames = [];
    private int _lastValidSecond = -1;

    public ProStreamReader(ProParameters parameters, CodecContext context, FindingCollector findings, ProTally tally)
    {
        _p = parameters;
        _context = context;
        _findings = findings;
        _reader = new ProFrameReader(parameters, tally);
        Data = new ProData(parameters);
        _packetBuffer = new byte[parameters.BlockAlignment + 16];
    }

    public ProData Data { get; }

    // Set by the node builders, so a finding can point at the node of its first case once the nodes exist.
    public Func<int, Node> PacketNode { get; set; } = _ => throw new InvalidOperationException();

    public Func<int, Node> FrameNode { get; set; } = _ => throw new InvalidOperationException();

    public Func<Node> FramesListNode { get; set; } = () => throw new InvalidOperationException();

    public long Packets { get; private set; }

    public long FullPackets { get; private set; }

    public long Skipped { get; private set; }

    public long InvalidFrames { get; private set; }

    public long ValidFrames { get; private set; }

    public long SpanningFrames { get; private set; }

    public long MostFramesInPacket { get; private set; }

    public long Samples { get; private set; }

    public long TrimmedSamples { get; private set; }

    public long TrimmedFrames { get; private set; }

    public long PostprocessingFrames { get; private set; }

    public long DynamicRangeFrames { get; private set; }

    public long SmallestBits { get; private set; } = long.MaxValue;

    public long LargestBits { get; private set; }

    public List<double> BitrateX { get; } = [];

    public List<double> BitrateY { get; } = [];

    // The tail of the last packet: the bits that start a frame no packet finishes.
    public int UnfinishedBits { get; private set; }

    public IReadOnlyList<long> SecondBits => _secondBits;

    public IReadOnlyList<int> SecondFrames => _secondFrames;

    public int LastValidSecond => _lastValidSecond;

    public void Run(IEnumerable<MappedBytes> packets)
    {
        long index = 0;
        foreach (MappedBytes packet in packets)
        {
            if (index++ % 1024 == 0)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (Data.Packets.Count >= MaxPackets)
            {
                Skipped++;
                continue;
            }

            ReadPacket(packet);
        }

        Packets = Data.Packets.Count;
    }

    private void ReadPacket(MappedBytes packet)
    {
        ProParameters p = _p;
        int index = Data.Packets.Count;
        _currentPacket = index;
        int length = Math.Min(packet.Length, p.BlockAlignment);
        Data.Table.Add(packet.FileRanges(0, length));
        var record = new PacketRecord();
        _framesInCall = 0;
        record.FirstFrame = (int)Data.Frames.Count;

        if (packet.Length < p.BlockAlignment)
        {
            record.Kind = PacketKind.Short;
            record.HasHeader = packet.Length * 8 >= p.HeaderBits;
            Data.Packets.Add(record);
            long firstOffset = packet.FileStart;
            _findings.Add(Severity.Warning, "wma.packet-short", () => PacketNode(index), count =>
                count == 1
                    ? $"A media object holds fewer bytes than the block alignment of {p.BlockAlignment}, which a decoder rejects (wmaprodec.c); the first is at 0x{WmaText.Hex(firstOffset)}."
                    : $"{count} media objects hold fewer bytes than the block alignment of {p.BlockAlignment}, which a decoder rejects (wmaprodec.c); the first is at 0x{WmaText.Hex(firstOffset)}.");
            _isLoss = true;
            return;
        }

        if (packet.Length > p.BlockAlignment)
        {
            _findings.Add(Severity.Info, "wma.packet-long", () => PacketNode(index), count =>
                count == 1
                    ? $"A media object holds more bytes than the block alignment of {p.BlockAlignment}; a decoder reads them as further packets of {p.BlockAlignment} bytes, which this parser does not."
                    : $"{count} media objects hold more bytes than the block alignment of {p.BlockAlignment}; a decoder reads them as further packets of {p.BlockAlignment} bytes, which this parser does not.");
        }

        FullPackets++;
        packet.Span[..p.BlockAlignment].CopyTo(_packetBuffer);
        Array.Clear(_packetBuffer, p.BlockAlignment, _packetBuffer.Length - p.BlockAlignment);
        var bits = new Bits(_packetBuffer, p.PacketBits);

        int sequence = (int)bits.Read(4);
        bits.Skip(2);
        int stated = (int)bits.Read(p.CountWidth);
        int payloadStart = p.HeaderBits;
        int payloadBits = p.PacketBits - payloadStart;
        int count = Math.Min(stated, payloadBits);
        record.Kind = p.HasLengthPrefix ? PacketKind.Read : PacketKind.HeaderOnly;
        record.HasHeader = true;
        record.Sequence = (byte)sequence;
        record.Count = p.HasLengthPrefix ? count : 0;

        bool isGap = !_isLoss && _hasSequence && sequence != ((_previousSequence + 1) & 15);
        if (isGap)
        {
            int found = sequence;
            int expected = (_previousSequence + 1) & 15;
            _findings.Add(Severity.Warning, "wma.packet-sequence", () => PacketNode(index), n =>
                n == 1
                    ? $"A packet's sequence number does not follow the packet before ({found} for {expected}); a decoder takes packets to be lost and drops the frame that crosses into it."
                    : $"{n} packets' sequence numbers do not follow the packet before (the first is {found} for {expected}); a decoder takes packets to be lost and drops the frame that crosses into it.");
        }

        _previousSequence = sequence;
        _hasSequence = true;
        bool isDropped = _isLoss || isGap;
        _isLoss = false;

        Data.Packets.Add(record);
        if (p.HasLengthPrefix)
        {
            ReadWithPrefix(bits, index, payloadStart, count, isDropped);
        }
        else
        {
            ReadWithoutPrefix(index, payloadStart, count, isDropped);
        }

        MostFramesInPacket = Math.Max(MostFramesInPacket, _framesInCall);
    }

    private ref PacketRecord Record(int packet) => ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Data.Packets)[packet];

    private void ReadWithPrefix(Bits bits, int packet, int payloadStart, int count, bool isDropped)
    {
        ProParameters p = _p;
        int end = p.PacketBits;
        int position = payloadStart;
        if (isDropped)
        {
            ClearSaved();
            position += count;
        }
        else if (count == 0)
        {
            ClearSaved();
        }
        else
        {
            int total = _savedBits + count;
            var parts = new List<FramePart>(_saved) { new(packet, payloadStart, count) };
            if (((total + 7) / 8) > BufferBytes)
            {
                ClearSaved();
                _isLoss = true;
            }
            else
            {
                byte[] buffer = new byte[((total + 7) / 8) + 8];
                BitCopy.Copy(_savedBuffer, 0, buffer, 0, _savedBits);
                BitCopy.Copy(_packetBuffer, payloadStart, buffer, _savedBits, count);
                var joined = new Bits(buffer, total);
                int declared = total > p.CountWidth ? (int)joined.Peek(p.CountWidth) : 0;
                int extent = declared > 0 && declared < total ? declared : total;
                ReadOne(joined, 0, extent, declared, extent < total ? Slice(parts, 0, extent) : parts);
            }

            ClearSaved();
            position += count;
        }

        bool isTrailerOne = false;
        bool isError = false;
        while (true)
        {
            int left = end - position;
            if (left <= p.CountWidth)
            {
                break;
            }

            bits.Position = position;
            int declared = (int)bits.Peek(p.CountWidth);
            if (declared == 0 || declared > left)
            {
                break;
            }

            if ((((position & 7) + declared + 7) / 8) > BufferBytes)
            {
                _isLoss = true;
                break;
            }

            ProFrameResult result = ReadOne(bits, position, position + declared, declared, [new FramePart(packet, position, declared)]);
            position += declared;
            if (!result.IsValid)
            {
                isError = true;
                _isLoss = true;
                isTrailerOne = false;
                break;
            }

            isTrailerOne = result.Trailer;
            if (!result.Trailer)
            {
                break;
            }
        }

        int tail = end - position;
        ref PacketRecord own = ref Record(packet);
        own.TailStart = position;
        own.TailBits = tail;
        own.IsTailUnfinished = isTrailerOne;
        if (!isError && tail > 0)
        {
            Save(packet, position, tail);
        }
        else
        {
            ClearSaved();
        }

        UnfinishedBits = isTrailerOne && !_isLoss ? tail : 0;
    }

    private void ReadWithoutPrefix(int packet, int payloadStart, int count, bool isDropped)
    {
        ProParameters p = _p;
        int end = p.PacketBits;
        if (isDropped)
        {
            ClearSaved();
        }

        int total = _savedBits + (isDropped ? 0 : count);
        bool isError = false;
        if (total > 0)
        {
            var parts = new List<FramePart>(_saved);
            if (!isDropped && count > 0)
            {
                parts.Add(new FramePart(packet, payloadStart, count));
            }

            byte[] buffer = new byte[((total + 7) / 8) + 8];
            BitCopy.Copy(_savedBuffer, 0, buffer, 0, _savedBits);
            if (!isDropped && count > 0)
            {
                BitCopy.Copy(_packetBuffer, payloadStart, buffer, _savedBits, count);
            }

            var joined = new Bits(buffer, total);
            int position = 0;
            while (position < total)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                ProFrameResult result = ReadOne(joined, position, total, 0, [], parts, position);
                position += Math.Max(result.Bits, 1);
                if (!result.IsValid)
                {
                    isError = true;
                    _isLoss = true;
                    break;
                }

                if (!result.Trailer)
                {
                    break;
                }
            }
        }

        ClearSaved();
        int position2 = payloadStart + count;
        int tail = end - position2;
        if (!isError && tail > 0)
        {
            Save(packet, position2, tail);
        }
    }

    private void ClearSaved()
    {
        _saved.Clear();
        _savedBits = 0;
    }

    private void Save(int packet, int startBit, int bits)
    {
        ClearSaved();
        if ((((startBit & 7) + bits + 7) / 8) > BufferBytes)
        {
            _isLoss = true;
            return;
        }

        int bytes = ((bits + 7) / 8) + 16;
        if (_savedBuffer.Length < bytes)
        {
            _savedBuffer = new byte[bytes];
        }
        else
        {
            Array.Clear(_savedBuffer, 0, bytes);
        }

        BitCopy.Copy(_packetBuffer, startBit, _savedBuffer, 0, bits);
        _saved.Add(new FramePart(packet, startBit, bits));
        _savedBits = bits;
    }

    // Reads one frame whose bits are parts, one after another, and records it. startPacket is the packet the frame is listed under. For
    // frames read from a joined buffer of several frames, source holds the parts of the whole buffer and the frame starts at position.
    private ProFrameResult ReadOne(Bits bits, int start, int limit, int declared, IReadOnlyList<FramePart> parts, List<FramePart>? source = null, int position = 0)
    {
        ProFrameResult result = _reader.Read(bits, start, limit, declared, _trace);
        if (source is not null)
        {
            parts = Slice(source, position, Math.Max(result.Bits, 1));
        }


        int index = (int)Data.Frames.Count;
        uint trace = Data.Traces.Add(_trace.Span);
        int firstPart = (int)Data.Parts.Count;
        foreach (FramePart part in parts)
        {
            Data.Parts.Add(part);
        }

        Data.Frames.Add(new FrameRecord
        {
            FirstPart = firstPart,
            PartCount = parts.Count,
            Trace = trace,
            TraceLength = _trace.Length,
            Bits = result.Bits,
            Fault = result.Fault,
            Subframes = _reader.SubframesRead,
        });

        _framesInCall++;
        if (_p.HasLengthPrefix && parts.Count > 0)
        {
            ref PacketRecord owner = ref Record(parts[0].Packet);
            if (owner.FrameCount == 0)
            {
                owner.FirstFrame = index;
            }

            owner.FrameCount++;
            if (parts[0].Packet != _currentPacket)
            {
                // The bits left at the end of that packet were the start of this frame.
                owner.TailBits = 0;
            }
        }

        Account(result, index, parts);
        return result;
    }

    public static double RateOf(long bits, ProParameters p) => bits * (p.SampleRate / 1000.0) / p.FrameLength;

    private static List<FramePart> Slice(List<FramePart> source, int from, int bits)
    {
        var result = new List<FramePart>(2);
        int position = 0;
        int to = from + bits;
        foreach (FramePart part in source)
        {
            int partEnd = position + part.Bits;
            int a = Math.Max(from, position);
            int b = Math.Min(to, partEnd);
            if (b > a)
            {
                result.Add(new FramePart(part.Packet, part.StartBit + (a - position), b - a));
            }

            position = partEnd;
        }

        if (result.Count == 0 && source.Count > 0)
        {
            result.Add(new FramePart(source[^1].Packet, source[^1].StartBit, 0));
        }

        return result;
    }

    private void Account(ProFrameResult result, int index, IReadOnlyList<FramePart> parts)
    {
        ProParameters p = _p;
        double rate = 0;
        if (parts.Count > 1 && parts[0].Packet != parts[^1].Packet)
        {
            SpanningFrames++;
        }

        long offset = BitNodes.OffsetOf(Data.Table, parts.ToArray(), 0);
        if (!result.IsValid)
        {
            InvalidFrames++;
            ProFault fault = result.Fault;
            _findings.Add(Severity.Warning, "wma.frame-invalid", () => FrameNode(index), count =>
                count == 1
                    ? $"A frame is invalid: {ProExplain.Reason(fault)}; a decoder drops it and the frame that crosses into the next packet. The first is at 0x{WmaText.Hex(offset)}."
                    : $"{count} frames are invalid: {ProExplain.Reason(fault)}; a decoder drops it and the frame that crosses into the next packet. The first is at 0x{WmaText.Hex(offset)}.");
        }
        else
        {
            rate = RateOf(result.Bits, p);
        }

        BitrateX.Add(offset);
        BitrateY.Add(rate);

        if (result.AreSubframesRead)
        {
            if (!_isFirstSeen)
            {
                _isFirstSeen = true;
            }
            else if (result.IsValid)
            {
                long output = p.FrameLength;
                output = result.TrimStart >= output ? 0 : output - result.TrimStart;
                output = result.TrimEnd >= output ? 0 : output - result.TrimEnd;
                Samples += output;
            }
        }

        if (!result.IsValid)
        {
            return;
        }

        ValidFrames++;
        SmallestBits = Math.Min(SmallestBits, result.Bits);
        LargestBits = Math.Max(LargestBits, result.Bits);
        if (result.TrimStart != 0 || result.TrimEnd != 0)
        {
            TrimmedFrames++;
            TrimmedSamples += result.TrimStart + (long)result.TrimEnd;
        }

        if (result.HasPostprocessing)
        {
            PostprocessingFrames++;
        }

        if (p.HasDynamicRange)
        {
            DynamicRangeFrames++;
        }

        int second = (int)(index * (long)p.FrameLength / p.SampleRate);
        while (_secondBits.Count <= second)
        {
            _secondBits.Add(0);
            _secondFrames.Add(0);
        }

        _secondBits[second] += result.Bits;
        _secondFrames[second]++;
        _lastValidSecond = Math.Max(_lastValidSecond, second);
    }
}
