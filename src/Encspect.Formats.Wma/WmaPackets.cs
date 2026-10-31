using Encspect.Core;

namespace Encspect.Formats.Wma;

// Reads the packets of a Professional stream and the frames in them (docs/design/wma-pro-format.md, "Packets" and "Sequence numbers, errors
// and loss"), and keeps what the statistics, the series and the nodes need.
internal sealed class WmaPacketReader
{
    public const int MaxPackets = 262_144;

    // The frames of a Lossless stream that are kept as records, past which they are only counted: a frame can take 7 bits, so a stream's frames
    // are not bounded by its media objects.
    public const int MaxFrames = 524_288;

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

    // The bit offset (0 to 7) of the last saved tail in its first byte: a decoder keeps it, also after the run is dropped, and starts reading
    // the next bits it appends to an empty buffer there. _savedOff is the part of it the current run counts against the buffer.
    private int _savedOff;
    private int _lastOff;
    private int _previousSequence;
    private bool _hasSequence;
    private bool _isLoss = true;
    private bool _isFirstSeen;
    private int _currentPacket;
    private long _framesInCall;
    private long _frameNumber;
    private long _lastFrameNumber = -1;
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

    // How many frames are kept as records (Data.Frames and what hangs on them); the figures count the others too.
    public long FrameLimit { get; init; } = long.MaxValue;

    // The frames read, those past FrameLimit included; a lost frame's end (see ReadRun) is no frame.
    public long FramesRead => _frameNumber;

    public long SkippedFrames => Math.Max(0, _frameNumber - Data.Frames.Count);

    private string DroppedFor(int fault)
    {
        if (!_isLossless)
        {
            return "a decoder drops it and the frame that crosses into the next packet";
        }

        if (_p.HasLengthPrefix)
        {
            return "a decoder drops it and the rest of its media object";
        }

        return (LosslessFault)fault switch
        {
            LosslessFault.TrimTooLarge => "a decoder rejects it without a loss and reads on from the bit where its trim ended, as if a frame started there, except in the flush after the last packet, where the reading ends",
            LosslessFault.NeedsMoreBits => "a decoder does not detect it: it reads what lies past the bits it kept, outputs the frame whole and reads no more frames from them",
            _ => "a decoder drops the rest of its run and, where it is not the first frame of the run, the start of the packet that arrived with it",
        };
    }

    private string Crossing => _isLossless && !_p.HasLengthPrefix
        ? "the frames that start in the packet before it"
        : "the frame that crosses into it";

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
            DecodeRun(true, false, false);
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
        int packetBits = length * 8;

        if (packet.Length < p.BlockAlignment)
        {
            long firstOffset = packet.FileStart;
            if (_isLossless)
            {
                // A Lossless decoder has no minimum packet size: it reads the bytes it has, up to the block alignment.
                _findings.Add(Severity.Info, "wma.packet-short", () => PacketNode(index), count =>
                    count == 1
                        ? $"A media object holds fewer bytes than the block alignment of {p.BlockAlignment}; a decoder reads the bytes it has as a packet ({DecoderFile}). The first is at 0x{WmaText.Hex(firstOffset)}."
                        : $"{count} media objects hold fewer bytes than the block alignment of {p.BlockAlignment}; a decoder reads the bytes they have as packets ({DecoderFile}). The first is at 0x{WmaText.Hex(firstOffset)}.");
            }
            else
            {
                record.Kind = PacketKind.Short;
                record.HasHeader = packet.Length * 8 >= p.HeaderBits;
                Data.Packets.Add(record);
                _findings.Add(Severity.Warning, "wma.packet-short", () => PacketNode(index), count =>
                    count == 1
                        ? $"A media object holds fewer bytes than the block alignment of {p.BlockAlignment}, which a decoder rejects ({DecoderFile}); the first is at 0x{WmaText.Hex(firstOffset)}."
                        : $"{count} media objects hold fewer bytes than the block alignment of {p.BlockAlignment}, which a decoder rejects ({DecoderFile}); the first is at 0x{WmaText.Hex(firstOffset)}.");
                _isLoss = true;
                return;
            }
        }

