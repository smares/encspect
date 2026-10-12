using Encspect.Core;

namespace Encspect.Formats.Ac3;

// Records of every access unit, in blocks: a long stream's records are never copied into a larger
// array and never need one array of their full size. The first block grows as a List does, so a
// short stream allocates little.
internal sealed class ChunkedList<T>
    where T : struct
{
    private const int BlockBits = 12;
    private const int BlockSize = 1 << BlockBits;

    private readonly List<T[]> _blocks = [];

    public int Count { get; private set; }

    public ref readonly T this[int index]
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

// The children of a node that holds many access units or frames: leading nodes (configuration,
// encoder, statistics), then one node per record, built in blocks of 1,024 when first read and held
// weakly, as Node holds children built on demand, so writing a stream of a million access units
// never keeps all their nodes, and readers on several threads get the same nodes while a block
// lives.
internal sealed class BlockedChildren(IReadOnlyList<Node> leading, int count, Func<int, Node> build) : IReadOnlyList<Node>
{
    private const int BlockSize = 1024;

    private readonly WeakReference<Node[]>?[] _blocks = new WeakReference<Node[]>?[(count + BlockSize - 1) / BlockSize];

    // The block read last, so reading nodes in order does not look up a weak reference each time.
    private Block? _last;

    public int Count => leading.Count + count;

    public Node this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            if (index < leading.Count)
            {
                return leading[index];
            }

            int entry = index - leading.Count;
            return BlockOf(entry / BlockSize)[entry % BlockSize];
        }
    }

    public IEnumerator<Node> GetEnumerator()
    {
        foreach (Node node in leading)
        {
            yield return node;
        }

        for (int block = 0; block < _blocks.Length; block++)
        {
            foreach (Node node in BlockOf(block))
            {
                yield return node;
            }
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private Node[] BlockOf(int block)
    {
        if (Volatile.Read(ref _last) is { } last && last.Index == block)
        {
            return last.Nodes;
        }

        Node[]? built = null;
        while (true)
        {
            WeakReference<Node[]>? current = Volatile.Read(ref _blocks[block]);
            if (current is not null && current.TryGetTarget(out Node[]? existing))
            {
                Volatile.Write(ref _last, new Block(block, existing));
                return existing;
            }

            if (built is null)
            {
                int start = block * BlockSize;
                built = new Node[Math.Min(BlockSize, count - start)];
                for (int i = 0; i < built.Length; i++)
                {
                    built[i] = build(start + i);
                }
            }

            if (Interlocked.CompareExchange(ref _blocks[block], new WeakReference<Node[]>(built), current) == current)
            {
                Volatile.Write(ref _last, new Block(block, built));
                return built;
            }
        }
    }

    private sealed record Block(int Index, Node[] Nodes);
}
