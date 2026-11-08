using System.Text.Json;
using Encspect.Host;
using Encspect.Core;

namespace Encspect.Tests;

// The marker lossyWAV leaves (the text of a fact chunk before the data) and the claim of the processor role it makes, in the four formats that keep it.
// The committed files come with a provenance record that holds lossyWAV's own answer: --check takes a file for its output by this chunk alone.
public class LossyWavMarkerTests
{
    private static readonly string[] Folders = ["wav", "flac", "wavpack", "monkeys-audio"];

    public static TheoryData<string> Provenances
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string folder in Folders)
            {
                foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, folder), "*.provenance.json").Order(StringComparer.Ordinal))
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.TryGetProperty("lossyWavCheck", out _) && document.RootElement.GetProperty("file").GetString() is { } file)
                    {
                        data.Add($"{folder}/{file}");
                    }
                }
            }

            return data;
        }
    }

    private static Report Analyze(string name) => Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, name));

    // The files that keep the marker get the claim, high confidence, and the others get none.
    [Theory]
    [MemberData(nameof(Provenances))]
    public void TheClaimFollowsLossyWavsOwnCheck(string name)
    {
        string provenance = Path.Combine(Fixtures.FixtureDirectory, name[..name.LastIndexOf('.')] + ".provenance.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(provenance));
        bool isMarked = document.RootElement.GetProperty("lossyWavCheck").GetProperty("exit").GetInt32() == 255;

        Report report = Analyze(name);
        StreamInfo stream = Assert.Single(report.Streams);

        if (isMarked)
        {
            Assert.Equal("lossyWAV 1.4.2", stream.Processor);
            Assert.Equal(Confidence.High, stream.ProcessorConfidence);
            Assert.DoesNotContain(report.Findings, f => f.Code.EndsWith("fact-text-unrecognized", StringComparison.Ordinal));
        }
        else
        {
            Assert.Null(stream.Processor);
            Assert.Null(stream.ProcessorConfidence);
        }

        Assert.DoesNotContain(report.Findings, f => f.Code == "wav.fact-samples-mismatch");
    }

    [Fact]
    public void TheEvidenceNamesTheSettingsAsWritten()
    {
        Report report = Analyze("flac/external-lossyflac-b512-foreign.flac");
        Node processor = Walk(report.Root).Single(n => n.Name == "Processor");
        Assert.Equal("lossyWAV 1.4.2", processor.Value?.ToString());
        Assert.Equal("High", processor.Children.Single(c => c.Name == "Confidence").Value?.ToString());
        Node evidence = processor.Children.Single(c => c.Name == "Evidence");
        Assert.Contains("with the settings \"--quality standard\"", evidence.Value?.ToString(), StringComparison.Ordinal);

        Node text = Walk(report.Root).Single(n => n.Name == "Text");
        Assert.Contains($"at 0x{text.Offset:X}", evidence.Value?.ToString(), StringComparison.Ordinal);
        Assert.Equal(["Version", "Date and Time", "Settings"], text.Children.Select(c => c.Name));
        Assert.Equal("--quality standard", text.Children[2].Value?.ToString());
    }

    [Fact]
    public void TheSettingsOfTheSixChannelFileAreTheOnesItWasMadeWith()
    {
        Report report = Analyze("flac/external-lossyflac-6ch.flac");
        Assert.Equal("lossyWAV 1.4.2", Assert.Single(report.Streams).Processor);
        Assert.Contains(Walk(report.Root), n => n.Name == "Settings" && n.Value?.ToString() == "--quality extraportable");
    }

    [Fact]
    public void ProcessorFieldsAreInTheJsonTheCsvAndTheSummary()
    {
        Report report = Analyze("wavpack/external-lossywav.wv");
        ReportSummary summary = ReportSummary.From(report);
        Assert.Equal(("lossyWAV 1.4.2", Confidence.High), (summary.Processor, summary.ProcessorConfidence));

        var json = new MemoryStream();
        ReportJsonWriter.Write([report], json);
        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        JsonElement stream = document.RootElement[0].GetProperty("streams")[0];
        Assert.Equal("lossyWAV 1.4.2", stream.GetProperty("processor").GetString());
        Assert.Equal("High", stream.GetProperty("processorConfidence").GetString());

        var csv = new StringWriter();
        ReportCsvWriter.Write([report], csv);
        string[] lines = csv.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(",Writer,Writer Confidence,Processor,Processor Confidence,Tags,", lines[0], StringComparison.Ordinal);
        Assert.Contains(",lossyWAV 1.4.2,High,", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithoutTheMarkerHasNoProcessorFields()
    {
        Report report = Analyze("flac/external-lossyflac-b512.flac");
        var json = new MemoryStream();
        ReportJsonWriter.Write([report], json);
        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        JsonElement stream = document.RootElement[0].GetProperty("streams")[0];
        Assert.False(stream.TryGetProperty("processor", out _));
        Assert.False(stream.TryGetProperty("processorConfidence", out _));
    }

    // A text that starts with "lossyWAV " but has not the form is shown as written, names no version and is an information finding of the format.
    [Theory]
    [InlineData("wav/lossywav-standard.wav", "wav.fact-text-unrecognized", false)]
    [InlineData("flac/external-lossyflac-b512-foreign.flac", "flac.riff-fact-text-unrecognized", false)]
    [InlineData("wavpack/external-lossywav.wv", "wavpack.wrapper-fact-text-unrecognized", false)]
    [InlineData("monkeys-audio/external-lossywav.ape", "monkeys-audio.header-fact-text-unrecognized", false)]
    [InlineData("wav/lossywav-standard.wav", "wav.fact-text-unrecognized", true)]
    [InlineData("flac/external-lossyflac-b512-foreign.flac", "flac.riff-fact-text-unrecognized", true)]
    [InlineData("wavpack/external-lossywav.wv", "wavpack.wrapper-fact-text-unrecognized", true)]
    [InlineData("monkeys-audio/external-lossywav.ape", "monkeys-audio.header-fact-text-unrecognized", true)]
    public void ATextWithoutTheFormIsAFindingAndNoClaim(string fixture, string code, bool isSettingsSeparatorMissing)
    {
        byte[] file = isSettingsSeparatorMissing ? LossyWavFiles.WithoutSettingsSeparator(fixture) : LossyWavFiles.WithoutTimeSeparator(fixture);
        Report report = Analyzer.CreateDefault().Analyze("crafted" + Path.GetExtension(fixture), new MemoryStream(file));

        Finding finding = Assert.Single(report.Findings, f => f.Code == code);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.NotNull(finding.Path);
        Assert.Null(Assert.Single(report.Streams).Processor);
        Assert.DoesNotContain(Walk(report.Root), n => n.Name == "Processor");
        Node text = Walk(report.Root).Single(n => n.Name == "Text");
        Assert.StartsWith("lossyWAV 1.4.2", text.Value?.ToString(), StringComparison.Ordinal);
        Assert.Empty(text.Children);
    }

    [Fact]
    public void TheReaderSplitsTheTextAtTheFirstSeparators()
    {
        FactText text = FactText.Read("lossyWAV 1.4.2 @ 08/11/2026 08:37:19, --quality standard --linkchannels\r\n\0\0"u8)!;
        Assert.Equal(("lossyWAV 1.4.2", "08/11/2026 08:37:19", "--quality standard --linkchannels"), (text.Version, text.Time, text.Settings));
        Assert.Equal("lossyWAV 1.4.2 @ 08/11/2026 08:37:19, --quality standard --linkchannels", text.Text);

        // A date of another form is shown as written.
        Assert.Equal("2026-11-08T08:37:19Z", FactText.Read("lossyWAV 9.9.9 @ 2026-11-08T08:37:19Z, --quality 0"u8)!.Time);
        Assert.Null(FactText.Read("LOSSYwav 1.4.2 @ x, y"u8));
        Assert.Null(FactText.Read("lossyWAV"u8));
        Assert.False(FactText.Read("lossyWAV 1.4.2, x @ y"u8)!.IsRecognized);
    }

    private static IEnumerable<Node> Walk(Node node) => new[] { node }.Concat(node.Children.SelectMany(Walk));
}
