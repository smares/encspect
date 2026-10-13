namespace Encspect.Formats.Ac3;

// The records frame nodes are built from once the stream is closed (a report outlives the stream it
// was read from), packed one after another into shared blocks: no array per frame. A position
// holds the block in its high 16 bits and the offset in it in the low ones, so blocks are at most
// 64 KiB and the store holds 4 GiB; an item never spans two blocks. Written only while parsing,
// so builders on several threads read it safely.
internal sealed class ByteStore
{
    private const int BlockSize = 1 << 16;

    private readonly List<byte[]> _blocks = [];
    private int _used;

    // The two pieces are stored one after the other, so the second starts at the returned
    // position plus the first's length.
    public uint Add(ReadOnlySpan<byte> data, ReadOnlySpan<byte> more = default)
    {
        int length = data.Length + more.Length;
        if (length > BlockSize)
        {
            throw new InvalidDataException("A record is longer than a block of the store.");
        }

        if (_blocks.Count == 0 || length > _blocks[^1].Length - _used)
        {
            if (_blocks.Count == 1 << 16)
            {
                throw new InvalidDataException("The frames' records exceed 4 GiB.");
            }

            // Blocks double up to their full size, so a short file allocates little.
            int size = _blocks.Count == 0 ? 4096 : Math.Min(BlockSize, _blocks[^1].Length * 2);
            _blocks.Add(new byte[Math.Max(size, length)]);
            _used = 0;
        }

        uint position = ((uint)(_blocks.Count - 1) << 16) | (uint)_used;
        data.CopyTo(_blocks[^1].AsSpan(_used));
        more.CopyTo(_blocks[^1].AsSpan(_used + data.Length));
        _used += length;
        return position;
    }

    public ReadOnlySpan<byte> Span(uint position, int length) => _blocks[(int)(position >> 16)].AsSpan((int)(position & 0xFFFF), length);
}
