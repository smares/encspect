using Encspect.Core;

namespace Encspect.Formats.Wma;

// Reads a Windows Media Audio Voice stream media object by media object, as docs/design/wma-voice-format.md describes: it cuts a media object into
// packets, reads each header, keeps the start of the superframe a packet ends in until the next packet finishes it, reads the superframes and their
// frames, and records what each took, to build the nodes and the figures from. Not thread safe: one instance reads one stream.
internal sealed class VoiceStream
{
    public const int MaxPackets = 262_144;
    public const int MaxSuperframes = 524_288;

    private readonly VoiceParameters _p;
    private readonly FindingCollector _findings;
    private readonly Func<int, Node> _packetNode;
    private readonly Func<int, Node> _superframeNode;
    private readonly CancellationToken _cancellation;

    // State that carries across packets and media objects and is never reset.
    private bool _residual;
    private int _remaining;
    private readonly byte[] _cache = new byte[VoiceParameters.CacheBytes];
    private int _put;
    private int _cached;
    private int _skip;
    private int _pitch = 40;
    private int _kind;
    private int _lastSequence = -1;

    // What the cached start is: where it came from.
    private int _tailPacket = -1;
    private int _tailStart;
    private bool _tailCopied;

    // What a packet did that the next header judges: whether the last superframe to fail in a packet since the header before was music.
    private VoiceOutcome _lastFailure = VoiceOutcome.Read;

    // Within a media object.
    private bool _dropped;
    private int _pending = -1;
    private long _calls;

    public VoiceStream(VoiceParameters parameters, FindingCollector findings, Func<int, Node> packetNode, Func<int, Node> superframeNode, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(findings);
        _p = parameters;
        _findings = findings;
        _packetNode = packetNode;
        _superframeNode = superframeNode;
        _cancellation = cancellation;
    }

    public VoiceParameters Parameters => _p;

    public VoiceList<VoicePacket> PacketRecords { get; } = new();

    public VoiceList<VoiceSuperframe> SuperframeRecords { get; } = new();

    public VoiceList<ByteRange> Ranges { get; } = new();

    public int Packets => PacketRecords.Count;

    public int Superframes => SuperframeRecords.Count;

    public VoiceTotals Totals { get; } = new();

    public bool Truncated { get; private set; }

    public string Limit { get; private set; } = "";

    public long LimitCount { get; private set; }

    private sealed class LimitReached : Exception;

    public void Add(MappedBytes mediaObject)
    {
        ArgumentNullException.ThrowIfNull(mediaObject);
        if (Truncated || mediaObject.Length == 0)
        {
            return;
        }

        int length = mediaObject.Length;
        int align = _p.BlockAlign;
        int lead = length % align;
        int first = PacketRecords.Count;
        if (length < align)
        {
            _findings.Add(Severity.Warning, "wma.packet-short", () => _packetNode(first), count => ShortMessage(align, count));
        }
        else if (lead != 0)
        {
            _findings.Add(Severity.Info, "wma.packet-long", () => _packetNode(first), count => LongMessage(align, lead, count));
        }

        _dropped = false;
        _pending = -1;
        try
        {
            int start = 0;
            if (lead > 0)
            {
                ProcessPacket(mediaObject, 0, lead, hasHeader: false);
                start = lead;
            }

            for (; start < length; start += align)
            {
                ProcessPacket(mediaObject, start, align, hasHeader: true);
            }
        }
        catch (LimitReached)
        {
        }
    }

