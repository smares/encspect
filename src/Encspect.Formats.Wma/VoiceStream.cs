using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Walks a stream's media objects and reads the superframes in them, as FFmpeg's wmavoice_decode_packet is driven by libavcodec's
// decode loop. A packet is a block of the block alignment's size. Its header holds a sequence number, the flag for residual LSPs,
// the count of superframes that begin in it, and the bits of the superframe before that continue in it (the spillover). Those bits
// come first; then whole superframes follow one another bit by bit; the last of the counted superframes goes on to the end of the
// packet and is finished by the next packet's spillover bits.
//
// The decoder's state is kept as the decoder keeps it, so that the walk reads what it reads: the count of superframes the last
// header still has to read, the cache of the superframe a packet ends in (a buffer of 256 bytes whose bytes stay until written
// again), and the flag for residual LSPs, which the header sets before it can fail.
internal sealed class VoiceStream
{
    private readonly VoiceDecoder _decoder;
    private VoiceState _state = new();

    // Every bit copied into it is taken to be stored. FFmpeg's put_bits keeps bits in a register until flush_put_bits (up to 63 on x86-64,
    // where BitBuf is 64 bits, else up to 31), so after a dropped tail a refused copy reads bytes that never held them there; the size of
    // that gap depends on the build and it is not copied.
    // wmavoice.c: sframe_cache, the buffer the start of a superframe is kept in until the next packet finishes it. Bytes that a
    // copy did not write are those an earlier superframe left (or zero), and a decoder reads them.
    private readonly byte[] _cache = new byte[VoiceParameters.CacheBytes];

    // wmavoice.c: sframe_cache_size: the bits of the cached start and the spillover bits added to them, or 0.
    private int _cacheBits;

    // The bits written to the cache (the put position), which a copy that does not fit leaves where they were.
    private int _cacheWritten;

    // Where the cached start lies in its packet, for the nodes.
    private int _cachePacket;
    private int _cacheStart;
    private int _tailBits;

    // The start of the superframe was copied into the cache, and so is in it; else the cache holds what was there before.
    private bool _tailCopied;

    private readonly CancellationToken _cancellation;
    private bool _haveSequence;
    private int _lastSequence;

    // wmavoice.c: has_residual_lsps: the flag of the last header read, also one that could not be read to its end.
    private bool _residual;

    // wmavoice.c: nb_superframes: the superframes the last header counted that are still to read. It is 0 once a packet's last
    // superframe is cached, and below 0 after a packet that counted none was passed over.
    private int _pending;

    // wmavoice.c: skip_bits_next: the bits to skip at the start of the next call, set when a superframe ended inside a byte.
    private int _skipNext;

    // Within one media object: the bits of the next block that an earlier superframe's reading went on into, and whether an error
    // dropped the rest of the object (libavcodec drops the whole remaining packet then).
    private int _carry;
    private bool _dropped;

    // The last packet's superframes were lost to a superframe of music, which a decoder cannot read.
    private bool _lostToMusic;

    public VoiceStream(VoiceParameters parameters, FindingCollector findings, Func<int, Node> packetNode, Func<int, Node> superframeNode, CancellationToken cancellation = default)
    {
        _cancellation = cancellation;
        Records = new VoiceRecords(parameters);
        Parameters = parameters;
        Findings = findings;
        PacketNode = packetNode;
        SuperframeNode = superframeNode;
        _decoder = new VoiceDecoder(parameters);
    }

    public VoiceParameters Parameters { get; }

    public FindingCollector Findings { get; }

    public Func<int, Node> PacketNode { get; }

    public Func<int, Node> SuperframeNode { get; }

    public VoiceRecords Records { get; }

    public ChunkedList<VoicePacketRecord> Packets => Records.Packets;

    public ChunkedList<VoiceSuperframeRecord> Superframes => Records.Superframes;

    public ChunkedList<VoiceFrameRecord> Frames => Records.Frames;

    public ChunkedList<FramePart> Parts => Records.Parts;

    public Dictionary<int, ByteRange[]> Ranges => Records.Ranges;

    public VoiceTotals Totals { get; } = new();

