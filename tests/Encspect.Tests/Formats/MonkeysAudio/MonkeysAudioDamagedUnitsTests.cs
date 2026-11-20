using Encspect.Core;
using Encspect.Formats.MonkeysAudio.Decoding;
using Encspect.Host;

namespace Encspect.Tests;

// Frames whose CRC fails (docs/design/damaged-units.md): the CRC is known only after the frame is decoded, so the decoder holds every frame until it is
// (the 24-bit retry's machinery, up to 2^23 samples over all channels; a larger frame is decoded twice) and reports a failed frame to the measurements
// before it writes it. The front end's PCM, the verification and the findings stay as they were.
public class MonkeysAudioDamagedUnitsTests
{
    public static TheoryData<string> Names => new()
    {
        "monkeys-audio/built-frame-crc-changed.ape",
        "monkeys-audio/built-24-bit-frame-crc-changed.ape",
        "monkeys-audio/built-old-3850-checksum-changed.ape",
        "monkeys-audio/built-old-3830-crc-changed.ape",
        "monkeys-audio/built-old-3860-crc-changed.ape",
    };

    private static (Report Report, string Pcm) Analyze(string name, byte[]? data = null, bool measure = true, Analyzer? analyzer = null)
    {
        using Stream stream = data is null ? File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name)) : new MemoryStream(data);
        var sink = new PcmHashingSink();
        Report report = (analyzer ?? Analyzer.CreateDefault()).Analyze(Path.GetFileName(name), stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = measure ? new MeasureOptions() : null } });
        return (report, sink.Hash());
    }

    // The samples of the one frame of each file whose CRC (or checksum) is changed.
    [Theory]
    [InlineData("monkeys-audio/built-frame-crc-changed.ape", 73728L)]
    [InlineData("monkeys-audio/built-24-bit-frame-crc-changed.ape", 8000L)]
    [InlineData("monkeys-audio/built-old-3850-checksum-changed.ape", 3000L)]
    [InlineData("monkeys-audio/built-old-3830-crc-changed.ape", 3000L)]
    [InlineData("monkeys-audio/built-old-3860-crc-changed.ape", 9216L)]
    public void AFrameWhoseCrcFailsIsWithheld(string name, long frameSamples)
    {
        (Report report, _) = Analyze(name);
        StreamInfo stream = report.Streams[0];
        StreamMeasurements m = stream.Measurements!;

        Assert.Equal(1, stream.Verification!.Checksums.Single(c => c.Name is "frame CRC-32" or "frame checksum").Failed);
        Assert.Equal(frameSamples, m.WithheldSamples);
        Assert.Equal(frameSamples, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(1, m.Gaps);
        Assert.Equal(0, m.LostSamples);
        Assert.Equal(stream.Verification.DecodedSamples, m.Samples + m.WithheldSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed");
    }

    // The cap on the samples a frame may hold (every width now): a larger frame is decoded twice and gives the same PCM and the same measurements.
    [Theory]
    [InlineData("monkeys-audio/built-frame-crc-changed.ape")]
    [InlineData("monkeys-audio/built-24-bit-frame-crc-changed.ape")]
    [InlineData("monkeys-audio/external-level-normal-16-stereo.ape")]
    public void AFrameAboveTheCapIsDecodedTwiceAndGivesTheSamePcmAndMeasurements(string name)
    {
        (Report held, string heldPcm) = Analyze(name);
        var twice = new Analyzer(DefaultParsers.Tags, DefaultParsers.Formats, DefaultParsers.Codecs, [new MonkeysAudioDecoder(0)]);

        (Report report, string pcm) = Analyze(name, analyzer: twice);

        Assert.Equal(heldPcm, pcm);
        StreamMeasurements expected = held.Streams[0].Measurements!;
        StreamMeasurements actual = report.Streams[0].Measurements!;
        Assert.Equal((expected.Samples, expected.WithheldSamples, expected.Gaps, expected.LostSamples), (actual.Samples, actual.WithheldSamples, actual.Gaps, actual.LostSamples));
        Assert.Equal(held.Findings.Select(f => (f.Code, f.Count)), report.Findings.Select(f => (f.Code, f.Count)));
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

    [Fact]
    public void AnUndamagedFileWithholdsNothing()
    {
        (Report report, _) = Analyze("monkeys-audio/external-level-normal-16-stereo.ape");

        Assert.Equal(0, report.Streams[0].Measurements!.WithheldSamples);
        Assert.Equal(0, report.Streams[0].Measurements!.Gaps);
    }
}
