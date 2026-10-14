namespace Encspect.Core;

// The children of a node that holds many records: leading nodes (configuration, encoder,
// statistics), then one node per record, built in blocks of 1,024 when first read and held weakly,
// as Node holds children built on demand, so writing a stream of a million records never keeps all
// their nodes. Readers on several threads get the same nodes while a block lives; after a
// collection a block is built again from the same records.
//
// A builder that throws does not reach the reader: the entries of its block are error nodes (as a
// node whose factory throws has one), over the bytes of the node that holds the list.
//
// Thread safety rests on three rules: a block is published with a compare-and-swap, so two threads
// that build the same block both succeed and one result is dropped; the hint of the block read last
// is an immutable record read once per lookup, so an index and its nodes can never come from two
// different blocks; and the builders only read data that nobody changes any more.
public sealed class BlockedChildren : IReadOnlyList<Node>
{
    private const int BlockSize = 1024;

    private readonly IReadOnlyList<Node> _leading;
    private readonly int _count;
    private readonly Func<int, int, Node[]> _buildBlock;
    private readonly WeakReference<Node[]>?[] _blocks;

    // The block read last, strongly held, so reading nodes in order does not look up a weak
    // reference each time.
    private Block? _last;

    // The offset and length of the node that holds the list, for the error nodes of a failed block.
    private (long Offset, long Length) _owner;

    // Builds the node of the record at an index. Called once per record of a block, in order, on the
    // thread that reads it; it must not depend on state shared with other builds.
    public BlockedChildren(IReadOnlyList<Node> leading, int count, Func<int, Node> build)
        : this(leading, count, (start, length) =>
        {
            var nodes = new Node[length];
            for (int i = 0; i < nodes.Length; i++)
            {
                nodes[i] = build(start + i);
            }

            return nodes;
        })
    {
    }

    // Builds the nodes of a whole block: the records [start, start + length), for a builder that
    // shares work between neighbors. The same rules apply.
    public BlockedChildren(IReadOnlyList<Node> leading, int count, Func<int, int, Node[]> buildBlock)
    {
        ArgumentNullException.ThrowIfNull(leading);
        ArgumentNullException.ThrowIfNull(buildBlock);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _leading = leading;
        _count = count;
        _buildBlock = buildBlock;
        _blocks = new WeakReference<Node[]>?[(count + BlockSize - 1) / BlockSize];
    }

    public int Count => _leading.Count + _count;

    public Node this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            if (index < _leading.Count)
            {
                return _leading[index];
            }

            int entry = index - _leading.Count;
            return BlockOf(entry / BlockSize)[entry % BlockSize];
        }
    }

    public IEnumerator<Node> GetEnumerator()
    {
        foreach (Node node in _leading)
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
                built = BuildBlock(start, Math.Min(BlockSize, _count - start));
            }

            if (Interlocked.CompareExchange(ref _blocks[block], new WeakReference<Node[]>(built), current) == current)
            {
                Volatile.Write(ref _last, new Block(block, built));
                return built;
            }
        }
    }

    internal void SetOwner(long offset, long length) => _owner = (offset, length);

    private Node[] BuildBlock(int start, int length)
    {
        try
        {
            return _buildBlock(start, length);
        }
        catch (Exception ex) when (Node.IsBuildFailure(ex))
        {
            (long offset, long span) = _owner;
            var failed = new Node[length];
            for (int i = 0; i < failed.Length; i++)
            {
                failed[i] = Node.BuildFailure(offset, span, ex);
            }

            return failed;
        }
    }

    private sealed record Block(int Index, Node[] Nodes);
}
