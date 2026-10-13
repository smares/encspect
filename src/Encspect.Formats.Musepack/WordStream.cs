using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Musepack;

// The bitstream of stream versions 4 to 7: 32-bit little-endian words, each read from its most
// significant bit (libmpcdec r495 mpc_demux_fill with MPC_BUFFER_SWAP, mppenc 1.16 WriteBits), so a
// field's bits lie in a word's bytes from the last to the first. Bit positions count from the start
// of the stream; bits past the end of the data read as zero, as libmpcdec fills its buffer with zeros.
internal sealed class WordStream(ByteReader reader, long start, long end)
{
    private const int BlockSize = 1 << 16;

    private readonly byte[] _block = new byte[BlockSize];
    private long _blockStart = -1;
    private int _blockLength;

    public WordLayout Layout { get; } = new(start, end);

    // Every byte counts; Read pads a partial last word with zero bytes.
    public long BitLength => (end - start) * 8;

    // Up to 32 bits from bit position bit.
    public uint Read(long bit, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        long word = bit >> 5;
        ulong both = ((ulong)Word(word) << 32) | Word(word + 1);
        return (uint)((both << (int)(bit & 31)) >> (64 - count));
    }

    private uint Word(long index)
    {
        long offset = start + (index << 2);
        if (index < 0 || offset >= end)
        {
            return 0;
        }

        Span<byte> bytes = stackalloc byte[4];
        bytes.Clear();
        for (int i = 0; i < 4 && offset + i < end; i++)
        {
            bytes[i] = ByteAt(offset + i);
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private byte ByteAt(long offset)
    {
        if (offset < _blockStart || offset >= _blockStart + _blockLength)
        {
            // Blocks start on a word boundary of the stream, so a word never straddles two.
            _blockStart = start + (((offset - start) / BlockSize) * BlockSize);
            _blockLength = (int)Math.Min(BlockSize, end - _blockStart);
            reader.Position = _blockStart;
            reader.Read(_block.AsSpan(0, _blockLength));
        }

        return _block[offset - _blockStart];
    }
}

// Where the bits of a word stream lie in the file, without reading it, so nodes built after the
// analysis can place their fields.
internal sealed class WordLayout(long start, long end)
{
    public long Start => start;

    public long End => end;

    // The file offset of the byte that holds bit.
    public long OffsetOf(long bit) => start + ((bit >> 5) << 2) + 3 - ((bit & 31) >> 3);

    // The file offset of the word that holds bit, where a frame that starts there begins in the file.
    public long WordStart(long bit) => start + ((bit >> 5) << 2);

    // The file offset just past the word that holds the bit before bit: where a stream that ends at
    // bit ends in the file.
    public long WordEnd(long bit) => start + (((bit + 31) >> 5) << 2);

    // The file ranges that hold bits [first, stop), in file order: within a word the bits of a field
    // lie in contiguous bytes, from its first word on in the word's low bytes and in its last word in
    // its high bytes, so a field spans at most three ranges.
    public List<ByteRange> Ranges(long first, long stop)
    {
        var ranges = new List<ByteRange>(3);
        if (stop <= first)
        {
            long at = Math.Min(OffsetOf(first), end);
            ranges.Add(new ByteRange(at, 0));
            return ranges;
        }

        long firstWord = first >> 5;
        long lastWord = (stop - 1) >> 5;
        for (long word = firstWord; word <= lastWord; word++)
        {
            if (word > firstWord && word < lastWord)
            {
                long run = lastWord - word;
                Add(ranges, start + (word << 2), run * 4);
                word = lastWord - 1;
                continue;
            }

            long from = Math.Max(first, word << 5) - (word << 5);
            long to = Math.Min(stop, (word + 1) << 5) - (word << 5);
            long low = 3 - ((to - 1) >> 3);
            long high = 3 - (from >> 3);
            Add(ranges, start + (word << 2) + low, high - low + 1);
        }

        // Bytes past the data are not the file's.
        for (int i = ranges.Count - 1; i >= 0; i--)
        {
            ByteRange range = ranges[i];
            if (range.Offset >= end)
            {
                ranges.RemoveAt(i);
            }
            else if (range.End > end)
            {
                ranges[i] = range with { Length = end - range.Offset };
            }
        }

        if (ranges.Count == 0)
        {
            ranges.Add(new ByteRange(end, 0));
        }

        return ranges;
    }

    // A node for bits [first, end): a field inside one byte carries its bit position, a longer one
    // its bytes, with Ranges where they are not contiguous.
    public Node Field(string name, long first, long end, NodeValue? value, string description, string? topic = null, NodeKind kind = NodeKind.Detail)
    {
        List<ByteRange> ranges = Ranges(first, end);
        if (ranges.Count == 1 && ranges[0].Length == 1 && end - first < 8 && end > first)
        {
            int bit = (int)(first & 7);
            return Node.FromBits(name, ranges[0].Offset, bit, bit + (end - first), value, kind, description, topic);
        }

        long offset = ranges[0].Offset;
        long length = ranges[^1].End - offset;
        return new Node(name, offset, length, value)
        {
            Kind = kind,
            Description = description,
            Topic = topic,
            Ranges = ranges.Count > 1 ? ranges : null,
        };
    }

    private static void Add(List<ByteRange> ranges, long offset, long length)
    {
        if (ranges.Count > 0 && ranges[^1].End >= offset)
        {
            ByteRange last = ranges[^1];
            ranges[^1] = last with { Length = Math.Max(last.End, offset + length) - last.Offset };
            return;
        }

        ranges.Add(new ByteRange(offset, length));
    }
}
