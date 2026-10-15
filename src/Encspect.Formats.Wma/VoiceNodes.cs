using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Builds the nodes of the packets, superframes and frames of a Voice stream from its records, when they are read.
internal sealed class VoiceNodes
{
    private readonly VoiceRecords _records;
    private readonly PartsMap _map;

    public VoiceNodes(VoiceRecords records)
    {
        _records = records;
        _map = new PartsMap(Packet);
    }

    private VoiceParameters P => _records.Parameters;

    private MappedBytes Packet(int index)
    {
        VoicePacketRecord record = _records.Packets[index];
        IReadOnlyList<ByteRange> ranges = record.RangeIndex >= 0 ? _records.Ranges[record.RangeIndex] : [new ByteRange(record.Offset, record.Length)];
        return PartsMap.Mapped(record.Length, ranges, record.Offset);
    }

    // The whole packet: its header, the end of the superframe that began before it, the superframes that lie in it and the start of
    // the one that goes on.
    public Node PacketNode(int index)
    {
        VoicePacketRecord record = _records.Packets[index];
        IReadOnlyList<ByteRange> ranges = record.RangeIndex >= 0 ? _records.Ranges[record.RangeIndex] : [new ByteRange(record.Offset, record.Length)];
        long end = ranges.Count > 0 ? ranges[^1].End : record.Offset + record.Length;
        string value = record.Has(VoicePacketRecord.Short) ? "not read"
            : record.Has(VoicePacketRecord.BadHeader) ? "invalid header"
            : Count(StartedIn(index).Count, "superframe", "superframes");
        return new Node("Packet", record.Offset, end - record.Offset, value, () => PacketChildren(index))
        {
            Kind = NodeKind.Frame,
            Ranges = ranges.Count > 1 ? ranges : null,
            Description = VoiceExplain.Packet,
            Topic = Topics.WmaVoiceSuperframes,
        };
    }

    // The superframes that begin in a packet: those it holds whole, and the one its tail begins.
    private List<int> StartedIn(int packet)
    {
        VoicePacketRecord record = _records.Packets[packet];
        var started = new List<int>();
        for (int i = record.FirstSuperframe; i < record.FirstSuperframe + record.SuperframeCount; i++)
        {
            if (!_records.Superframes[i].Has(VoiceSuperframeRecord.StartsEarlier))
            {
                started.Add(i);
            }
        }

        if (_records.TailOwners.TryGetValue(packet, out int owner))
        {
            started.Add(owner);
        }

        return started;
    }

    private List<Node> PacketChildren(int index)
    {
        VoicePacketRecord record = _records.Packets[index];
        var nodes = new List<Node>();
        int total = record.Length * 8;
        FramePart[] whole = [new FramePart(index, 0, total)];
        int covered = 0;
        void Add(int start, int end, string name, NodeValue? value, string description, string? topic = null)
        {
            nodes.Add(_map.Field(whole, Math.Min(start, total), Math.Min(end, total), name, value, description, topic));
            covered = Math.Max(covered, Math.Min(end, total));
        }

        if (record.Has(VoicePacketRecord.Short))
        {
            return nodes;
        }

        Add(0, 4, "Packet Sequence Number", NodeValue.Integer(record.Sequence), VoiceExplain.PacketSequence);
        Add(4, 5, "Has Residual LSPs", NodeValue.Boolean(record.Has(VoicePacketRecord.ResidualLsps)), VoiceExplain.PacketResidual, Topics.WmaVoiceFrames);
        if (record.Has(VoicePacketRecord.BadHeader))
        {
            Unread();
            return nodes;
        }

        int groups = (record.StatedSuperframes / 63) + 1;
        Add(5, 5 + (6 * groups), "Superframes in Packet", NodeValue.Integer(record.StatedSuperframes), VoiceExplain.PacketSuperframes, Topics.WmaVoiceSuperframes);
        Add(5 + (6 * groups), record.HeaderEnd, "Spillover Bits", NodeValue.Integer(record.StatedSpillover, "bits"), VoiceExplain.PacketSpillover, Topics.WmaVoiceSuperframes);
        int at = record.HeaderEnd;
        if (record.Has(VoicePacketRecord.CompletesPrevious))
        {
            VoiceSuperframeRecord first = _records.Superframes[record.FirstSuperframe];
            int used = first.PartCount > 1 ? _records.Parts[first.FirstPart + 1].BitCount : 0;
            if (used > 0)
            {
                Add(at, at + used, "Superframe End", NodeValue.Integer(used, "bits"), VoiceExplain.PacketSuperframeEnd, Topics.WmaVoiceSuperframes);
            }

            if (record.Spillover > used)
            {
                Add(at + used, at + record.Spillover, "Padding", NodeValue.Integer(record.Spillover - used, "bits"), VoiceExplain.PacketPadding);
            }
        }
        else if (record.Has(VoicePacketRecord.SpilloverDropped) && record.Spillover > 0)
        {
            Add(at, at + record.Spillover, "Skipped Spillover", NodeValue.Integer(record.Spillover, "bits"), VoiceExplain.PacketSkippedSpillover, Topics.WmaVoiceSuperframes);
        }

        for (int i = record.FirstSuperframe; i < record.FirstSuperframe + record.SuperframeCount; i++)
        {
            VoiceSuperframeRecord superframe = _records.Superframes[i];
            if (superframe.Has(VoiceSuperframeRecord.StartsEarlier))
            {
                continue;
            }

            FramePart part = _records.Parts[superframe.FirstPart];
            string value = superframe.Status == 0 ? Count(superframe.FrameCount, "frame", "frames") : "invalid";
            Add(part.StartBit, part.StartBit + part.BitCount, "Superframe Data", value, VoiceExplain.PacketSuperframe, Topics.WmaVoiceSuperframes);
        }

        if (record.TailBits > 0)
        {
            int used = record.TailBits;
            bool finished = false;
            string name = "Unfinished Superframe";
            if (_records.TailOwners.TryGetValue(index, out int owner))
            {
                VoiceSuperframeRecord superframe = _records.Superframes[owner];
                used = Math.Min(record.TailBits, _records.Parts[superframe.FirstPart].BitCount);
                finished = superframe.Status == 0 || superframe.PartCount > 1;
                name = finished ? "Superframe Start" : "Unfinished Superframe";
            }

            Add(record.TailStart, record.TailStart + used, name, NodeValue.Integer(used, "bits"), finished ? VoiceExplain.PacketSuperframeStart : VoiceExplain.PacketUnfinished, Topics.WmaVoiceSuperframes);
            if (used < record.TailBits)
            {
                Add(record.TailStart + used, record.TailStart + record.TailBits, "Padding", NodeValue.Integer(record.TailBits - used, "bits"), VoiceExplain.PacketPadding);
            }
        }

        Unread();
        return nodes;

        // What the reading did not take: after a superframe that failed, or in a packet whose count states no more superframes.
        void Unread()
        {
            if (covered < total)
            {
                Add(covered, total, "Unread Bits", NodeValue.Integer(total - covered, "bits"), VoiceExplain.PacketUnread);
            }
        }
    }

