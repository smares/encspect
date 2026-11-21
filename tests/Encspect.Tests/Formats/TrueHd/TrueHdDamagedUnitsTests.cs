using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Substreams whose parity byte or CRC-8 fails (docs/design/damaged-units.md): the parser flags the unit, and the decoder withholds the units after it up
// to the next one that restarts every substream, since the filter histories and matrices they start from are the damaged unit's. A lossless check that
// fails withholds nothing: it covers output already written. The front end's PCM, the verification and the findings stay as they were.
public class TrueHdDamagedUnitsTests
{
    public static TheoryData<string> Names => new()
    {
        "truehd/built-substream-crc-mismatch.thd",
        "truehd/built-substream-parity-mismatch.thd",
        "truehd/built-mlp-substream-crc-mismatch.mlp",
        "truehd/built-audio-bit-flipped.thd",
    };

    private static (Report Report, string Pcm) Analyze(string name, byte[]? data = null, bool measure = true, Analyzer? analyzer = null)
    {
        using Stream stream = data is null ? File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name)) : new MemoryStream(data);
        var sink = new PcmHashingSink();
        Report report = (analyzer ?? Analyzer.CreateDefault()).Analyze(Path.GetFileName(name), stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = measure ? new MeasureOptions() : null } });
        return (report, sink.Hash());
    }

    private static IEnumerable<Node> Walk(Node node) => new[] { node }.Concat(node.Children.SelectMany(Walk));

    private static bool IsInvalid(Node unit) =>
        Walk(unit).Any(n => n.Name is "Parity Byte" or "CRC-8" && n.Children.Any(c => c.Name == "IsValid" && c.Value?.AsBoolean() == false));

    // From the first unit whose substream check fails up to the unit before the next one with a major sync (which carries a restart header in every
    // substream of these files), or the end of the stream.
    [Theory]
    [InlineData("truehd/built-substream-crc-mismatch.thd")]
    [InlineData("truehd/built-substream-parity-mismatch.thd")]
    [InlineData("truehd/built-mlp-substream-crc-mismatch.mlp")]
    public void AFailedUnitAndTheUnitsThatDependOnItToTheNextRestartHeaderAreWithheld(string name)
    {
        (Report report, _) = Analyze(name);
        StreamInfo stream = report.Streams[0];
        StreamMeasurements m = stream.Measurements!;
        List<Node> units = stream.Nodes.SelectMany(Walk).Where(n => n.Name == "Access Unit").ToList();
        long perUnit = stream.Verification!.DecodedSamples / units.Count;
        int failed = units.FindIndex(IsInvalid);
        int next = units.FindIndex(failed + 1, u => u.Children.Any(c => c.Name == "Major Sync"));
        int end = next < 0 ? units.Count : next;

        Assert.True(failed >= 0);
        Assert.Equal((end - failed) * perUnit, m.WithheldSamples);
        Assert.Equal(m.WithheldSamples, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(1, m.Gaps);
        Assert.Equal(0, m.LostSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed");
    }

    [Fact]
    public void AFailedLosslessCheckWithholdsNothing()
    {
        (Report report, _) = Analyze("truehd/built-audio-bit-flipped.thd");
        StreamInfo stream = report.Streams[0];

        Assert.Equal(1, stream.Verification!.Checksums.Single(c => c.Name.StartsWith("lossless check", StringComparison.Ordinal)).Failed);
        Assert.Equal(0, stream.Measurements!.WithheldSamples);
        Assert.Equal(0, stream.Measurements.Gaps);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void MeasuringTheMeasurementsAddsWhatTheMeasureGoldenHolds(string name)
    {
        string actual = Encspect.Tests.Measurement.MeasurementFixtureTests.Golden(Analyze(name).Report, Path.GetFileNameWithoutExtension(name));
        string path = Fixtures.MpegCompanion(name, ".measure.json");
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void WithholdingChangesNeitherTheDecodedPcmNorTheVerificationNorAFinding(string name)
    {
        (Report plain, string plainPcm) = Analyze(name, measure: false);
        (Report measured, string measuredPcm) = Analyze(name);

        Assert.Equal(plainPcm, measuredPcm);
        Assert.Equal(plain.Streams[0].Verification!.DecodedSamples, measured.Streams[0].Verification!.DecodedSamples);
        Assert.True(plain.Streams[0].Verification!.Checksums.SequenceEqual(measured.Streams[0].Verification!.Checksums));
        Assert.Equal(plain.Findings.Select(f => f.Code).Order(), measured.Findings.Where(f => !f.Code.StartsWith("measure.", StringComparison.Ordinal)).Select(f => f.Code).Order());
    }
}
