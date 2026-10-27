using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Measurement;

// The measurements on the streams of the decoders that exist (FLAC, WavPack, Monkey's Audio, TrueHD and MLP): measuring changes neither the
// verification nor a decode finding of any fixture, also when the measurements' own budget runs out first; and the figures do not depend on
// how the decoder cut its blocks.
public class MeasurementDecoderFixtureTests
{
    private static readonly string[] Extensions = [".flac", ".oga", ".ogg", ".wv", ".ape", ".thd", ".mlp"];

    public static TheoryData<string> Families() => new() { "flac", "wavpack", "monkeys-audio", "truehd" };

    private static IEnumerable<string> Files(string family) =>
        Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, family)).Where(p => Extensions.Contains(Path.GetExtension(p))).Order(StringComparer.Ordinal);

    private static Report Analyze(string path, MeasureOptions? measure)
    {
        using FileStream stream = File.OpenRead(path);
        return Analyzer.CreateDefault().Analyze(Path.GetFileName(path), stream, new AnalysisOptions { Decoding = new DecodeOptions { Measure = measure } });
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void MeasuringChangesNeitherTheVerificationNorADecodeFindingOfAnyFixture(string family)
    {
        int measured = 0;
        foreach (string path in Files(family))
        {
            Report plain = Analyze(path, null);
            foreach (MeasureOptions measure in new MeasureOptions[] { new(), new() { MaxWork = 1 << 14 } })
            {
                Report other = Analyze(path, measure);
                Assert.Equal(plain.Streams.Count, other.Streams.Count);
                for (int i = 0; i < plain.Streams.Count; i++)
                {
                    StreamVerification? a = plain.Streams[i].Verification;
                    StreamVerification? b = other.Streams[i].Verification;
                    Assert.Equal(a is null, b is null);
                    if (a is not null && b is not null)
                    {
                        Assert.Equal((a.Decoding, a.DecodedSamples, a.IsVerified, a.IsLossy), (b.Decoding, b.DecodedSamples, b.IsVerified, b.IsLossy));
                        Assert.True(a.Checksums.SequenceEqual(b.Checksums), Path.GetFileName(path));
                    }
                }

                Assert.True(plain.Findings.Select(f => f.Code).Order().SequenceEqual(other.Findings.Where(f => !f.Code.StartsWith("measure.", StringComparison.Ordinal)).Select(f => f.Code).Order()), Path.GetFileName(path));
                Assert.DoesNotContain(other.Findings, f => f.Code == "measure.failed");
                measured += other.Streams.Count(s => s.Measurements is { Spectrum: not null });
            }
        }

        Assert.True(measured > 10, $"Only {measured} streams of {family} were measured.");
    }

    // Every analysis of a decoded stream that has a decoder carries a record, whatever it holds.
    [Theory]
    [MemberData(nameof(Families))]
    public void EveryStreamWithASessionCarriesARecordInAMeasuredAnalysis(string family)
    {
        foreach (string path in Files(family))
        {
            Report report = Analyze(path, new MeasureOptions());
            foreach (StreamInfo stream in report.Streams.Where(s => s.Verification is not null))
            {
                Assert.True(stream.Measurements is not null, $"{Path.GetFileName(path)} has a verification and no measurements");
            }
        }
    }

    // The decoded blocks of a fixture, kept, then handed to a measurer cut another way.
    private sealed class Keeping : IPcmSink
    {
        public List<(long Position, int Length, int[] Planar)> Blocks { get; } = [];

        public PcmFormat? Format { get; set; }

        public void Write(PcmBlock block)
        {
            if (block.Format != SampleFormat.Integer)
            {
                return;
            }

            int[] planar = new int[block.Length * block.Channels];
            for (int c = 0; c < block.Channels; c++)
            {
                block.IntegerChannel(c).CopyTo(planar.AsSpan(c * block.Length, block.Length));
            }

            Blocks.Add((block.Position, block.Length, planar));
        }

        public void End(long samples)
        {
        }
    }

    [Theory]
    [InlineData("flac", "external-16-stereo-l5.flac")]
    [InlineData("flac", "external-24-stereo.flac")]
    [InlineData("flac", "external-16-5ch.flac")]
    [InlineData("wavpack", "external-16-stereo-fast.wv")]
    [InlineData("monkeys-audio", "external-level-normal-16-stereo.ape")]
    [InlineData("truehd", "external-truehd-stereo.thd")]
    public void TheFiguresOfARealDecoderDoNotDependOnItsBlocks(string family, string file)
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, family, file);
        if (!File.Exists(path))
        {
            path = Files(family).First(p => Path.GetFileName(p).StartsWith(file.Split('-')[0], StringComparison.Ordinal) && !p.EndsWith(".wvc", StringComparison.Ordinal));
        }

        var keeping = new Keeping();
        using (FileStream stream = File.OpenRead(path))
        {
            Analyzer.CreateDefault().Analyze(Path.GetFileName(path), stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = s => { keeping.Format = s.Format; return keeping; } } });
        }

        Assert.NotNull(keeping.Format);
        Assert.NotEmpty(keeping.Blocks);
        PcmFormat format = keeping.Format;
        StreamMeasurements? first = null;
        foreach (int chunk in new[] { 0, 1000, 4099, 1 << 20 })
        {
            var measurer = new Encspect.Host.Measurement.StreamMeasurer(format, new MeasureOptions(), CancellationToken.None, _ => true);
            foreach ((long position, int length, int[] planar) in keeping.Blocks)
            {
                int step = chunk == 0 ? length : chunk;
                for (int start = 0; start < length; start += step)
                {
                    int n = Math.Min(step, length - start);
                    int[] part = new int[n * format.Channels];
                    for (int c = 0; c < format.Channels; c++)
                    {
                        planar.AsSpan((c * length) + start, n).CopyTo(part.AsSpan(c * n, n));
                    }

                    measurer.Write(new PcmBlock(position + start, n, format.Channels, n, part));
                }
            }

            Assert.Null(measurer.Failure);
            StreamMeasurements result = measurer.Result(MeasurementStatus.Complete, null);
            if (first is null)
            {
                first = result;
            }
            else
            {
                MeasurementTests.AssertSame(first, result);
            }
        }
    }

    [Fact]
    public void AMeasurementCanceledInsideALargeBlockStopsWithinAChunk()
    {
        using var cts = new CancellationTokenSource();
        var measurer = new Encspect.Host.Measurement.StreamMeasurer(new PcmFormat(44100, 1, 16, SampleFormat.Integer), new MeasureOptions(), cts.Token, _ =>
        {
            cts.Cancel();
            return true;
        });
        int[] block = new int[1 << 22];
        measurer.Write(new PcmBlock(0, block.Length, 1, block.Length, block));

        Assert.True(measurer.IsCanceled);
        Assert.InRange(measurer.Samples, 1, 1 << 17);
        Assert.Null(measurer.Failure);
    }

    [Fact]
    public void ACancelAnywhereEndsTheAnalysisAndNeverLeavesACompleteRecord()
    {
        foreach (int cancelAt in new[] { 0, 1, 3 })
        {
            using var cts = new CancellationTokenSource();
            var decoder = new MeasurementRouteTests.EmittingDecoder(new PcmFormat(44100, 2, 16, SampleFormat.Integer), (sink, index) =>
            {
                sink.Write(new PcmBlock(index * 100, 100, 2, 100, new int[200]));
                if (index == cancelAt)
                {
                    cts.Cancel();
                }
            });
            var analyzer = new Analyzer([], [new DecodeRouteTests.FrameParser()], [], [decoder]);
            var options = new AnalysisOptions { CancellationToken = cts.Token, Decoding = new DecodeOptions { Measure = new MeasureOptions() } };

            // The last block is the fourth: a cancel that comes while it is measured has no later check by the decoder, and the host throws.
            Assert.Throws<OperationCanceledException>(() => analyzer.Analyze("s.bin", new MemoryStream(new byte[64]), options));
        }
    }

    [Fact]
    public void ADecoderStoppedByTheFrontEndLeavesAStoppedRecord()
    {
        using var stop = new CancellationTokenSource();
        var decoder = new MeasurementRouteTests.EmittingDecoder(new PcmFormat(44100, 2, 16, SampleFormat.Integer), (sink, index) =>
        {
            sink.Write(new PcmBlock(index * 100, 100, 2, 100, new int[200]));
            if (index == 3)
            {
                stop.Cancel();
            }
        });
        var analyzer = new Analyzer([], [new DecodeRouteTests.FrameParser()], [], [decoder]);

        Report report = analyzer.Analyze("s.bin", new MemoryStream(new byte[64]), new AnalysisOptions { Decoding = new DecodeOptions { Stop = stop.Token, Measure = new MeasureOptions() } });

        // The stop came after the last block: nothing was cut short, and the record says what it measured.
        StreamMeasurements m = report.Streams.Single().Measurements!;
        Assert.Equal(400, m.Samples);
        Assert.Equal(MeasurementStatus.Stopped, m.Status);
    }
}
