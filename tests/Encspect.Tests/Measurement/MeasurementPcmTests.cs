using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Measurement;

// The PCM route (docs/design/spectral-analysis.md, "WAV, Wave64 and AIFF: a PCM decoder"): the twins of the FLAC fixtures as WAV, Wave64 and AIFF files hold
// the same integers, so they decode to the same samples and measure to the same figures whatever the container and the way it cuts its blocks (a FLAC block of
// 4,096 samples, a PCM slice of 64 KiB); and the fixtures that only WAV can hold (floats, non-finite samples, many channels) measure as they were built to.
public class MeasurementPcmTests
{
    private static readonly string Directory_ = Path.Combine(Fixtures.FixtureDirectory, "measurement");

    private static readonly string[] Extensions = [".wav", ".w64", ".aiff"];

    public static TheoryData<string> Twins()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(Directory_, "synthetic-*.*").Where(p => Extensions.Contains(Path.GetExtension(p)) && File.Exists(Path.ChangeExtension(p, ".flac")))
            .Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    public static TheoryData<string> WavOnly()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(Directory_, "synthetic-pcm-*.wav").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    private static Report Analyze(string file, MeasureOptions? measure, Func<DecodedStream, IPcmSink>? output = null)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Directory_, file));
        return Analyzer.CreateDefault().Analyze(file, stream, new AnalysisOptions
        {
            Decoding = new DecodeOptions { Measure = measure, Output = output },
        });
    }

    [Fact]
    public void ThereAreTwinsOfEveryContainer()
    {
        string[] names = [.. ((IEnumerable<object[]>)Twins()).Select(d => (string)d[0])];

        Assert.True(names.Count(n => n.EndsWith(".wav", StringComparison.Ordinal)) >= 20);
        Assert.Contains("synthetic-sine-bin-center.w64", names);
        Assert.Contains("synthetic-sine-bin-center.aiff", names);
        Assert.Contains("synthetic-sine-24-bits.aiff", names);
    }

    [Theory]
    [MemberData(nameof(Twins))]
    public void ATwinMeasuresLikeItsFlacFile(string twin)
    {
        string name = Path.GetFileNameWithoutExtension(twin);
        Report flac = Analyze(name + ".flac", new MeasureOptions());
        Report report = Analyze(twin, new MeasureOptions());

        Assert.Equal(MeasurementStatus.Complete, report.Streams.Single().Measurements!.Status);
        Assert.Equal(MeasurementFixtureTests.Golden(flac, name), MeasurementFixtureTests.Golden(report, name));
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed" or "parse.failed");

        // A PCM stream has a session and no checksum to compare.
        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.False(verification.HasChecksums);
        Assert.Null(verification.IsVerified);
        Assert.Equal(report.Streams.Single().Measurements!.Samples, verification.DecodedSamples);
    }

    [Theory]
    [MemberData(nameof(Twins))]
    public void ATwinDecodesToTheSamplesOfItsFlacFile(string twin)
    {
        var flacSink = new PcmHashingSink();
        var twinSink = new PcmHashingSink();
        Analyze(Path.ChangeExtension(twin, ".flac"), null, flacSink.Declare);
        Analyze(twin, null, twinSink.Declare);

        Assert.Equal(flacSink.Samples, twinSink.Samples);
        Assert.Equal(flacSink.Bits, twinSink.Bits);
        Assert.Equal(flacSink.Channels, twinSink.Channels);
        Assert.Equal(flacSink.SampleRate, twinSink.SampleRate);
        Assert.Equal(flacSink.Hash(), twinSink.Hash());
    }

    // A decode that only verifies compares nothing and reads no sample; measuring or output reads them.
    [Theory]
    [InlineData("synthetic-sine-bin-center.wav")]
    [InlineData("synthetic-sine-bin-center.aiff")]
    public void VerifyingAloneDecodesNothingFromPcm(string twin)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Directory_, twin));
        Report report = Analyzer.CreateDefault().Analyze(twin, stream, new AnalysisOptions { Decoding = new DecodeOptions() });

        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Equal(DecodeStatus.NotRequested, verification.Decoding);
        Assert.Equal(0, verification.DecodedSamples);
        Assert.Empty(report.Findings);
        Assert.Null(report.Streams.Single().Measurements);
    }

    [Theory]
    [MemberData(nameof(Twins))]
    public void MeasuringAPcmFileChangesNothingElseInTheReport(string twin)
    {
        Report plain = Analyze(twin, null);
        Report measured = Analyze(twin, new MeasureOptions());
        Report starved = Analyze(twin, new MeasureOptions { MaxWork = 1000 });

        Assert.Null(plain.Streams.Single().Measurements);
        Assert.Equal(plain.Findings.Select(f => f.Code), measured.Findings.Select(f => f.Code));
        Assert.Equal(Tree(plain.Root), Tree(measured.Root));
        Assert.Contains(starved.Findings, f => f.Code == "measure.limit");
        Assert.Equal(plain.Findings.Select(f => f.Code), starved.Findings.Where(f => !f.Code.StartsWith("measure.", StringComparison.Ordinal)).Select(f => f.Code));
        Assert.Equal(DecodeStatus.Decoded, starved.Streams.Single().Verification!.Decoding);
        Assert.Equal(measured.Streams.Single().Verification!.DecodedSamples, starved.Streams.Single().Verification!.DecodedSamples);
    }

    private static string Tree(Node node) => $"{node.Name}|{node.Value}|{node.Offset}+{node.Length}\n" + string.Concat(node.Children.Select(Tree));

    [Theory]
    [MemberData(nameof(WavOnly))]
    public void TheMeasureGoldenMatches(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        string actual = MeasurementFixtureTests.Golden(Analyze(file, new MeasureOptions()), name);
        string path = Path.Combine(Directory_, name + ".measure.json");
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    [Theory]
    [InlineData("synthetic-pcm-float32-sine.wav", SampleFormat.Float32)]
    [InlineData("synthetic-pcm-float64-sine.wav", SampleFormat.Float64)]
    public void AFloatSineIsMeasuredAsItIs(string file, SampleFormat format)
    {
        StreamMeasurements m = Analyze(file, new MeasureOptions()).Streams.Single().Measurements!;

        Assert.Equal(MeasurementStatus.Complete, m.Status);
        Assert.Equal(format, m.Levels!.Format);
        Assert.Equal(16384, m.Samples);
        Assert.Equal(0, m.NonFiniteSamples);
        Assert.False(m.HasClampedSamples);

        // Amplitude 0.5 is -6.02 dBFS; the sine on bin 400 and a Hann window reads as in the FLAC fixture of the same signal.
        Assert.Equal(-60, m.Levels.Stream.SamplePeakTenths);
        Assert.InRange(m.Spectrum!.Combined.Span[100], -112, -110);
        Assert.False(m.Levels.Stream.HasOnly16BitValues);
    }

    [Fact]
    public void NonFiniteFloatsAreCountedAndMeasuredAsZero()
    {
        Report report = Analyze("synthetic-pcm-float32-non-finite.wav", new MeasureOptions());
        StreamMeasurements m = report.Streams.Single().Measurements!;

        Assert.Equal(MeasurementStatus.Complete, m.Status);
        Assert.Equal(5, m.NonFiniteSamples);
        Assert.True(m.HasClampedSamples);
        Finding finding = Assert.Single(report.Findings, f => f.Code == "measure.non-finite-samples");
        Assert.Equal(Severity.Warning, finding.Severity);

        // The sample of 1e30 is clamped to 1,024 times full scale: +60.2 dBFS.
        Assert.Equal(602, m.Levels!.Stream.SamplePeakTenths);
        Assert.Equal(DecodeStatus.Decoded, report.Streams.Single().Verification!.Decoding);
    }

    [Fact]
    public void HugeAndTinyDoublesGiveNoNaNFigure()
    {
        Report report = Analyze("synthetic-pcm-float64-huge.wav", new MeasureOptions());
        StreamMeasurements m = report.Streams.Single().Measurements!;

        Assert.Equal(MeasurementStatus.Complete, m.Status);
        Assert.Equal(0, m.NonFiniteSamples);
        Assert.True(m.HasClampedSamples);
        Assert.Equal(602, m.Levels!.Stream.SamplePeakTenths);
        Assert.DoesNotContain(report.Findings, f => f.Code is "measure.failed" or "decode.failed");
        Assert.Contains(m.Spectrum!.Combined.ToArray(), v => v != MeasurementLimits.NoPower);
    }

    // A data chunk without a whole sample frame holds nothing to measure; it is not a stop.
    [Theory]
    [InlineData("wav/built-empty-data.wav")]
    [InlineData("aiff/built-sound-offset-past-end.aiff")]
    public void AStreamWithNoSampleFrameSaysSo(string fixture)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, fixture));
        Report report = Analyzer.CreateDefault().Analyze(fixture, stream, new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } });

        StreamInfo info = Assert.Single(report.Streams);
        Assert.Equal(DecodeStatus.Decoded, info.Verification!.Decoding);
        Assert.Equal(0, info.Verification.DecodedSamples);
        Assert.Equal(MeasurementStatus.NotPossible, info.Measurements!.Status);
        Assert.Equal("the stream holds no samples", info.Measurements.Reason);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("measure.", StringComparison.Ordinal) || f.Code.StartsWith("decode.", StringComparison.Ordinal));
    }

    // Streams that decode no frame but whose parser states samples (a frame that differs from STREAMINFO, a bad first block, a cut seek table, a header
    // that states too much data) or hold none in a FLAC or Monkey's Audio file are `Stopped`, as before the PCM route: only a stream whose setup states
    // 0 samples and which was handed no frame says "holds no samples" (a PCM stream with no sample frame).
    [Theory]
    [InlineData("flac/built-frame-differs-from-streaminfo.flac")]
    [InlineData("flac/built-metadata-truncated.flac")]
    [InlineData("fuzz/flac-findings-on-unlisted-metadata-blocks.bin")]
    [InlineData("wavpack/built-channel-info-after-bad-block.wv")]
    [InlineData("monkeys-audio/built-cut-in-seek-table.ape")]
    [InlineData("monkeys-audio/built-header-data-too-large.ape")]
    [InlineData("monkeys-audio/built-total-frames-zero.ape")]
    public void AStreamThatDecodedNoFrameStaysStoppedUnlessItsSetupStatesNoSamples(string fixture)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, fixture));
        Report report = Analyzer.CreateDefault().Analyze(fixture, stream, new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } });

        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;
        Assert.Equal(MeasurementStatus.Stopped, m.Status);
        Assert.Null(m.Reason);
        Assert.Equal(0, m.Samples);
    }

    [Theory]
    [InlineData("synthetic-pcm-channels-33.wav", 33)]
    [InlineData("synthetic-pcm-channels-65535.wav", 65535)]
    public void OnlyTheFirst32ChannelsAreMeasured(string file, int channels)
    {
        Report report = Analyze(file, new MeasureOptions());
        StreamMeasurements m = report.Streams.Single().Measurements!;

        Assert.Equal(MeasurementStatus.Complete, m.Status);
        Assert.Equal(channels, m.Channels);
        Assert.Equal(32, m.ChannelsMeasured);
        Assert.Contains(report.Findings, f => f.Code == "measure.channels-limited");
        Assert.DoesNotContain(report.Findings, f => f.Code is "measure.failed" or "decode.failed" or "parse.failed");
        Assert.Equal(32, m.Levels!.Channels.Count);
    }
}
