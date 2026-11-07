using Encspect.Core;

namespace Encspect.Formats.Flac;

// Makes the nodes of fields at positions inside a run of bytes, whether the bytes lie in the file one after another or came as a packet
// spanning Ogg pages: positions are counted from the start of the run.
internal sealed class FieldMap
{
    private readonly long _offset;
    private readonly IReadOnlyList<ByteRange>? _ranges;
    private readonly int _length;
    private readonly long _limit;
    private MappedBytes? _mapped;

    private long BitLimit => _limit > long.MaxValue / 8 ? long.MaxValue : _limit * 8;

    private FieldMap(long offset, IReadOnlyList<ByteRange>? ranges, int length, long limit = long.MaxValue)
    {
        _offset = offset;
        _ranges = ranges;
        _length = length;
        _limit = limit;
    }

    public static FieldMap At(long offset) => new(offset, null, 0);

    // The fields stay inside the limit bytes: a frame whose bytes a container changed has fewer or more bytes in the file than it was read from.
    public static FieldMap At(long offset, long limit) => new(offset, null, 0, limit);

    // The bytes sit at the ranges, which need not touch; the data itself is not kept.
    public static FieldMap Of(IReadOnlyList<ByteRange> ranges, int length) => new(0, ranges, length);

    public static FieldMap Of(MappedBytes bytes)
    {
        // Bytes a container changed (inflated or with stripped bytes put back) have no positions of their own in the file: the fields stay inside
        // the range they came from.
        if (bytes.IsOpaque)
        {
            return new FieldMap(bytes.FileStart, null, 0, bytes.FileEnd - bytes.FileStart);
        }

        IReadOnlyList<ByteRange> ranges = bytes.FileRanges();
        return ranges.Count <= 1 ? At(ranges.Count == 0 ? bytes.FileStart : ranges[0].Offset) : Of(ranges, bytes.Length);
    }

    private MappedBytes Mapped => _mapped ??= new MappedBytes(new byte[_length], _ranges!);

    public Node Bytes(string name, int start, int length, NodeValue? value, string description, string? topic = null, NodeKind kind = NodeKind.Detail) =>
        _ranges is null
            ? new Node(name, _offset + Math.Min(start, _limit), Math.Min(length, Math.Max(0, _limit - start)), value) { Kind = kind, Description = description, Topic = topic }
            : Node.FromMapped(name, Mapped, start, start + length, value, kind, description, topic);

    public Node Bits(string name, long startBit, long endBit, NodeValue? value, string description, string? topic = null, NodeKind kind = NodeKind.Detail) =>
        _ranges is null
            ? Node.FromBits(name, _offset, Math.Min(startBit, BitLimit), Math.Min(endBit, BitLimit), value, kind, description, topic)
            : Node.FromBits(name, Mapped, startBit, endBit, value, kind, description, topic);
}