    // Superframes are kept in records up to this count (about 92 MB with their frames), so a crafted file of packets that hold
    // nothing but tiny superframes stays bounded; after it the stream is not read, and Limit says which count was reached.
    internal const int MaxSuperframes = 1 << 19;

    // Packets (blocks) are kept in records up to this count (about 15 MB). The count of media objects is limited by the parser, but
    // one media object can hold millions of blocks of a few bytes.
    internal const int MaxBlocks = 1 << 18;

    public bool Truncated { get; private set; }

    // What was counted to the limit: "superframes" or "packets".
    public string Limit { get; private set; } = string.Empty;

    public long LimitCount => Limit == "packets" ? MaxBlocks : MaxSuperframes;

    // A media object of the container is one or more blocks of the block alignment's size: FFmpeg's decoder cuts what an ASF packet
    // holds into blocks of that size from its end (wmavoice.c: wmavoice_decode_packet). The bytes before the whole blocks are
    // read as the rest of a packet that has no header.
    public void Add(MappedBytes media)
    {
        if (Truncated)
        {
            return;
        }

        int align = Parameters.BlockAlign;
        int length = media.Length;
        int lead = length % align;
        _carry = 0;
        _dropped = false;
        int start = 0;
        if (lead != 0)
        {
            int index = Packets.Count;
            if (length < align)
            {
                Findings.Add(Severity.Warning, "wma.packet-short", () => PacketNode(Math.Min(index, Packets.Count - 1)), count =>
                    $"{(count == 1 ? "A media object holds" : $"{count} media objects hold")} fewer bytes than the block alignment of {align}; a decoder takes {(count == 1 ? "it" : "them")} for the rest of a packet and reads no header in {(count == 1 ? "it" : "them")}: it drops the superframe that goes on from the packet before, and takes a superframe or the start of one from the bytes only where the packet before counted more superframes than it held, or none (wmavoice.c).");
            }
            else
            {
                Findings.Add(Severity.Info, "wma.packet-long", () => PacketNode(Math.Min(index, Packets.Count - 1)), count =>
                    $"{(count == 1 ? "A media object is" : $"{count} media objects are")} not a whole number of blocks of {align} bytes ({(count == 1 ? $"{lead} bytes before its whole blocks" : "the first has " + lead + " bytes before them")}); a decoder cuts blocks from the end of a media object (wmavoice.c), so it takes those bytes for the rest of a packet with no header, as for a media object shorter than a block.");
            }

            AddBlock(media, 0, lead, isWhole: false);
            start = lead;
        }

        for (; start + align <= length && !Truncated; start += align)
        {
            AddBlock(media, start, align, isWhole: true);
        }
    }

