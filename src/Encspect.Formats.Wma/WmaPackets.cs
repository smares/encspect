using Encspect.Core;

namespace Encspect.Formats.Wma;

// Reads the packets of a Professional stream and the frames in them (docs/design/wma-pro-format.md, "Packets" and "Sequence numbers, errors
// and loss"), and keeps what the statistics, the series and the nodes need.
internal sealed class WmaPacketReader
{
    public const int MaxPackets = 262_144;

    // A frame's buffer holds this many bytes, counted from the byte that holds its first bit.
    private const int BufferBytes = 32_768;

    private readonly IStreamParameters _p;
    private readonly CodecContext _context;
    private readonly FindingCollector _findings;
    private readonly IFrameSyntax _reader;
    private readonly bool _isLossless;
    private readonly Func<int, string> _reason;
    private readonly int _bufferBytes;
    private bool _isFirstOfRun;
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
    private bool _isReturnedInCall;
    private readonly List<long> _secondBits = [];
    private readonly List<int> _secondFrames = [];
    private int _lastValidSecond = -1;

    public WmaPacketReader(IStreamParameters parameters, IFrameSyntax syntax, bool isLossless, int channels, Func<int, string> reason, CodecContext context, FindingCollector findings)
    {
        _p = parameters;
        _context = context;
        _findings = findings;
        _reader = syntax;
        _isLossless = isLossless;
        _reason = reason;
        _bufferBytes = isLossless ? BufferBytes * channels : BufferBytes;
        Data = new StreamData();
        _packetBuffer = new byte[parameters.BlockAlignment + 16];
    }

    public StreamData Data { get; }

    private string Dropped => _isLossless && !_p.HasLengthPrefix
        ? "Encspect reads no more frames from the bits kept with it"
        : "a decoder drops it and the frame that crosses into the next packet";

    private string DecoderFile => _isLossless ? "wmalosslessdec.c" : "wmaprodec.c";

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

        if (_isLossless && !_p.HasLengthPrefix)
        {
            DecodeRun(true, false);
        }

