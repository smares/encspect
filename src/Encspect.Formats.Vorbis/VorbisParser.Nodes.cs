using Encspect.Core;

namespace Encspect.Formats.Vorbis;

public sealed partial class VorbisParser
{
    // An audio packet's node, whose fields are built from its record when first read.
    private static Node PacketNode(Records records, int index)
    {
        PacketRecord packet = records.Packets[index];
        records.Ranges.TryGetValue(index, out ByteRange[]? ranges);
        long length = records.IsOpaque(index) ? records.SpanOf(index) : ranges is null ? packet.Length : ranges[^1].End - ranges[0].Offset;
        return new Node("Packet", records.OffsetOf(index), length, Summary(records, packet), () => PacketFields(records, index))
        {
            Kind = NodeKind.Frame,
            Ranges = ranges,
            Description = Explain.Packet,
        };
    }

    private static string Summary(Records records, PacketRecord packet)
    {
        string bytes = packet.Length == 1 ? "1 byte" : $"{packet.Length} bytes";
        return (packet.Flags & PacketFlags.NoBlock) switch
        {
            PacketFlags.Empty => "empty",
            PacketFlags.NotAudio => bytes + ", not audio",
            PacketFlags.BadMode => bytes + ", mode not readable",
            PacketFlags.Unread => bytes,
            _ => $"{bytes}, {(packet.IsLong ? "long" : "short")} block of {records.BlockSize(packet)}, mode {packet.Mode}",
        };
    }

    // The fields section 4.3.1 reads before the floors: the packet type, the mode and, in a long
    // block, the window flags; then the rest, which this analyzer does not decode.
    private static List<Node> PacketFields(Records records, int index)
    {
        PacketRecord packet = records.Packets[index];
        var nodes = new List<Node>();
        if (packet.Length == 0)
        {
            return nodes;
        }

        records.Ranges.TryGetValue(index, out ByteRange[]? ranges);
        var at = new BitPlace(records.OffsetOf(index), records.IsOpaque(index)
            ? MappedBytes.Opaque(new byte[packet.Length], records.OffsetOf(index), records.OffsetOf(index) + records.SpanOf(index))
            : ranges is null ? null : new MappedBytes(new byte[packet.Length], ranges));
        bool notAudio = (packet.Flags & PacketFlags.NotAudio) != 0;
        nodes.Add(at.Node("Packet Type", 0, 1, notAudio ? "1 (not audio)" : "0 (audio)", Explain.AudioPacketType));
        long position = 1;
        if ((packet.Flags & (PacketFlags.NotAudio | PacketFlags.Unread)) == 0 && records.ModeBits > 0)
        {
            long end = Math.Min(position + records.ModeBits, packet.Length * 8L);
            nodes.Add(at.Node("Mode Number", position, end, (packet.Flags & PacketFlags.BadMode) != 0 && end - position < records.ModeBits ? "cut short" : NodeValue.Integer(packet.Mode),
                Explain.ModeNumber));
            position = end;
        }

        if (packet.IsLong && packet.IsBlock)
        {
            nodes.Add(at.Node("Previous Window Flag", position, position + 1, Window(packet.Flags, PacketFlags.PreviousLong), Explain.PreviousWindowFlag));
            nodes.Add(at.Node("Next Window Flag", position + 1, position + 2, Window(packet.Flags, PacketFlags.NextLong), Explain.NextWindowFlag));
            position += 2;
        }

        long total = packet.Length * 8L;
        if (position < total)
        {
            nodes.Add(at.Node("Audio Data", position, total, NodeValue.Integer(total - position, "bits", "bit"), packet.IsBlock ? Explain.AudioData : Explain.UnreadData));
        }

        return nodes;
    }

    private static string Window(PacketFlags flags, PacketFlags flag) => (flags & flag) != 0 ? "1 (long)" : "0 (short)";

    // Places bit fields of a packet in the file, counted from the least significant bit: at its
    // offset when its bytes are contiguous, or through its ranges when it spans pages.
    private sealed class BitPlace(long offset, MappedBytes? mapped)
    {
        public Node Node(string name, long start, long end, NodeValue? value, string description) => mapped is null
            ? Core.Node.FromBits(name, offset, start, end, value, description: description, order: BitOrder.LeastSignificantFirst)
            : Core.Node.FromBits(name, mapped, start, end, value, description: description, order: BitOrder.LeastSignificantFirst);
    }
}