    private void AddBlock(MappedBytes media, int start, int length, bool isWhole)
    {
        if (Packets.Count >= MaxBlocks)
        {
            Truncated = true;
            Limit = "packets";
            return;
        }

        if (Packets.Count % 4096 == 0)
        {
            _cancellation.ThrowIfCancellationRequested();
        }

        int index = Packets.Count;
        int total = length * 8;
        var record = new VoicePacketRecord
        {
            Offset = media.OffsetOf(start),
            Length = length,
            RangeIndex = -1,
            FirstSuperframe = Superframes.Count,
            TailStart = total,
        };

        IReadOnlyList<ByteRange> ranges = media.FileRanges(start, start + length);
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
        Totals.UsedBytes += length;

        // The bits of this block an earlier superframe's reading went on into: a block read through to its end is passed over.
        int carry = _carry;
        _carry = 0;
        if (carry >= total && carry > 0)
        {
            record.Flags |= VoicePacketRecord.Dropped;
            _carry = (carry >> 3) == length ? 0 : carry - total;
            Packets.Add(record);
            return;
        }

        if (_dropped)
        {
            record.Flags |= VoicePacketRecord.Dropped;
            Packets.Add(record);
            return;
        }

        byte[] data = media.Span.Slice(start, length).ToArray();
        var bits = new Bits(data);
        int position;
        if (isWhole && carry == 0)
        {
            HeaderResult result = ReadHeader(index, data, bits, total, media.Length - start, ref record, out position);
            if (result != HeaderResult.Read)
            {
                record.SuperframeCount = Superframes.Count - record.FirstSuperframe;
                Totals.MaxSuperframesInPacket = Math.Max(Totals.MaxSuperframesInPacket, record.SuperframeCount);
                Packets.Add(record);
                return;
            }
        }
        else
        {
            // No header: wmavoice.c skips the bits the last superframe ended inside of, and goes on at once.
            record.Flags |= VoicePacketRecord.Headerless;
            Totals.HeaderlessPackets++;
            position = carry > 0 ? carry : _skipNext;
            if (carry == 0 && position > 0)
            {
                record.Flags |= VoicePacketRecord.SkipKept;
            }

            _skipNext = 0;
            record.HeaderEnd = position;
            _cacheBits = 0;
        }

        // wmavoice.c: wmavoice_decode_packet: each call reads one superframe that lies in the packet, or caches the start of the last.
        // The count of superframes is taken off first, so a packet that counted none is passed over.
        int inPacket = 0;
        while (true)
        {
            _skipNext = 0;
            int old = _pending;
            _pending = Math.Max(old - 1, -1);
            if (old == 0)
            {
                break;
            }

            if (_pending > 0)
            {
                if (Superframes.Count >= MaxSuperframes)
                {
                    Truncated = true;
                    Limit = "superframes";
                    break;
                }

                if (Superframes.Count % 4096 == 0)
                {
                    _cancellation.ThrowIfCancellationRequested();
                }

                bits.Position = position;
                VoiceStatus status = ReadInPacket(index, data, bits, _residual);
                if (status != VoiceStatus.Ok)
                {
                    // libavcodec drops what is left of the packet it handed over, which is the media object.
                    record.Flags |= VoicePacketRecord.Failed;
                    _dropped = true;
                    _lostToMusic = status == VoiceStatus.Music;
                    break;
                }

                inPacket++;
                position = bits.Position;
                if (position >= total)
                {
                    // The superframe ended with the packet; the superframes the header counted still are not read.
                    break;
                }

                continue;
            }

            int left = total - position;
            if (left > 0)
            {
                record.TailStart = position;
                record.TailBits = left;
                _cachePacket = index;
                _cacheStart = position;
                _tailBits = left;
                _cacheBits = left;
                _cacheWritten = 0;
                _tailCopied = left <= VoiceParameters.CacheBytes * 8;
                if (_tailCopied)
                {
                    Bits.Copy(data, position, _cache, 0, left);
                    _cacheWritten = left;
                }
                else
                {
                    record.Flags |= VoicePacketRecord.TailRefused;
                    CacheOverflow(index);
                }
            }

            break;
        }

        record.SuperframeCount = Superframes.Count - record.FirstSuperframe;
        Totals.MaxSuperframesInPacket = Math.Max(Totals.MaxSuperframesInPacket, inPacket + ((record.Flags & VoicePacketRecord.CompletesPrevious) != 0 ? 1 : 0));
        Packets.Add(record);
    }

    private enum HeaderResult
    {
        // The header is read; the packet's own superframes follow from the position.
        Read,

        // A decoder rejects the header and drops the media object.
        Rejected,

        // The superframe the packet before ended in used the packet up: the block is done.
        Used,
    }

