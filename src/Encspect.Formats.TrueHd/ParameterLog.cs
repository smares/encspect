using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// Collects the nodes of the fields a SubstreamReader reads, when it runs to build the nodes of a unit once the analysis is over. Bits are
// counted from the start of the substream, whose first byte is at the offset the log is made with.
internal sealed class ParameterLog(long substreamOffset)
{
    private sealed record Scope(string Name, int StartBit, string Description, string? Topic, List<Node> Children);

    private readonly Stack<Scope> _stack = new();
    private readonly List<Node> _roots = [];

    public IReadOnlyList<Node> Roots => _roots;

    public void Open(string name, int startBit, string description, string? topic) => _stack.Push(new Scope(name, startBit, description, topic, []));

    public void Close(int endBit, NodeValue? value)
    {
        Scope open = _stack.Pop();
        Node node = Node.FromBits(open.Name, substreamOffset, open.StartBit, endBit, value, NodeKind.Detail, open.Description, open.Topic);
        foreach (Node child in open.Children)
        {
            node.Add(child);
        }

        Add(node);
    }

    public void Field(string name, int startBit, int endBit, NodeValue? value, string description, string? topic = null) =>
        Add(Node.FromBits(name, substreamOffset, startBit, endBit, value, NodeKind.Detail, description, topic));

    // Closes every scope still open, as a read that failed leaves them.
    public void Unwind(int endBit)
    {
        while (_stack.Count > 0)
        {
            Close(Math.Max(endBit, _stack.Peek().StartBit), null);
        }
    }

    public long SubstreamOffset => substreamOffset;

    public void Add(Node node)
    {
        if (_stack.Count > 0)
        {
            _stack.Peek().Children.Add(node);
        }
        else
        {
            _roots.Add(node);
        }
    }
}
