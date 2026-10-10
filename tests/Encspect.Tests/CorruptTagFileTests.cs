using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Truncated, byte-flipped and garbage-prefixed variants of every tag and MPEG fixture must yield
// a report, never an exception or a hang, and every node must stay inside the file and its parent.
public class CorruptTagFileTests
{
    private const int FlipsPerFixture = 300;
    private static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(60);

    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in Fixtures.TagFixtureNames())
            {
                data.Add(Path.Combine("tags", name + ".mp3"));
            }

            foreach (string name in Fixtures.MpegFixtures())
            {
                data.Add(Path.Combine("mpeg", name));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task MutatedFixturesProduceReports(string name)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Fixtures.TagDirectory, "..", name));

        Task work = Task.Run(() =>
        {
            foreach (byte[] variant in Variants(original, name))
            {
                AnalyzeAndCheck(variant);
            }
        });

        Task finished = await Task.WhenAny(work, Task.Delay(TimeLimit));
        Assert.True(finished == work, $"Analyzing the variants of {name} took longer than {TimeLimit}.");
        await work;
    }

    [Fact]
    public void RandomFilesProduceReports()
    {
        var random = new Random(20261010);
        for (int i = 0; i < 500; i++)
        {
            var data = new byte[random.Next(0, 2048)];
            random.NextBytes(data);
            AnalyzeAndCheck(data);
        }
    }

    private static IEnumerable<byte[]> Variants(byte[] original, string name)
    {
        var random = new Random(StringComparer.Ordinal.GetHashCode(name) ^ 0x5EED);

        // Truncations, densest near the ends where tags live.
        var lengths = new SortedSet<int>();
        for (int i = 0; i <= Math.Min(original.Length, 600); i++)
        {
            lengths.Add(i);
            lengths.Add(original.Length - i);
        }

        for (int i = 0; i < 100; i++)
        {
            lengths.Add(random.Next(0, original.Length + 1));
        }

        foreach (int length in lengths)
        {
            yield return original[..length];
        }

        // Single and multiple byte changes, biased toward the tag regions at both ends.
        for (int i = 0; i < FlipsPerFixture; i++)
        {
            byte[] copy = (byte[])original.Clone();
            int changes = 1 + random.Next(4);
            for (int c = 0; c < changes && copy.Length > 0; c++)
            {
                int index = random.Next(3) switch
                {
                    0 => random.Next(Math.Min(copy.Length, 512)),
                    1 => copy.Length - 1 - random.Next(Math.Min(copy.Length, 512)),
                    _ => random.Next(copy.Length),
                };

                copy[index] = random.Next(4) switch
                {
                    0 => (byte)(copy[index] ^ (1 << random.Next(8))),
                    1 => 0xFF,
                    2 => 0x00,
                    _ => (byte)random.Next(256),
                };
            }

            yield return copy;
        }

        // Garbage before the first byte shifts every leading anchor.
        for (int prefix = 1; prefix <= 3; prefix++)
        {
            byte[] shifted = new byte[original.Length + prefix];
            original.CopyTo(shifted, prefix);
            yield return shifted;
        }
    }

    private static void AnalyzeAndCheck(byte[] data)
    {
        Report report = Analyzer.CreateDefault().Analyze("variant", new MemoryStream(data));

        Assert.Equal(data.Length, report.Root.Length);
        CheckNode(report.Root, report.Root);

        long previousEnd = 0;
        foreach (Node region in report.Root.Children)
        {
            Assert.True(region.Offset >= previousEnd, $"Region {region.Name} at {region.Offset} overlaps the previous region ending at {previousEnd}.");
            previousEnd = region.End;
        }

        Assert.All(report.Findings, f => Assert.NotNull(f.Node));
    }

    private static void CheckNode(Node node, Node parent)
    {
        Assert.True(node.Offset >= parent.Offset && node.End <= parent.End, $"{node.Name} [{node.Offset}, {node.End}) is outside {parent.Name} [{parent.Offset}, {parent.End}).");
        foreach (Node child in node.Children)
        {
            CheckNode(child, node);
        }
    }
}