        if (packet.Length > p.BlockAlignment)
        {
            _findings.Add(Severity.Info, "wma.packet-long", () => PacketNode(index), count =>
                count == 1
                    ? $"A media object holds more bytes than the block alignment of {p.BlockAlignment}; a decoder reads them as further packets of {p.BlockAlignment} bytes, which this parser does not."
                    : $"{count} media objects hold more bytes than the block alignment of {p.BlockAlignment}; a decoder reads them as further packets of {p.BlockAlignment} bytes, which this parser does not.");
        }

        if (_isLossless && packetBits < p.HeaderBits)
        {
            long overreadOffset = packet.FileStart;
            record.Kind = PacketKind.Short;
            Data.Packets.Add(record);
            _findings.Add(Severity.Warning, "wma.packet-overread", () => PacketNode(index), count =>
                count == 1
                    ? $"A media object holds fewer bits than the {p.HeaderBits} of a packet's header; a decoder reads past its end and counts a loss ({DecoderFile}). It is at 0x{WmaText.Hex(overreadOffset)}."
                    : $"{count} media objects hold fewer bits than the {p.HeaderBits} of a packet's header; a decoder reads past their ends and counts a loss ({DecoderFile}). The first is at 0x{WmaText.Hex(overreadOffset)}.");
            _isLoss = true;
            return;
        }

        if (length == p.BlockAlignment)
        {
            FullPackets++;
        }

