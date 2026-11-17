using Encspect.Core;

namespace Encspect.Formats.Ac3;

// Builds the nodes of one frame while FrameSyntax reads it: fields at their bits, and groups that
// cover the fields read between Open and Close.
internal sealed class Tree(long frameOffset, MappedBytes? mapped = null)
{
    private readonly List<Group> _open = [];

    public List<Node> Nodes { get; } = [];

    public long FrameOffset => frameOffset;

    public void Open(string name, int start, string description, string? topic = null, NodeKind kind = NodeKind.Detail) =>
        _open.Add(new Group(name, start, [], description, topic, kind));

    public Node Close(int end, NodeValue? value = null)
    {
        Group group = _open[^1];
        _open.RemoveAt(_open.Count - 1);
        Node node = Bits(group.Name, group.Start, Math.Max(group.Start, end), value, group.Kind, group.Description, group.Topic);
        foreach (Node child in group.Children)
        {
            node.Add(child);
        }

        Add(node);
        return node;
    }

    public int OpenCount => _open.Count;

    // Closes the groups opened after the first count of them, at end.
    public void CloseTo(int count, int end)
    {
        while (_open.Count > count)
        {
            Close(end);
        }
    }

    // Closes every open group at end, as where a frame's data stops inside them.
    public void CloseAll(int end)
    {
        while (_open.Count > 0)
        {
            Close(end);
        }
    }

    public Node Field(string name, int start, int end, NodeValue? value, string description, string? topic = null)
    {
        Node node = Bits(name, start, end, value, NodeKind.Detail, description, topic);
        Add(node);
        return node;
    }

    // The field at its bits of the frame: in the pieces of the file the frame is made of where it is in pieces (a field past the frame's own bytes is placed as if they were one run).
    private Node Bits(string name, int start, int end, NodeValue? value, NodeKind kind, string description, string? topic) =>
        mapped is not null && end <= mapped.Length * 8L
            ? Node.FromBits(name, mapped, start, end, value, kind, description, topic)
            : Node.FromBits(name, frameOffset, start, end, value, kind, description, topic);

    public void Add(Node node)
    {
        if (_open.Count > 0)
        {
            _open[^1].Children.Add(node);
        }
        else
        {
            Nodes.Add(node);
        }
    }

    private sealed record Group(string Name, int Start, List<Node> Children, string Description, string? Topic, NodeKind Kind);
}
