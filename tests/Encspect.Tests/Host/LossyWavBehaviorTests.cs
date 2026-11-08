using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The claim from behavior: the counts of wasted bits FLAC and WavPack state show lossyWAV's pattern, at low confidence, named without a version. The
// committed lossyWAV files come with the rule's answer for the file as coded (flacUnits.claim in the provenance), which the importer works out apart
// from the analyzer from the committed bytes (scripts/fixtures/import_lossywav_fixtures.py); the negative files are not lossyWAV's.
public class LossyWavBehaviorTests
{
    public static TheoryData<string> LossyFlacFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "flac"), "external-lossyflac-*.provenance.json").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }
    }

    private static Report Analyze(string relative) => Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, relative));

    private static IEnumerable<Node> Walk(Node node) => new[] { node }.Concat(node.Children.SelectMany(Walk));

    private const string Pattern = "changes of the wasted bits between consecutive";

    [Theory]
    [MemberData(nameof(LossyFlacFiles))]
    public void TheClaimFollowsTheRuleOfTheImporter(string provenance)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, "flac", provenance)));
        JsonElement root = document.RootElement;
        JsonElement units = root.GetProperty("flacUnitsFromCommittedBytes");
        bool claim = root.GetProperty("flacUnits").GetProperty("claim").GetBoolean();
        bool isMarked = root.GetProperty("lossyWavCheck").GetProperty("exit").GetInt32() == 255;
        Report report = Analyze("flac/" + root.GetProperty("file").GetString());
        StreamInfo stream = Assert.Single(report.Streams);
        Node[] nodes = Walk(report.Root).ToArray();

        // The marker names the version with high confidence; the pattern then adds its evidence, and alone it names lossyWAV at low confidence.
        Assert.Equal(isMarked ? "lossyWAV 1.4.2" : claim ? "lossyWAV" : null, stream.Processor);
        Assert.Equal(isMarked ? Confidence.High : claim ? Confidence.Low : null, stream.ProcessorConfidence);
        string[] evidence = nodes.Where(n => n.Name == "Evidence" && n.Value?.ToString() is { } text && text.Contains(Pattern, StringComparison.Ordinal)).Select(n => n.Value!.ToString()!).ToArray();
        Assert.Equal(claim ? 1 : 0, evidence.Length);

        // The statistic equals the importer's count from the committed bytes: the distribution, and the changes.
        Node? wasted = nodes.SingleOrDefault(n => n.Name == "Wasted Bits");
        var distribution = units.GetProperty("distribution").EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => p.Value.GetInt64());
        if (distribution.Keys.All(bits => bits == 0))
        {
            Assert.Null(wasted);
            return;
        }

        Assert.NotNull(wasted);
        Assert.Equal(units.GetProperty("subframes").GetInt64(), wasted.Value!.AsInteger()!.Value);
        Assert.Equal(units.GetProperty("changes").GetInt64(), wasted.Children.Single(c => c.Name == "Changes").Value!.AsInteger()!.Value);
        foreach ((int bits, long count) in distribution)
        {
            string value = wasted.Children.Single(c => c.Name == $"{bits} {(bits == 1 ? "Bit" : "Bits")}").Value!.ToString()!;
            Assert.StartsWith($"{count} subframe", value, StringComparison.Ordinal);
        }

        Assert.Equal(distribution.Count + 1, wasted.Children.Count);
        if (claim)
        {
            Assert.Contains($"{units.GetProperty("changes").GetInt64()} {Pattern}", evidence[0], StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("wavpack/external-lossywav-merge-blocks.wv", 36)]
    [InlineData("wavpack/external-lossywav-synthetic-merge-blocks.wv", 30)]
    public void WavPackBlocksOfWholeMultiplesOfTheLossyWavBlockShowThePatternToo(string name, int changes)
    {
        Report report = Analyze(name);
        Node[] nodes = Walk(report.Root).ToArray();
        Node wasted = nodes.Single(n => n.Name == "Wasted Bits");
        Assert.Equal(changes, wasted.Children.Single(c => c.Name == "Changes").Value!.AsInteger()!.Value);
        Assert.Contains(nodes, n => n.Name == "Evidence" && n.Value?.ToString() is { } text && text.StartsWith($"{changes} {Pattern} blocks of a channel, 100.0% of them at one phase of a grid of 512 samples", StringComparison.Ordinal));
        Assert.Equal("lossyWAV 1.4.2", Assert.Single(report.Streams).Processor);
    }

    // The default WavPack blocks (11,025 samples at 44.1 kHz) do not divide lossyWAV's block, so the stream states its counts for none of its blocks.
    [Fact]
    public void WavPackBlocksThatDoNotDivideTheLossyWavBlockCountTheirChangesAndMakeNoClaimFromThem()
    {
        Report report = Analyze("wavpack/external-lossywav.wv");
        Assert.DoesNotContain(Walk(report.Root), n => n.Name == "Evidence" && n.Value?.ToString() is { } text && text.Contains(Pattern, StringComparison.Ordinal));
        Assert.Equal("lossyWAV 1.4.2", Assert.Single(report.Streams).Processor);
    }

    // The negative set: FLAC and WavPack files of audio that lossyWAV did not touch are not claimed, whatever their counts of wasted bits.
    [Theory]
    [InlineData("flac/external-16-wasted-bits.flac")]
    [InlineData("flac/external-16-stereo-l5.flac")]
    [InlineData("flac/external-24-stereo.flac")]
    [InlineData("flac/external-48000-stereo.flac")]
    [InlineData("measurement/synthetic-lowbits-alaw-b512-44100.flac")]
    [InlineData("measurement/synthetic-lowbits-pad-16-in-24.flac")]
    public void FilesThatAreNotLossyWavsAreNotClaimed(string name)
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, name);
        if (!File.Exists(path))
        {
            return;
        }

        Report report = Analyze(name);
        Assert.Null(Assert.Single(report.Streams).Processor);
        Assert.DoesNotContain(Walk(report.Root), n => n.Name == "Processor");
    }

    [Fact]
    public void ALawExpandedTo16BitsAndCodedInFramesOf512ChangesItsWastedBitsAtMostThreeTimes()
    {
        Report report = Analyze("measurement/synthetic-lowbits-alaw-b512-44100.flac");
        Node? changes = Walk(report.Root).SingleOrDefault(n => n.Name == "Changes" && n.Description?.Contains("wasted bits", StringComparison.Ordinal) == true);
        Assert.True(changes is null || changes.Value!.AsInteger()!.Value <= 3);
        Assert.Null(Assert.Single(report.Streams).Processor);
    }
}
