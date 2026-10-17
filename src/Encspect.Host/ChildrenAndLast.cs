using System.Collections;
using Encspect.Core;

namespace Encspect.Host;

// A list of nodes followed by one more, without copying the list: the children of a tag, which may be built in blocks as they are read, and the
// detection node the host adds after them.
internal sealed class ChildrenAndLast(IReadOnlyList<Node> children, Node last) : IReadOnlyList<Node>
{
    public int Count => children.Count + 1;

    public Node this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            return index == children.Count ? last : children[index];
        }
    }

    public IEnumerator<Node> GetEnumerator()
    {
        foreach (Node child in children)
        {
            yield return child;
        }

        yield return last;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
