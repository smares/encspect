using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// Random access to a ByteReader through one cached block, so scanning for frame headers byte by
// byte does not turn into one stream read per byte, and large files are never loaded whole.
internal sealed class BlockReader(ByteReader reader)
{
    private const int BlockSize = 1 << 16;

    private byte[] _block = [];
    private long _blockStart = -1;

    public long Start => reader.Start;

    public long End => reader.End;

    public bool TryReadUInt32(long offset, out uint value)
    {
        value = 0;
        if (offset < reader.Start || offset + 4 > reader.End)
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
