using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Formats.Ac3;

// Frames whose CRC fails (docs/design/damaged-units.md): AC-3's parser flags a syncframe whose CRC1 or CRC2 fails, E-AC-3's one whose CRC fails, and a program frame in which
// a dependent frame's fails; the decoders withhold the first block after it (256 samples), which depends on the overlap the failed frame left. The measurements leave all of it
// out as withheld samples ("checksum failed") while the decoded PCM, the verification and the findings stay as they are.
public class Ac3DamagedUnitsTests
{
    // The failed frames (or program frames) and the samples withheld: each failed frame's own and the 256 after it.
    public static TheoryData<string, int, long> Names => new()
    {
        { "ac3/crc-damaged.ac3", 2, 2 * (1536 + 256) },
        { "ac3/substreams-dependent-crc.eac3", 1, 1536 + 256 },
        { "ac3/damaged-units/external-ffmpeg-stereo-192k-crc1-crc2-changed.ac3", 2, 2 * (1536 + 256) },
    };

    private static (Report Report, string Pcm) Analyze(string name, bool measure = true)
    {
        using Stream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        var sink = new PcmHashingSink();
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = measure ? new MeasureOptions() : null } });
        return (report, sink.Hash());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void AFailedFrameAndTheBlockAfterItAreWithheld(string name, int gaps, long withheld)
    {
        (Report report, _) = Analyze(name);
        StreamInfo stream = report.Streams[0];
        StreamMeasurements m = stream.Measurements!;

        Assert.Equal(withheld, m.WithheldSamples);
        Assert.Equal(withheld, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(gaps, m.Gaps);
        Assert.Equal(0, m.LostSamples);
        Assert.Equal(stream.Verification!.DecodedSamples, m.Samples + m.WithheldSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed");
    }

    // The file is external-ffmpeg-stereo-192k.ac3 with the CRC1 of one syncframe and the CRC2 of another changed: the audio is intact, so the PCM is the undamaged file's.
    [Fact]
    public void ChangingTheCrcsChangesNeitherTheDecodedPcmNorTheSamplesDecoded()
    {
        (Report before, string beforePcm) = Analyze("ac3/external-ffmpeg-stereo-192k.ac3");
        (Report after, string afterPcm) = Analyze("ac3/damaged-units/external-ffmpeg-stereo-192k-crc1-crc2-changed.ac3");

        Assert.Equal(beforePcm, afterPcm);
        Assert.Equal(before.Streams[0].Verification!.DecodedSamples, after.Streams[0].Verification!.DecodedSamples);
        Assert.Equal(0, before.Streams[0].Measurements!.WithheldSamples);
        // CRC2 covers the whole frame, CRC1 included, so the frame with the changed CRC1 fails both.
        Assert.Equal(3, after.Streams[0].Verification!.Checksums.Sum(c => c.Failed));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void MeasuringTheMeasurementsAddsWhatTheMeasureGoldenHolds(string name, int gaps, long withheld)
    {
        Assert.True(gaps > 0 && withheld > 0);
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
    public void WithholdingChangesNeitherTheDecodedSamplesNorTheVerificationNorAFinding(string name, int gaps, long withheld)
    {
        Assert.True(gaps > 0 && withheld > 0);
        (Report plain, string plainPcm) = Analyze(name, measure: false);
        (Report measured, string measuredPcm) = Analyze(name);

        Assert.Equal(plainPcm, measuredPcm);
        Assert.Equal(plain.Streams[0].Verification!.DecodedSamples, measured.Streams[0].Verification!.DecodedSamples);
        Assert.True(plain.Streams[0].Verification!.Checksums.SequenceEqual(measured.Streams[0].Verification!.Checksums));
        Assert.Equal(plain.Findings.Select(f => f.Code).Order(), measured.Findings.Where(f => !f.Code.StartsWith("measure.", StringComparison.Ordinal)).Select(f => f.Code).Order());
    }

    [Fact]
    public void FramesWithoutAFailedCheckWithholdNothing()
    {
        (Report report, _) = Analyze("ac3/external-ffmpeg-stereo-192k.ac3");

        Assert.Equal(0, report.Streams[0].Measurements!.WithheldSamples);
        Assert.Equal(0, report.Streams[0].Measurements!.Gaps);
    }

    // eac3-signal-transient.eac3 has transient pre-noise processing data in every frame, some for a transient in the next frame. With the CRC of one frame changed in memory (the
    // audio is intact, so the PCM stays), the corrections that copy from the frame, and those it adds, rewrite more than the frame and the block after it, and all of that is
    // withheld; the same file without the data (eac3-signal-transient-off) withholds exactly the frame and the block.
    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void CorrectionsThatCopyFromAFailedFrameAreWithheldBeyondItsBlock(int frame)
    {
        byte[] Change(string name)
        {
            byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
            data[((frame + 1) * 4096) - 1] ^= 0x55;
            return data;
        }

        (Report Report, string Pcm) Run(byte[] data, bool measure)
        {
            using var stream = new MemoryStream(data);
            var sink = new PcmHashingSink();
            Report report = Analyzer.CreateDefault().Analyze("x.eac3", stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = measure ? new MeasureOptions() : null } });
            return (report, sink.Hash());
        }

        (Report plain, string plainPcm) = Run(Change("ac3/eac3-signal-transient.eac3"), measure: false);
        (Report measured, string pcm) = Run(Change("ac3/eac3-signal-transient.eac3"), measure: true);
        (Report off, _) = Run(Change("ac3/eac3-signal-transient-off.eac3"), measure: true);
        (_, string undamagedPcm) = Run(File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ac3/eac3-signal-transient.eac3")), measure: false);

        Assert.Equal(1, measured.Streams[0].Verification!.Checksums.Single().Failed);
        Assert.Equal(1536 + 256, off.Streams[0].Measurements!.WithheldSamples);
        long withheld = measured.Streams[0].Measurements!.WithheldSamples;
        Assert.True(withheld > 1536 + 256, $"{withheld} samples withheld");
        Assert.Equal(withheld, measured.Streams[0].Measurements!.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(undamagedPcm, pcm);
        Assert.Equal(plainPcm, pcm);
        Assert.DoesNotContain(measured.Findings, f => f.Code is "decode.failed" or "measure.failed");
    }
}
