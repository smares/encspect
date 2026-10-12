namespace Encspect.Formats.Ogg;

// Records of every page of a file, in blocks: a long file's records are never copied into a larger
// array and never need one array of their full size. The first block grows as a List does, so a
// short file allocates little.
internal sealed class ChunkedList<T>
    where T : struct
{
    private const int BlockBits = 12;
    private const int BlockSize = 1 << BlockBits;

    private readonly List<T[]> _blocks = [];

    public int Count { get; private set; }

    public ref T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            return ref _blocks[index >> BlockBits][index & (BlockSize - 1)];
        }
    }

    public void Add(in T item)
    {
        int block = Count >> BlockBits;
        int slot = Count & (BlockSize - 1);
        if (block == _blocks.Count)
        {
            _blocks.Add(new T[block == 0 ? 16 : BlockSize]);
        }
        else if (slot == _blocks[block].Length)
        {
            T[] grown = new T[Math.Min(BlockSize, slot * 2)];
            Array.Copy(_blocks[block], grown, slot);
            _blocks[block] = grown;
        }

        _blocks[block][slot] = item;
        Count++;
    }
}

// The segment tables page nodes are built from once the stream is closed (a report outlives the
// stream it was read from), packed one after another into shared blocks: no array per page. A
// position holds the block in its high 32 bits and the offset in it in the low ones; an item never
// spans two blocks. Written only while parsing, so builders on several threads read it safely.
internal sealed class ByteStore
{
    private const int BlockSize = 1 << 20;

    private readonly List<byte[]> _blocks = [];
    private int _used;

    public long Add(ReadOnlySpan<byte> data)
    {
        if (_blocks.Count == 0 || data.Length > _blocks[^1].Length - _used)
        {
            // Blocks double up to their full size, so a short file allocates little.
            int size = _blocks.Count == 0 ? 4096 : Math.Min(BlockSize, _blocks[^1].Length * 2);
            _blocks.Add(new byte[Math.Max(size, data.Length)]);
            _used = 0;
        }

        long position = ((long)(_blocks.Count - 1) << 32) | (uint)_used;
        data.CopyTo(_blocks[^1].AsSpan(_used));
        _used += data.Length;
        return position;
    }

    public ReadOnlySpan<byte> Read(long position, int length) => _blocks[(int)(position >> 32)].AsSpan((int)(uint)position, length);
}
