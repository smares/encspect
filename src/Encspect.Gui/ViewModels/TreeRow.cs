using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// One node of the report as a row of the structure tree. Rows are made only when the list shows
// them or a path leads through them, and a node's children are read only when its row is first
// expanded.
public sealed class TreeRow : ObservableObject
{
    public const double IndentPerLevel = 16;

    private bool _isExpanded;

    internal TreeRow(Node node, TreeRow? parent, int childIndex)
    {
        Node = node;
        Parent = parent;
        ChildIndex = childIndex;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    public Node Node { get; }

    public TreeRow? Parent { get; }

    public int ChildIndex { get; }

    // 0 for the report's root.
    public int Depth { get; }

    public double Indent => Depth * IndentPerLevel;

    public string Name => Node.Name;

    public string? Value => Node.Value?.ToString();

    // What the row's tooltip shows: the name and value, then what the field is.
    public string Heading => Value is { } value ? $"{Name}: {value}" : Name;

    public string? Description => Node.Description;

    public bool HasDescription => Node.Description is not null;

    public NodeKind Kind => Node.Kind;

    // Tags, streams and statistics stand out from the details and frames around them.
    public bool IsSection => Node.Kind is NodeKind.Tag or NodeKind.Stream or NodeKind.Statistics;

    // Asks the node without building its children. Trees nest at most Node.MaxDepth levels below
    // the root, as the writers show them.
    public bool HasChildren => Depth < Node.MaxDepth && Node.HasChildren;

    public bool IsExpanded
    {
        get => _isExpanded;
        internal set => Set(ref _isExpanded, value);
    }

    // The node's children, held while the row lives once it has been expanded: Node keeps
    // children built on demand only weakly.
    internal IReadOnlyList<Node>? Children { get; private set; }

    // Rows made so far for the children, by child index.
    internal TreeRow?[]? Rows { get; private set; }

    // The expanded children, by child index; a collapsed child keeps its own state for when it is
    // expanded again, but is not listed here.
    internal SortedList<int, TreeRow> ExpandedChildren { get; } = [];

    // How many rows the list shows below this row while it is expanded: its children, and the rows
    // below those of them that are expanded.
    internal int VisibleCount { get; set; }

    internal IReadOnlyList<Node> LoadChildren()
    {
        if (Children is null)
        {
            Children = Node.Children;
            Rows = new TreeRow?[Children.Count];
            VisibleCount = Children.Count;
        }

        return Children;
    }

    internal TreeRow RowAt(int childIndex)
    {
        IReadOnlyList<Node> children = LoadChildren();
        return Rows![childIndex] ??= new TreeRow(children[childIndex], this, childIndex);
    }
}
