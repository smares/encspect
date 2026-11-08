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

    [Fact]
    public void FailingBuilderYieldsOneErrorChild()
    {
        var node = new Node("Packets", 4, 10, null, () => throw new IndexOutOfRangeException("bug"));

        Node error = Assert.Single(node.Children);

        Assert.Equal(("Error", 4L, 10L), (error.Name, error.Offset, error.Length));
        Assert.Equal("The children could not be built: IndexOutOfRangeException: bug", error.Value);
    }

    [Fact]
    public void HasChildrenDoesNotBuildThem()
    {
        int builds = 0;
        var lazy = new Node("Frame", 0, 10, null, () =>
        {
            builds++;
            return [];
        });

        Assert.True(lazy.HasChildren);
        Assert.Equal(0, builds);
        IReadOnlyList<Node> children = lazy.Children;
        Assert.False(lazy.HasChildren);
        Assert.Empty(children);

        var plain = new Node("File", 0, 10);
        Assert.False(plain.HasChildren);
        plain.Add("A", 0, 1);
        Assert.True(plain.HasChildren);
    }

    // Threads that read a lazily built node at the same time all get the same children.
    [Fact]
    public void ChildrenBuiltOnDemandAreTheSameForEveryThread()
    {
        for (int round = 0; round < 50; round++)
        {
            var node = new Node("Frame", 0, 10, null, () =>
            {
                Thread.SpinWait(1000);
                return [new Node("Header", 0, 4)];
            });
            using var start = new Barrier(8);

            IReadOnlyList<Node>[] seen = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() =>
                {
                    start.SignalAndWait();
                    return node.Children;
                }))
                .ToArray()
                .Select(t => t.Result)
                .ToArray();

            Assert.All(seen, children => Assert.Same(seen[0], children));
        }
    }

    [Theory]
    [InlineData(11, 13, 1L, 1L, 3, 2)]
    [InlineData(0, 9, 0L, 2L, 0, 9)]
    [InlineData(7, 17, 0L, 3L, 7, 10)]
    [InlineData(30, 32, 3L, 1L, 6, 2)]
    public void FromBitsCoversTheBytesTheBitsTouch(int start, int end, long offset, long length, int bitOffset, int bitLength)
    {
        Node node = Node.FromBits("Field", 100, start, end, "x");

        Assert.Equal((100 + offset, length), (node.Offset, node.Length));
        Assert.Equal(bitOffset, node.BitOffset);
        Assert.Equal(bitLength, node.BitLength);
        Assert.Equal("x", node.Value);
    }

    [Fact]
    public void FromBitsKeepsTheBitOrder()
    {
        Node msb = Node.FromBits("Field", 100, 11, 13, "x");
        Node lsb = Node.FromBits("Field", 100, 11, 13, "x", order: BitOrder.LeastSignificantFirst);

        Assert.Equal(BitOrder.MostSignificantFirst, msb.BitOrder);
        Assert.Equal(BitOrder.LeastSignificantFirst, lsb.BitOrder);
        Assert.Equal((msb.Offset, msb.Length, msb.BitOffset, msb.BitLength), (lsb.Offset, lsb.Length, lsb.BitOffset, lsb.BitLength));

        // A field of whole bytes has no bit position, so no order either.
        Assert.Equal(BitOrder.MostSignificantFirst, Node.FromBits("Bytes", 0, 8, 24, order: BitOrder.LeastSignificantFirst).BitOrder);

        // Over mapped bytes, bits a gap splits keep the order too.
        var bytes = new MappedBytes(new byte[4], [new ByteRange(10, 2), new ByteRange(20, 2)]);
        Node split = Node.FromBits("Split", bytes, 12, 20, order: BitOrder.LeastSignificantFirst);
        Assert.Equal((BitOrder.LeastSignificantFirst, 4, 8), (split.BitOrder, split.BitOffset!.Value, split.BitLength!.Value));
    }

    [Fact]
    public void FromBitsLeavesWholeBytesWithoutBitPositions()
    {
        Node node = Node.FromBits("CRC", 10, 16, 32);

        Assert.Equal((12L, 2L), (node.Offset, node.Length));
        Assert.Null(node.BitOffset);
        Assert.Null(node.BitLength);
    }

    [Fact]
    public void EmptyBitFieldPointsAtItsByte()
    {
        Node inside = Node.FromBits("SCFSI", 10, 13, 13);
        Node between = Node.FromBits("SCFSI", 10, 16, 16);

        Assert.Equal((11L, 1L, 5, 0), (inside.Offset, inside.Length, inside.BitOffset!.Value, inside.BitLength!.Value));
        Assert.Equal((12L, 0L), (between.Offset, between.Length));
        Assert.Null(between.BitOffset);
    }

    [Fact]
    public void FromBitsRejectsBackwardRanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Node.FromBits("A", 0, 8, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => Node.FromBits("A", 0, -1, 7));
    }
}
