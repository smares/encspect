using System.Buffers.Binary;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.WavPack;

// Where a legacy file's trailer starts. 3.97's encoder writes, after the bitstream, 16 bytes of 0xFF and the chunks of the source that followed its audio (wavpack.c
// 1068-1112); libwavpack keeps the bytes after the bitstream as the wrapper only when the first 16 are 0xFF (unpack3.c 1176-1214). Without a decoder the end of the
// bitstream is not known, so the trailer is the first run of 16 bytes of 0xFF that chunk headers chain from, to the last byte of the data.
internal static class LegacyTrailer
{
    public const int MarkerLength = 16;

    // The chunks of a trailer that are listed; the rest are counted.
    public const int MaxListed = 64;

    // How many chunk headers all the candidates of a file may read, so that a file of many runs of 0xFF cannot cost more than this.
    private const int StepBudget = 1 << 20;

    private const int BufferLength = 1 << 16;

    public readonly record struct Chunk(string Id, long At, uint Size);

    // At is where the marker lies, or -1 where none chains to the end; FirstRun is where the first run of 0xFF of 16 bytes or more starts, or -1 where there is none.
    public readonly record struct Found(long At, long FirstRun, int Count, IReadOnlyList<Chunk> Chunks);

    public static Found Find(ByteReader reader, long from, long end)
    {
        long firstRun = -1;
        long runStart = -1;
        int steps = StepBudget;
        byte[] buffer = new byte[BufferLength];
        for (long position = from; position < end;)
        {
            int length = (int)Math.Min(BufferLength, end - position);
            reader.Position = position;
            reader.Read(buffer.AsSpan(0, length));
            ReadOnlySpan<byte> bytes = buffer.AsSpan(0, length);
            int i = 0;
            while (i < length)
            {
                if (runStart < 0)
                {
                    int found = bytes[i..].IndexOf((byte)0xFF);
                    if (found < 0)
                    {
                        break;
                    }

                    runStart = position + i + found;
                    i += found;
                }

                int stop = bytes[i..].IndexOfAnyExcept((byte)0xFF);
                if (stop < 0)
                {
                    break;
                }

                long runEnd = position + i + stop;
                if (runEnd - runStart >= MarkerLength)
                {
                    firstRun = firstRun < 0 ? runStart : firstRun;
                    if (Chain(reader, runEnd, end, ref steps) is { } chained)
                    {
                        return new Found(runEnd - MarkerLength, firstRun, chained.Count, chained.Chunks);
                    }
                }

                runStart = -1;
                i += stop;
            }

            position += length;
        }

        if (runStart >= 0 && end - runStart >= MarkerLength)
        {
            firstRun = firstRun < 0 ? runStart : firstRun;
        }

        return new Found(-1, firstRun, 0, []);
    }

    // The chunks that follow a marker whose 16 bytes end at markerEnd, if they chain to the end of the data. A chunk's ID is four printable characters and its size
    // leaves it inside the data; a chunk of an odd size is followed by a pad byte.
    private static (int Count, List<Chunk> Chunks)? Chain(ByteReader reader, long markerEnd, long end, ref int steps)
    {
        var chunks = new List<Chunk>();
        int count = 0;
        long position = markerEnd;
        Span<byte> header = stackalloc byte[8];
        while (position + 8 <= end)
        {
            if (--steps < 0)
            {
                return null;
            }

            reader.Position = position;
            reader.Read(header);
            if (header[..4].IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) >= 0)
            {
                break;
            }

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long next = position + 8 + size;
            if (next > end)
            {
                break;
            }

            if (chunks.Count < MaxListed)
            {
                chunks.Add(new Chunk(Encoding.Latin1.GetString(header[..4]), position, size));
            }

            count++;
            position = next + (size & 1);
            if (position >= end)
            {
                return (count, chunks);
            }
        }

        return null;
    }
}