    // After the last media object: the start that is still cached is read from the cache alone.
    public void Finish()
    {
        if (Truncated || _cached <= 0)
        {
            return;
        }

        try
        {
            ClearAfterPut();
            int count = _cached;
            VoiceSuperframe sf = ReadSuperframe(_cache, 0, count, _residual);
            sf.IsCached = true;
            sf.IsLast = true;
            sf.Packet = _tailPacket;
            sf.Start = _tailStart;
            sf.TailBits = _tailCopied ? count : 0;
            sf.SpillPacket = -1;
            sf.Offset = TailOffset();
            int index = AddSuperframe(ref sf);
            if (_tailPacket >= 0)
            {
                PacketRecords[_tailPacket].TailSuperframe = index;
            }

            _cached = 0;
            if (!_tailCopied)
            {
                Totals.BitsAfterLast = count;
            }
            else if (sf.Outcome == VoiceOutcome.Read)
            {
                Totals.BitsAfterLast = count - sf.Needed;
            }
            else
            {
                Totals.BitsAfterLast = count;
            }

            if (sf.Outcome == VoiceOutcome.Overrun)
            {
                int available = sf.Available;
                int needed = sf.Needed;
                _findings.Add(Severity.Info, "wma.voice-unfinished", () => _superframeNode(index),
                    _ => $"The last packet ends with {Counts.Of(available, "bit", "bits")} that begin a superframe needing {needed} or more, and no packet follows to finish it; a decoder drops them.");
            }
            else if (sf.Outcome != VoiceOutcome.Read)
            {
                ReportFailure(index, sf, Disposition.End);
            }
        }
        catch (LimitReached)
        {
        }
    }

    private long TailOffset()
    {
        if (_tailPacket < 0)
        {
            return 0;
        }

        VoicePacket packet = PacketRecords[_tailPacket];
        return OffsetOfByte(packet, _tailStart >> 3);
    }

    private long OffsetOfByte(in VoicePacket packet, int index)
    {
        int rest = index;
        for (int i = 0; i < packet.RangeCount; i++)
        {
            ByteRange range = Ranges[packet.RangeStart + i];
            if (rest < range.Length)
            {
                return range.Offset + rest;
            }

            rest -= (int)range.Length;
        }

        return packet.RangeCount > 0 ? Ranges[packet.RangeStart + packet.RangeCount - 1].End : 0;
    }

    private enum Disposition
    {
        End,
        Cached,
        Packet,
    }

    private void ProcessPacket(MappedBytes mediaObject, int start, int length, bool hasHeader)
    {
        if ((++_calls & 255) == 0)
        {
            _cancellation.ThrowIfCancellationRequested();
        }

        if (PacketRecords.Count >= MaxPackets)
        {
            Stop("packets", MaxPackets);
        }

        var packet = new VoicePacket
        {
            Length = length,
            RangeStart = Ranges.Count,
            Finished = -1,
            FirstOwn = -1,
            TailSuperframe = -1,
            Kind = VoicePacketKind.NotRead,
        };
        foreach (ByteRange range in mediaObject.FileRanges(start, start + length))
        {
            Ranges.Add(range);
            packet.RangeCount++;
        }

        Totals.Packets++;
        Totals.PacketBytes += length;
        int index = PacketRecords.Count;
        if (_dropped)
        {
            PacketRecords.Add(packet);
            return;
        }

        ReadOnlySpan<byte> data = mediaObject.Span.Slice(start, length);
        int bits = length * 8;
        if (_pending >= 0)
        {
            int at = _pending;
            _pending = -1;
            if (at >= bits)
            {
                // Passed over: the reading that went past the packet before went past this one too.
                PacketRecords.Add(packet);
                PastEnd(at, bits);
                return;
            }

            ReadHeaderless(ref packet, index, data, at, mediaObject, start);
            return;
        }

        if (!hasHeader)
        {
            int at = _skip;
            _skip = 0;
            ReadHeaderless(ref packet, index, data, at, mediaObject, start);
            return;
        }

        ReadHeadered(ref packet, index, data, mediaObject, start);
    }

    private void Stop(string limit, long count)
    {
        Truncated = true;
        Limit = limit;
        LimitCount = count;
        throw new LimitReached();
    }

    private int AddSuperframe(ref VoiceSuperframe sf)
    {
        if (SuperframeRecords.Count >= MaxSuperframes)
        {
            Stop("superframes", MaxSuperframes);
        }

        int index = SuperframeRecords.Add(sf);
        if (sf.Outcome == VoiceOutcome.Read)
        {
            Count(in sf);
        }
        else
        {
            Totals.Invalid++;
        }

        return index;
    }

