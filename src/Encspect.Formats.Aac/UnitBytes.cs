using Encspect.Core;

namespace Encspect.Formats.Aac;

// Where an access unit's bytes lie in the file, kept without the bytes themselves: one offset, or
// its ranges where it is not contiguous.
internal readonly struct UnitBytes(long offset, int length, IReadOnlyList<ByteRange>? ranges)
{
    public long Offset => offset;

    public int Length => length;

    public Node Bits(string name, int startBit, int endBit, NodeValue? value, string description, string? topic = null, NodeKind kind = NodeKind.Detail) => ranges is null
        ? Node.FromBits(name, offset, startBit, endBit, value, kind, description, topic)
        : Node.FromBits(name, Mapped(), startBit, endBit, value, kind, description, topic);

    public Node Bytes(string name, int start, int end, NodeValue? value, string description) => ranges is null
        ? new Node(name, offset + start, end - start, value) { Description = description }
        : Node.FromMapped(name, Mapped(), start, end, value, description: description);

    // The bytes of a block that starts start bytes into the unit.
    public UnitBytes Block(int start, int blockLength) => ranges is null ? new UnitBytes(offset + start, blockLength, null) : this;

    private MappedBytes Mapped() => new(new byte[length], ranges!);
}
