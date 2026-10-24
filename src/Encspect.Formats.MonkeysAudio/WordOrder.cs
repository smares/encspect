// Follows the Monkey's Audio SDK 13.42 (BitArray.cpp, APECompressCreate.cpp, APEDecompress.cpp), 3-clause BSD license:
// Copyright 2000-2026 Matthew T. Ashland. See THIRD-PARTY-NOTICES.md.
using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio;

// How the coded bytes of the frames lie in the file. The encoder fills 32-bit words from their most significant byte and writes each word
// little-endian (BitArray.cpp, APE_PUTC and APECompressCreate.cpp, FixupFrame), so the bytes of every aligned word are stored in reverse,
// and frames follow one another in the stream of bytes the words make, so that one frame may end in the middle of the word the next begins
// in. The seek table gives where each frame starts in that stream, counted in the file (the bytes before the first frame included). The
// decoder reads a frame from the word its first byte is in and skips the bytes before it (APEDecompress.cpp, ScheduleFrameDecode).
internal static class WordOrder
{
    // The file offset of the byte at index (stream) of the bytes that start at start, the first frame's first byte.
    public static long FileOffset(long start, long index) => start + ((index >> 2) << 2) + 3 - (index & 3);

    // The file ranges that hold the stream bytes [from, to) of the data that starts at start, in file order: at most three, the part of the
    // first word, the whole words between and the part of the last word. Merged where they touch.
    public static List<ByteRange> Ranges(long start, long from, long to)
    {
        var ranges = new List<ByteRange>(3);
        if (to <= from)
        {
            return ranges;
        }

        long firstWord = from >> 2;
        long lastWord = (to - 1) >> 2;
        for (long word = firstWord; word <= lastWord; word++)
        {
            long first = Math.Max(from, word << 2) & 3;
            long last = (Math.Min(to, (word + 1) << 2) - 1) & 3;
            Add(ranges, new ByteRange(start + (word << 2) + 3 - last, last - first + 1));
            if (word == firstWord && lastWord > firstWord + 1)
            {
                // The whole words in the middle are one range.
                Add(ranges, new ByteRange(start + ((word + 1) << 2), (lastWord - firstWord - 1) << 2));
                word = lastWord - 1;
            }
        }

        return ranges;
    }

    private static void Add(List<ByteRange> ranges, ByteRange range)
    {
        if (ranges.Count > 0 && ranges[^1].End == range.Offset)
        {
            ranges[^1] = new ByteRange(ranges[^1].Offset, ranges[^1].Length + range.Length);
        }
        else
        {
            ranges.Add(range);
        }
    }

    // The stream bytes of a frame (its bytes in file order, from the ranges of its node) in stream order: each piece of a word reversed. start is the file
    // offset of the first frame's first byte, which fixes where the words are. destination holds at least bytes.Length bytes.
    public static void ToStream(MappedBytes bytes, long start, byte[] destination)
    {
        ReadOnlySpan<byte> data = bytes.Span;
        int at = 0;
        foreach (ByteRange range in bytes.FileRanges())
        {
            long position = range.Offset;
            long end = range.End;
            while (position < end)
            {
                long wordEnd = start + ((((position - start) >> 2) + 1) << 2);
                if (position == wordEnd - 4 && end - position >= 4)
                {
                    // Whole words: four bytes reversed at a time.
                    int words = (int)((end - position) >> 2);
                    for (int w = 0; w < words; w++)
                    {
                        uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at + (w * 4)));
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(at + (w * 4)), value);
                    }

                    at += words * 4;
                    position += words * 4L;
                    continue;
                }

                int piece = (int)(Math.Min(end, wordEnd) - position);
                for (int i = 0; i < piece; i++)
                {
                    destination[at + i] = data[at + piece - 1 - i];
                }

                at += piece;
                position += piece;
            }
        }
    }
}
