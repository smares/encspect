using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The format detection takes the evidence, never the order of the parser list: every fixture, as it is
// and with random leading bytes of two lengths in front of it, is detected with the parsers in
// several shuffled orders, and the choice must not change. 352 bytes is the junk in front of the
// E-AC-3 sample that FFmpeg's suite keeps. Only the tags and the sniffs run (Analyzer.DetectFormat).
public class DetectionOrderTests
{
    private static readonly int[] JunkLengths = [352, 1000];

    private static readonly int[] Seeds = [3, 17, 101, 2026, 31337];

    public static TheoryData<string> Inputs() => DescriptionTests.AllFixtures();

    [Theory]
    [MemberData(nameof(Inputs))]
    public void TheChoiceDoesNotDependOnTheOrderOfTheParsers(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        string extension = Path.GetExtension(name);
        var problems = new List<string>();

        foreach (int junk in (int[])[0, .. JunkLengths])
        {
            byte[] data = junk == 0 ? file : [.. Junk(junk), .. file];
            string? expected = Detect(DefaultParsers.Formats, data, extension);
            foreach (int seed in Seeds)
            {
                IFormatParser[] shuffled = [.. DefaultParsers.Formats];
                new Random(seed).Shuffle(shuffled);
                string? actual = Detect(shuffled, data, extension);
                if (actual != expected)
                {
                    problems.Add($"{junk} bytes of junk, seed {seed} ({string.Join(", ", shuffled.Select(p => p.Name))}): {actual ?? "none"} instead of {expected ?? "none"}");
                }
            }
        }

        Assert.True(problems.Count == 0, $"{name}:\n" + string.Join("\n", problems));
    }

    private static string? Detect(IReadOnlyList<IFormatParser> parsers, byte[] data, string extension) =>
        new Analyzer(DefaultParsers.Tags, parsers, DefaultParsers.Codecs).DetectFormat("input" + extension, new MemoryStream(data));

    private static byte[] Junk(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }
}
