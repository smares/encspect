using Encspect.Core;

namespace Encspect.Host;

// Finds where a node sits in a report's tree: the child indexes from the root to the node equal to
// it in Name, Offset and Length, which is the contract (a parser may point at a node it built again,
// as frames are). Several nodes can be equal, as the fields of bytes that are not in the file as
// they are (an inflated CodecPrivate) all cover the same range, so the very same Node object, where
// the tree holds it, wins over an equal one that is met earlier. Only children whose bytes contain
// the node's are searched, so of a stream's frames only the ones on the way have their children
// built; each list of children is built once and kept while the finder lives.
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
        if (Find(root, target, path, isIdentity: true))
        {
            return path;
        }

        path.Clear();
        return Find(root, target, path, isIdentity: false) ? path : null;
    }

    private static bool Matches(Node node, Node target, bool isIdentity) =>
        isIdentity ? ReferenceEquals(node, target) : node == target || (node.Offset == target.Offset && node.Length == target.Length && node.Name == target.Name);

    private bool Find(Node node, Node target, List<int> path, bool isIdentity)
    {
        if (Matches(node, target, isIdentity))
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
            if (Matches(index.Children[i], target, isIdentity))
            {
                path.Add(i);
                return true;
            }
        }

        foreach (int i in containing)
        {
            path.Add(i);
            if (Find(index.Children[i], target, path, isIdentity))
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
    // are many. Each child is read once, in order, since a long list such as a stream's frames or
    // packets builds its nodes when read and need not keep them. Children in file order whose ends
    // never go back, as frames, pages and packets are, are searched by bisection and need no arrays;
    // others are sorted by offset, and a tree over that order holds the furthest end in each part,
    // so only parts that can reach the range's end are searched.
    private sealed class ChildIndex
    {
        private const int LinearLimit = 16;

        private readonly bool _ordered;
        private readonly int[] _order = [];
        private readonly long[] _offsets = [];
        private readonly long[] _maxEnds = [];
        private readonly int _leaves;

        public ChildIndex(IReadOnlyList<Node> children)
        {
            Children = children;
            int count = children.Count;
            if (count <= LinearLimit)
            {
                return;
            }

            _ordered = true;
            long lastOffset = long.MinValue;
            long lastEnd = long.MinValue;
            foreach (Node child in children)
            {
                _ordered &= lastOffset <= child.Offset && lastEnd <= child.End;
                (lastOffset, lastEnd) = (child.Offset, child.End);
            }

            if (_ordered)
            {
                return;
            }

            var offsets = new long[count];
            var ends = new long[count];
            int i = 0;
            foreach (Node child in children)
            {
                offsets[i] = child.Offset;
                ends[i] = child.End;
                i++;
            }

            _order = Enumerable.Range(0, count).ToArray();
            Array.Sort(_order, (a, b) => offsets[a] != offsets[b] ? offsets[a].CompareTo(offsets[b]) : a.CompareTo(b));
            _offsets = _order.Select(o => offsets[o]).ToArray();
            _leaves = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)count);
            _maxEnds = new long[2 * _leaves];
            Array.Fill(_maxEnds, long.MinValue);
            for (int j = 0; j < _order.Length; j++)
            {
                _maxEnds[_leaves + j] = ends[_order[j]];
            }

            for (int j = _leaves - 1; j > 0; j--)
            {
                _maxEnds[j] = Math.Max(_maxEnds[2 * j], _maxEnds[(2 * j) + 1]);
            }
        }

        public IReadOnlyList<Node> Children { get; }

        // In the order of the children.
        public List<int> Containing(Node target)
        {
            var found = new List<int>();
            if (_ordered)
            {
                // The children that start at or before the range, of which those from the first that
                // reaches the range's end on contain it, since their ends never go back.
                int last = FirstIndex(i => Children[i].Offset > target.Offset) - 1;
                for (int i = FirstIndex(i => Children[i].End >= target.End); i <= last; i++)
                {
                    found.Add(i);
                }

                return found;
            }

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

        // The first index for which a condition that holds from some index on holds, or Count.
        private int FirstIndex(Func<int, bool> holds)
        {
            int low = 0;
            int high = Children.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                (low, high) = holds(middle) ? (low, middle) : (middle + 1, high);
            }

            return low;
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
