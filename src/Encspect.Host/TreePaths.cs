using Encspect.Core;

namespace Encspect.Host;

// Finds where a node sits in a report's tree: the child indexes from the root to the node equal to
// it in Name, Offset and Length. Only children whose bytes contain the node's are searched, so of
// a stream's frames only the ones on the way have their children built; each list of children is
// built once and kept while the finder lives.
internal sealed class TreePaths(Node root)
{
    private readonly Dictionary<Node, ChildIndex> _indexes = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<int>? Find(Node? target)
    {
        if (target is null)
        {
            return null;
        }

        var path = new List<int>();
        return Find(root, target, path) ? path : null;
    }

    private static bool Matches(Node node, Node target) =>
        node == target || (node.Offset == target.Offset && node.Length == target.Length && node.Name == target.Name);

    private bool Find(Node node, Node target, List<int> path)
    {
        if (Matches(node, target))
        {
            return true;
        }

        // The writers stop at Node.MaxDepth, so a node below it is not in the report they write.
        if (path.Count >= Node.MaxDepth || !node.HasChildren)
        {
            return false;
        }

        ChildIndex index = IndexOf(node);
        List<int> containing = index.Containing(target);
        foreach (int i in containing)
        {
            if (Matches(index.Children[i], target))
            {
                path.Add(i);
                return true;
            }
        }

        foreach (int i in containing)
        {
            path.Add(i);
            if (Find(index.Children[i], target, path))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    private ChildIndex IndexOf(Node node)
    {
        if (!_indexes.TryGetValue(node, out ChildIndex? index))
        {
            _indexes[node] = index = new ChildIndex(node.Children);
        }

        return index;
    }

    // The children whose bytes contain a range, found without looking at every child when there
    // are many: they are sorted by offset, and a tree over that order holds the furthest end in
    // each part, so only parts that can reach the range's end are searched.
    private sealed class ChildIndex
    {
        private const int LinearLimit = 16;

        private readonly int[] _order = [];
        private readonly long[] _offsets = [];
        private readonly long[] _maxEnds = [];
        private readonly int _leaves;

        public ChildIndex(IReadOnlyList<Node> children)
        {
            Children = children;
            if (children.Count <= LinearLimit)
            {
                return;
            }

            _order = Enumerable.Range(0, children.Count).ToArray();
            Array.Sort(_order, (a, b) => children[a].Offset != children[b].Offset ? children[a].Offset.CompareTo(children[b].Offset) : a.CompareTo(b));
            _offsets = _order.Select(i => children[i].Offset).ToArray();
            _leaves = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)children.Count);
            _maxEnds = new long[2 * _leaves];
            Array.Fill(_maxEnds, long.MinValue);
            for (int i = 0; i < _order.Length; i++)
            {
                _maxEnds[_leaves + i] = children[_order[i]].End;
            }

            for (int i = _leaves - 1; i > 0; i--)
            {
                _maxEnds[i] = Math.Max(_maxEnds[2 * i], _maxEnds[(2 * i) + 1]);
            }
        }

        public IReadOnlyList<Node> Children { get; }

        // In the order of the children.
        public List<int> Containing(Node target)
        {
            var found = new List<int>();
            if (_maxEnds.Length == 0)
            {
                for (int i = 0; i < Children.Count; i++)
                {
                    if (Contains(Children[i], target))
                    {
                        found.Add(i);
                    }
                }

                return found;
            }

            // Children that start at or before the range.
            int count = UpperBound(_offsets, target.Offset);
            Collect(1, 0, _leaves, count, target.End, found);
            found.Sort();
            return found;
        }

        private static bool Contains(Node node, Node target) => node.Offset <= target.Offset && target.End <= node.End;

        private void Collect(int tree, int low, int high, int count, long end, List<int> found)
        {
            if (low >= count || _maxEnds[tree] < end)
            {
                return;
            }

            if (high - low == 1)
            {
                found.Add(_order[low]);
                return;
            }

            int middle = (low + high) / 2;
            Collect(2 * tree, low, middle, count, end, found);
            Collect((2 * tree) + 1, middle, high, count, end, found);
        }

        private static int UpperBound(long[] values, long value)
        {
            int low = 0;
            int high = values.Length;
            while (low < high)
            {
                int middle = (low + high) / 2;
                (low, high) = values[middle] <= value ? (middle + 1, high) : (low, middle);
            }

            return low;
        }
    }
}
