using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Measurement;

// The low bits unused per span of lossyWAV's own output (the committed lossyWAV fixtures) against the figures their provenance files record, which
// import_lossywav_fixtures.py recomputes from the committed bytes with the prototype scripts/fixtures/lossywav_figures.py: the files the feature exists
// for have their figures pinned in CI, whatever the coding (WAV, FLAC, WavPack, Monkey's Audio). The grid is compared as the provenance records it; its count
// of changes on the grid is not, since the grid counts moments (span positions) and the provenance counts changes.
public class MeasurementLossyWavFixtureTests
{
    public static TheoryData<string> Provenance()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*.provenance.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("figures", out JsonElement figures) && figures.ValueKind == JsonValueKind.Object)
            {
                data.Add(Path.GetRelativePath(Fixtures.FixtureDirectory, path));
            }
        }

        return data;
    }

    [Fact]
    public void ThereAreLossyWavFixturesWithFigures()
    {
        Assert.True(Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*lossy*.provenance.json", SearchOption.AllDirectories).Count() >= 20);
    }

    [Theory]
    [MemberData(nameof(Provenance))]
    public void TheMeasuredFiguresAreTheRecordedOnes(string relative)
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, relative);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        JsonElement recorded = root.GetProperty("figures");
        string file = Path.Combine(Path.GetDirectoryName(path)!, root.GetProperty("file").GetString()!);

        using FileStream stream = File.OpenRead(file);
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(file), stream, new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions { Include = MeasurementKinds.Levels } } });
        LevelFigures levels = report.Streams.Single(s => s.Measurements?.Levels is not null).Measurements!.Levels!;
        LowBitsSpanFigures spans = levels.Spans!;

        Assert.Equal(recorded.GetProperty("lowBitsUnused").ValueKind == JsonValueKind.Null ? null : recorded.GetProperty("lowBitsUnused").GetInt32(), levels.Stream.LowBitsUnused);
        var distribution = new Dictionary<int, long>();
        for (int count = 0; count < spans.Distribution.Count; count++)
        {
            if (spans.Distribution[count] != 0)
            {
                distribution[count] = spans.Distribution[count];
            }
        }

        Assert.Equal(recorded.GetProperty("distribution").EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => p.Value.GetInt64()), distribution);
        Assert.Equal(
            [recorded.GetProperty("spans").GetInt64(), recorded.GetProperty("silentSpans").GetInt64(), recorded.GetProperty("thinSpans").GetInt64(), recorded.GetProperty("spansAboveLowBitsUnused").GetInt64(), recorded.GetProperty("changes").GetInt64()],
            [spans.Spans, spans.SilentSpans, spans.ThinSpans, spans.SpansAboveLowBitsUnused, spans.Changes]);
        JsonElement grid = recorded.GetProperty("grid");
        Assert.Equal(grid.ValueKind == JsonValueKind.Null ? 0 : grid.GetInt32(), spans.Grid);
        if (levels.Channels.Count > 1)
        {
            Assert.Equal((recorded.GetProperty("times").GetInt64(), recorded.GetProperty("timesWithEqualCounts").GetInt64()), (spans.Times, spans.TimesWithEqualCounts));
        }
    }
}
