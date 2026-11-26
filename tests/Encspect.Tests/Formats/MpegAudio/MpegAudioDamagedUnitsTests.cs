using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Formats.MpegAudio;

// Frames whose CRC-16 fails (docs/design/damaged-units.md): MPEG audio's parser flags a protected frame whose CRC does not match, and its decoder withholds the span
// after it that a gap's reset would withhold too (1,056 samples of Layer III, 480 of Layers I and II), since the decoder keeps the overlap and the synthesis history the
// failed frame left. The measurements leave all of it out as withheld samples ("checksum failed") while the decoded PCM, the verification and the findings stay as they are.
public class MpegAudioDamagedUnitsTests
{
    // The files: the first three are hand-built or made with the encoders that wrote their sources, with the third frame's CRC wrong; the last three are encoder-written
    // files with one frame's stored CRC changed (make_mpeg_ac3_damaged_units_fixtures.py).
    public static TheoryData<string, long> Names => new()
    {
        { "mpeg-audio/mpeg1-layer3-crc-mismatch.mp3", 1152 + 1056 },
        { "mpeg-audio/layer2/crc-mismatch.mp2", 1152 + 480 },
        { "mpeg-audio/damaged-units/layer1-crc-changed.mp1", 384 + 480 },
        { "mpeg-audio/damaged-units/external-twolame-crc-stereo-crc-changed.mp2", 1152 + 480 },
        { "mpeg-audio/damaged-units/external-lame-crc-crc-changed.mp3", 1152 + 1056 },
    };

    private static (Report Report, string Pcm) Analyze(string name, byte[]? data = null, bool measure = true)
    {
        using Stream stream = data is null ? File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name)) : new MemoryStream(data);
        var sink = new PcmHashingSink();
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = measure ? new MeasureOptions() : null } });
        return (report, sink.Hash());
    }

    // The failed frame's samples and the span after it.
    [Theory]
    [MemberData(nameof(Names))]
    public void AFailedFrameAndTheSpanAfterItAreWithheld(string name, long withheld)
    {
        (Report report, _) = Analyze(name);
        StreamInfo stream = report.Streams[0];
        StreamMeasurements m = stream.Measurements!;

        Assert.Equal(1, stream.Verification!.Checksums.Single().Failed);
        Assert.Equal(withheld, m.WithheldSamples);
        Assert.Equal(withheld, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(1, m.Gaps);
        Assert.Equal(0, m.LostSamples);
        Assert.Equal(stream.Verification.DecodedSamples, m.Samples + m.WithheldSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed");
    }

    // The file is the undamaged one with a frame's stored CRC changed, which the parser alone sees: the decoded PCM is the undamaged file's.
    [Theory]
    [InlineData("mpeg-audio/external-lame-crc.mp3", "mpeg-audio/damaged-units/external-lame-crc-crc-changed.mp3")]
    [InlineData("mpeg-audio/external-twolame-crc-stereo.mp2", "mpeg-audio/damaged-units/external-twolame-crc-stereo-crc-changed.mp2")]
    [InlineData("mpeg-audio/mpeg1-layer1-crc.mp1", "mpeg-audio/damaged-units/layer1-crc-changed.mp1")]
    public void ChangingACrcChangesNeitherTheDecodedPcmNorTheSamplesDecoded(string undamaged, string damaged)
    {
        (Report before, string beforePcm) = Analyze(undamaged);
        (Report after, string afterPcm) = Analyze(damaged);

        Assert.Equal(beforePcm, afterPcm);
        Assert.Equal(before.Streams[0].Verification!.DecodedSamples, after.Streams[0].Verification!.DecodedSamples);
        Assert.Equal(0, before.Streams[0].Measurements!.WithheldSamples);
        Assert.True(after.Streams[0].Measurements!.WithheldSamples > 0);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void MeasuringTheMeasurementsAddsWhatTheMeasureGoldenHolds(string name, long withheld)
    {
        Assert.True(withheld > 0);
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
    public void WithholdingChangesNeitherTheDecodedSamplesNorTheVerificationNorAFinding(string name, long withheld)
    {
        Assert.True(withheld > 0);
        (Report plain, string plainPcm) = Analyze(name, measure: false);
        (Report measured, string measuredPcm) = Analyze(name);

        Assert.Equal(plainPcm, measuredPcm);
        Assert.Equal(plain.Streams[0].Verification!.DecodedSamples, measured.Streams[0].Verification!.DecodedSamples);
        Assert.True(plain.Streams[0].Verification!.Checksums.SequenceEqual(measured.Streams[0].Verification!.Checksums));
        Assert.Equal(plain.Findings.Select(f => f.Code).Order(), measured.Findings.Where(f => !f.Code.StartsWith("measure.", StringComparison.Ordinal)).Select(f => f.Code).Order());
    }

    // A protected frame that holds its CRC, and an unprotected stream, withhold nothing.
    [Theory]
    [InlineData("mpeg-audio/external-lame-crc.mp3")]
    [InlineData("mpeg-audio/external-lame-cbr128-joint.mp3")]
    [InlineData("mpeg-audio/mpeg1-layer1-crc.mp1")]
    public void FramesWithoutAFailedCheckWithholdNothing(string name)
    {
        (Report report, _) = Analyze(name);
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Equal(0, m.WithheldSamples);
        Assert.Equal(0, m.Gaps);
    }

    // mpeg1-layer1-crc.mp1 is six frames of 256 bytes and 384 samples. The file is the six frames, a gap of 100 bytes and the six again with the CRC of the first of them changed: the first 480 samples after the gap are withheld
    // as after a gap (the frame and 96 of the next), and the 480 after the failed frame count from the same place, so the larger is withheld, not their sum: 384 + 480 in all.
    [Fact]
    public void AFailedFrameRightAfterAGapWithholdsTheLargerOfTheTwoSpansNotTheirSum()
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "mpeg-audio/mpeg1-layer1-crc.mp1"));
        byte[] data = [.. source, .. Enumerable.Repeat((byte)0x11, 100), .. source];
        data[source.Length + 100 + 4] ^= 0x55;
        (Report report, _) = Analyze("gap.mp1", data);
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Equal(384 + 480, m.WithheldSamples);
        Assert.Equal(480, m.WithheldByReason.AfterGap);
        Assert.Equal(384, m.WithheldByReason[WithholdReason.ChecksumFailed]);
    }
}
