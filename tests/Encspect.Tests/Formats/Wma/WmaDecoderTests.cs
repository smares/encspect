using Encspect.Core;
using Encspect.Formats.Wma;
using Encspect.Formats.Wma.Decoding;
using Encspect.Host;

namespace Encspect.Tests.Formats.Wma;

// The decoder of Windows Media Audio versions 1 and 2 (docs/design/wma-standard-decoding.md): the inverse transform against its definition, the positions of what it writes, and what
// a lost frame takes with it. The samples against FFmpeg's are the cross-check's (crosscheck_wma_pcm.py), their hashes the goldens'.
public class WmaDecoderTests
{
    private sealed class RunSink : IPcmSink
    {
        public List<(long First, int Length)> Runs { get; } = [];

        public long Samples => Runs.Sum(r => (long)r.Length);

        public void Write(PcmBlock block)
        {
            if (Runs.Count > 0 && Runs[^1].First + Runs[^1].Length == block.Position)
            {
                Runs[^1] = (Runs[^1].First, Runs[^1].Length + block.Length);
            }
            else
            {
                Runs.Add((block.Position, block.Length));
            }
        }

        public void End(long samples)
        {
        }
    }

    private static (Report Report, RunSink Sink) Decode(string name, MeasureOptions? measure = null)
    {
        var sink = new RunSink();
        using FileStream file = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), file, new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => sink, Measure = measure } });
        return (report, sink);
    }

    [Fact]
    public void TheInverseTransformIsTheSumOfTheDescription()
    {
        foreach (int length in new[] { 128, 256, 2048 })
        {
            var random = new Random(length);
            float[] lines = new float[length];
            for (int i = 0; i < length; i++)
            {
                lines[i] = (float)((random.NextDouble() * 2) - 1) * 1000f;
            }

            float[] output = new float[2 * length];
            new InverseMdct(2 * length, -1f / 32768f).Transform(lines, output);
            foreach (int n in new[] { 0, 1, length / 2, length - 1, length, (3 * length / 2) + 3, (2 * length) - 1 })
            {
                double sum = 0;
                for (int q = 0; q < length; q++)
                {
                    sum += lines[q] * Math.Cos(Math.PI / length * (n + 0.5 + (length / 2.0)) * (q + 0.5));
                }

                Assert.True(Math.Abs((-sum / 32768) - output[n]) < 1e-4, $"length {length}, output {n}: {-sum / 32768} against {output[n]}");
            }
        }
    }

    [Fact]
    public void TheLineSpectralPairCodebookHasARowForEachIndex()
    {
        Assert.Equal([8, 16, 16, 16, 16, 16, 16, 16, 8, 8], Tables.LspCodebook.Select(r => r.Length));
        Assert.All(Tables.LspCodebook.SelectMany(r => r), v => Assert.InRange(v, -2f, 2f));
    }

    // The first two frames' output is the start the syntax trims: the decoder writes from the third frame on, and the tail of the last overlap follows the last frame.
    [Fact]
    public void TheOutputStartsAtTheThirdFrameAndEndsWithTheLastOverlapsCompletePart()
    {
        (Report report, RunSink sink) = Decode("asf/external-windows-wma92-cbr-stereo-44k-128.wma");
        StreamInfo stream = Assert.Single(report.Streams);

        Assert.Equal([(4096L, 133_632)], sink.Runs.Select(r => (r.First, r.Length)));
        Assert.Equal(stream.Samples, sink.Samples);
        Assert.Equal(DecodeStatus.Decoded, stream.Verification!.Decoding);
        Assert.Equal(133_632, stream.Verification.DecodedSamples);
    }

    [Fact]
    public void AStreamWithoutBlockSwitchingEndsWithTheLastFrame()
    {
        (Report report, RunSink sink) = Decode("asf/external-ffmpeg-wmav2-stereo-44k-128k.wma");

        // 44 frames, the first two not written, and no tail (the last block is as long as the frame).
        Assert.Equal([(4096L, 42 * 2048)], sink.Runs.Select(r => (r.First, r.Length)));
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal));
    }

    // A frame that fails its syntax is lost; the frame after it decodes for its overlap and the span that depends on the lost frame is withheld (after a gap): the first half frame and
    // half of the first block's overlap, here a block as long as the frame.
    [Fact]
    public void AFrameAfterAGapWithholdsTheOverlapItShapes()
    {
        (Report report, RunSink sink) = Decode("asf/derived-windows-wma92-stereo-44k-128-damaged.asf", new MeasureOptions());
        StreamInfo stream = Assert.Single(report.Streams);

        Assert.True(sink.Runs.Count > 1, "A lost frame leaves a jump in the positions.");
        StreamMeasurements measurements = stream.Measurements!;
        Assert.True(measurements.WithheldSamples > 0);
        Assert.Equal(measurements.WithheldSamples, measurements.WithheldByReason.AfterGap);
        Assert.True(stream.LostSamples > 0);
    }
}