    private void Count(in VoiceSuperframe sf)
    {
        VoiceTotals t = Totals;
        t.Superframes++;
        t.Samples += sf.Samples;
        t.SuperframeBits += sf.Needed;
        t.SmallestBits = Math.Min(t.SmallestBits, sf.Needed);
        t.LargestBits = Math.Max(t.LargestBits, sf.Needed);
        if (sf.HasSampleCount)
        {
            t.WithSampleCount++;
        }

        if (sf.HasResidualLsps)
        {
            t.WithResidualLsps++;
        }

        if (sf.AdditionalGroups > 0)
        {
            t.WithAdditionalData++;
        }

        for (int i = 0; i < sf.Frames; i++)
        {
            byte[] descriptor = VoiceTables.FrameDescriptors[sf[i].Type];
            t.FrameTypes[sf[i].Type]++;
            t.FixedCodebooks[descriptor[3]]++;
            t.AdaptiveCodebooks[descriptor[2]]++;
            t.BlockCounts[descriptor[1]]++;
        }
    }

    // A reading that began in a packet ended at bit end, counted from the start of the packet whose length is bits.
    private void PastEnd(int end, int bits)
    {
        if (end < bits + 8)
        {
            _skip = end % 8;
        }
        else
        {
            _pending = end - bits;
        }
    }

    private void ReadHeaderless(ref VoicePacket packet, int index, ReadOnlySpan<byte> data, int at, MappedBytes mediaObject, int start)
    {
        packet.Kind = VoicePacketKind.Headerless;
        packet.BodyStart = at;
        _cached = 0;
        int position = at;
        ReadOwn(ref packet, index, data, ref position, mediaObject, start);
        PacketRecords.Add(packet);
    }

