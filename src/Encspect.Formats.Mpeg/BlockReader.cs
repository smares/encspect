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
