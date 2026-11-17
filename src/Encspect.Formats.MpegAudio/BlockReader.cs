using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.MpegAudio;

// Random access to a ByteReader through one cached block, so scanning for frame headers byte by
// byte does not turn into one stream read per byte, and large files are never loaded whole.
internal sealed class BlockReader(ByteReader reader)
{
    private const int BlockSize = 1 << 16;

    private byte[] _block = [];
    private long _blockStart = -1;

    // The stretches of the reader that hold the stream where a container hands it over in several pieces (MP4 chunks, Matroska blocks, IEC 61937 payloads), sorted and apart;
    // null where the reader is the stream whole. The bytes between them are the container's own and no part of the stream: a search skips them and the frame after the end
    // of a stretch is the one at the start of the next.
    public IReadOnlyList<ByteRange>? Stretches { get; init; }

    // Where the byte at position continues the stream: the position itself inside a stretch, the start of the next stretch at the end of one or in the container's bytes
    // between two, and the end of the last stretch past it.
    public long Hop(long position) => Hop(position, true);

    private long Hop(long position, bool skipsTail)
    {
        if (Stretches is not { } stretches)
        {
            return position;
        }

        int low = 0;
        int high = stretches.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            if (stretches[middle].End <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low >= stretches.Count)
        {
            return Math.Max(position, stretches.Count == 0 ? position : stretches[^1].End);
        }

        // The last bytes of a stretch that cannot hold a frame header (the padding word that ends the payload of an IEC 61937 burst of an odd number of bytes) are no frame.
        if (skipsTail && position >= stretches[low].Offset && stretches[low].End - position < 4 && low + 1 < stretches.Count)
        {
            return stretches[low + 1].Offset;
        }

        return Math.Max(position, stretches[low].Offset);
    }

    // The offset after count bytes of the stream from position on, which is the end of a stretch where they end there (not the start of the next one); past the reader's end,
    // End plus what is missing.
    public long Advance(long position, long count)
    {
        if (Stretches is null)
        {
            return position + count;
        }

        long at = position;
        while (count > 0)
        {
            at = Hop(at, false);
            if (at >= reader.End)
            {
                return reader.End + count;
            }

            long take = Math.Min(count, StretchEnd(at) - at);
            at += take;
            count -= take;
        }

        return at;
    }

    // The bytes of the stream from one position to a later one, the container's bytes between the stretches not counted.
    public long Between(long from, long to)
    {
        if (Stretches is null)
        {
            return to - from;
        }

        long total = 0;
        for (long at = Hop(from, false); at < to;)
        {
            long end = Math.Min(to, StretchEnd(at));
            total += end - at;
            at = Hop(end, false);
        }

        return total;
    }

    // Whether count bytes from offset lie in one stretch (always, for a reader that is one stretch): a frame header does not start in one stretch and end in the next.
    public bool IsInStretch(long offset, int count)
    {
        if (Stretches is not { } stretches)
        {
            return true;
        }

        int low = 0;
        int high = stretches.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            if (stretches[middle].End <= offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low < stretches.Count && stretches[low].Offset <= offset && offset + count <= stretches[low].End;
    }

    // The end of the stretch that position is in or hops to; the reader's end for a reader that is one stretch.
    public long StretchEnd(long position)
    {
        if (Stretches is not { Count: > 0 } stretches)
        {
            return reader.End;
        }

        long at = Hop(position, false);
        foreach (ByteRange range in stretches)
        {
            if (at >= range.Offset && at < range.End)
            {
                return range.End;
            }
        }

        return reader.End;
    }

    public long Start => reader.Start;

    public long End => reader.End;

    public bool TryReadUInt32(long offset, out uint value)
    {
        value = 0;
        if (offset < reader.Start || offset + 4 > reader.End || !IsInStretch(offset, 4))
        {
            return false;
        }

        long index = offset - _blockStart;
        if (offset >= _blockStart && index + 4 <= _block.Length)
        {
            value = BinaryPrimitives.ReadUInt32BigEndian(_block.AsSpan((int)index));
            return true;
        }

        for (int i = 0; i < 4; i++)
        {
            value = (value << 8) | ByteAt(offset + i);
        }

        return true;
    }

    public byte ByteAt(long offset)
    {
        if (offset < _blockStart || offset >= _blockStart + _block.Length)
        {
            _blockStart = offset;
            _block = reader.ReadBytesAt(offset, (int)Math.Min(BlockSize, reader.End - offset));
        }

        return _block[offset - _blockStart];
    }

    // The first offset in [from, limit) that holds value, or -1, searched a block at a time. Positions outside the reader are not searched.
    public long IndexOf(byte value, long from, long limit)
    {
        from = Math.Max(from, reader.Start);
        limit = Math.Min(limit, reader.End);
        if (Stretches is not null)
        {
            // Stretch by stretch, so that the container's bytes between them are not searched.
            while (from < limit)
            {
                from = Hop(from, false);
                if (from >= limit)
                {
                    return -1;
                }

                long found = IndexInStretch(value, from, Math.Min(limit, StretchEnd(from)));
                if (found >= 0)
                {
                    return found;
                }

                from = StretchEnd(from);
            }

            return -1;
        }

        return IndexInStretch(value, from, limit);
    }

    private long IndexInStretch(byte value, long from, long limit)
    {
        while (from < limit)
        {
            ByteAt(from);
            int index = (int)(from - _blockStart);
            int count = (int)Math.Min(_block.Length - index, limit - from);
            int found = _block.AsSpan(index, count).IndexOf(value);
            if (found >= 0)
            {
                return from + found;
            }

            from += count;
        }

        return -1;
    }

    // Fills destination with the bytes from offset on; those past the end of the reader are zeros.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public void ReadInto(long offset, Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length && offset + done < reader.End)
        {
            ByteAt(offset + done);
            int start = (int)(offset + done - _blockStart);
            int length = Math.Min(destination.Length - done, _block.Length - start);
            _block.AsSpan(start, length).CopyTo(destination[done..]);
            done += length;
        }

        destination[done..].Clear();
    }

    public byte[] Read(long offset, int count)
    {
        var data = new byte[count];
        int done = 0;
        while (done < count)
        {
            ByteAt(offset + done);
            int start = (int)(offset + done - _blockStart);
            int length = Math.Min(count - done, _block.Length - start);
            Array.Copy(_block, start, data, done, length);
            done += length;
        }

        return data;
    }
}