    // wmavoice.c: parse_packet_header and the first part of wmavoice_decode_packet: the header, then the bits that finish the cached
    // superframe or are skipped. Sets the position the superframes of the packet are read from.
    private HeaderResult ReadHeader(int index, byte[] data, Bits bits, int total, int objectBytes, ref VoicePacketRecord record, out int position)
    {
        VoiceParameters p = Parameters;
        position = 0;
        int sequence = (int)bits.Read(4);
        bool residual = bits.ReadBit() != 0;
        record.Sequence = (byte)sequence;
        if (residual)
        {
            record.Flags |= VoicePacketRecord.ResidualLsps;
        }

        bool changed = _residual != residual;
        _residual = residual;

        if (_haveSequence && ((_lastSequence + 1) & 0xF) != sequence)
        {
            record.Flags |= VoicePacketRecord.SequenceBreak;
            Totals.SequenceBreaks++;
            int expected = (_lastSequence + 1) & 0xF;
            Findings.Add(Severity.Info, "wma.voice-packet-sequence", () => PacketNode(index), count =>
                $"{(count == 1 ? "A packet's sequence number does not" : $"{count} packets' sequence numbers do not")} follow the packet before ({(count == 1 ? $"{sequence} for {expected}" : "the first is " + sequence + " for " + expected)}); a packet may be missing. FFmpeg's decoder does not read the number.");
        }

        _haveSequence = true;
        _lastSequence = sequence;

        // wmavoice.c: parse_packet_header: the count is a sum of 6-bit values, each 63 telling that another follows.
        int stated = 0;
        int value;
        do
        {
            if (bits.Remaining < 6 + p.SpilloverBits)
            {
                record.Flags |= VoicePacketRecord.BadHeader;
                Totals.BadHeaders++;
                _dropped = true;
                Findings.Add(Severity.Warning, "wma.voice-packet-header", () => PacketNode(index), count =>
                    $"{(count == 1 ? "A packet's header runs" : $"{count} packets' headers run")} past the packet; a decoder rejects {(count == 1 ? "it" : "them")} and drops the rest of the media object.");
                return HeaderResult.Rejected;
            }

            value = (int)bits.Read(6);
            stated += value;
        }
        while (value == 0x3F);

        int spillover = (int)bits.Read(p.SpilloverBits);
        record.StatedSuperframes = stated;
        record.StatedSpillover = spillover;
        record.HeaderEnd = bits.Position;
        position = bits.Position;
        _pending = stated;

        // wmavoice.c clears skip_bits_next only after the header was read: a header it rejects leaves the bits to skip for the next call.
        _skipNext = 0;
        bool lostToMusic = _lostToMusic;
        _lostToMusic = false;

        // The bits that finish the superframe the packet before ended in. A packet with no such superframe skips them.
        if (_cacheBits > 0)
        {
            int left = total - position;

            // wmavoice.c: the spillover is cut to the media object that is left, not to the block; a copy takes bits that lie in the
            // block and fit the cache whole, else it takes none.
            int give = Math.Min(spillover, (objectBytes * 8) - position);
            bool copied = left >= give && give <= (VoiceParameters.CacheBytes * 8) - _cacheWritten;
            if (copied)
            {
                Bits.Copy(data, position, _cache, _cacheWritten, give);
                _cacheWritten += give;
            }
            else
            {
                record.Flags |= VoicePacketRecord.SpilloverRefused;
                CacheOverflow(index);
            }

            // flush_put_bits: the last byte written is completed with zero bits.
            if ((_cacheWritten & 7) != 0)
            {
                _cache[_cacheWritten >> 3] &= (byte)(0xFF << (8 - (_cacheWritten & 7)));
            }

            _cacheBits += give;
            int shown = Math.Max(0, Math.Min(give, left));
            record.Flags |= VoicePacketRecord.CompletesPrevious;
            record.Spillover = shown;
            if (changed)
            {
                Findings.Add(Severity.Info, "wma.voice-lsp-flag-changes", () => PacketNode(index), count =>
                    $"{(count == 1 ? "A packet sets" : $"{count} packets set")} the flag for residual LSPs differently from the packet before, and the superframe that goes on into {(count == 1 ? "it is" : "them is")} read with the later flag, as FFmpeg's decoder reads it.");
            }

            VoiceStatus status = ReadCached(index, position, copied ? shown : 0, atEnd: false);
            int end = position + give;
            if (status == VoiceStatus.Ok)
            {
                position = end;
                if (position >= total)
                {
                    // The packet is used up (or more): a decoder goes on at the byte the reading ended in, which may be in the next block.
                    if (position > total)
                    {
                        EndOutsideBlock(position, total, record.Length);
                    }

                    return HeaderResult.Used;
                }
            }
            else
            {
                // wmavoice.c: the resync after a superframe that failed skips the spillover bits from a position that copy_bits has
                // already moved on by the bits that keep the rest of the copy byte aligned, and so ends that many bits late.
                position = end + (copied ? 2 * Math.Min(left & 7, give) : 0);
            }
        }
        else if (spillover > 0)
        {
            record.Flags |= VoicePacketRecord.SpilloverDropped;
            record.Spillover = Math.Max(0, Math.Min(spillover, total - position));
            Totals.SkippedSpilloverBits += spillover;
            if (lostToMusic)
            {
                Findings.Add(Severity.Info, "wma.voice-spillover-after-music", () => PacketNode(index), count =>
                    $"{(count == 1 ? "A packet states" : $"{count} packets state")} spillover bits ({(count == 1 ? spillover.ToString(CultureInfo.InvariantCulture) : "the first has " + spillover)}) of a superframe that began in the packet before, and that packet's superframes were lost to a superframe of music, which a decoder cannot read: it drops the rest of the media object, the start of this superframe with it, and skips the bits.");
            }
            else
            {
                Findings.Add(Severity.Warning, "wma.voice-spillover-orphan", () => PacketNode(index), count =>
                    $"{(count == 1 ? "A packet states" : $"{count} packets state")} spillover bits ({(count == 1 ? spillover.ToString(CultureInfo.InvariantCulture) : "the first has " + spillover)}) of a superframe that began in the packet before, and no start is left from it (the first packet, a packet that could not be read, a media object with no header, or a superframe that began exactly at its end); a decoder skips them.");
            }

            position += spillover;
        }

        return HeaderResult.Read;
    }

