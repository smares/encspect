using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// A Matroska Cluster whose CRC-32 fails over MPEG audio, AC-3, E-AC-3, DTS, and TrueHD and MLP packets (docs/design/damaged-units.md, decision 2): these parsers read the packets as
// frames of their own, so a frame is flagged (CodedFrame.FailsChecksum) when any of its bytes comes from a flagged packet, and the decoders withhold it and the output that depends on
// it as they do for a frame whose own check fails. The files are the single-Cluster encoder-written fixtures written again with a few blocks to a Cluster and the stored CRC-32 of the
// fourth Cluster changed (make_ogg_matroska_damaged_units_fixtures.py).
public class MatroskaByteStreamDamagedUnitsTests
{
    // The file, its source, the samples withheld: the frames of the failed Cluster, and the span after them (Layer III 1,056, Layer II 480, AC-3 and E-AC-3 256, the DTS core 480, TrueHD
    // and MLP up to the next unit that restarts every substream).
    public static TheoryData<string, string, long> Names => new()
    {
        { "external-ffmpeg-mp3", "MPEG-1 Layer III", (3 * 1152) + 1056 },
        { "external-ffmpeg-mp2", "MPEG-1 Layer II", (12 * 1152) + 480 },
        { "external-ffmpeg-ac3-51", "AC-3", (2 * 1536) + 256 },
        { "external-ffmpeg-eac3-51", "E-AC-3", (2 * 1536) + 256 },
        { "external-ffmpeg-dts-51", "DTS", (6 * 512) + 480 },
        { "external-mkvmerge-truehd-50", "Dolby TrueHD", (15 * 40) + 160 },
        { "external-ffmpeg-mlp-51", "MLP", (30 * 40) + 320 },
    };

    private static string Damaged(string source) => $"matroska/damaged-units/{source}-small-clusters-cluster-crc-changed.mka";

    private static (Report Report, string Pcm) Analyze(string name, bool measure = true)
    {
        using Stream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        var sink = new PcmHashingSink();
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = measure ? new MeasureOptions() : null } });
        return (report, sink.Hash());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheFramesOfTheFailedClusterAndTheSpanAfterThemAreWithheld(string source, string codec, long withheld)
    {
        (Report report, _) = Analyze(Damaged(source));
        StreamInfo stream = report.Streams[0];
        StreamMeasurements m = stream.Measurements!;

        Assert.Contains(codec, stream.Codec);
        Assert.Contains(report.Findings, f => f.Code == "matroska.crc-mismatch");
        Assert.Equal(withheld, m.WithheldSamples);
        Assert.Equal(withheld, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(1, m.Gaps);
        Assert.Equal(0, m.LostSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed" or "parse.failed");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheSourceWithItsOneClusterWithholdsNothingAndTheDecodedPcmIsTheSame(string source, string codec, long withheld)
    {
        Assert.True(withheld > 0);
        (Report before, string beforePcm) = Analyze($"matroska/{source}.mka");
        (Report after, string afterPcm) = Analyze(Damaged(source));

        Assert.Contains(codec, before.Streams[0].Codec);
        Assert.Equal(0, before.Streams[0].Measurements!.WithheldSamples);
        Assert.Equal(beforePcm, afterPcm);
        Assert.Equal(before.Streams[0].Verification!.DecodedSamples, after.Streams[0].Verification!.DecodedSamples);
        Assert.True(before.Streams[0].Verification!.Checksums.SequenceEqual(after.Streams[0].Verification!.Checksums));
        Assert.Equal(before.Streams[0].Samples, after.Streams[0].Samples);
        Assert.Equal(before.Streams[0].Verification!.Checksums.Count, after.Streams[0].Verification!.Checksums.Count);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void WithholdingChangesNeitherTheDecodedSamplesNorTheVerificationNorAFinding(string source, string codec, long withheld)
    {
        Assert.True(withheld > 0 && codec.Length > 0);
        (Report plain, string plainPcm) = Analyze(Damaged(source), measure: false);
        (Report measured, string measuredPcm) = Analyze(Damaged(source));

        Assert.Equal(plainPcm, measuredPcm);
        Assert.Equal(plain.Streams[0].Verification!.DecodedSamples, measured.Streams[0].Verification!.DecodedSamples);
        Assert.Equal(plain.Findings.Select(f => f.Code).Order(), measured.Findings.Where(f => !f.Code.StartsWith("measure.", StringComparison.Ordinal)).Select(f => f.Code).Order());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void MeasuringTheMeasurementsAddsWhatTheMeasureGoldenHolds(string source, string codec, long withheld)
    {
        Assert.True(withheld > 0 && codec.Length > 0);
        string name = Damaged(source);
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
}