    private void ReadHeadered(ref VoicePacket packet, int index, ReadOnlySpan<byte> data, MappedBytes mediaObject, int start)
    {
        int bits = data.Length * 8;
        int spillBits = _p.SpilloverBits;
        bool previousResidual = _residual;
        var src = new BitSource(data, bits);
        int sequence = (int)src.Read(4);
        _residual = src.Read(1) != 0;
        packet.Sequence = (byte)sequence;
        packet.HasResidualLsps = _residual;
        if (_lastSequence >= 0 && sequence != ((_lastSequence + 1) & 15))
        {
            int expected = (_lastSequence + 1) & 15;
            _findings.Add(Severity.Info, "wma.voice-packet-sequence", () => _packetNode(index),
                count => count == 1
                    ? $"A packet's sequence number does not follow the packet before ({sequence} for {expected}); a packet may be missing. FFmpeg's decoder does not read the number."
                    : $"{count} packets' sequence numbers do not follow the packet before (the first is {sequence} for {expected}); a packet may be missing. FFmpeg's decoder does not read the number.");
        }

        _lastSequence = sequence;

        int total = 0;
        int values = 0;
        bool rejected = false;
        while (true)
        {
            if (bits - src.Position < 6 + spillBits)
            {
                rejected = true;
                break;
            }

            int value = (int)src.Read(6);
            values++;
            total += value;
            if (value != 63)
            {
                break;
            }
        }

        if (rejected)
        {
            packet.Kind = VoicePacketKind.InvalidHeader;
            packet.ValueCount = (byte)Math.Min(values, 255);
            packet.HeaderBits = 5;
            packet.BodyStart = 5;
            _dropped = true;
            _findings.Add(Severity.Warning, "wma.voice-packet-header", () => _packetNode(index),
                count => count == 1
                    ? "A packet's header runs past the packet; a decoder rejects it and drops the rest of the media object."
                    : $"{count} packets' headers run past the packet; a decoder rejects them and drops the rest of the media object.");
            PacketRecords.Add(packet);
            return;
        }

        int spillover = (int)src.Read(spillBits);
        int headerEnd = src.Position;
        packet.Kind = VoicePacketKind.Header;
        packet.ValueCount = (byte)Math.Min(values, 255);
        packet.Spillover = spillover;
        packet.HeaderBits = headerEnd;
        packet.Count = total;
        _remaining = total;
        _skip = 0;

        int position = headerEnd;
        bool done = false;
        int objectBits = ((mediaObject.Length - start) * 8) - headerEnd;
        VoiceOutcome failure = _lastFailure;
        _lastFailure = VoiceOutcome.Read;
        int own = 0;
        if (_cached > 0)
        {
            int g = Math.Min(spillover, objectBits);
            bool copy = g <= bits - headerEnd && _put + g <= VoiceParameters.CacheBits;
            int at = _put;
            if (copy)
            {
                CopyBits(data, headerEnd, _cache, _put, g);
                _put += g;
            }
            else
            {
                _findings.Add(Severity.Info, "wma.voice-cache-overflow", () => _packetNode(index), OverflowMessage);
            }

            ClearAfterPut();
            _cached += g;
            int count = _cached;
            VoiceSuperframe sf = ReadSuperframe(_cache, 0, count, _residual);
            sf.IsCached = true;
            sf.Packet = _tailPacket;
            sf.Start = _tailStart;
            sf.TailBits = _tailCopied ? PacketRecords[_tailPacket].TailLength : 0;
            sf.SpillPacket = index;
            sf.SpillStart = headerEnd;
            sf.SpillBits = (ushort)(copy ? g : 0);
            sf.SpillAt = (ushort)at;
            sf.Offset = TailOffset();
            _cached = 0;
            int finished = AddSuperframe(ref sf);
            packet.Finished = finished;
            if (_tailPacket >= 0)
            {
                PacketRecords[_tailPacket].TailSuperframe = finished;
            }

            if (_residual != previousResidual)
            {
                _findings.Add(Severity.Info, "wma.voice-lsp-flag-changes", () => _packetNode(index),
                    count => count == 1
                        ? "A packet sets the flag for residual LSPs differently from the packet before, and the superframe that goes on into it is read with the later flag, as FFmpeg's decoder reads it."
                        : $"{count} packets set the flag for residual LSPs differently from the packet before, and the superframe that goes on into them is read with the later flag, as FFmpeg's decoder reads it.");
            }

            if (sf.Outcome == VoiceOutcome.Read)
            {
                Totals.SpanningPackets++;
                position = headerEnd + g;
                int packet0Tail = _tailPacket >= 0 ? PacketRecords[_tailPacket].TailLength : 0;
                int copied = (_tailCopied ? packet0Tail : 0) + (copy ? g : 0);
                int unused = copied - sf.Needed;
                if (unused > 0)
                {
                    Totals.IgnoredBits += unused;
                    _findings.Add(Severity.Info, "wma.voice-superframe-padding", () => _superframeNode(finished),
                        n => n == 1
                            ? $"A superframe ends before the bits the packets give it ({unused} bits); a decoder ignores them and goes on at the position the next packet's header states."
                            : $"{n} superframes end before the bits the packets give them (the first leaves {unused} bits); a decoder ignores them and goes on at the position the next packet's header states.");
                }

                packet.Lead = copy ? VoiceLead.Finished : VoiceLead.FinishedNotCopied;
                packet.LeadBits = g;
                if (position >= bits)
                {
                    done = true;
                    PastEnd(position, bits);
                }
            }
            else
            {
                int k = copy ? Math.Min(g, (bits - headerEnd) % 8) : 0;
                position = headerEnd + g + (2 * k);
                packet.Lead = copy ? VoiceLead.Finished : VoiceLead.FinishedNotCopied;
                packet.LeadBits = g;
                packet.ResyncBits = 2 * k;
                ReportFailure(finished, sf, Disposition.Cached);
            }

            packet.Spillover = spillover;
            own = 1;
        }
        else if (spillover > 0)
        {
            packet.Lead = VoiceLead.Skipped;
            Totals.SkippedSpilloverBits += spillover;
            position = headerEnd + spillover;
            bool afterMusic = failure == VoiceOutcome.Music;
            if (afterMusic)
            {
                _findings.Add(Severity.Info, "wma.voice-spillover-after-music", () => _packetNode(index),
                    count => count == 1
                        ? $"A packet states spillover bits ({spillover}) of a superframe that began in the packet before, and that packet's superframes were lost to a superframe of music, which a decoder cannot read: it drops the rest of the media object, the start of this superframe with it, and skips the bits."
                        : $"{count} packets state spillover bits (the first has {spillover}) of a superframe that began in the packet before, and that packet's superframes were lost to a superframe of music, which a decoder cannot read: it drops the rest of the media object, the start of this superframe with it, and skips the bits.");
            }
            else
            {
                _findings.Add(Severity.Warning, "wma.voice-spillover-orphan", () => _packetNode(index),
                    count => count == 1
                        ? $"A packet states spillover bits ({spillover}) of a superframe that began in the packet before, and no start is left from it (the first packet, a packet that could not be read, a media object with no header, or a superframe that began exactly at its end); a decoder skips them."
                        : $"{count} packets state spillover bits (the first has {spillover}) of a superframe that began in the packet before, and no start is left from it (the first packet, a packet that could not be read, a media object with no header, or a superframe that began exactly at its end); a decoder skips them.");
            }
        }

        packet.BodyStart = position;
        if (!done)
        {
            ReadOwn(ref packet, index, data, ref position, mediaObject, start);
        }

        Totals.MostInPacket = Math.Max(Totals.MostInPacket, own + packet.OwnRead);
        PacketRecords.Add(packet);
    }