    // The reading of a superframe went on past the block's end: libavcodec hands the decoder what is left of the media object from the
    // byte it ended in, which is a header again only where that is the next block's first byte.
    private void EndOutsideBlock(int position, int total, int length)
    {
        int extra = (position >> 3) - length;
        _carry = extra == 0 ? 0 : position - total;
        _skipNext = position & 7;
    }

    private void CacheOverflow(int packet)
    {
        Totals.OverlongCaches++;
        Findings.Add(Severity.Info, "wma.voice-cache-overflow", () => PacketNode(packet), count =>
            $"{(count == 1 ? "A packet" : $"{count} packets")} start{(count == 1 ? "s" : string.Empty)} or finish{(count == 1 ? "es" : string.Empty)} a superframe that does not fit the {VoiceParameters.CacheBytes} bytes a decoder keeps of one that goes on in the next packet, or whose spillover bits are not in the packet; FFmpeg's decoder copies none of the bits that do not fit (wmavoice.c: copy_bits), so it reads the cache's old bytes in their place.");
    }

    // Called when the last media object is in: FFmpeg's decoder is asked for what it still holds, and reads the superframe the last
    // packet ended in from the bits of that packet alone.
    public void Finish()
    {
        if (_cacheBits > 0 && !Truncated)
        {
            if ((_cacheWritten & 7) != 0)
            {
                _cache[_cacheWritten >> 3] &= (byte)(0xFF << (8 - (_cacheWritten & 7)));
            }

            long before = Totals.TailBitsAtEnd;
            ReadCached(Packets.Count - 1, 0, 0, atEnd: true);
            _cacheBits = 0;

            // A start the cache refused was not read from the file: its bits are bits no superframe took, whatever the reading made of
            // the cache's old bytes (and what a failed reading added is replaced by them).
            if (!_tailCopied)
            {
                Totals.TailBitsAtEnd = before + _tailBits;
            }
        }
    }

    // A superframe that lies in the packet, from the reader's position.
    private VoiceStatus ReadInPacket(int packet, byte[] data, Bits bits, bool residual)
    {
        int start = bits.Position;
        var record = new VoiceSuperframeRecord { Packet = packet, AvailableBits = Math.Max(0, (data.Length * 8) - start) };
        Span<VoiceFrameRecord> frames = stackalloc VoiceFrameRecord[VoiceParameters.FramesPerSuperframe];
        VoiceStatus status = _decoder.Read(bits, residual, ref _state, ref record, frames);
        int used = Math.Max(0, Math.Min(record.Bits, record.AvailableBits));
        Add(ref record, frames, status, [new FramePart(packet, Math.Min(start, data.Length * 8), used)], leftover: 0, atEnd: false);
        return status;
    }

