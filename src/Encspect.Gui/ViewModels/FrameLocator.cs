using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// Finds the frame node at a file offset by containment, as the host finds a finding's node: it
// descends only into children whose bytes hold the offset, and among the many frames of a stream it
// searches by offset instead of reading them in turn, so of 100,000 frames only a few are read and
// none has its own children built.
public static class FrameLocator
{
    // Lists up to this long are read child by child.
    private const int LinearLimit = 32;

    // What Search returns when the offsets it read are out of order.
    private const int Unordered = -2;

    // The child indexes from start to the frame (NodeKind.Frame) that holds the offset, or null.
    public static List<int>? PathFrom(Node start, long offset)
    {
        ArgumentNullException.ThrowIfNull(start);

        var path = new List<int>();
        return Descend(start, offset, path, 0) ? path : null;
    }

    private static bool Descend(Node node, long offset, List<int> path, int depth)
    {
        if (depth >= Node.MaxDepth || !node.HasChildren)
        {
            return false;
        }

        IReadOnlyList<Node> children = node.Children;
        foreach (int index in Containing(children, offset))
        {
            Node child = children[index];
            path.Add(index);
            if (child.Kind == NodeKind.Frame)
            {
                return true;
            }

            // Tags, statistics and side information hold no frames.
            if (child.Kind is NodeKind.Stream or NodeKind.Detail && Descend(child, offset, path, depth + 1))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    // The children that hold the offset. A long list is the leading details of a stream followed
    // by its frames and gaps in file order: the leading ones are read in turn up to the first
    // frame, and the rest is searched by offset. Where that order may not hold, every other child
    // is read in turn: when the search finds the children out of order, when what it finds does
    // not hold the offset or leads to no frame (frames out of file order, a trailing child that
    // starts before them). Only an offset past the last child is left at that, as in order.
    private static IEnumerable<int> Containing(IReadOnlyList<Node> children, long offset)
    {
        int count = children.Count;
        int first = 0;
        int found = -1;
        if (count > LinearLimit)
        {
            while (first < count && children[first].Kind != NodeKind.Frame)
            {
                if (Holds(children[first], offset))
                {
                    yield return first;
                }

                first++;
            }

            found = Search(children, first, count, offset);
            if (found >= 0 && Holds(children[found], offset))
            {
                yield return found;
            }
            else if (found == count - 1)
            {
                yield break;
            }
        }

        // A short list, or a long one whose search found nothing that leads to a frame.
        for (int i = first; i < count; i++)
        {
            if (i != found && Holds(children[i], offset))
            {
                yield return i;
            }
        }
    }

    private static bool Holds(Node node, long offset) => node.Offset <= offset && (offset < node.End || (node.Length == 0 && offset == node.Offset));

    // The last child in [low, high) that starts at or before the offset, -1 when none does, or
    // Unordered when the children it read are not in file order. Each step guesses
    // the position from the offsets at both ends, which lands at once or nearly in a stream of
    // frames of similar sizes, and every other step halves the range, so a stream of uneven frames
    // still takes no more than twice the steps of a binary search.
    private static int Search(IReadOnlyList<Node> children, int low, int high, long offset)
    {
        if (low >= high)
        {
            return -1;
        }

        // Invariant: children[low] starts at or before the offset, children[high] after it.
        high--;
        long first = children[low].Offset;
        long last = children[high].Offset;
        if (last < first)
        {
            return Unordered;
        }

        if (first > offset)
        {
            return -1;
        }

        if (last <= offset)
        {
            return high;
        }

        bool interpolate = true;
        long lowOffset = first;
        long highOffset = last;
        while (high - low > 1)
        {
            int middle = interpolate && highOffset > lowOffset
                ? low + (int)Math.Clamp((double)(offset - lowOffset) / (highOffset - lowOffset) * (high - low), 1, high - low - 1)
                : low + ((high - low) / 2);
            interpolate = !interpolate;
            long middleOffset = children[middle].Offset;
            if (middleOffset < lowOffset || middleOffset > highOffset)
            {
                return Unordered;
            }

            if (middleOffset <= offset)
            {
                (low, lowOffset) = (middle, middleOffset);
            }
            else
            {
                (high, highOffset) = (middle, middleOffset);
            }
        }

        return low;
    }
}