    // The superframes the packet counts that begin in it and are read there, from position; then the start of the one the packet ends in.
    private void ReadOwn(ref VoicePacket packet, int index, ReadOnlySpan<byte> data, ref int position, MappedBytes mediaObject, int start)
    {
        int bits = data.Length * 8;
        packet.FirstOwn = SuperframeRecords.Count;
        while (true)
        {
            if (_remaining == 0)
            {
                _remaining = -1;
                break;
            }

            if (_remaining >= 2)
            {
                _remaining--;
                VoiceSuperframe sf = ReadSuperframe(data, position, bits, _residual);
                sf.Packet = index;
                sf.Start = position;
                sf.Offset = OffsetOfByte(packet, position >> 3);
                int added = AddSuperframe(ref sf);
                packet.OwnCount++;
                packet.Begun++;
                if (sf.Outcome != VoiceOutcome.Read)
                {
                    _dropped = true;
                    _lastFailure = sf.Outcome == VoiceOutcome.Music ? VoiceOutcome.Music : VoiceOutcome.Overrun;
                    ReportFailure(added, sf, Disposition.Packet);
                    break;
                }

                packet.OwnRead++;
                position += sf.Needed;
                if (position >= bits)
                {
                    break;
                }

                continue;
            }

            if (_remaining == 1)
            {
                _remaining = 0;
            }

            int length = bits - position;
            if (length > 0)
            {
                _cached = length;
                _put = 0;
                _tailPacket = index;
                _tailStart = position;
                packet.TailStart = position;
                packet.TailLength = length;
                packet.Begun++;
                if (length <= VoiceParameters.CacheBits)
                {
                    CopyBits(data, position, _cache, 0, length);
                    _put = length;
                    _tailCopied = true;
                    packet.Tail = VoiceTail.Cached;
                }
                else
                {
                    _tailCopied = false;
                    packet.Tail = VoiceTail.Refused;
                    _findings.Add(Severity.Info, "wma.voice-cache-overflow", () => _packetNode(index), OverflowMessage);
                }
            }

            break;
        }
    }

