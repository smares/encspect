using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Lists of children past two blocks, read by several threads at once (see ThreadSafetyTests). Its own
// class, so that xUnit runs it beside the others.
public class LongListReadTests
{
    // Each long list is read for at least this many reads per thread and this long.
    private const int MinimumAccesses = 10_000;
    private const int HammerMilliseconds = 250;

    // Eight threads read random children of every long list (and runs of neighbors, as writers and views
    // read), with collections going on, and every node read must be the node of the private report of
    // the same file at that index. This is the check that catches a list that reads its hint of the block
    // read last twice, or lets a block be mixed up with another.
    [Theory]
    [MemberData(nameof(ThreadSafetyTests.LongInputNames), MemberType = typeof(ThreadSafetyTests))]
    public void ManyThreadsReadingLongListsGetTheRightNodes(string name)
    {
        byte[] data = LongInputs.Build(name);
        var analyzer = Analyzer.CreateDefault();
        Report reference = analyzer.Analyze(name, new MemoryStream(data));
        Report shared = analyzer.Analyze(name, new MemoryStream(data));
        List<int[]> paths = [.. ThreadSafetyTests.LongLists(reference.Root).Select(p => p.Path)];
        Assert.NotEmpty(paths);

        using var collector = new ReportRendering.Collector();
        foreach (int[] path in paths)
        {
            Node want = ThreadSafetyTests.Follow(reference.Root, path);
            Node got = ThreadSafetyTests.Follow(shared.Root, path);
            (string Name, long Offset, long Length)[] expected = [.. want.Children.Select(n => (n.Name, n.Offset, n.Length))];
            int bad = 0;
            string? example = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
            {
                var random = new Random(t + 17);
                IReadOnlyList<Node> list = got.Children;
                for (int k = 0; k < MinimumAccesses || clock.ElapsedMilliseconds < HammerMilliseconds; k++)
                {
                    int i = random.Next(4) == 0 ? k % expected.Length : random.Next(expected.Length);
                    Node node = list[i];
                    if (node.Name != expected[i].Name || node.Offset != expected[i].Offset || node.Length != expected[i].Length)
                    {
                        Interlocked.Increment(ref bad);
                        example ??= $"index {i}: got {node.Name} at {node.Offset} +{node.Length}, want {expected[i].Name} at {expected[i].Offset} +{expected[i].Length}";
                    }

                    if ((k & 1023) == 0)
                    {
                        list = got.Children;
                    }
                }
            });

            Assert.True(bad == 0, $"{name} [{string.Join('/', path)}] ({expected.Length} children): {bad} wrong reads, e.g. {example}");
        }
    }
}
