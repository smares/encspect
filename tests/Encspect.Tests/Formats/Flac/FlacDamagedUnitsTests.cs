using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Units whose frame checksum fails (docs/design/damaged-units.md): Flac's parser flags them, and the measurements leave their positions out as
// withheld samples ("checksum failed") while the decoded samples, the verification and the findings stay as the decoding gives them.
public class FlacDamagedUnitsTests
{
    public static TheoryData<string> Names => new() { "flac/built-crc16-fails.flac", "flac/built-residual-bit-flipped.flac" };

    private static Report Analyze(string name, byte[]? data = null, bool measure = true)
    {
        using Stream stream = data is null ? File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name)) : new MemoryStream(data);
        var sink = new PcmHashingSink();
        return Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = measure ? new MeasureOptions() : null } });
    }

    private static IEnumerable<Node> Frames(Report report) => report.Streams[0].Nodes.SelectMany(Walk).Where(n => n.Name == "Frame");

    private static IEnumerable<Node> Walk(Node node) => new[] { node }.Concat(node.Children.SelectMany(Walk));

    private static long FailedSamples(Report report) =>
        Frames(report).Where(f => f.Children.Any(c => c.Name == "IsCrc16Valid" && c.Value?.AsBoolean() == false)).Sum(f => f.Value!.AsInteger()!.Value);

    // The frame of this file fails its CRC-16 and cannot be decoded: a unit the decoder cannot decode is lost, not withheld.
    [Fact]
    public void AFailedFrameThatCannotBeDecodedIsLost()
    {
        Report report = Analyze("flac/built-residual-bit-flipped.flac");
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Equal(1, report.Streams[0].Verification!.Checksums.Single(c => c.Name == "frame CRC-16").Failed);
        Assert.Equal((4096L, 0L, 1), (m.LostSamples, m.WithheldSamples, m.Gaps));
    }

    [Theory]
    [InlineData("flac/built-crc16-fails.flac")]
    public void TheWithheldPositionsAreThoseOfTheFramesTheTalliesCountAsFailed(string name)
    {
        Report report = Analyze(name);
        StreamInfo stream = report.Streams[0];
        StreamMeasurements m = stream.Measurements!;
        ChecksumTally tally = stream.Verification!.Checksums.Single(c => c.Name == "frame CRC-16");

        Assert.True(tally.Failed > 0);
        Assert.Equal(FailedSamples(report), m.WithheldSamples);
        Assert.Equal(m.WithheldSamples, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void MeasuringTheMeasurementsAddsWhatTheMeasureGoldenHolds(string name)
    {
        string actual = Encspect.Tests.Measurement.MeasurementFixtureTests.Golden(Analyze(name), Path.GetFileNameWithoutExtension(name));
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
    public void WithholdingChangesNeitherTheDecodedSamplesNorTheVerificationNorAFinding(string name)
    {
        Report plain = Analyze(name, measure: false);
        Report measured = Analyze(name);

        Assert.Equal(plain.Streams[0].Verification!.DecodedSamples, measured.Streams[0].Verification!.DecodedSamples);
        Assert.True(plain.Streams[0].Verification!.Checksums.SequenceEqual(measured.Streams[0].Verification!.Checksums));
        Assert.Equal(plain.Findings.Select(f => f.Code).Order(), measured.Findings.Where(f => !f.Code.StartsWith("measure.", StringComparison.Ordinal)).Select(f => f.Code).Order());
    }

    // The last 2 bytes of every frame, which hold its checksum, changed: every frame fails, and nothing is measured.
    [Fact]
    public void AStreamWhereEveryFrameFailsMeasuresNothingWithoutAnError()
    {
        string name = "flac/built-crc16-fails.flac";
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        foreach (Node frame in Frames(Analyze(name, measure: false)))
        {
            data[frame.Offset + frame.Length - 1] ^= 0x55;
        }

        Report report = Analyze(name, data);
        StreamInfo stream = Assert.Single(report.Streams);
        StreamMeasurements m = stream.Measurements!;

        Assert.Equal(MeasurementStatus.Complete, m.Status);
        Assert.Equal(0, m.Samples);
        Assert.Equal(stream.Verification!.DecodedSamples, m.WithheldSamples);
        Assert.True(m.WithheldSamples > 0);
        Assert.Null(m.Spectrum);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed" or "measure.limit");
    }
}
