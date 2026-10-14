using Encspect.Core;

namespace Encspect.Formats.Dts;

// Builds the nodes of one unit while UnitSyntax reads it: fields at their bits, and groups that
// cover the fields read between Open and Close.
internal sealed class Tree(Locator locator)
{
    private readonly List<Group> _open = [];

    public List<Node> Nodes { get; } = [];

    public Locator Locator => locator;

    public int Depth => _open.Count;

    public void Open(string name, int start, string description, string? topic = null, NodeKind kind = NodeKind.Detail) =>
        _open.Add(new Group(name, start, [], description, topic, kind));

    public Node Close(int end, NodeValue? value = null)
    {
        Group group = _open[^1];
        _open.RemoveAt(_open.Count - 1);
        Node node = locator.Group(group.Name, group.Start, Math.Max(group.Start, end), value, group.Description, group.Topic, group.Kind);
        foreach (Node child in group.Children)
        {
            node.Add(child);
        }

        Add(node);
        return node;
    }

    // Closes the groups opened after depth groups were open, at end.
    public void CloseTo(int depth, int end)
    {
        while (_open.Count > depth)
        {
            Close(end);
        }
    }

    public Node Field(string name, int start, int end, NodeValue? value, string description, string? topic = null)
    {
        Node node = locator.Field(name, start, end, value, description, topic);
        Add(node);
        return node;
    }

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

    // The last node added to the group that is open (or the unit), to hang a child on.
    public Node? Last => _open.Count > 0 ? _open[^1].Children.LastOrDefault() : Nodes.LastOrDefault();

    private sealed record Group(string Name, int Start, List<Node> Children, string Description, string? Topic, NodeKind Kind);
}