    // A superframe, with its header and frames. It may begin in one packet and end in the next, and then lies in the ranges of both.
    public Node SuperframeNode(int index)
    {
        VoiceSuperframeRecord record = _records.Superframes[index];
        List<FramePart> parts = PartsOf(record);
        int used = Math.Min(record.Bits, record.AvailableBits);
        (long offset, long length, IReadOnlyList<ByteRange>? ranges) = _map.Cover(parts, 0, used);
        string value = record.Status == 0
            ? $"{Count(record.FrameCount, "frame", "frames")}, {Count(record.Samples, "sample", "samples")}"
            : $"invalid: {StatusText((VoiceStatus)record.Status)}";
        return new Node("Superframe", offset, length, value, () => SuperframeChildren(index, parts))
        {
            Kind = NodeKind.Frame,
            Ranges = ranges,
            Description = VoiceExplain.Superframe,
            Topic = Topics.WmaVoiceSuperframes,
        };
    }

    // The file offset of the first byte of a superframe.
    public long SuperframeOffset(int index)
    {
        VoiceSuperframeRecord record = _records.Superframes[index];
        return _map.Cover(PartsOf(record), 0, Math.Max(1, Math.Min(record.Bits, record.AvailableBits))).Offset;
    }

    private List<FramePart> PartsOf(VoiceSuperframeRecord record)
    {
        var parts = new List<FramePart>(record.PartCount);
        for (int i = 0; i < record.PartCount; i++)
        {
            parts.Add(_records.Parts[record.FirstPart + i]);
        }

        return parts;
    }

    internal static string StatusText(VoiceStatus status) => status switch
    {
        VoiceStatus.Music => "music",
        VoiceStatus.SampleCount => "more than 480 samples",
        VoiceStatus.FrameType => "a frame type code with no type",
        VoiceStatus.Overrun => "it needs more bits than it has",
        _ => "an error",
    };

