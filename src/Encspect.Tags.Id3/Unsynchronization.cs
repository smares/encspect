using Encspect.Core;

namespace Encspect.Tags.Id3;

// ID3v2.4 section 6.1: unsynchronization inserts $00 after every $FF that would otherwise form a
// false sync, so decoding drops each $00 that directly follows $FF.
internal static class Unsynchronization
{
    // The decoded bytes, still mapped to the file bytes they came from. falseSync is the file
    // offset of the first $FF that is still followed by %111xxxxx, which unsynchronized data
    // cannot hold.
    public static MappedBytes Resynchronize(MappedBytes bytes, out long? falseSync)
    {
        ReadOnlySpan<byte> source = bytes.Span;
        var data = new byte[source.Length];
        var ranges = new List<ByteRange>();
        int length = 0;
        bool previousWasFF = false;
        long previousOffset = -1;
        falseSync = null;

        for (int i = 0; i < source.Length; i++)
        {
            byte b = source[i];
            if (previousWasFF && b == 0x00)
            {
                previousWasFF = false;
                continue;
            }

            if (previousWasFF && b >= 0xE0)
            {
                falseSync ??= previousOffset;
            }

            long offset = bytes.OffsetOf(i);
            if (!bytes.IsOpaque)
            {
                Extend(ranges, offset);
            }

            data[length++] = b;
            previousOffset = offset;
            previousWasFF = b == 0xFF;
        }

        return bytes.IsOpaque ? MappedBytes.Opaque(data.AsMemory(0, length), bytes.FileStart, bytes.FileEnd) : new MappedBytes(data.AsMemory(0, length), ranges, bytes.FileEnd);
    }

    // Adds the file byte at offset to the ranges, which grow while bytes follow each other.
    public static void Extend(List<ByteRange> ranges, long offset)
    {
        if (ranges.Count > 0 && ranges[^1].End == offset)
        {
            ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + 1 };
        }
        else
        {
            ranges.Add(new ByteRange(offset, 1));
        }
    }

    // The decoded length of the unsynchronized file bytes [start, end), and the offset of the
    // first false sync in them, read in chunks so a large frame is never held whole.
    // previousWasFF says whether the byte before start was $FF.
    public static (long Length, long? FalseSync) MeasureResynchronized(ByteReader reader, long start, long end, bool previousWasFF)
    {
        long length = 0;
        long? falseSync = null;
        for (long at = start; at < end;)
        {
            byte[] chunk = reader.ReadBytesAt(at, (int)Math.Min(end - at, 1 << 16));
            for (int i = 0; i < chunk.Length; i++)
            {
                byte b = chunk[i];
                if (previousWasFF && b == 0x00)
                {
                    previousWasFF = false;
                    continue;
                }

                if (previousWasFF && b >= 0xE0)
                {
                    falseSync ??= at + i - 1;
                }

                length++;
                previousWasFF = b == 0xFF;
            }

            at += chunk.Length;
        }

        return (length, falseSync);
    }
}
