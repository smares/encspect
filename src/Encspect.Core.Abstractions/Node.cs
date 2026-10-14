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

    // The added children (a List<Node>) or the function that builds them on demand: one field for
    // both, so Ranges costs a report of hundreds of thousands of nodes no memory.
    private readonly object? _source;
    private WeakReference<IReadOnlyList<Node>>? _built;

    // The bit offset in the low three bits, the bit order in the fourth and the bit length above
    // them, or -1: reports hold hundreds of thousands of nodes, so the bit position takes no more
    // than an int.
    private int _bits = -1;

    private const int LeastSignificantFirstFlag = 8;

    private readonly IReadOnlyList<ByteRange>? _ranges;

    public Node(string name, long offset, long length, NodeValue? value = null)
        : this(name, offset, length, value, null)
    {
        _source = new List<Node>();
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
        _source = build;
    }

    public string Name { get; }

    public long Offset { get; }

    public long Length { get; }

    // For a field that does not cover whole bytes: the bit within the byte at Offset where it
    // starts (0 to 7), counted as BitOrder says, and its width in bits. Null for a field of whole
    // bytes. Offset and Length still cover every byte the field touches.
    public int? BitOffset => _bits < 0 ? null : _bits & 7;

    public int? BitLength => _bits < 0 ? null : _bits >> 4;

    // How BitOffset counts and where the field's bits run on: from the most significant bit of a
    // byte toward the least, then into the next byte's most significant bit (MPEG, Speex, Opus),
    // or from the least significant bit toward the most, then into the next byte's least
    // significant bit (Vorbis I specification, section 2.1.4). Within each byte the field's bits
    // are contiguous either way.
    public BitOrder BitOrder => _bits >= 0 && (_bits & LeastSignificantFirstFlag) != 0 ? BitOrder.LeastSignificantFirst : BitOrder.MostSignificantFirst;

    // For a node whose bytes are not contiguous in the file, such as a packet that spans Ogg pages:
    // the ranges that hold them, in file order. Offset and Length cover them from the first to the
    // last. Null for contiguous bytes; ranges that touch are merged, so a node never holds one range.
    public IReadOnlyList<ByteRange>? Ranges
    {
        get => _ranges;
        init => _ranges = Normalize(value);
    }

    public NodeValue? Value { get; set; }

    public NodeKind Kind { get; init; }

    // What the field is and, where it helps, what this value means, in one or two sentences. The
    // parser that makes the node writes it, from strings it shares across nodes, so a report of a
    // million frames allocates none. Front ends show it as it is.
    public string? Description { get; init; }

    // The ID of a background article on the concept behind the node, such as "mid-side-stereo",
    // which the host serves (TopicLibrary). A node without one belongs to its nearest ancestor's.
    public string? Topic { get; init; }

    public IReadOnlyList<Node> Children
    {
        get
        {
            if (_source is not Func<IReadOnlyList<Node>>)
            {
                return (IReadOnlyList<Node>?)_source ?? [];
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
            if (_source is not Func<IReadOnlyList<Node>>)
            {
                return _source is List<Node> { Count: > 0 };
            }

            return Volatile.Read(ref _built) is not { } built || !built.TryGetTarget(out IReadOnlyList<Node>? children) || children.Count > 0;
        }
    }

    public long End => Offset + Length;

    // A node for bits [startBit, endBit) counted from the most significant bit of the byte at
    // offset, or from its least significant bit in BitOrder.LeastSignificantFirst. It covers every
    // byte those bits touch, and carries the bit position unless the bits are whole bytes; an empty
    // field inside a byte covers that byte.
    public static Node FromBits(string name, long offset, long startBit, long endBit, NodeValue? value = null, NodeKind kind = NodeKind.Detail,
        string? description = null, string? topic = null, BitOrder order = BitOrder.MostSignificantFirst)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startBit);
        ArgumentOutOfRangeException.ThrowIfLessThan(endBit, startBit);

        long first = offset + (startBit / 8);
        long length = endBit == startBit ? (startBit % 8 == 0 ? 0 : 1) : ((endBit - 1) / 8) - (startBit / 8) + 1;
        var node = new Node(name, first, length, value) { Kind = kind, Description = description, Topic = topic };

        node.SetBits(startBit, endBit, order);
        return node;
    }

    // A node for bytes [start, end) of mapped data, at the file bytes they came from, with Ranges
    // where those are not contiguous. Opaque data maps every field to its whole source.
    public static Node FromMapped(string name, MappedBytes bytes, int start, int end, NodeValue? value = null, NodeKind kind = NodeKind.Detail,
        string? description = null, string? topic = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        IReadOnlyList<ByteRange> ranges = bytes.FileRanges(start, end);
        if (bytes.IsOpaque || ranges.Count == 0)
        {
            return new Node(name, ranges.Count > 0 ? ranges[0].Offset : bytes.OffsetOf(start), ranges.Count > 0 ? ranges[0].Length : 0, value)
            {
                Kind = kind,
                Description = description,
                Topic = topic,
            };
        }

        return new Node(name, ranges[0].Offset, ranges[^1].End - ranges[0].Offset, value) { Kind = kind, Ranges = ranges, Description = description, Topic = topic };
    }

    // A node for bits [startBit, endBit) of mapped data, counted from the most significant bit of
    // its first byte, or from its least significant bit in BitOrder.LeastSignificantFirst. Within
    // one file range it is the node FromBits gives there; bits that continue after a gap (a field
    // split by an Ogg page header) also get Ranges, and BitOffset is then the first bit in the byte
    // at Offset and BitLength the field's width. Opaque data maps every field to its whole source,
    // without a bit position.
    public static Node FromBits(string name, MappedBytes bytes, long startBit, long endBit, NodeValue? value = null, NodeKind kind = NodeKind.Detail,
        string? description = null, string? topic = null, BitOrder order = BitOrder.MostSignificantFirst)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(startBit);
        ArgumentOutOfRangeException.ThrowIfLessThan(endBit, startBit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(endBit, bytes.Length * 8L);

        if (bytes.IsOpaque)
        {
            return new Node(name, bytes.FileStart, bytes.FileEnd - bytes.FileStart, value) { Kind = kind, Description = description, Topic = topic };
        }

        int first = (int)(startBit / 8);
        int end = endBit == startBit ? (startBit % 8 == 0 ? first : first + 1) : (int)((endBit + 7) / 8);
        IReadOnlyList<ByteRange> ranges = bytes.FileRanges(first, end);
        if (ranges.Count <= 1)
        {
            return FromBits(name, bytes.OffsetOf(first), startBit % 8, (startBit % 8) + endBit - startBit, value, kind, description, topic, order);
        }

        var node = new Node(name, ranges[0].Offset, ranges[^1].End - ranges[0].Offset, value) { Kind = kind, Ranges = ranges, Description = description, Topic = topic };
        node.SetBits(startBit, endBit, order);
        return node;
    }

    // Leaves a field of whole bytes, and a span of more than 128 Mbit, which is no field, without a
    // bit position: its bytes alone place it.
    private void SetBits(long startBit, long endBit, BitOrder order)
    {
        bool isWholeBytes = startBit % 8 == 0 && endBit % 8 == 0;
        if (!isWholeBytes && endBit - startBit <= int.MaxValue >> 4)
        {
            int flag = order == BitOrder.LeastSignificantFirst ? LeastSignificantFirstFlag : 0;
            _bits = ((int)(endBit - startBit) << 4) | flag | (int)(startBit % 8);
        }
    }

    public Node Add(string name, long offset, long length, NodeValue? value = null, string? description = null, string? topic = null) =>
        Add(new Node(name, offset, length, value) { Description = description, Topic = topic });

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
        _source as List<Node> ?? throw new InvalidOperationException($"The children of {Name} are built on demand and cannot be added to.");

    private ByteRange[]? Normalize(IReadOnlyList<ByteRange>? ranges)
    {
        if (ranges is null)
        {
            return null;
        }

        var merged = new List<ByteRange>(ranges.Count);
        foreach (ByteRange range in ranges)
        {
            if (range.Offset < 0 || range.Length < 0)
            {
                throw new ArgumentException("A range cannot have a negative offset or length.", nameof(ranges));
            }

            if (range.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0 && range.Offset < merged[^1].End)
            {
                throw new ArgumentException($"The ranges of {Name} must be in file order and must not overlap.", nameof(ranges));
            }

            if (merged.Count > 0 && range.Offset == merged[^1].End)
            {
                merged[^1] = merged[^1] with { Length = merged[^1].Length + range.Length };
            }
            else
            {
                merged.Add(range);
            }
        }

        if (merged.Count == 0 ? Length != 0 : merged[0].Offset != Offset || merged[^1].End != End)
        {
            throw new ArgumentException($"The ranges of {Name} must reach from its offset to its end.", nameof(ranges));
        }

        return merged.Count > 1 ? [.. merged] : null;
    }

    private const string ErrorDescription =
        "The parser failed to build this node's children, so they are missing. That is a bug in the analyzer, not damage in the file.";

    // A builder runs long after the analysis, inside a writer or the GUI, where an exception would
    // end the batch, so a failing builder shows as an error in the tree instead.
    private IReadOnlyList<Node> Build()
    {
        try
        {
            IReadOnlyList<Node> children = ((Func<IReadOnlyList<Node>>)_source!)();
            (children as BlockedChildren)?.SetOwner(Offset, Length);
            return children;
        }
        catch (Exception ex) when (IsBuildFailure(ex))
        {
            return [BuildFailure(Offset, Length, ex)];
        }
    }

    // Whether an exception from a builder shows as an error node: all but cancellation and memory.
    internal static bool IsBuildFailure(Exception ex) => ex is not OperationCanceledException and not OutOfMemoryException;

    // The node that stands for children that could not be built, over the bytes of their parent.
    internal static Node BuildFailure(long offset, long length, Exception ex) =>
        new("Error", offset, length, $"The children could not be built: {ex.GetType().Name}: {ex.Message}") { Description = ErrorDescription };
}
