using Encspect.Core;

namespace Encspect.Formats.Dts;

// Where the bits of the bit-packed stream lie in the file, for the nodes of one unit (a core
// frame and the extension substreams after it): the unit starts at offset and is length bytes long
// in the file. The 16-bit big endian form is the stream byte for byte, so a field is an ordinary
// bit field. In the little endian form each pair of bytes is swapped, so a field of one byte keeps
// its bits and a field across bytes covers the bytes of the pairs it touches, listing their ranges
// where they are not next to each other. In the 14-bit forms a word holds 14 bits, so a field
// within one big endian word is a bit field of that word's bytes and any other field covers the
// words it touches.
internal readonly struct Locator(StreamForm form, long offset, int length)
{
    public StreamForm Form => form;

    public long Offset => offset;

    public int Length => length;

    public Node Field(string name, int startBit, int endBit, NodeValue? value = null, string? description = null, string? topic = null, NodeKind kind = NodeKind.Detail)
    {
        switch (form)
        {
            case StreamForm.BigEndian16:
                return Node.FromBits(name, offset, startBit, endBit, value, kind, description, topic);
            case StreamForm.LittleEndian16:
                return LittleEndian16(name, startBit, endBit, value, description, topic, kind);
            default:
                return Packed14(name, startBit, endBit, value, description, topic, kind);
        }
    }

    // A node that covers every byte the bits [startBit, endBit) touch, for a group of fields.
    public Node Group(string name, int startBit, int endBit, NodeValue? value = null, string? description = null, string? topic = null, NodeKind kind = NodeKind.Detail)
    {
        if (form == StreamForm.BigEndian16)
        {
            return Node.FromBits(name, offset, startBit, endBit, value, kind, description, topic);
        }

        (long first, long last) = Cover(startBit, endBit);
        return new Node(name, first, last - first + 1, value) { Kind = kind, Description = description, Topic = topic };
    }

    private (long First, long Last) Cover(int startBit, int endBit)
    {
        int lastBit = Math.Max(startBit, endBit - 1);
        if (form == StreamForm.LittleEndian16)
        {
            int first = startBit >> 3;
            int last = lastBit >> 3;
            return (Clamp(offset + (first & ~1)), Clamp(offset + (last | 1)));
        }

        int firstWord = startBit / 14;
        int lastWord = lastBit / 14;
        return (Clamp(offset + (firstWord * 2)), Clamp(offset + (lastWord * 2) + 1));
    }

    private long Clamp(long position) => Math.Clamp(position, offset, offset + Math.Max(0, length - 1));

    private Node LittleEndian16(string name, int startBit, int endBit, NodeValue? value, string? description, string? topic, NodeKind kind)
    {
        int first = startBit >> 3;
        int last = Math.Max(startBit, endBit - 1) >> 3;
        if (first == last)
        {
            int mapped = Forms.FileIndex(first, form, length);
            return Node.FromBits(name, offset + mapped, startBit & 7, (startBit & 7) + (endBit - startBit), value, kind, description, topic);
        }

        // At most a few bytes: a field is up to 32 bits.
        List<long> bytes = [];
        for (int b = first; b <= last; b++)
        {
            bytes.Add(Clamp(offset + Forms.FileIndex(b, form, length)));
        }

        bytes.Sort();
        var ranges = new List<ByteRange>();
        foreach (long position in bytes.Distinct())
        {
            if (ranges.Count > 0 && ranges[^1].End == position)
            {
                ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + 1 };
            }
            else
            {
                ranges.Add(new ByteRange(position, 1));
            }
        }

        return new Node(name, ranges[0].Offset, ranges[^1].End - ranges[0].Offset, value) { Kind = kind, Description = description, Topic = topic, Ranges = ranges };
    }

    private Node Packed14(string name, int startBit, int endBit, NodeValue? value, string? description, string? topic, NodeKind kind)
    {
        int lastBit = Math.Max(startBit, endBit - 1);
        int firstWord = startBit / 14;
        int lastWord = lastBit / 14;
        if (firstWord == lastWord && endBit > startBit)
        {
            int r0 = startBit % 14;
            int width = endBit - startBit;
            long word = offset + (firstWord * 2);
            if (form == StreamForm.BigEndian14)
            {
                return Node.FromBits(name, word, 2 + r0, 2 + r0 + width, value, kind, description, topic);
            }

            // Little endian: the word's high byte comes second, so bit 13 is the first bit of byte 1.
            int v0 = 13 - r0;
            int v1 = 13 - (r0 + width - 1);
            if ((v0 >= 8) == (v1 >= 8))
            {
                return Node.FromBits(name, word + (v0 >= 8 ? 1 : 0), 7 - (v0 & 7), 7 - (v0 & 7) + width, value, kind, description, topic);
            }
        }

        (long first, long last) = Cover(startBit, endBit);
        if (form == StreamForm.BigEndian14)
        {
            first = Clamp(offset + (firstWord * 2) + (((2 + (startBit % 14)) >> 3) & 1));
            last = Clamp(offset + (lastWord * 2) + (((2 + (lastBit % 14)) >> 3) & 1));
        }

        return new Node(name, first, last - first + 1, value) { Kind = kind, Description = description, Topic = topic };
    }
}
