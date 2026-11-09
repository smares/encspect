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

    // The statistic is the Wasted Bits node of the Statistics node; the field of each subframe in the frames has the same name.
    private static Node? Statistic(Report report) => Walk(report.Root).Where(n => n.Name == "Statistics").SelectMany(n => n.Children).SingleOrDefault(n => n.Name == "Wasted Bits");

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
        Node? wasted = Statistic(report);
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
        Node wasted = Statistic(report)!;
        Assert.Equal(changes, wasted.Children.Single(c => c.Name == "Changes").Value!.AsInteger()!.Value);
        Assert.Contains(nodes, n => n.Name == "Evidence" && n.Value?.ToString() is { } text && text.StartsWith($"{changes} {Pattern} blocks at the same place of consecutive block sets, at {changes} positions, all on a grid of 512 samples", StringComparison.Ordinal));
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
    [InlineData("flac/external-96000-stereo.flac")]
    [InlineData("measurement/synthetic-lowbits-silence-16-in-24.flac")]
    [InlineData("measurement/synthetic-lowbits-silence-8ch-16-in-24.flac")]
    [InlineData("measurement/synthetic-lowbits-alaw-b64-44100.flac")]
    [InlineData("measurement/synthetic-lowbits-other-4096.flac")]
    [InlineData("wavpack/external-silence-16-in-24-merge-blocks.wv")]
    [InlineData("wavpack/external-ffmpeg-silence-16-in-24.wv")]
    [InlineData("matroska/external-mkvmerge-wavpack-silence.mka")]
    [InlineData("measurement/synthetic-lowbits-alaw-b512-44100.flac")]
    [InlineData("measurement/synthetic-lowbits-pad-16-in-24.flac")]
    public void FilesThatAreNotLossyWavsAreNotClaimed(string name)
    {
        Report report = Analyze(name);
        Assert.Null(Assert.Single(report.Streams).Processor);
        Assert.DoesNotContain(Walk(report.Root), n => n.Name == "Processor");
    }

    [Fact]
    public void ALawExpandedTo16BitsAndCodedInFramesOf512ChangesItsWastedBitsAtMostThreeTimes()
    {
        Report report = Analyze("measurement/synthetic-lowbits-alaw-b512-44100.flac");
        Node? changes = Statistic(report)?.Children.Single(n => n.Name == "Changes");
        Assert.NotNull(changes);
        Assert.True(changes.Value!.AsInteger()!.Value <= 3);
        Assert.Null(Assert.Single(report.Streams).Processor);
    }

    // Digital silence has no count: a passage in audio with a constant count of wasted bits is no change, and the gate counts positions, not channels.
    [Theory]
    [InlineData("measurement/synthetic-lowbits-silence-16-in-24.flac", 18)]
    [InlineData("measurement/synthetic-lowbits-silence-8ch-16-in-24.flac", 32)]
    public void SilentSubframesAreListedApartAndBreakNoChain(string name, int silent)
    {
        Report report = Analyze(name);
        Node wasted = Statistic(report)!;
        Assert.Equal(0, wasted.Children.Single(c => c.Name == "Changes").Value!.AsInteger()!.Value);
        Assert.StartsWith($"{silent} subframes", wasted.Children.Single(c => c.Name == "Silent").Value!.ToString(), StringComparison.Ordinal);
    }

    // The counts alone, with no marker: WavPack blocks of lossyWAV's output with the fact chunk absent, and the same file in Matroska, whose packets keep the
    // blocks' counts but not their positions (the position is the samples of the packets before).
    [Theory]
    [InlineData("wavpack/external-lossywav-floor-44k-merge-blocks.wv")]
    [InlineData("matroska/external-mkvmerge-wavpack-lossywav.mka")]
    public void WavPackBlocksNameLossyWavFromTheirCountsAlone(string name)
    {
        Report report = Analyze(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(("lossyWAV", Confidence.Low), (stream.Processor, stream.ProcessorConfidence));
        Node[] nodes = Walk(report.Root).ToArray();
        Assert.Contains(nodes, n => n.Name == "Evidence" && n.Value?.ToString() is { } text && text.StartsWith("31 changes of the wasted bits between consecutive blocks at the same place of consecutive block sets, at 31 positions, all on a grid of 512 samples", StringComparison.Ordinal));
        Node wasted = Statistic(report)!;
        Assert.Equal(31, wasted.Children.Single(c => c.Name == "Changes").Value!.AsInteger()!.Value);
    }

    // A WavPack block whose maximum magnitude is 0 is digital silence: no count, listed apart, and no break in the chain (wavpack and ffmpeg's encoder both write it).
    [Theory]
    [InlineData("wavpack/external-silence-16-in-24-merge-blocks.wv")]
    [InlineData("wavpack/external-ffmpeg-silence-16-in-24.wv")]
    [InlineData("matroska/external-mkvmerge-wavpack-silence.mka")]
    public void SilentWavPackBlocksAreListedApart(string name)
    {
        Node wasted = Statistic(Analyze(name))!;
        Assert.Contains(wasted.Children, c => c.Name == "Silent");
        Assert.True(wasted.Children.Single(c => c.Name == "Changes").Value!.AsInteger()!.Value < 20);
    }
}