    private List<Node> SuperframeChildren(int index, List<FramePart> parts)
    {
        VoiceSuperframeRecord record = _records.Superframes[index];
        VoiceParameters p = P;
        var nodes = new List<Node>();
        Node Field(int start, int end, string name, NodeValue? value, string description, string? topic = null) =>
            _map.Field(parts, start, end, name, value, description, topic);

        bool music = record.Status == (byte)VoiceStatus.Music;
        nodes.Add(Field(0, 1, "Speech Flag", NodeValue.Boolean(!music), VoiceExplain.SpeechFlag, Topics.WmaVoice));
        if (music)
        {
            return nodes;
        }

        bool hasCount = record.Has(VoiceSuperframeRecord.HasSampleCount);
        nodes.Add(Field(1, 2, "Has Sample Count", NodeValue.Boolean(hasCount), VoiceExplain.HasSampleCount));
        int at = 2;
        if (hasCount)
        {
            nodes.Add(Field(2, 14, "Sample Count", NodeValue.Integer(record.Samples, "samples"), VoiceExplain.SampleCount));
            at = 14;
        }

        if (record.Status == (byte)VoiceStatus.SampleCount)
        {
            return nodes;
        }

        bool residual = record.Has(VoiceSuperframeRecord.ResidualLsps);
        if (residual)
        {
            nodes.Add(Field(at, at + p.ResidualLspBits, "LSPs", NodeValue.Text($"{p.LspCount} LSPs for the three frames"), VoiceExplain.SuperframeLsps, Topics.WmaVoiceFrames));
        }

        int last = Math.Max(at, record.HeaderBits);
        for (int i = 0; i < record.FrameCount; i++)
        {
            VoiceFrameRecord frame = _records.Frames[record.FirstFrame + i];
            nodes.Add(FrameNode(parts, frame, residual));
            last = frame.EndBit;
        }

        if (record.Status == 0)
        {
            int end = record.FramesEnd;
            bool extra = record.Has(VoiceSuperframeRecord.HasAdditionalData);
            nodes.Add(Field(end, record.Bits, "Additional Data", extra ? NodeValue.Integer(record.ExtraBits, "bits") : NodeValue.Text("none"), VoiceExplain.AdditionalData));
        }
        else if (record.Bits > last)
        {
            nodes.Add(Field(last, record.Bits, "Unread Bits", NodeValue.Integer(record.Bits - last, "bits"), VoiceExplain.UnreadBits));
        }

        return nodes;
    }

    private Node FrameNode(List<FramePart> parts, VoiceFrameRecord frame, bool residual)
    {
        VoiceParameters p = P;
        byte[] descriptor = VoiceTables.FrameDescriptors[frame.Type];
        Node node = _map.Field(parts, frame.StartBit, frame.EndBit, "Frame", NodeValue.Text($"Type {frame.Type}: {VoiceNames.Frame(frame.Type)}"), VoiceExplain.Frame, Topics.WmaVoiceFrames);

        Node Field(int start, int end, string name, NodeValue? value, string description, string? topic = null) =>
            _map.Field(parts, start, Math.Max(start, end), name, value, description, topic);

        int at = frame.StartBit;
        if (!residual)
        {
            node.Add(Field(at, at + p.IndependentLspBits, "LSPs", NodeValue.Text($"{p.LspCount} LSPs"), VoiceExplain.FrameLsps, Topics.WmaVoiceFrames));
            at += p.IndependentLspBits;
        }

        node.Add(Field(at, at + frame.TypeBits, "Frame Type", NodeValue.Integer(frame.Type), VoiceExplain.FrameType, Topics.WmaVoiceFrames));
        at += frame.TypeBits;
        if (descriptor[2] == 1)
        {
            node.Add(Field(at, at + p.PitchBits, "Pitch", NodeValue.Integer(frame.Pitch, "samples"), VoiceExplain.FramePitch, Topics.WmaVoiceFrames));
            at += p.PitchBits;
        }

        if (descriptor[3] == 0)
        {
            node.Add(Field(at, at + 8, "Silence Gain", NodeValue.Integer(frame.Parameter), VoiceExplain.SilenceGain, Topics.WmaVoiceFrames));
            at += 8;
        }
        else if (descriptor[3] == 2)
        {
            int width = frame.Has(VoiceFrameRecord.WindowExtended) ? 8 : 6;
            node.Add(Field(at, at + width, "Window Index", NodeValue.Integer(frame.Parameter), VoiceExplain.WindowIndex, Topics.WmaVoiceFrames));
            at += width;
        }

        int blocks = descriptor[0];
        Node blockList = Field(at, frame.EndBit, "Blocks", NodeValue.Integer(blocks, "blocks", "block"), VoiceExplain.Blocks, Topics.WmaVoiceFrames);
        for (int n = 0; n < blocks; n++)
        {
            int width = BlockBits(frame, descriptor, n);
            blockList.Add(Field(at, Math.Min(at + width, frame.EndBit), $"Block {n + 1}", NodeValue.Integer(width, "bits"), VoiceExplain.Block, Topics.WmaVoiceFrames));
            at += width;
        }

        node.Add(blockList);
        return node;
    }

    // The bits of a block, as VoiceDecoder reads them.
    private int BlockBits(VoiceFrameRecord frame, byte[] descriptor, int block)
    {
        VoiceParameters p = P;
        int bits = 0;
        if (descriptor[2] == 2)
        {
            bits += block == 0 ? p.BlockPitchBits : p.BlockDeltaPitchBits;
        }

        if (descriptor[2] == 0)
        {
            return bits + (descriptor[3] == 1 ? 8 + 6 : 0);
        }

        if (descriptor[3] == 2)
        {
            bits += 12 - (frame.Has(VoiceFrameRecord.WindowExtended) && block == 0 ? 2 : 0);
            bits += frame.Has(VoiceFrameRecord.WindowPulsesInFirstBlock) ? 5 - (2 * block) : 4;
            bits += 1;
        }
        else
        {
            int positionBits = 5 - descriptor[1];
            bits += 5 + (5 * positionBits) + (descriptor[4] * positionBits);
        }

        return bits + 7;
    }

    internal static string Count(long count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}
