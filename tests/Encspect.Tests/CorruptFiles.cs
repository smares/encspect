using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The check behind the CorruptTagFileTests classes: truncated, byte-flipped and garbage-prefixed variants of a
// fixture must yield a report, never an exception or a hang, and every node must stay inside the file and its
// parent. The hang guard is per variant: a variant takes milliseconds, so the limit is far from anything CPU
// contention on a slow runner causes, where a limit on a fixture's 1,300 or so variants together was not
// (macOS on x64 once took over a minute for one fixture's).
internal static class CorruptFiles
{
    private const int FlipsPerFixture = 300;
    private static readonly TimeSpan VariantLimit = TimeSpan.FromSeconds(10);

    public static TheoryData<string> Names(Func<string, bool> include)
    {
        var data = new TheoryData<string>();
        foreach (string name in All().Where(include))
        {
            data.Add(name);
        }

        return data;
    }

    public static IEnumerable<string> All()
    {
        foreach (string name in Fixtures.TagFixtureNames())
        {
            yield return $"tags/{name}.mp3";
        }

        foreach (string name in Fixtures.MpegFixtures())
        {
            yield return name;
        }

        foreach (string name in Fixtures.Mp4FixtureNames())
        {
            yield return name;
        }

        foreach (string name in Fixtures.OggFixtures())
        {
            yield return name;
        }

        foreach (string family in new[] { "dts", "ac3" })
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, family), "*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)
                .Where(p => !p.EndsWith(".json", StringComparison.Ordinal)))
            {
                yield return $"{family}/{Path.GetFileName(path)}";
            }
        }
    }

    public static async Task MutatedFixturesProduceReports(string name)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Fixtures.TagDirectory, "..", name));

        int index = 0;
        foreach (byte[] variant in Variants(original, name))
        {
            try
            {
                // The limit counts from the start of the analysis, not from Task.Run: parallel test
                // classes can keep the pool busy for seconds on a loaded runner before it starts.
                var started = new TaskCompletionSource();
                Task work = Task.Run(() =>
                {
                    started.SetResult();
                    AnalyzeAndCheck(variant);
                });
                await started.Task;
                await work.WaitAsync(VariantLimit);
            }
            catch (TimeoutException)
            {
                Assert.Fail($"Variant {index} of {name} ({variant.Length} bytes) took longer than {VariantLimit}.");
            }

            index++;
        }
    }

    public static void RandomFilesProduceReports()
    {
        var random = new Random(20261010);
        for (int i = 0; i < 500; i++)
        {
            var data = new byte[random.Next(0, 2048)];
            random.NextBytes(data);
            AnalyzeAndCheck(data);
        }
    }

    // string.GetHashCode is randomized per process, which would test different variants on every
    // run; FNV-1a gives each fixture the same variants everywhere, so a failure reproduces.
    private static int StableSeed(string name)
    {
        uint hash = 2166136261;
        foreach (char c in name)
        {
            hash = (hash ^ c) * 16777619;
        }

        return (int)hash;
    }

    private static IEnumerable<byte[]> Variants(byte[] original, string name)
    {
        var random = new Random(StableSeed(name));

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

        // A parser exception the host caught is a bug, and every finding points at a node of the
        // tree, as the fuzzer requires.
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.True(f.Path is not null, $"The node of {f.Code} is not in the tree."));
    }

    private static void CheckNode(Node node, Node parent)
    {
        Assert.True(node.Offset >= parent.Offset && node.End <= parent.End, $"{node.Name} [{node.Offset}, {node.End}) is outside {parent.Name} [{parent.Offset}, {parent.End}).");
        if (parent.Ranges is { } ranges && node != parent)
        {
            foreach (ByteRange range in node.Ranges ?? [new ByteRange(node.Offset, node.Length)])
            {
                Assert.True(range.Length == 0 || ranges.Any(r => r.Offset <= range.Offset && range.End <= r.End), $"{node.Name} lies in a gap between the ranges of {parent.Name}.");
            }
        }

        foreach (Node child in node.Children)
        {
            CheckNode(child, node);
        }
    }
}
