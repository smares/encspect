namespace Encspect.Core;

// Nodes may be read from several threads at once, for example by an export while the GUI shows
// the tree: children built on demand are built under a compare-and-swap, so all threads see the
// same list while it lives.
public sealed class Node
{
    // How deep a tree may nest below its root. Writers stop there and say so, so a tree that a
    // damaged container nests thousands deep cannot overflow their stack; a parser that would nest
    // deeper reports a finding instead.
    public const int MaxDepth = 64;

    private readonly List<Node>? _children;
    private readonly Func<IReadOnlyList<Node>>? _build;
    private WeakReference<IReadOnlyList<Node>>? _built;

    // The bit offset in the low three bits and the bit length above them, or -1: reports hold
    // hundreds of thousands of nodes, so the bit position takes no more than an int.
    private int _bits = -1;

    public Node(string name, long offset, long length, NodeValue? value = null)
        : this(name, offset, length, value, null)
    {
        _children = [];
    }

    // For parts that repeat by the thousand, such as the frames of an audio stream: the children
    // are built when they are first read and held only weakly, so they are built again once
    // collected and a report never keeps all of them in memory. Such a node takes no added
    // children.
    public Node(string name, long offset, long length, NodeValue? value, Func<IReadOnlyList<Node>>? build)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        Name = name;
        Offset = offset;
        Length = length;
        Value = value;
        _build = build;
    }

    public string Name { get; }

    public long Offset { get; }

    public long Length { get; }

    // For a field that does not cover whole bytes: the bit within the byte at Offset where it
    // starts, counted from the most significant bit (0 to 7), and its width in bits. Null for a
    // field of whole bytes. Offset and Length still cover every byte the field touches.
    public int? BitOffset => _bits < 0 ? null : _bits & 7;

    public int? BitLength => _bits < 0 ? null : _bits >> 3;

    public NodeValue? Value { get; set; }

    public NodeKind Kind { get; init; }

    public IReadOnlyList<Node> Children
    {
        get
        {
            if (_build is null)
            {
                return _children ?? [];
            }

            IReadOnlyList<Node>? built = null;
            while (true)
            {
                WeakReference<IReadOnlyList<Node>>? current = Volatile.Read(ref _built);
                if (current is not null && current.TryGetTarget(out IReadOnlyList<Node>? existing))
                {
                    return existing;
                }

                built ??= Build();
                if (Interlocked.CompareExchange(ref _built, new WeakReference<IReadOnlyList<Node>>(built), current) == current)
                {
                    return built;
                }
            }
        }
    }

    // Whether the node has children, without building them: true for a node whose children are
    // built on demand until a build shows there are none.
    public bool HasChildren
    {
        get
        {
            if (_build is null)
            {
                return _children is { Count: > 0 };
            }

            return Volatile.Read(ref _built) is not { } built || !built.TryGetTarget(out IReadOnlyList<Node>? children) || children.Count > 0;
        }
    }

    public long End => Offset + Length;

    // A node for bits [startBit, endBit) counted from the most significant bit of the byte at
    // offset. It covers every byte those bits touch, and carries the bit position unless the bits
    // are whole bytes; an empty field inside a byte covers that byte.
    public static Node FromBits(string name, long offset, long startBit, long endBit, NodeValue? value = null, NodeKind kind = NodeKind.Detail)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startBit);
        ArgumentOutOfRangeException.ThrowIfLessThan(endBit, startBit);

        long first = offset + (startBit / 8);
        long length = endBit == startBit ? (startBit % 8 == 0 ? 0 : 1) : ((endBit - 1) / 8) - (startBit / 8) + 1;
        var node = new Node(name, first, length, value) { Kind = kind };

        // A span of more than 256 Mbit is no field; its bytes alone place it.
        bool isWholeBytes = startBit % 8 == 0 && endBit % 8 == 0;
        if (!isWholeBytes && endBit - startBit <= int.MaxValue >> 3)
        {
            node._bits = (int)(((endBit - startBit) << 3) | (startBit % 8));
        }

        return node;
    }

    public Node Add(string name, long offset, long length, NodeValue? value = null) => Add(new Node(name, offset, length, value));

    public Node Add(Node child)
    {
        ArgumentNullException.ThrowIfNull(child);

        AddedChildren.Add(child);
        return child;
    }

    // Stable, so children at the same offset keep their order.
    public void SortChildrenByOffset()
    {
        var sorted = AddedChildren.OrderBy(c => c.Offset).ToList();
        AddedChildren.Clear();
        AddedChildren.AddRange(sorted);
    }

    private List<Node> AddedChildren =>
        _children ?? throw new InvalidOperationException($"The children of {Name} are built on demand and cannot be added to.");

    // A builder runs long after the analysis, inside a writer or the GUI, where an exception would
    // end the batch, so a failing builder shows as an error in the tree instead.
    private IReadOnlyList<Node> Build()
    {
        try
        {
            return _build!();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return [new Node("Error", Offset, Length, $"The children could not be built: {ex.GetType().Name}: {ex.Message}")];
        }
    }
}
