namespace Encspect.Core;

public sealed class Node
{
    private readonly List<Node> _children = [];

    public Node(string name, long offset, long length, string? value = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        Name = name;
        Offset = offset;
        Length = length;
        Value = value;
    }

    public string Name { get; }

    public long Offset { get; }

    public long Length { get; }

    public string? Value { get; set; }

    public IReadOnlyList<Node> Children => _children;

    public long End => Offset + Length;

    public Node Add(string name, long offset, long length, string? value = null)
    {
        var child = new Node(name, offset, length, value);
        _children.Add(child);
        return child;
    }

    public Node Add(Node child)
    {
        ArgumentNullException.ThrowIfNull(child);

        _children.Add(child);
        return child;
    }
}
