using Encspect.Core;

namespace Encspect.Formats.Wma;

// Walks a stream's packets and reads the superframes in them, as FFmpeg's wmavoice_decode_packet does. A packet is a block of the
// block alignment's size. Its header holds a sequence number, the flag for residual LSPs, the count of superframes that begin in
// it, and the bits of the superframe before that continue in it (the spillover). Those bits come first; then whole superframes
// follow one another bit by bit; the last of the counted superframes goes on to the end of the packet and is finished by the next
// packet's spillover bits.
internal sealed class VoiceStream
{
    private readonly VoiceDecoder _decoder;
    private VoiceState _state = new();

    // The bits of the superframe that the last packet ends in, to be finished by the next one's spillover.
    private byte[] _cache = [];
    private int _cacheBits;
    private int _cachePacket;
    private int _cacheStart;

    private readonly CancellationToken _cancellation;
    private bool _haveSequence;
    private int _lastSequence;
    private bool _lastResidual;
    private bool _havePacket;

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

    // Superframes are kept in records up to this count (about 65 MB), so a crafted file of packets that hold nothing but tiny superframes
    // stays bounded; after it the stream is not read, and Truncated says so.
    internal const int MaxSuperframes = 1 << 19;

    public bool Truncated { get; private set; }

    // A media object of the container is one or more blocks of the block alignment's size: FFmpeg's decoder cuts what an ASF packet
    // holds into blocks of that size (wmavoice.c: wmavoice_decode_packet).
    public void Add(MappedBytes media)
    {
        if (Truncated)
        {
            return;
        }

        int align = Parameters.BlockAlign;
        if (media.Length < align)
        {
            AddShort(media);
            return;
        }

        if (media.Length % align != 0)
        {
            int index = Packets.Count;
            int excess = media.Length % align;
            Findings.Add(Severity.Info, "wma.packet-long", () => PacketNode(Math.Min(index, Packets.Count - 1)), count =>
                $"{(count == 1 ? "A media object holds" : $"{count} media objects hold")} bytes after its last whole block of {align}, {(count == 1 ? $"{excess} of them" : "the first with " + excess)}; they are not read.");
        }

        for (int offset = 0; offset + align <= media.Length; offset += align)
        {
            AddBlock(media, offset);
        }
    }

    private void AddShort(MappedBytes media)
    {
        int index = Packets.Count;
        int align = Parameters.BlockAlign;
        var record = new VoicePacketRecord
        {
            Offset = media.FileStart,
            Length = media.Length,
            RangeIndex = -1,
            FirstSuperframe = Superframes.Count,
            Flags = VoicePacketRecord.Short,
        };
        IReadOnlyList<ByteRange> ranges = media.FileRanges();
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
        Totals.ShortPackets++;
        Packets.Add(record);
        Findings.Add(Severity.Warning, "wma.packet-short", () => PacketNode(index), count =>
            $"{(count == 1 ? "A media object holds" : $"{count} media objects hold")} fewer bytes than the block alignment of {align}; a decoder takes it for the continuation of the packet before and reads no superframe in it (wmavoice.c), and this analysis does not read it.");
    }

    private void AddBlock(MappedBytes media, int offset)
    {
        VoiceParameters p = Parameters;
        int index = Packets.Count;
        int align = p.BlockAlign;
        int packetBits = align * 8;
        var record = new VoicePacketRecord
        {
            Offset = media.OffsetOf(offset),
            Length = align,
            RangeIndex = -1,
            FirstSuperframe = Superframes.Count,
            TailStart = packetBits,
        };

        IReadOnlyList<ByteRange> ranges = media.FileRanges(offset, offset + align);
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
        Totals.UsedBytes += align;
        byte[] data = media.Span.Slice(offset, align).ToArray();
        var bits = new Bits(data);
        int sequence = (int)bits.Read(4);
        bool residual = bits.ReadBit() != 0;
        record.Sequence = (byte)sequence;
        if (residual)
        {
            record.Flags |= VoicePacketRecord.ResidualLsps;
        }

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
                Packets.Add(record);
                Findings.Add(Severity.Warning, "wma.voice-packet-header", () => PacketNode(index), count =>
                    $"{(count == 1 ? "A packet's header runs" : $"{count} packets' headers run")} past the packet; a decoder rejects it and reads nothing of the packet.");
                return;
            }