    private static string OverflowMessage(int count) => count == 1
        ? "A packet starts or finishes a superframe that does not fit the 256 bytes a decoder keeps of one that goes on in the next packet, or whose spillover bits are not in the packet; FFmpeg's decoder copies none of the bits that do not fit (wmavoice.c), so it reads the cache's old bytes in their place."
        : $"{count} packets start or finish a superframe that does not fit the 256 bytes a decoder keeps of one that goes on in the next packet, or whose spillover bits are not in the packet; FFmpeg's decoder copies none of the bits that do not fit (wmavoice.c), so it reads the cache's old bytes in their place.";

    // The bits of the last byte written to the cache that lie after the put position are cleared.
    private void ClearAfterPut()
    {
        if ((_put & 7) != 0)
        {
            _cache[_put >> 3] &= (byte)(0xFF << (8 - (_put & 7)));
        }
    }

    private static void CopyBits(ReadOnlySpan<byte> source, int sourceBit, byte[] target, int targetBit, int count)
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

    private void ReportFailure(int index, in VoiceSuperframe sf, Disposition disposition)
    {
        string code;
        Severity severity;
        switch (sf.Outcome)
        {
            case VoiceOutcome.Music:
                code = "wma.voice-music";
                severity = Severity.Info;
                break;
            case VoiceOutcome.SampleCount:
                code = "wma.voice-sample-count";
                severity = Severity.Warning;
                break;
            case VoiceOutcome.FrameType:
                code = "wma.voice-frame-type";
                severity = Severity.Warning;
                break;
            default:
                code = "wma.voice-superframe-overrun";
                severity = Severity.Warning;
                break;
        }

        int needed = sf.Needed;
        int available = sf.Available;
        VoiceOutcome outcome = sf.Outcome;
        string Dropped(int count) => count == 1
            ? disposition switch
            {
                Disposition.End => "it",
                Disposition.Cached => "it and goes on with the packet's own superframes",
                _ => "it and the rest of its media object",
            }
            : "each, and with one that lies in a packet the rest of its media object";

        _findings.Add(severity, code, () => _superframeNode(index), count =>
        {
            string x = Dropped(count);
            bool one = count == 1;
            return outcome switch
            {
                VoiceOutcome.Music => one
                    ? $"A superframe's first bit is clear: it holds music, Windows Media Audio Professional data inside Voice superframes, which FFmpeg's decoder does not support (wmavoice.c) and this analysis does not read; a decoder drops {x}."
                    : $"{count} superframes' first bit is clear: they hold music, Windows Media Audio Professional data inside Voice superframes, which FFmpeg's decoder does not support (wmavoice.c) and this analysis does not read; a decoder drops {x}.",
                VoiceOutcome.SampleCount => one
                    ? $"A superframe states more samples than the 480 it holds; a decoder drops {x}."
                    : $"{count} superframes state more samples than the 480 it holds; a decoder drops {x}.",
                VoiceOutcome.FrameType => one
                    ? $"A frame's type code is one that the frame type table gives no type, so the superframe fails; a decoder drops {x}."
                    : $"{count} frames' type codes are one that the frame type table gives no type, so the superframes fail; a decoder drops {x}.",
                _ => one
                    ? $"A superframe needs more bits than the packets give it ({needed} for {available}); a decoder drops {x}."
                    : $"{count} superframes need more bits than the packets give them (the first needs {needed} for {available}); a decoder drops {x}.",
            };
        });
    }

    private static string ShortMessage(int align, int count) => count == 1
        ? $"A media object holds fewer bytes than the block alignment of {align}; a decoder takes it for the rest of a packet and reads no header in it: it drops the superframe that goes on from the packet before, and takes a superframe or the start of one from the bytes only where the packet before counted more superframes than it held, or none (wmavoice.c)."
        : $"{count} media objects hold fewer bytes than the block alignment of {align}; a decoder takes them for the rest of a packet and reads no header in them: it drops the superframe that goes on from the packet before, and takes a superframe or the start of one from the bytes only where the packet before counted more superframes than it held, or none (wmavoice.c).";

