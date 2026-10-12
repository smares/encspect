namespace Encspect.Formats.Ac3;

// The records frame nodes are built from once the stream is closed (a report outlives the stream it
// was read from), packed one after another into shared blocks: no array per frame. A position
// holds the block in its high 32 bits and the offset in it in the low ones; an item never spans
// two blocks. Written only while parsing, so builders on several threads read it safely.
internal sealed class ByteStore
{
    private const int BlockSize = 1 << 20;

    private readonly List<byte[]> _blocks = [];
    private int _used;

    // The two pieces are stored one after the other, so the second starts at the returned
    // position plus the first's length.
    public long Add(ReadOnlySpan<byte> data, ReadOnlySpan<byte> more = default)
    {
        int length = data.Length + more.Length;
        if (_blocks.Count == 0 || length > _blocks[^1].Length - _used)
        {
            // Blocks double up to their full size, so a short file allocates little.
            int size = _blocks.Count == 0 ? 4096 : Math.Min(BlockSize, _blocks[^1].Length * 2);
            _blocks.Add(new byte[Math.Max(size, length)]);
            _used = 0;
        }

        long position = ((long)(_blocks.Count - 1) << 32) | (uint)_used;
        data.CopyTo(_blocks[^1].AsSpan(_used));
        more.CopyTo(_blocks[^1].AsSpan(_used + data.Length));
        _used += length;
        return position;
    }

    public byte[] Read(long position, int length) => _blocks[(int)(position >> 32)].AsSpan((int)(uint)position, length).ToArray();
}