            value = (int)bits.Read(6);
            stated += value;
        }
        while (value == 0x3F);

        int spillover = (int)bits.Read(p.SpilloverBits);
        record.StatedSuperframes = stated;
        record.StatedSpillover = spillover;
        record.HeaderEnd = bits.Position;
        int position = bits.Position;

        // The bits that finish the superframe the packet before ended in. A packet with no such superframe skips them.
        if (_cacheBits > 0)
        {
            int available = Math.Min(spillover, packetBits - position);
            record.Flags |= VoicePacketRecord.CompletesPrevious;
            record.Spillover = available;
            if (_lastResidual != residual)
            {
                Findings.Add(Severity.Info, "wma.voice-lsp-flag-changes", () => PacketNode(index), count =>
                    $"{(count == 1 ? "A packet sets" : $"{count} packets set")} the flag for residual LSPs differently from the packet before, and the superframe that goes on into {(count == 1 ? "it is" : "them is")} read with the later flag, as FFmpeg's decoder reads it.");
            }

            VoiceStatus status = ReadCached(index, data, position, available, residual, atEnd: false);
            position += available;
            if (status != VoiceStatus.Ok)
            {
                // wmavoice.c: wmavoice_decode_packet: the resync after a superframe that failed skips the spillover bits from a position
                // that copy_bits has already moved on by the bits that keep the rest of the copy byte aligned, and so ends that many bits late.
                position += 2 * Math.Min((packetBits - record.HeaderEnd) & 7, available);
            }
        }
        else if (spillover > 0)
        {
            record.Flags |= VoicePacketRecord.SpilloverDropped;
            record.Spillover = Math.Max(0, Math.Min(spillover, packetBits - position));
            Totals.SkippedSpilloverBits += spillover;
            Findings.Add(Severity.Warning, "wma.voice-spillover-orphan", () => PacketNode(index), count =>
                $"{(count == 1 ? "A packet states" : $"{count} packets state")} spillover bits ({(count == 1 ? spillover.ToString(System.Globalization.CultureInfo.InvariantCulture) : "the first has " + spillover)}) of a superframe that began in the packet before, and no start is left from it (the first packet, a packet that could not be read, or a superframe that began exactly at its end); a decoder skips them.");
            position += spillover;
        }

        _cacheBits = 0;
        _lastResidual = residual;
        _havePacket = true;
        bits.Position = position;
        bool failed = false;
        int inPacket = 0;
        for (int k = 0; k < stated; k++)
        {
            if (k % 4096 == 0)
            {
                _cancellation.ThrowIfCancellationRequested();
            }

            if (Superframes.Count >= MaxSuperframes)
            {
                Truncated = true;
                break;
            }

            if (k < stated - 1)
            {
                if (ReadInPacket(index, data, bits, residual) != VoiceStatus.Ok)
                {
                    failed = true;
                    break;
                }

                inPacket++;
            }
            else
            {
                int left = packetBits - bits.Position;
                if (left > 0)
                {
                    record.TailStart = bits.Position;
                    record.TailBits = left;
                    _cache = new byte[(left + 7) / 8];
                    Bits.Copy(data, bits.Position, _cache, 0, left);
                    _cacheBits = left;
                    _cachePacket = index;
                    _cacheStart = bits.Position;
                    if (left > VoiceParameters.CacheBytes * 8)
                    {
                        Totals.OverlongCaches++;
                        Findings.Add(Severity.Info, "wma.voice-cache-overflow", () => PacketNode(index), count =>
                            $"{(count == 1 ? "A packet ends" : $"{count} packets end")} with more than {VoiceParameters.CacheBytes} bytes of a superframe that goes on in the next packet; FFmpeg's decoder keeps {VoiceParameters.CacheBytes} bytes of it and copies nothing for more (wmavoice.c: copy_bits), so it reads stale cache bytes there.");
                    }
                }
            }
        }

        if (failed)
        {
            record.Flags |= VoicePacketRecord.Failed;
        }

        record.SuperframeCount = Superframes.Count - record.FirstSuperframe;
        Totals.MaxSuperframesInPacket = Math.Max(Totals.MaxSuperframesInPacket, inPacket + ((record.Flags & VoicePacketRecord.CompletesPrevious) != 0 ? 1 : 0));
        Packets.Add(record);
    }

    // Called when the last packet is in: FFmpeg's decoder is asked for what it still holds, and reads the superframe the last
    // packet ended in from the bits of that packet alone.
    public void Finish()
    {
        if (_cacheBits > 0 && _havePacket && !Truncated)
        {
            ReadCached(Packets.Count - 1, [], 0, 0, _lastResidual, atEnd: true);
            _cacheBits = 0;
        }
    }

    // A superframe that lies in the packet, from the reader's position.
    private VoiceStatus ReadInPacket(int packet, byte[] data, Bits bits, bool residual)
    {
        int start = bits.Position;
        var record = new VoiceSuperframeRecord { Packet = packet, AvailableBits = (data.Length * 8) - start };
        Span<VoiceFrameRecord> frames = stackalloc VoiceFrameRecord[VoiceParameters.FramesPerSuperframe];
        VoiceStatus status = _decoder.Read(bits, residual, ref _state, ref record, frames);
        int used = Math.Min(record.Bits, record.AvailableBits);
        Add(ref record, frames, status, [new FramePart(packet, start, used)], leftover: 0, atEnd: false);
        return status;
    }

    // The superframe begun in an earlier packet: its bits are the tail that packet ended in and the spillover bits of this one.
    private VoiceStatus ReadCached(int packet, byte[] data, int spilloverStart, int spillover, bool residual, bool atEnd)
    {
        int total = _cacheBits + spillover;
        byte[] joined = new byte[(total + 7) / 8];
        Array.Copy(_cache, joined, Math.Min(_cache.Length, joined.Length));
        if (spillover > 0)
        {
            Bits.Copy(data, spilloverStart, joined, _cacheBits, spillover);
        }

        var bits = new Bits(joined, total);
        var record = new VoiceSuperframeRecord { Packet = packet, AvailableBits = total, Flags = VoiceSuperframeRecord.StartsEarlier };
        if (atEnd)
        {
            record.Flags |= VoiceSuperframeRecord.AtEnd;
        }

        Span<VoiceFrameRecord> frames = stackalloc VoiceFrameRecord[VoiceParameters.FramesPerSuperframe];
        VoiceStatus status = _decoder.Read(bits, residual, ref _state, ref record, frames);
        int used = Math.Min(record.Bits, total);
        FramePart[] parts = used <= _cacheBits
            ? [new FramePart(_cachePacket, _cacheStart, used)]
            : [new FramePart(_cachePacket, _cacheStart, _cacheBits), new FramePart(packet, spilloverStart, used - _cacheBits)];
        Add(ref record, frames, status, parts, leftover: status == VoiceStatus.Ok ? total - record.Bits : 0, atEnd);
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
                    $"{(count == 1 ? "A superframe ends" : $"{count} superframes end")} before the bits the packets give {(count == 1 ? "it" : "them")} ({(count == 1 ? leftover.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bits" : "the first leaves " + leftover + " bits")}); a decoder ignores them and goes on at the position the next packet's header states.");
            }
        }
    }

    private void ReportFailure(int index, VoiceStatus status, VoiceSuperframeRecord record, bool atEnd)
    {
        int bits = record.Bits;
        int available = record.AvailableBits;

        // A superframe in a packet that fails ends the packet for a decoder; one finished from the packet before leaves the packet's own.
        bool cached = record.Has(VoiceSuperframeRecord.StartsEarlier);
        string After(int count) => (count == 1 ? "it" : "them") + (cached ? " and goes on with the packet's own superframes" : " and the rest of the packet");
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
                        $"The last packet ends with {available} bits that begin a superframe needing {bits} or more, and no packet follows to finish it; a decoder drops them.");
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