    private static string LongMessage(int align, int lead, int count) => count == 1
        ? $"A media object is not a whole number of blocks of {align} bytes ({lead} bytes before its whole blocks); a decoder cuts blocks from the end of a media object (wmavoice.c), so it takes those bytes for the rest of a packet with no header, as for a media object shorter than a block."
        : $"{count} media objects are not a whole number of blocks of {align} bytes (the first has {lead} bytes before them); a decoder cuts blocks from the end of a media object (wmavoice.c), so it takes those bytes for the rest of a packet with no header, as for a media object shorter than a block.";

    // Reads the bits of the bytes given as the data, up to the bit end; beyond it zeros.
    private ref struct BitSource(ReadOnlySpan<byte> data, int end)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; }

        public uint Read(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            ulong window = 0;
            int first = Position >> 3;
            for (int i = 0; i < 5; i++)
            {
                int index = first + i;
                window = (window << 8) | (uint)(index < _data.Length && index * 8 < end ? _data[index] : 0);
            }

            ulong value = ((window << (Position & 7)) >> (40 - count)) & ((1UL << count) - 1);
            int over = Position + count - end;
            if (over > 0)
            {
                value &= over >= count ? 0 : ~((1UL << over) - 1);
            }

            Position += count;
            return (uint)value;
        }

        public void Skip(int count) => Position += count;

        public void Seek(int position) => Position = position;
    }

    // Reads a superframe from the bits [start, end) of the data. The frames update the pitch state, as they are read, also past the end.
    private VoiceSuperframe ReadSuperframe(ReadOnlySpan<byte> data, int start, int end, bool residual)
    {
        var sf = new VoiceSuperframe { Available = Math.Max(0, end - start), HasResidualLsps = residual, SpillPacket = -1, Samples = 0, Packet = -1 };
        var src = new BitSource(data, end);
        src.Seek(start);

        if (src.Read(1) == 0)
        {
            sf.Needed = src.Position - start;
            sf.Outcome = src.Position > end ? VoiceOutcome.Overrun : VoiceOutcome.Music;
            return sf;
        }

        int samples = VoiceParameters.SuperframeSamples;
        if (src.Read(1) != 0)
        {
            sf.HasSampleCount = true;
            samples = (int)src.Read(12);
            sf.SampleCount = (ushort)samples;
            if (samples > VoiceParameters.SuperframeSamples)
            {
                sf.Needed = src.Position - start;
                sf.Outcome = src.Position > end ? VoiceOutcome.Overrun : VoiceOutcome.SampleCount;
                return sf;
            }
        }

        if (residual)
        {
            src.Skip(_p.ResidualLspBits);
        }

        for (int i = 0; i < VoiceParameters.FramesPerSuperframe; i++)
        {
            VoiceFrame frame = default;
            if (!ReadFrame(ref src, residual, ref frame))
            {
                sf.Needed = src.Position - start;
                sf.Outcome = src.Position > end ? VoiceOutcome.Overrun : VoiceOutcome.FrameType;
                return sf;
            }

            switch (i)
            {
                case 0: sf.Frame0 = frame; break;
                case 1: sf.Frame1 = frame; break;
                default: sf.Frame2 = frame; break;
            }

            sf.Frames++;
        }

        if (src.Read(1) != 0)
        {
            int groups = (int)src.Read(4) + 1;
            sf.AdditionalGroups = (byte)groups;
            src.Skip(10 * groups);
        }

        sf.Needed = src.Position - start;
        if (src.Position > end)
        {
            sf.Outcome = VoiceOutcome.Overrun;
            return sf;
        }

        sf.Outcome = VoiceOutcome.Read;
        sf.Samples = (ushort)samples;
        return sf;
    }

    // Reads one frame; false where its type code has no type (the position is then after the code).
    private bool ReadFrame(ref BitSource src, bool residual, ref VoiceFrame frame)
    {
        int frameStart = src.Position;
        if (!residual)
        {
            src.Skip(_p.IndependentLspBits);
        }

        int slot;
        int units = 0;
        while (true)
        {
            int unit = (int)src.Read(2);
            if (unit < 3)
            {
                slot = (3 * units) + unit;
                break;
            }

            if (++units == 6)
            {
                slot = 18 + (int)src.Read(2);
                break;
            }
        }

        int type = _p.TypeOfSlot[slot];
        if (type < 0)
        {
            return false;
        }

        byte[] descriptor = VoiceTables.FrameDescriptors[type];
        int blocks = descriptor[0];
        int adaptive = descriptor[2];
        int fixedKind = descriptor[3];
        int pairs = descriptor[4];
        frame.Type = (byte)type;
        int pitch = _pitch;
        int framePitch = 0;
        if (adaptive == 1)
        {
            int field = (int)src.Read(_p.PitchBits);
            framePitch = Math.Min(_p.MinPitch + field, _p.MaxPitch - 1);
            if (_kind == 0 || 20 * Math.Abs(framePitch - pitch) > framePitch + pitch)
            {
                pitch = framePitch;
            }

            frame.Pitch = (ushort)framePitch;
        }

        if (fixedKind == 0)
        {
            frame.Parameter = (ushort)src.Read(8);
        }

        if (fixedKind == 2)
        {
            int value = (int)src.Read(6);
            int index = value;
            bool extended = false;
            if (value >= 54)
            {
                extended = true;
                index = 54 + (4 * (value - 54)) + (int)src.Read(2);
            }

            // The first block has pulses at the pitch lag when its first pulse lies in the first half of the frame.
            int first = (framePitch + (((2 * blocks) - 1) * pitch) + blocks) / (2 * blocks);
            int offset = VoiceTables.StartOffsets[index];
            while (offset < 0)
            {
                offset += first;
            }

            bool lag = offset < VoiceParameters.FrameSamples / 2;
            frame.Parameter = (ushort)index;
            frame.Flags = (byte)((extended ? 1 : 0) | (lag ? 2 : 0));
            src.Skip((extended ? 10 : 12) + (lag ? 5 : 4) + 1 + 7);
            src.Skip(12 + (lag ? 3 : 4) + 1 + 7);
        }
        else
        {
            int q = 5 - descriptor[1];
            int blockPitch = 0;
            int value = 0;
            for (int n = 0; n < blocks; n++)
            {
                if (adaptive == 2)
                {
                    if (n == 0)
                    {
                        value = (int)src.Read(_p.BlockPitchBits);
                    }
                    else
                    {
                        int limited = Math.Min(Math.Max(value, _p.HalfStep), _p.BlockPitchRange - _p.HalfStep);
                        value = limited - _p.HalfStep + (int)src.Read(_p.BlockDeltaPitchBits);
                    }

                    blockPitch = BlockPitch(value);
                }

                if (fixedKind == 1)
                {
                    src.Skip(14);
                }
                else if (fixedKind == 3)
                {
                    src.Skip(12 + ((5 + pairs) * q));
                }
            }

            if (adaptive == 2)
            {
                pitch = blockPitch;
                frame.Pitch = (ushort)blockPitch;
            }
        }

        _pitch = adaptive == 1 ? framePitch : adaptive == 2 ? pitch : 0;
        _kind = adaptive;
        frame.Bits = (ushort)Math.Min(src.Position - frameStart, ushort.MaxValue);
        return true;
    }

    private int BlockPitch(int value)
    {
        int a0 = _p.MinPitch;
        int a1 = _p.Anchor1;
        int a2 = _p.Anchor2;
        int a3 = _p.MaxPitch - 1;
        int s1 = 4 * (a1 - a0);
        int s2 = 2 * (a2 - a1);
        int s3 = a3 - a2 + 1;
        if (value < s1)
        {
            return a0 + (value / 4);
        }

        value -= s1;
        if (value < s2)
        {
            return a1 + (value / 2);
        }

        value -= s2;
        return value < s3 ? a2 + value : a3;
    }
}
