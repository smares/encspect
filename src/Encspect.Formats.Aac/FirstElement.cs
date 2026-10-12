using System.Runtime.InteropServices;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Aac;

// The first syntax element of a raw_data_block (ISO/IEC 14496-3 4.4.2.1, Table 4.3): its ID and
// instance tag, or a fill element's or data stream element's count and, where its payload holds a
// run of printable text, that text. Reading further needs the Huffman-coded spectral data of each
// channel element, which is not done yet. A USAC frame (ISO/IEC 23003-3 5.3.1) starts with
// usacIndependencyFlag instead.
[StructLayout(LayoutKind.Auto)]
internal readonly record struct FirstElement
{
    public const int None = -1;

    // The element's ID (0 to 7), or None where the access unit is empty or holds no IDs.
    public int Id { get; init; }

    // The instance tag of a channel element, data stream element or PCE; a fill element's count.
    public int Tag { get; init; }

    // A fill element's extension type, or -1.
    public int ExtensionType { get; init; }

    // Where in the access unit the element ends, in bits, when it is a fill or data stream element
    // whose length is known; -1 otherwise.
    public int EndBit { get; init; }

    // Printable text in a fill or data stream element's payload: its first byte in the access unit
    // and its length, or -1 and 0.
    public int TextStart { get; init; }

    public int TextLength { get; init; }

    public static FirstElement Empty { get; } = new() { Id = None, ExtensionType = -1, EndBit = -1, TextStart = -1 };

    private const int MinimumText = 4;

    public static FirstElement Read(ReadOnlySpan<byte> unit, int startBit)
    {
        int bits = (unit.Length * 8) - startBit;
        if (bits < 3)
        {
            return Empty;
        }

        int id = (int)Bits(unit, startBit, 3);
        var element = Empty with { Id = id };
        if (id == 7 || bits < 7)
        {
            return element;
        }

        int tag = (int)Bits(unit, startBit + 3, 4);
        element = element with { Tag = tag };
        if (id == 6)
        {
            // fill_element (4.4.2.7, Table 4.11): count, esc_count where count is 15, then count
            // bytes of extension_payload (Table 4.57), which starts with its extension type.
            int at = startBit + 7;
            int count = tag;
            if (count == 15)
            {
                if (bits < 15)
                {
                    return element;
                }

                count += (int)Bits(unit, at, 8) - 1;
                at += 8;
            }

            int end = at + (count * 8);
            return end > unit.Length * 8 ? element
                : FindText(unit, at, end, element with { EndBit = end, ExtensionType = count > 0 ? (int)Bits(unit, at, 4) : -1 });
        }

        if (id == 4 && bits >= 16)
        {
            // data_stream_element (4.4.2.8, Table 4.10): tag, data_byte_align_flag, count,
            // esc_count where count is 255, then the bytes, byte-aligned when the flag says so.
            bool aligned = Bits(unit, startBit + 7, 1) != 0;
            int count = (int)Bits(unit, startBit + 8, 8);
            int at = startBit + 16;
            if (count == 255)
            {
                if (bits < 24)
                {
                    return element;
                }

                count += (int)Bits(unit, at, 8);
                at += 8;
            }

            if (aligned)
            {
                at = (at + 7) & ~7;
            }

            int end = at + (count * 8);
            return end > unit.Length * 8 ? element : FindText(unit, at, end, element with { EndBit = end });
        }

        return element;
    }

    public static long Bits(ReadOnlySpan<byte> data, int start, int count)
    {
        long value = 0;
        for (int bit = start; bit < start + count; bit++)
        {
            value = (value << 1) | (uint)((data[bit >> 3] >> (7 - (bit & 7))) & 1);
        }

        return value;
    }

    // The longest run of printable ASCII in the whole bytes of the payload, which encoders that sign
    // their streams write byte-aligned (ffmpeg's AAC encoder puts its version into a fill element).
    private static FirstElement FindText(ReadOnlySpan<byte> unit, int startBit, int endBit, FirstElement element)
    {
        int first = (startBit + 7) / 8;
        int end = endBit / 8;
        int bestStart = -1;
        int bestLength = 0;
        int runStart = -1;
        for (int i = first; i <= end; i++)
        {
            bool printable = i < end && unit[i] is >= 0x20 and < 0x7F;
            if (printable && runStart < 0)
            {
                runStart = i;
            }
            else if (!printable && runStart >= 0)
            {
                if (i - runStart > bestLength)
                {
                    (bestStart, bestLength) = (runStart, i - runStart);
                }

                runStart = -1;
            }
        }

        return bestLength >= MinimumText ? element with { TextStart = bestStart, TextLength = bestLength } : element;
    }

    public string? Text(ReadOnlySpan<byte> unit) => TextStart < 0 ? null : Encoding.ASCII.GetString(unit.Slice(TextStart, TextLength));

    public string Summary => Id == None ? "no element" : AacTables.ElementName(Id);

    // The element's nodes at the access unit's bits.
    public IEnumerable<Node> Nodes(UnitBytes unit, int startBit, string? text)
    {
        if (Id == None)
        {
            yield break;
        }

        yield return unit.Bits("Element", startBit, startBit + 3, $"{AacTables.ElementName(Id)} ({AacTables.ElementDescription(Id)})");
        if (Id == 7 || startBit + 7 > unit.Length * 8)
        {
            yield break;
        }

        if (Id == 6)
        {
            yield return unit.Bits("Count", startBit + 3, startBit + 7, NodeValue.Integer(Tag));
            int at = startBit + 7;
            if (Tag == 15 && EndBit >= 0)
            {
                yield return unit.Bits("Escape Count", at, at + 8, NodeValue.Integer(((EndBit - at - 8) / 8) - 14));
                at += 8;
            }

            if (ExtensionType >= 0)
            {
                yield return unit.Bits("Extension Type", at, at + 4, $"{ExtensionType} ({AacTables.ExtensionTypeName(ExtensionType)})");
            }
        }
        else
        {
            yield return unit.Bits("Element Instance Tag", startBit + 3, startBit + 7, NodeValue.Integer(Tag));
        }

        if (text is not null)
        {
            yield return unit.Bytes("Text", TextStart, TextStart + TextLength, text);
        }
    }
}

// Where an access unit's bytes lie in the file, kept without the bytes themselves: one offset, or
// its ranges where it is not contiguous.
internal readonly struct UnitBytes(long offset, int length, IReadOnlyList<ByteRange>? ranges)
{
    public long Offset => offset;

    public int Length => length;

    public Node Bits(string name, int startBit, int endBit, NodeValue? value = null) =>
        ranges is null ? Node.FromBits(name, offset, startBit, endBit, value) : Node.FromBits(name, Mapped(), startBit, endBit, value);

    public Node Bytes(string name, int start, int end, NodeValue? value = null) =>
        ranges is null ? new Node(name, offset + start, end - start, value) : Node.FromMapped(name, Mapped(), start, end, value);

    private MappedBytes Mapped() => new(new byte[length], ranges!);
}
