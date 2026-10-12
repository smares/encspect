using Encspect.Core;

namespace Encspect.Formats.Aac;

// Reads the file through one cached block, so scanning for sync words and reading frames one after
// another does not seek for every few bytes.
internal sealed class BlockCache(ByteReader reader)
{
    private const int BlockSize = 1 << 16;

    private readonly byte[] _block = new byte[BlockSize];
    private long _start = -1;
    private int _length;

    public long Start => reader.Start;

    public long End => reader.End;

    public ByteReader Reader => reader;

    // The byte at offset, or -1 outside the reader.
    public int ByteAt(long offset)
    {
        if (offset < reader.Start || offset >= reader.End)
        {
            return -1;
        }

        if (offset < _start || offset >= _start + _length)
        {
            Load(offset);
        }

        return _block[offset - _start];
    }

    // Copies bytes from offset into buffer, as many as the reader holds; returns how many.
    public int Read(long offset, Span<byte> buffer)
    {
        int count = (int)Math.Clamp(reader.End - offset, 0, buffer.Length);
        if (offset < reader.Start || count == 0)
        {
            return 0;
        }

        if (offset >= _start && offset + count <= _start + _length)
        {
            _block.AsSpan((int)(offset - _start), count).CopyTo(buffer);
            return count;
        }

        if (count > BlockSize / 2)
        {
            reader.Position = offset;
            reader.Read(buffer[..count]);
            return count;
        }

        Load(offset);
        _block.AsSpan(0, count).CopyTo(buffer);
        return count;
    }

    private void Load(long offset)
    {
        _start = offset;
        _length = (int)Math.Min(BlockSize, reader.End - offset);
        reader.Position = offset;
        reader.Read(_block.AsSpan(0, _length));
    }
}
