namespace Encspect.Formats.Opus;

// Records of every packet and frame of a stream, in blocks: a long stream's records are never
// copied into a larger array and never need one array of their full size. The first block grows as
// a List does, so a short stream allocates little.
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

    public void RemoveLast()
    {
        ArgumentOutOfRangeException.ThrowIfZero(Count);
        Count--;
    }
}
