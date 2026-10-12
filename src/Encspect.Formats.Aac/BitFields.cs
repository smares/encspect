using Encspect.Core;

namespace Encspect.Formats.Aac;

// Reads bit fields of mapped bytes, most significant bit first, and adds a node for each at the
// file bits it came from. A field that runs past the end throws EndOfStreamException, which the
// structure being read catches and reports as cut short.
internal sealed class BitFields(MappedBytes bytes)
{
    public MappedBytes Bytes => bytes;

    public int Position { get; private set; }

    public int Length => bytes.Length * 8;

    public int Remaining => Length - Position;

    // Reads width bits and adds a node named name under parent, whose value describe gives (the
    // raw number when describe is null).
    public long Read(Node parent, string name, int width, string description, Func<long, NodeValue?>? describe = null, string? topic = null)
    {
        int start = Position;
        long value = Take(width);
        parent.Add(Node.FromBits(name, bytes, start, start + width, describe?.Invoke(value) ?? NodeValue.Integer(value), description: description, topic: topic));
        return value;
    }

    public bool Flag(Node parent, string name, string description, string? topic = null) =>
        Read(parent, name, 1, description, v => NodeValue.Boolean(v != 0), topic) != 0;

    // Reads width bits, at most 63, without a node.
    public long Take(int width)
    {
        long value = Look(width);
        Position += width;
        return value;
    }

    // The next width bits, without moving on.
    public long Look(int width)
    {
        if (width < 0 || width > 63 || width > Remaining)
        {
            throw new EndOfStreamException();
        }

        ReadOnlySpan<byte> data = bytes.Span;
        long value = 0;
        for (int bit = Position; bit < Position + width; bit++)
        {
            value = (value << 1) | (uint)((data[bit >> 3] >> (7 - (bit & 7))) & 1);
        }

        return value;
    }

    public void Skip(long width)
    {
        if (width < 0 || width > Remaining)
        {
            throw new EndOfStreamException();
        }

        Position += (int)width;
    }

    // A node for the bits from start to the current position, with the children of from.
    public Node Span(string name, int start, NodeValue? value, string description, Node? from = null, string? topic = null)
    {
        Node node = Node.FromBits(name, bytes, start, Position, value, description: description, topic: topic);
        foreach (Node child in from?.Children ?? [])
        {
            node.Add(child);
        }

        return node;
    }

    // ISO/IEC 23003-3 5.2 (escapedValue): bits1, then bits2 more where those are all ones, and bits3
    // more where the second ones are too.
    public long Escaped(Node parent, string name, string description, int bits1, int bits2, int bits3, long add = 0)
    {
        int start = Position;
        long value = Take(bits1);
        if (value == (1L << bits1) - 1)
        {
            long more = Take(bits2);
            value += more;
            if (more == (1L << bits2) - 1 && bits3 > 0)
            {
                value += Take(bits3);
            }
        }

        parent.Add(Node.FromBits(name, bytes, start, Position, NodeValue.Integer(value + add), description: description));
        return value + add;
    }

    // Skips to the next byte boundary counted from anchor, a bit position.
    public void Align(int anchor)
    {
        int misaligned = (Position - anchor) & 7;
        if (misaligned != 0)
        {
            Skip(8 - misaligned);
        }
    }
}
