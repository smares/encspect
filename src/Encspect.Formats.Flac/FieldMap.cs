using Encspect.Core;

namespace Encspect.Formats.Flac;

// Makes the nodes of fields at positions inside a run of bytes, whether the bytes lie in the file one after another or came as a packet
// spanning Ogg pages: positions are counted from the start of the run.
internal sealed class FieldMap
{
    private readonly long _offset;
    private readonly IReadOnlyList<ByteRange>? _ranges;
    private readonly int _length;
    private MappedBytes? _mapped;

    private FieldMap(long offset, IReadOnlyList<ByteRange>? ranges, int length)
    {
        _offset = offset;
        _ranges = ranges;
        _length = length;
    }

    public static FieldMap At(long offset) => new(offset, null, 0);

    // The bytes sit at the ranges, which need not touch; the data itself is not kept.
    public static FieldMap Of(IReadOnlyList<ByteRange> ranges, int length) => new(0, ranges, length);

    public static FieldMap Of(MappedBytes bytes)
    {
        IReadOnlyList<ByteRange> ranges = bytes.FileRanges();
        return ranges.Count <= 1 ? At(ranges.Count == 0 ? bytes.FileStart : ranges[0].Offset) : Of(ranges, bytes.Length);
    }

    private MappedBytes Mapped => _mapped ??= new MappedBytes(new byte[_length], _ranges!);

    public Node Bytes(string name, int start, int length, NodeValue? value, string description, string? topic = null, NodeKind kind = NodeKind.Detail) =>
        _ranges is null
            ? new Node(name, _offset + start, length, value) { Kind = kind, Description = description, Topic = topic }
            : Node.FromMapped(name, Mapped, start, start + length, value, kind, description, topic);

    public Node Bits(string name, long startBit, long endBit, NodeValue? value, string description, string? topic = null, NodeKind kind = NodeKind.Detail) =>
        _ranges is null
            ? Node.FromBits(name, _offset, startBit, endBit, value, kind, description, topic)
            : Node.FromBits(name, Mapped, startBit, endBit, value, kind, description, topic);
}
