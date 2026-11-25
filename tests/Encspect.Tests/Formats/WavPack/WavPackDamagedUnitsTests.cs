using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Blocks whose checksum or CRC fails (docs/design/damaged-units.md): WavPack's parser flags a block whose checksum fails or is missing, its decoder a set
// with a block whose CRC or extra CRC fails, a muted block included, and the measurements leave out the whole set as withheld samples ("checksum failed")
// while the decoded PCM, the verification and the findings stay as the decoding gives them.
public class WavPackDamagedUnitsTests
{
    public static TheoryData<string> Names => new()
    {
        "wavpack/built-block-crc-changed.wv",
        "wavpack/built-audio-bit-flipped.wv",
        "wavpack/built-stereo-mute-limit.wv",
        "wavpack/built-bitstream-cut.wv",
        "wavpack/built-bad-checksum.wv",
        "wavpack/built-checksum-missing.wv",
        "wavpack/damaged-units/set-block-checksum-changed.wv",
    };

    private static (Report Report, string Pcm) Analyze(string name, byte[]? data = null, bool measure = true, Analyzer? analyzer = null)
    {
        using Stream stream = data is null ? File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name)) : new MemoryStream(data);
        var sink = new PcmHashingSink();
        Report report = (analyzer ?? Analyzer.CreateDefault()).Analyze(Path.GetFileName(name), stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = measure ? new MeasureOptions() : null } });
        return (report, sink.Hash());
    }

    // The samples of the set whose block fails (the built files have one block to a set: 11,025, 6,487 and 6,488 samples).
    [Theory]
    [InlineData("wavpack/built-block-crc-changed.wv", 6487L)]
    [InlineData("wavpack/built-audio-bit-flipped.wv", 11025L)]
    [InlineData("wavpack/built-stereo-mute-limit.wv", 11025L)]
    [InlineData("wavpack/built-bitstream-cut.wv", 6487L)]
    [InlineData("wavpack/built-bad-checksum.wv", 6487L)]
    [InlineData("wavpack/built-checksum-missing.wv", 6487L)]
    [InlineData("wavpack/damaged-units/set-block-checksum-changed.wv", 11025L)]
    public void AFailedBlockIsWithheldWhatItsFailureIs(string name, long withheld)
    {
        (Report report, _) = Analyze(name);
        StreamInfo stream = report.Streams[0];
        StreamMeasurements m = stream.Measurements!;

        Assert.Equal(withheld, m.WithheldSamples);
        Assert.Equal(withheld, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(1, m.Gaps);
        Assert.Equal(0, m.LostSamples);
        Assert.Equal(stream.Verification!.DecodedSamples, m.Samples + m.WithheldSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed");
    }

    // The file is external-lossless-3ch-unassigned.wv with the stored checksum of the second of the three blocks of its first set changed
    // (make_wavpack_damaged_units_fixtures.py): a block that is not the set's last fails, and the whole set is withheld once, no other position.
    [Fact]
    public void AFailedBlockThatIsNotTheLastOfItsSetWithholdsTheWholeSet()
    {
        (Report report, _) = Analyze("wavpack/damaged-units/set-block-checksum-changed.wv");
        StreamInfo stream = report.Streams[0];
        StreamMeasurements m = stream.Measurements!;

        Assert.Equal(1, stream.Verification!.Checksums.Single(c => c.Name == "block checksum").Failed);
        Assert.Equal(11025, m.WithheldSamples);
        Assert.Equal(1, m.Gaps);
        Assert.Equal(24000 - 11025, m.Samples);
    }

    // The muted block of these files is zeros that fail their CRC: it is no stretch of digital silence.
    [Theory]
    [InlineData("wavpack/built-stereo-mute-limit.wv")]
    [InlineData("wavpack/built-bitstream-cut.wv")]
    public void AMutedBlockIsNotDigitalSilence(string name)
    {
        (Report report, _) = Analyze(name);
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Equal(0, m.Levels!.Stream.SilentSamples);
        Assert.True(m.WithheldSamples > 0);
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

    // A block of the main file of a hybrid pair whose stored checksum is changed (its data intact; the second block, since a first block that fails its checksum is not one libwavpack opens the file with) is withheld whether or not the correction file is beside it; a correction block
    // that fails its checksum leaves its main block decoded as the lossy audio, which counts as a failed block (wavpack.correction-block-missing), so its set is withheld too.
    [Fact]
    public void AFailedBlockOfAPairIsWithheldWithAndWithoutItsCorrectionFile()
    {
        string main = Path.Combine(Fixtures.FixtureDirectory, "wavpack", "external-hybrid-b4-correction.wv");
        string correction = Path.Combine(Fixtures.FixtureDirectory, "wavpack", "external-hybrid-b4-correction-wvc.wvc");
        byte[] damaged = File.ReadAllBytes(main);
        damaged[0x36CC] ^= 0x55;

        using var with = new TempFolder();
        File.WriteAllBytes(with["song.wv"], damaged);
        with.Copy(correction, "song.wvc");
        using var without = new TempFolder();
        File.WriteAllBytes(without["song.wv"], damaged);
        using var intact = new TempFolder();
        intact.Copy(main, "song.wv");
        byte[] damagedCorrection = File.ReadAllBytes(correction);
        damagedCorrection[0xF08] ^= 0x55;
        File.WriteAllBytes(intact["song.wvc"], damagedCorrection);

        static AnalysisOptions Options() => new() { Decoding = new DecodeOptions { Output = new PcmHashingSink().Declare, Measure = new MeasureOptions() } };
        Report pair = Analyzer.CreateDefault().AnalyzeFile(with["song.wv"], Options());
        Report alone = Analyzer.CreateDefault().AnalyzeFile(without["song.wv"], Options());
        Report brokenCorrection = Analyzer.CreateDefault().AnalyzeFile(intact["song.wv"], Options());

        StreamMeasurements withCorrection = pair.Streams[0].Measurements!;
        StreamMeasurements withoutCorrection = alone.Streams[0].Measurements!;
        Assert.Equal(3936, withoutCorrection.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(withoutCorrection.WithheldByReason[WithholdReason.ChecksumFailed], withCorrection.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(pair.Streams[0].Verification!.DecodedSamples, withCorrection.Samples + withCorrection.WithheldSamples);
        Assert.Contains(brokenCorrection.Findings, f => f.Code == "wavpack.correction-block-missing");
        Assert.Equal(11025, brokenCorrection.Streams[0].Measurements!.WithheldByReason[WithholdReason.ChecksumFailed]);
    }
}