    // The superframe begun in an earlier packet: its bits are the cache, the tail that packet ended in and the spillover bits of this
    // one. A decoder reads the cache whatever it holds, and so does this.
    private VoiceStatus ReadCached(int packet, int spilloverStart, int shown, bool atEnd)
    {
        int total = _cacheBits;
        _cacheBits = 0;
        byte[] joined = new byte[(total + 7) / 8];
        Array.Copy(_cache, joined, Math.Min(_cache.Length, joined.Length));

        var bits = new Bits(joined, total);
        var record = new VoiceSuperframeRecord { Packet = packet, AvailableBits = total, Flags = VoiceSuperframeRecord.StartsEarlier };
        if (atEnd)
        {
            record.Flags |= VoiceSuperframeRecord.AtEnd;
        }

        Span<VoiceFrameRecord> frames = stackalloc VoiceFrameRecord[VoiceParameters.FramesPerSuperframe];
        VoiceStatus status = _decoder.Read(bits, _residual, ref _state, ref record, frames);
        int used = Math.Max(0, Math.Min(record.Bits, total));

        // Only the bits that were copied into the cache come from the file: the start of the superframe at the cache's first bit if it
        // was copied, and the spillover bits after it if they were. Any other bit the reading used is one the cache held before.
        var parts = new List<FramePart>(2);
        int fromTail = _tailCopied ? Math.Min(used, _tailBits) : 0;
        if (fromTail > 0)
        {
            parts.Add(new FramePart(_cachePacket, _cacheStart, fromTail));
        }

        int spillAt = _tailCopied ? _tailBits : 0;
        int fromSpill = shown > 0 && used > spillAt && (fromTail == spillAt || !_tailCopied) ? Math.Min(used - spillAt, shown) : 0;
        if (fromSpill > 0)
        {
            parts.Add(new FramePart(packet, spilloverStart, fromSpill));
        }

        if (parts.Count == 0)
        {
            // Where the superframe's reading began: in its own packet if the start was copied or the stream ends, else in the spillover.
            parts.Add(atEnd || _tailCopied ? new FramePart(_cachePacket, _cacheStart, 0) : new FramePart(packet, spilloverStart, 0));
        }

        int fromFile = fromTail + fromSpill;
        if (used > fromFile)
        {
            record.Flags |= VoiceSuperframeRecord.ReadsCache;
        }

        Add(ref record, frames, status, [.. parts], leftover: status == VoiceStatus.Ok && used == fromFile ? Math.Max(0, (_tailCopied ? _tailBits : 0) + shown - record.Bits) : 0, atEnd);
        return status;
    }

    // Keeps a superframe's records and counts it.
    private void Add(ref VoiceSuperframeRecord record, ReadOnlySpan<VoiceFrameRecord> frames, VoiceStatus status, FramePart[] parts, int leftover, bool atEnd)
    {
        int index = Superframes.Count;
        record.Status = (byte)status;
        record.FirstPart = Parts.Count;
        record.PartCount = (byte)parts.Length;
        foreach (FramePart part in parts)
        {
            Parts.Add(part);
        }

        record.FirstFrame = Frames.Count;
        for (int i = 0; i < record.FrameCount; i++)
        {
            Frames.Add(frames[i]);
        }

        if (record.Has(VoiceSuperframeRecord.StartsEarlier))
        {
            Records.TailOwners[_cachePacket] = index;
        }

        Superframes.Add(record);
        if (status != VoiceStatus.Ok)
        {
            Totals.FailedSuperframes++;
            ReportFailure(index, status, record, atEnd);
            return;
        }

        Totals.Superframes++;
        Totals.Frames += record.FrameCount;
        for (int i = 0; i < record.FrameCount; i++)
        {
            Totals.FramesByType[frames[i].Type]++;
        }

        Totals.Samples += record.Samples;
        Totals.SuperframeBits += record.Bits;
        Totals.MinSuperframeBits = Math.Min(Totals.MinSuperframeBits, record.Bits);
        Totals.MaxSuperframeBits = Math.Max(Totals.MaxSuperframeBits, record.Bits);
        if (record.Has(VoiceSuperframeRecord.HasSampleCount))
        {
            Totals.SuperframesWithSampleCount++;
        }

        if (record.Has(VoiceSuperframeRecord.ResidualLsps))
        {
            Totals.ResidualSuperframes++;
        }

        if (record.Has(VoiceSuperframeRecord.HasAdditionalData))
        {
            Totals.AdditionalDataSuperframes++;
            Totals.AdditionalDataBits += record.ExtraBits + 5;
        }

        if (record.Has(VoiceSuperframeRecord.StartsEarlier) && !atEnd)
        {
            Totals.StraddlingSuperframes++;
        }

        if (leftover > 0)
        {
            if (atEnd)
            {
                Totals.TailBitsAtEnd += leftover;
            }
            else
            {
                Totals.IgnoredBits += leftover;
                Findings.Add(Severity.Info, "wma.voice-superframe-padding", () => SuperframeNode(index), count =>
                    $"{(count == 1 ? "A superframe ends" : $"{count} superframes end")} before the bits the packets give {(count == 1 ? "it" : "them")} ({(count == 1 ? leftover.ToString(CultureInfo.InvariantCulture) + " bits" : "the first leaves " + leftover + " bits")}); a decoder ignores them and goes on at the position the next packet's header states.");
            }
        }
    }

