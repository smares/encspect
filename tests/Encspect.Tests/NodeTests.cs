using Encspect.Core;

namespace Encspect.Tests;

public class NodeTests
{
    [Fact]
    public void AddAppendsChildrenInOrder()
    {
        var root = new Node("File", 0, 100);

        Node first = root.Add("A", 0, 10);
        Node second = root.Add("B", 10, 90, "x");

        Assert.Collection(
            root.Children,
            child => Assert.Same(first, child),
            child => Assert.Same(second, child));
        Assert.Equal("x", second.Value);
    }

    [Fact]
    public void ChildrenAreBuiltWhenFirstRead()
    {
        int builds = 0;
        var node = new Node("Frame", 0, 10, "x", () =>
        {
            builds++;
            return [new Node("Header", 0, 4)];
        });

        Assert.Equal(0, builds);
        IReadOnlyList<Node> children = node.Children;

        Assert.Equal("Header", Assert.Single(children).Name);
        Assert.Same(children, node.Children);
        Assert.Equal(1, builds);
    }

    [Fact]
    public void NodeWithBuiltChildrenTakesNoAddedChildren()
    {
        var node = new Node("Frame", 0, 10, null, () => []);

        Assert.Throws<InvalidOperationException>(() => node.Add("Header", 0, 4));
        Assert.Throws<InvalidOperationException>(node.SortChildrenByOffset);
    }

    [Fact]
    public void RejectsNegativeOffsetAndLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Node("A", -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Node("A", 0, -1));
    }

    [Fact]
    public void RejectsEmptyName()
    {
        Assert.Throws<ArgumentException>(() => new Node(string.Empty, 0, 0));
    }
}
