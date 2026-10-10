namespace Encspect.Core;

public sealed class Node
{
    private readonly List<Node>? _children;
    private readonly Func<IReadOnlyList<Node>>? _build;
    private WeakReference<IReadOnlyList<Node>>? _built;

    public Node(string name, long offset, long length, string? value = null)
        : this(name, offset, length, value, null)
    {
        _children = [];
    }

    // For parts that repeat by the thousand, such as the frames of an audio stream: the children
    // are built when they are first read and held only weakly, so they are built again once
    // collected and a report never keeps all of them in memory. Such a node takes no added
    // children.
    public Node(string name, long offset, long length, string? value, Func<IReadOnlyList<Node>>? build)
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

    public string? Value { get; set; }

    public IReadOnlyList<Node> Children
    {
        get
        {
            if (_build is null)
            {
                return _children ?? [];
            }

            if (_built is not null && _built.TryGetTarget(out IReadOnlyList<Node>? built))
            {
                return built;
            }

            built = _build();
            _built = new WeakReference<IReadOnlyList<Node>>(built);
            return built;
        }
    }

    public long End => Offset + Length;

    public Node Add(string name, long offset, long length, string? value = null) => Add(new Node(name, offset, length, value));

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
}