    private void ReportFailure(int index, VoiceStatus status, VoiceSuperframeRecord record, bool atEnd)
    {
        int bits = record.Bits;
        int available = Math.Max(0, record.AvailableBits);

        // A superframe that lies in a packet and fails drops the rest of its media object; one finished from the packet before leaves
        // the packet's own superframes to be read, and one read at the end of the stream has nothing after it.
        bool cached = record.Has(VoiceSuperframeRecord.StartsEarlier);
        string After(int count) => count != 1
            ? "each, and with one that lies in a packet the rest of its media object"
            : "it" + (atEnd ? string.Empty : cached ? " and goes on with the packet's own superframes" : " and the rest of its media object");
        switch (status)
        {
            case VoiceStatus.Music:
                Totals.MusicSuperframes++;
                Findings.Add(Severity.Info, "wma.voice-music", () => SuperframeNode(index), count =>
                    $"{(count == 1 ? "A superframe's" : $"{count} superframes'")} first bit is clear: {(count == 1 ? "it holds" : "they hold")} music, Windows Media Audio Professional data inside Voice superframes, which FFmpeg's decoder does not support (wmavoice.c) and this analysis does not read; a decoder drops {After(count)}.");
                break;
            case VoiceStatus.SampleCount:
                Findings.Add(Severity.Warning, "wma.voice-sample-count", () => SuperframeNode(index), count =>
                    $"{(count == 1 ? "A superframe states" : $"{count} superframes state")} more samples than the 480 it holds; a decoder drops {After(count)}.");
                break;
            case VoiceStatus.FrameType:
                Findings.Add(Severity.Warning, "wma.voice-frame-type", () => SuperframeNode(index), count =>
                    $"{(count == 1 ? "A frame's type code is" : $"{count} frames' type codes are")} one that the frame type table gives no type, so {(count == 1 ? "the superframe fails" : "the superframes fail")}; a decoder drops {After(count)}.");
                break;
            default:
                if (atEnd)
                {
                    Totals.UnfinishedAtEnd++;
                    Totals.TailBitsAtEnd += available;
                    Findings.Add(Severity.Info, "wma.voice-unfinished", () => SuperframeNode(index), _ =>
                        $"The last packet ends with {Counts.Of(available, "bit", "bits")} that begin a superframe needing {bits} or more, and no packet follows to finish it; a decoder drops them.");
                }
                else
                {
                    Findings.Add(Severity.Warning, "wma.voice-superframe-overrun", () => SuperframeNode(index), count =>
                        $"{(count == 1 ? "A superframe needs" : $"{count} superframes need")} more bits than the packets give {(count == 1 ? "it" : "them")} ({(count == 1 ? $"{bits} for {available}" : "the first needs " + bits + " for " + available)}); a decoder drops {After(count)}.");
                }

                break;
        }
    }
}