        packet.Span[..length].CopyTo(_packetBuffer);
        Array.Clear(_packetBuffer, length, _packetBuffer.Length - length);
        var bits = new Bits(_packetBuffer, packetBits);

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
        int payloadBits = Math.Max(0, packetBits - payloadStart);
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
                    ? $"A packet's sequence number does not follow the packet before ({found} for {expected}); a decoder takes packets to be lost and drops {Crossing}."
                    : $"{n} packets' sequence numbers do not follow the packet before (the first is {found} for {expected}); a decoder takes packets to be lost and drops {Crossing}.");
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
            ReadWithPrefix(bits, index, packetBits, payloadStart, count, isDropped);
        }
        else if (_isLossless)
        {
            ReadRun(index, packetBits, payloadStart, count, stated >= payloadBits, isDropped);
        }
        else
        {
            ReadWithoutPrefix(index, packetBits, payloadStart, count, isDropped);
        }

        MostFramesInPacket = Math.Max(MostFramesInPacket, _framesInCall);
    }

    private ref PacketRecord Record(int packet) => ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Data.Packets)[packet];

    private void ReadWithPrefix(Bits bits, int packet, int end, int payloadStart, int count, bool isDropped)
    {
        IStreamParameters p = _p;
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
                RefuseRun(packet);
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
                RefuseRun(packet);
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

    private void ReadWithoutPrefix(int packet, int end, int payloadStart, int count, bool isDropped)
    {
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
    private void ReadRun(int packet, int end, int payloadStart, int count, bool isCovering, bool isDropped)
    {
        // Bits that finish a frame when no run is kept for it (its start was lost, or no frame was started) are read as a frame all the same,
        // from the bit offset the last save left.
        bool isLostStart = !isDropped && count > 0 && _savedBits == 0;
        int taken = 0;
        bool isRefused = false;
        if (isDropped)
        {
            ClearSaved();
            taken = count;
        }
        else if (count > 0)
        {
            // A refused append drops the run and clears the loss in the same header handling, but the count bits are not passed: the packet's
            // payload is saved from its first bit as the next run, and the next packet's header call reads it.
            if (Append(packet, payloadStart, count))
            {
                taken = count;
            }
            else
            {
                isRefused = true;
            }
        }

        if (isCovering && !isRefused)
        {
            ref PacketRecord own = ref Record(packet);
            own.TailStart = 0;
            own.TailBits = 0;
            return;
        }

        bool isRestDropped = !isCovering && !DecodeRun(false, taken > 0 && !isDropped, isLostStart);
        ClearSaved();
        int start = payloadStart + taken;
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
    // false this returns. A frame that runs past the kept bits, and one whose end trim is F or more, are no errors; in the flush after the last
    // packet the second ends the run. isLostStart: the first frame is the end of one whose start was lost.
    private bool DecodeRun(bool isFlush, bool isHeaderCall, bool isLostStart)
    {
        int total = _savedBits;
        if (total <= 0)
        {
            return true;
        }

        var parts = new List<FramePart>(_saved);
        var joined = new Bits(_savedBuffer, total);
        int position = isLostStart ? Math.Min(_lastOff, total) : 0;
        bool isFirst = true;
        bool isOk = true;
        bool isTrailerOne = false;
        bool isTrailerSet = false;
        int gap = 0;
        long gapFrame = -1;
        while (position < total)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            bool isInHeaderCall = isFirst && isHeaderCall;
            _isFirstOfRun = isInHeaderCall;
            FrameOutcome result = ReadOne(joined, position, total, 0, [], parts, position, isFirst && isLostStart);
            isFirst = false;
            if (!result.IsValid && result.Fault is not ((int)LosslessFault.NeedsMoreBits or (int)LosslessFault.TrimTooLarge))
            {
                isOk = isInHeaderCall;
                position = total;
                isTrailerOne = false;
                isTrailerSet = false;
                break;
            }

            position += Math.Max(result.Bits, 1);
            // A frame rejected for its end trim stops before its trailer bit, and the next frame is read from there, except in the flush.
            isTrailerSet = result.Trailer;
            isTrailerOne = result.Trailer || (result.Fault == (int)LosslessFault.TrimTooLarge && !isFlush);
            if (!isTrailerOne && position < total && result.IsValid && gap == 0)
            {
                gap = total - position;
                gapFrame = _lastFrameNumber;
            }

            if (!isTrailerOne && !isInHeaderCall)
            {
                break;
            }
        }

        if (!isFlush && isOk && gap > 0)
        {
            long frame = gapFrame;
            int before = gap;
            _findings.Add(Severity.Warning, "wma.frame-boundary", () => NodeOfFrame(frame), n =>
                n == 1
                    ? $"A frame that starts in a packet says that no frame follows it, but {Counts.Bits(before)} lie between its end and the place the next packet's count of bits that finish the frame before gives. A decoder reads the bits left after the first frame of a packet's frames as further frames, whatever its trailer bit says, and leaves those after a later frame unread."
                    : $"{n} packets have frames that say that no frame follows them, but bits lie between their ends and the place the next packet's count of bits that finish the frame before gives (the first {Counts.Bits(before)}). A decoder reads the bits left after the first frame of a packet's frames as further frames, whatever its trailer bit says, and leaves those after a later frame unread.");
        }

        if (isOk && isTrailerSet && position >= total)
        {
            long frame = _lastFrameNumber;
            _findings.Add(Severity.Info, "wma.trailer-without-frame", () => NodeOfFrame(frame), n =>
                n == 1
                    ? "A frame says that another frame follows it in its packet, but no bits are left for one; a decoder reads nothing there."
                    : $"{n} frames say that another frame follows them in their packets, but no bits are left for one; a decoder reads nothing there.");
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

    private Node NodeOfFrame(long number) => number >= 0 && number < Data.Frames.Count ? FrameNode((int)number) : FramesListNode();

    // A save of bits to the buffer; false where the buffer is too small for them: the run is dropped, which counts as a loss only where the
    // bits are a packet's own tail (Save), not the next packet's bits that finish the frame (the loss set here is cleared in the same header
    // handling).
    private bool Append(int packet, int startBit, int bits)
    {
        int total = _savedBits + bits;
        if ((((_isLossless ? _savedOff : 0) + total + 8) / 8) > _bufferBytes)
        {
            ClearSaved();
            RefuseRun(packet);
            return false;
        }

        int bytes = ((total + 7) / 8) + 16;
        if (_savedBuffer.Length < bytes)
        {
            Array.Resize(ref _savedBuffer, bytes);
        }

        BitCopy.Copy(_packetBuffer, startBit, _savedBuffer, _savedBits, bits);
        _saved.Add(new FramePart(packet, startBit, bits));
        _savedBits = total;
        return true;
    }

    private void RefuseRun(int packet)
    {
        if (!_isLossless)
        {
            return;
        }

        int limit = _bufferBytes;
        _findings.Add(Severity.Warning, "wma.run-too-long", () => PacketNode(packet), n =>
            n == 1
                ? $"The bits a decoder keeps for the frames that start in a packet, with the bits that finish the last of them, do not fit its buffer of {Counts.Bytes(limit)} ({DecoderFile}); it drops them and the frames in them. Encspect does not walk those frames."
                : $"The bits a decoder keeps for the frames that start in {n} packets, with the bits that finish the last of them, do not fit its buffer of {Counts.Bytes(limit)} ({DecoderFile}); it drops them and the frames in them. Encspect does not walk those frames.");
    }

    private void ClearSaved()
    {
        if (_savedBits > 0)
        {
            Array.Clear(_savedBuffer, 0, Math.Min(_savedBuffer.Length, ((_savedBits + 7) / 8) + 16));
        }

        _saved.Clear();
        _savedBits = 0;
        _savedOff = 0;
    }

    private void Save(int packet, int startBit, int bits)
    {
        ClearSaved();
        if (_isLossless)
        {
            _lastOff = startBit & 7;
        }

        if ((((startBit & 7) + bits + (_isLossless ? 8 : 7)) / 8) > _bufferBytes)
        {
            RefuseRun(packet);
            _isLoss = true;
            return;
        }

        _savedOff = _isLossless ? startBit & 7 : 0;

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
    // frames read from a joined buffer of several frames, source holds the parts of the whole buffer and the frame starts at position. A frame
    // past FrameLimit is read and counted but not recorded. isLost: the bits are the end of a frame whose start was lost; they are read as a
    // frame, as a decoder does, and their samples counted, but they are no frame of the list.
    private FrameOutcome ReadOne(Bits bits, int start, int limit, int declared, IReadOnlyList<FramePart> parts, List<FramePart>? source = null, int position = 0, bool isLost = false)
    {
        FrameOutcome result = _reader.Read(bits, start, limit, declared, _trace);
        if (isLost)
        {
            _lastFrameNumber = -1;
            Samples += OutputOf(result);
            return result;
        }

        long number = _frameNumber++;
        _lastFrameNumber = number;
        bool isRecorded = number < FrameLimit;
        if (source is not null)
        {
            parts = Slice(source, position, Math.Max(result.Bits, 1));
        }

        int index = (int)Math.Min(number, int.MaxValue);
        if (isRecorded)
        {
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
        }

        _framesInCall++;
        Account(result, index, isRecorded, parts);
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

    // The samples a decoder outputs for a Lossless frame.
    private long OutputOf(in FrameOutcome result)
    {
        IStreamParameters p = _p;
        long whole = result.TrimEnd >= p.FrameLength ? 0 : p.FrameLength - result.TrimEnd;
        if (result.IsValid)
        {
            return whole;
        }

        if (p.HasLengthPrefix)
        {
            // Both ways the length check fails count F less the end trim, and only the first frame of a call returns anything.
            if (_framesInCall > 1)
            {
                return 0;
            }

            return result.Fault is (int)LosslessFault.NeedsMoreBits or (int)LosslessFault.LengthPrefix ? whole : result.CompletedSamples;
        }

        if (result.Fault == (int)LosslessFault.NeedsMoreBits)
        {
            return whole;
        }

        return _isFirstOfRun ? result.CompletedSamples : 0;
    }

    private void Account(FrameOutcome result, int index, bool isRecorded, IReadOnlyList<FramePart> parts)
    {
        IStreamParameters p = _p;
        double rate = 0;
        if (parts.Count > 1 && parts[0].Packet != parts[^1].Packet)
        {
            SpanningFrames++;
        }

        long offset = 0;
        if (isRecorded || !result.IsValid)
        {
            offset = BitNodes.OffsetOf(Data.Table, parts.ToArray(), 0);
        }

        if (!result.IsValid)
        {
            InvalidFrames++;
            int fault = result.Fault;
            _findings.Add(Severity.Warning, "wma.frame-invalid", () => isRecorded ? FrameNode(index) : FramesListNode(), count =>
                count == 1
                    ? $"A frame is invalid: {_reason(fault)}; {DroppedFor(fault)}. The first is at 0x{WmaText.Hex(offset)}."
                    : $"{count} frames are invalid: {_reason(fault)}; {DroppedFor(fault)}. The first is at 0x{WmaText.Hex(offset)}.");
        }
        else
        {
            rate = RateOf(result.Bits, p);
        }

        if (isRecorded)
        {
            BitrateX.Add(offset);
            BitrateY.Add(rate);
        }

        if (_isLossless)
        {
            Samples += OutputOf(result);
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

        if (!isRecorded)
        {
            return;
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