        Packets = Data.Packets.Count;
    }

    private void ReadPacket(MappedBytes packet)
    {
        IStreamParameters p = _p;
        int index = Data.Packets.Count;
        _currentPacket = index;
        int length = Math.Min(packet.Length, p.BlockAlignment);
        Data.Table.Add(packet.FileRanges(0, length));
        var record = new PacketRecord();
        _framesInCall = 0;
        _isReturnedInCall = false;
        record.FirstFrame = (int)Data.Frames.Count;

        if (packet.Length < p.BlockAlignment)
        {
            record.Kind = PacketKind.Short;
            record.HasHeader = packet.Length * 8 >= p.HeaderBits;
            Data.Packets.Add(record);
            long firstOffset = packet.FileStart;
            _findings.Add(Severity.Warning, "wma.packet-short", () => PacketNode(index), count =>
                count == 1
                    ? $"A media object holds fewer bytes than the block alignment of {p.BlockAlignment}, which a decoder rejects ({DecoderFile}); the first is at 0x{WmaText.Hex(firstOffset)}."
                    : $"{count} media objects hold fewer bytes than the block alignment of {p.BlockAlignment}, which a decoder rejects ({DecoderFile}); the first is at 0x{WmaText.Hex(firstOffset)}.");
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
        bool isSeekable = false;
        bool isSpliced = false;
        if (_isLossless)
        {
            isSeekable = bits.ReadBit() != 0;
            isSpliced = bits.ReadBit() != 0;
        }
        else
        {
            bits.Skip(2);
        }

        int stated = (int)bits.Read(p.CountWidth);
        int payloadStart = p.HeaderBits;
        int payloadBits = Math.Max(0, p.PacketBits - payloadStart);
        int count = Math.Min(stated, payloadBits);
        bool isListed = p.HasLengthPrefix || _isLossless;
        record.Kind = isListed ? PacketKind.Read : PacketKind.HeaderOnly;
        record.HasHeader = true;
        record.Sequence = (byte)sequence;
        record.IsSeekable = isSeekable;
        record.IsSpliced = isSpliced;
        record.Count = isListed ? count : 0;

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

        if (isSpliced)
        {
            _findings.Add(Severity.Info, "wma.lossless-spliced", () => PacketNode(index), n =>
                n == 1
                    ? "A packet sets the spliced bit of its header, which a decoder does not support (it asks for a sample file); Encspect reads the packet as it does the others."
                    : $"{n} packets set the spliced bit of their headers, which a decoder does not support (it asks for a sample file); Encspect reads them as it does the others.");
        }

        Data.Packets.Add(record);
        if (p.HasLengthPrefix)
        {
            ReadWithPrefix(bits, index, payloadStart, count, isDropped);
        }
        else if (_isLossless)
        {
            ReadRun(index, payloadStart, count, stated >= payloadBits, isDropped);
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
        IStreamParameters p = _p;
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
            if (((total + 7) / 8) > _bufferBytes)
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
        int read = 0;
        while (true)
        {
            if (++read % 1024 == 0)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
            }

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

            if ((((position & 7) + declared + 7) / 8) > _bufferBytes)
            {
                _isLoss = true;
                break;
            }

            FrameOutcome result = ReadOne(bits, position, position + declared, declared, [new FramePart(packet, position, declared)]);
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

        int tail = Math.Max(0, end - position);
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
        IStreamParameters p = _p;
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
                FrameOutcome result = ReadOne(joined, position, total, 0, [], parts, position);
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
        int tail = Math.Max(0, end - position2);
        if (!isError && tail > 0)
        {
            Save(packet, position2, tail);
        }
    }

    // Lossless without the length prefix: the bits from the first frame that starts in a packet to the end of its payload are kept as a run,
    // joined by the first bits of the next packets, and the frames in the run are read when a packet arrives that does not only continue it.
    private void ReadRun(int packet, int payloadStart, int count, bool isCovering, bool isDropped)
    {
        int end = _p.PacketBits;
        if (isDropped)
        {
            ClearSaved();
        }
        else if (count > 0)
        {
            Append(packet, payloadStart, count);
        }

        if (isCovering)
        {
            ref PacketRecord own = ref Record(packet);
            own.TailStart = 0;
            own.TailBits = 0;
            return;
        }

        bool isRestDropped = !DecodeRun(false, count > 0 && !isDropped);
        ClearSaved();
        int start = payloadStart + count;
        int tail = Math.Max(0, end - start);
        ref PacketRecord record = ref Record(packet);
        record.TailStart = start;
        record.TailBits = tail;
        record.IsTailUnfinished = true;
        if (isRestDropped)
        {
            _isLoss = true;
        }
        else if (tail > 0)
        {
            Save(packet, start, tail);
        }
    }

    // Reads the frames of the run (docs/design/wma-lossless-format.md, "Which frames of a run are decoded"). The first frame is the one a decoder
    // reads in the call that reads the packet's header (isHeaderCall), where the packet states a count of bits that finish the frame before: its
    // trailer bit is ignored, bits left after it are read as further frames, and an error in it is cleared in the same call. Each further frame is
    // read while bits of the run are left, until a trailer bit of 0; an error in one drops the rest of the packet that arrived, which is the
    // false this returns. A frame that runs past the kept bits, and one whose end trim is F or more, are no errors.
    private bool DecodeRun(bool isFlush, bool isHeaderCall)
    {
        int total = _savedBits;
        if (total <= 0)
        {
            return true;
        }

        var parts = new List<FramePart>(_saved);
        var joined = new Bits(_savedBuffer, total);
        int position = 0;
        bool isFirst = true;
        bool isOk = true;
        bool isTrailerOne = false;
        int gap = 0;
        while (position < total)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            bool isInHeaderCall = isFirst && isHeaderCall;
            _isFirstOfRun = isInHeaderCall;
            FrameOutcome result = ReadOne(joined, position, total, 0, [], parts, position);
            isFirst = false;
            if (!result.IsValid && result.Fault is not ((int)LosslessFault.NeedsMoreBits or (int)LosslessFault.TrimTooLarge))
            {
                isOk = isInHeaderCall;
                position = total;
                isTrailerOne = false;
                break;
            }

            position += Math.Max(result.Bits, 1);
            // A frame rejected for its end trim stops before its trailer bit, and the next frame is read from there.
            isTrailerOne = result.Trailer || result.Fault == (int)LosslessFault.TrimTooLarge;
            if (!isTrailerOne && position < total && result.IsValid && gap == 0)
            {
                gap = total - position;
            }

            if (!isTrailerOne && !isInHeaderCall)
            {
                break;
            }
        }

        if (!isFlush && isOk && gap > 0)
        {
            int frame = (int)Data.Frames.Count - 1;
            int before = gap;
            _findings.Add(Severity.Warning, "wma.frame-boundary", () => FrameNode(frame), n =>
                n == 1
                    ? $"A frame that starts in a packet says that no frame follows it, but {Counts.Bits(before)} lie between its end and the place the next packet's count of bits that finish the frame before gives. A decoder reads the bits left after the first frame of a packet's frames as further frames, whatever its trailer bit says, and leaves those after a later frame unread."
                    : $"{n} packets have frames that say that no frame follows them, but bits lie between their ends and the place the next packet's count of bits that finish the frame before gives (the first {Counts.Bits(before)}). A decoder reads the bits left after the first frame of a packet's frames as further frames, whatever its trailer bit says, and leaves those after a later frame unread.");
        }

        FramePart owner = parts[0];
        ref PacketRecord record = ref Record(owner.Packet);
        if (isOk && position < owner.Bits)
        {
            record.TailStart = owner.StartBit + position;
            record.TailBits = owner.Bits - position;
            record.IsTailUnfinished = isTrailerOne;
        }
        else if (isOk)
        {
            record.TailBits = 0;
        }

        return isOk;
    }

    private void Append(int packet, int startBit, int bits)
    {
        int total = _savedBits + bits;
        if (((total + 8) / 8) > _bufferBytes)
        {
            ClearSaved();
            _isLoss = true;
            return;
        }

        int bytes = ((total + 7) / 8) + 16;
        if (_savedBuffer.Length < bytes)
        {
            Array.Resize(ref _savedBuffer, bytes);
        }

        BitCopy.Copy(_packetBuffer, startBit, _savedBuffer, _savedBits, bits);
        _saved.Add(new FramePart(packet, startBit, bits));
        _savedBits = total;
    }

    private void ClearSaved()
    {
        if (_savedBits > 0)
        {
            Array.Clear(_savedBuffer, 0, Math.Min(_savedBuffer.Length, ((_savedBits + 7) / 8) + 16));
        }

        _saved.Clear();
        _savedBits = 0;
    }

    private void Save(int packet, int startBit, int bits)
    {
        ClearSaved();
        if ((((startBit & 7) + bits + 7) / 8) > _bufferBytes)
        {
            _isLoss = true;
            return;
        }

        int bytes = ((bits + 7) / 8) + 16;
        if (_savedBuffer.Length < bytes)
        {
            _savedBuffer = new byte[bytes];
        }

        BitCopy.Copy(_packetBuffer, startBit, _savedBuffer, 0, bits);
        _saved.Add(new FramePart(packet, startBit, bits));
        _savedBits = bits;
    }

    // Reads one frame whose bits are parts, one after another, and records it. startPacket is the packet the frame is listed under. For
    // frames read from a joined buffer of several frames, source holds the parts of the whole buffer and the frame starts at position.
    private FrameOutcome ReadOne(Bits bits, int start, int limit, int declared, IReadOnlyList<FramePart> parts, List<FramePart>? source = null, int position = 0)
    {
        FrameOutcome result = _reader.Read(bits, start, limit, declared, _trace);
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
        if ((_p.HasLengthPrefix || _isLossless) && parts.Count > 0)
        {
            ref PacketRecord owner = ref Record(parts[0].Packet);
            if (owner.FrameCount == 0)
            {
                owner.FirstFrame = index;
            }

            owner.FrameCount++;
            if (_p.HasLengthPrefix && parts[0].Packet != _currentPacket)
            {
                // The bits left at the end of that packet were the start of this frame.
                owner.TailBits = 0;
            }
        }

        Account(result, index, parts);
        return result;
    }

    public static double RateOf(long bits, IStreamParameters p) => bits * (p.SampleRate / 1000.0) / p.FrameLength;

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

    private void Account(FrameOutcome result, int index, IReadOnlyList<FramePart> parts)
    {
        IStreamParameters p = _p;
        double rate = 0;
        if (parts.Count > 1 && parts[0].Packet != parts[^1].Packet)
        {
            SpanningFrames++;
        }

        long offset = BitNodes.OffsetOf(Data.Table, parts.ToArray(), 0);
        if (!result.IsValid)
        {
            InvalidFrames++;
            int fault = result.Fault;
            _findings.Add(Severity.Warning, "wma.frame-invalid", () => FrameNode(index), count =>
                count == 1
                    ? $"A frame is invalid: {_reason(fault)}; {Dropped}. The first is at 0x{WmaText.Hex(offset)}."
                    : $"{count} frames are invalid: {_reason(fault)}; {Dropped}. The first is at 0x{WmaText.Hex(offset)}.");
        }
        else
        {
            rate = RateOf(result.Bits, p);
        }

        BitrateX.Add(offset);
        BitrateY.Add(rate);

        if (_isLossless)
        {
            long output = 0;
            if (result.IsValid || result.Fault == (int)LosslessFault.NeedsMoreBits)
            {
                output = result.TrimEnd >= p.FrameLength ? 0 : p.FrameLength - result.TrimEnd;
            }
            else if (_isFirstOfRun || p.HasLengthPrefix)
            {
                output = result.CompletedSamples;
            }

            Samples += output;
        }
        else if (result.AreSubframesRead || result.Fault == (int)ProFault.NeedsMoreBits)
        {
            // A decoder reads on to the end of the subframes past the bits a frame has, so a frame that reads past its end got there too. A frame
            // that fails only the check of its length, or reads past its end, is output where a decoder returns it from the call that
            // reads the packet's header: with its trims where it is the frame that crosses into the packet, without them where it is the
            // first whole frame (docs/design/wma-pro-format.md, "What FFmpeg outputs").
            bool isCrossing = p.HasLengthPrefix && parts.Count > 0 && parts[0].Packet != _currentPacket;
            bool isLengthOnly = result.Fault is (int)ProFault.LengthPrefix or (int)ProFault.NeedsMoreBits;
            if (!_isFirstSeen)
            {
                _isFirstSeen = true;
            }
            else if (result.IsValid || (isLengthOnly && p.HasLengthPrefix && !_isReturnedInCall))
            {
                long output = p.FrameLength;
                if (result.IsValid || isCrossing)
                {
                    output = result.TrimStart >= output ? 0 : output - result.TrimStart;
                    output = result.TrimEnd >= output ? 0 : output - result.TrimEnd;
                }

                Samples += output;
                _isReturnedInCall = true;
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
            TrimmedSamples += _isLossless ? result.TrimEnd : result.TrimStart + (long)result.TrimEnd;
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
