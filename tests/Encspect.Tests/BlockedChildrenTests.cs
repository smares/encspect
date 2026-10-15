using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Tests;

// The one implementation of the weak, blocked list of a node's children that every format's long
// lists use: blocks of 1,024 built when first read, held weakly, the block read last held strongly.
[Collection("Thread hammers")]
public class BlockedChildrenTests
{
    private const int Block = 1024;

    private static Node[] Leading(int count) => [.. Enumerable.Range(0, count).Select(i => new Node($"lead{i}", i, 1))];

    private static Node EntryNode(int i) => new($"n{i}", 1000 + i, 1 + (i % 7));

    private static void CheckNode(Node node, int i)
    {
        Assert.Equal($"n{i}", node.Name);
        Assert.Equal(1000 + i, node.Offset);
        Assert.Equal(1 + (i % 7), node.Length);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 5)]
    [InlineData(3, 0)]
    [InlineData(2, Block)]
    [InlineData(2, Block + 1)]
    [InlineData(0, (3 * Block) - 1)]
    [InlineData(5, (3 * Block) + 17)]
    public void ReadsLeadingNodesThenEveryEntryInOrder(int leading, int count)
    {
        var list = new BlockedChildren(Leading(leading), count, EntryNode);

        Assert.Equal(leading + count, list.Count);
        for (int i = 0; i < leading; i++)
        {
            Assert.Equal($"lead{i}", list[i].Name);
        }

        for (int i = 0; i < count; i++)
        {
            CheckNode(list[leading + i], i);
        }

        Node[] listed = [.. list];
        Assert.Equal(leading + count, listed.Length);
        Assert.Equal(Enumerable.Range(0, count).Select(i => $"n{i}"), listed.Skip(leading).Select(n => n.Name));
        Assert.Throws<ArgumentOutOfRangeException>(() => list[leading + count]);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[-1]);
    }

    [Fact]
    public void BlocksAreBuiltWhenFirstReadAndOnlyOnce()
    {
        int built = 0;
        var list = new BlockedChildren([], 3 * Block, i =>
        {
            Interlocked.Increment(ref built);
            return EntryNode(i);
        });

        Assert.Equal(0, built);
        _ = list[5];
        Assert.Equal(Block, built);
        _ = list[Block - 1];
        Assert.Equal(Block, built);
        _ = list[(2 * Block) + 3];
        Assert.Equal(2 * Block, built);
        _ = list[5];
        Assert.Equal(2 * Block, built);
    }

    // Switching between blocks must always return the node of the index asked for, not of the block
    // the hint holds.
    [Fact]
    public void SwitchingBetweenBlocksReturnsTheRightNodes()
    {
        var list = new BlockedChildren(Leading(3), (4 * Block) + 100, EntryNode);
        int[] order = [0, Block, 0, (2 * Block) + 5, Block - 1, (4 * Block) + 99, 1, Block + 1, (3 * Block) + 7, Block - 1, Block];
        foreach (int i in order)
        {
            CheckNode(list[3 + i], i);
        }

        var random = new Random(5);
        for (int k = 0; k < 20_000; k++)
        {
            int i = random.Next((4 * Block) + 100);
            CheckNode(list[3 + i], i);
        }
    }

    [Fact]
    public void ANodeReadAgainIsTheSameNodeWhileItsBlockLives()
    {
        var list = new BlockedChildren([], 2 * Block, EntryNode);
        Node first = list[10];
        _ = list[Block + 10];
        Assert.Same(first, list[10]);
        Assert.Same(list[Block + 10], list[Block + 10]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Touch(BlockedChildren list, params int[] indexes)
    {
        foreach (int i in indexes)
        {
            CheckNode(list[i], i);
        }
    }

    // A block nobody holds is built again from the records after a collection and gives the same
    // nodes; the block read last stays alive, so reading in order does not rebuild it.
    [Fact]
    public void ABlockCollectedIsBuiltAgainAndTheOneReadLastIsKept()
    {
        var builds = new int[3];
        var list = new BlockedChildren([], 3 * Block, i =>
        {
            Interlocked.Increment(ref builds[i / Block]);
            return EntryNode(i);
        });

        Touch(list, 0, Block, 2 * Block);
        Assert.Equal([Block, Block, Block], builds);

        Collect();
        Touch(list, 2 * Block + 1);
        Assert.Equal([Block, Block, Block], builds);

        Touch(list, 7);
        Assert.Equal([2 * Block, Block, Block], builds);
        Touch(list, Block + 7);
        Assert.Equal([2 * Block, 2 * Block, Block], builds);
        Touch(list, 7, 8);
        Assert.Equal([2 * Block, 2 * Block, Block], builds);
        Collect();
        Touch(list, 2 * Block);
        Assert.Equal([2 * Block, 2 * Block, 2 * Block], builds);
    }

    private static void Collect()
    {
        for (int i = 0; i < 2; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    // Threads that read the same block at the same moment each build it; one result is published and
    // every thread gets a block of correct nodes.
    [Fact]
    public void ThreadsBuildingTheSameBlockAtOnceAllGetCorrectNodes()
    {
        for (int round = 0; round < 8; round++)
        {
            using var barrier = new Barrier(8);
            var list = new BlockedChildren(Leading(1), 2 * Block, i =>
            {
                Thread.SpinWait(50);
                return EntryNode(i);
            });
            var seen = new Node[8][];
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
            {
                barrier.SignalAndWait();
                seen[t] = [list[1 + 3], list[1 + Block + 3], list[1 + 3]];
            });

            foreach (Node[] nodes in seen)
            {
                CheckNode(nodes[0], 3);
                CheckNode(nodes[1], Block + 3);
                CheckNode(nodes[2], 3);
            }

            Assert.Same(list[1 + 3], list[1 + 3]);
        }
    }

    [Fact]
    public void ConcurrentReadersOfManyBlocksWithCollectionsAlwaysGetTheRightNode()
    {
        const int Count = (6 * Block) + 321;
        var list = new BlockedChildren(Leading(2), Count, EntryNode);
        using var stop = new CancellationTokenSource();
        var collector = new Thread(() =>
        {
            var random = new Random(3);
            while (!stop.IsCancellationRequested)
            {
                GC.Collect(random.Next(3), GCCollectionMode.Forced, blocking: true, compacting: false);
                Thread.Sleep(random.Next(3));
            }
        })
        { IsBackground = true };
        collector.Start();
        int bad = 0;
        try
        {
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
            {
                var random = new Random(t + 11);
                for (int k = 0; k < 150_000; k++)
                {
                    int i = random.Next(4) == 0 ? k % Count : random.Next(Count);
                    Node node = list[2 + i];
                    if (node.Name != $"n{i}" || node.Offset != 1000 + i || node.Length != 1 + (i % 7))
                    {
                        Interlocked.Increment(ref bad);
                    }
                }
            });
        }
        finally
        {
            stop.Cancel();
            collector.Join();
        }

        Assert.Equal(0, bad);
    }

    [Fact]
    public void ABlockBuilderGetsTheStartAndLengthOfEachBlock()
    {
        var calls = new List<(int Start, int Length)>();
        var list = new BlockedChildren([], (2 * Block) + 10, (start, length) =>
        {
            lock (calls)
            {
                calls.Add((start, length));
            }

            return [.. Enumerable.Range(start, length).Select(EntryNode)];
        });

        CheckNode(list[(2 * Block) + 9], (2 * Block) + 9);
        CheckNode(list[0], 0);
        CheckNode(list[Block], Block);
        Assert.Equal([(2 * Block, 10), (0, Block), (Block, Block)], calls);
    }

    // A builder that throws shows as error nodes for the entries of its block, as a node whose factory
    // throws shows as one error node; the other blocks are unaffected, and the block is built again
    // once it has been collected.
    [Fact]
    public void ABuilderThatThrowsGivesErrorNodesForItsBlockOnly()
    {
        bool fail = true;
        var owner = new Node("Frames", 40, 900, null, () => new BlockedChildren([], Block + 1, i => fail && i == 5 ? throw new InvalidDataException("bad") : EntryNode(i)));

        IReadOnlyList<Node> list = owner.Children;
        Assert.Equal(Block + 1, list.Count);
        Node[] failed = [.. list.Take(Block)];

        Assert.All(failed, n => Assert.Equal(("Error", 40L, 900L, "The children could not be built: InvalidDataException: bad"), (n.Name, n.Offset, n.Length, n.Value?.ToString())));
        Assert.All(failed, n => Assert.False(string.IsNullOrEmpty(n.Description)));
        Assert.Equal(Block, failed.Distinct().Count());
        CheckNode(list[Block], Block);

        fail = false;
        failed = [];
        ReportRendering.Collect();
        CheckNode(list[5], 5);
    }

    [Fact]
    public void ABuilderThatIsCanceledStillThrows()
    {
        var list = new BlockedChildren([], 3, i => throw new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => list[0]);
    }

    [Fact]
    public void ChunkedListKeepsRecordsInOrderAcrossBlocks()
    {
        var list = new ChunkedList<long>();
        const int Count = 3 * 4096 + 17;
        for (int i = 0; i < Count; i++)
        {
            list.Add(i * 3L);
        }

        Assert.Equal(Count, list.Count);
        for (int i = 0; i < Count; i++)
        {
            Assert.Equal(i * 3L, list[i]);
        }

        list.SetLast(-1);
        Assert.Equal(-1, list[Count - 1]);
        Assert.Equal((Count - 2) * 3L, list[Count - 2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[Count]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkedList<int>().SetLast(1));
    }

    [Fact]
    public void ByteStoreReturnsWhatWasAddedAtTheReturnedPositions()
    {
        var store = new ByteStore();
        var random = new Random(9);
        var added = new List<(uint Position, byte[] Data)>();
        for (int i = 0; i < 3000; i++)
        {
            byte[] data = new byte[random.Next(1, 2000)];
            random.NextBytes(data);
            int split = random.Next(data.Length + 1);
            added.Add((store.Add(data.AsSpan(0, split), data.AsSpan(split)), data));
        }

        foreach ((uint position, byte[] data) in added)
        {
            Assert.True(store.Span(position, data.Length).SequenceEqual(data));
        }

        Assert.Throws<InvalidDataException>(() => store.Add(new byte[1 << 16], new byte[1]));
    }
}
