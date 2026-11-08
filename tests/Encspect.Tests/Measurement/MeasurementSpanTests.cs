using System.Numerics;
using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// The low bits unused per span (docs/design/lossywav-detection.md, "The measurement"): the figures against a model written from the definitions on
// signals built here, the rules for silent, short and thin spans, the grid, and figures that do not depend on how a decoder cuts blocks.
public class MeasurementSpanTests
{
    private static readonly MeasureOptions LevelsOnly = new() { Include = MeasurementKinds.Levels };

    private sealed record Segment(long Position, int[][] Channels);

    private static StreamMeasurements Run(int bits, int blockSize, params Segment[] segments)
    {
        int channels = segments[0].Channels.Length;
        var measurer = new StreamMeasurer(new PcmFormat(44100, channels, bits, SampleFormat.Integer), LevelsOnly, CancellationToken.None, _ => true);
        foreach (Segment segment in segments)
        {
            int length = segment.Channels[0].Length;
            for (int start = 0; start < length; start += blockSize)
            {
                int n = Math.Min(blockSize, length - start);
                int[] planar = new int[channels * n];
                for (int c = 0; c < channels; c++)
                {
                    segment.Channels[c].AsSpan(start, n).CopyTo(planar.AsSpan(c * n, n));
                }

                measurer.Write(new PcmBlock(segment.Position + start, n, channels, n, planar));
            }
        }

        Assert.Null(measurer.Failure);
        return measurer.Result(MeasurementStatus.Complete, null);
    }

    private static LowBitsSpanFigures Spans(int bits, int blockSize, int[][] channels, long position = 0) =>
        Run(bits, blockSize, new Segment(position, channels)).Levels!.Spans!;

    // lossyWAV-like audio: noise of the given amplitude whose lowest bits are zeroed, a count for each block of `block` samples and channel that depends
    // on the block (and on the channel unless linked).
    internal static int[][] Zeroed(int channels, int length, int block, int amplitude, uint seed, bool isLinked = false, int first = 0)
    {
        var data = new int[channels][];
        for (int c = 0; c < channels; c++)
        {
            double[] noise = MeasurementSignals.Noise(length, seed + (uint)c);
            data[c] = new int[length];
            for (int i = 0; i < length; i++)
            {
                int k = Count(((i + first) / block) + (isLinked ? 0 : 7 * c), seed);
                data[c][i] = ((int)(noise[i] * amplitude) >> k) << k;
            }
        }

        return data;
    }

    // A count from 3 to 8 that changes with most blocks, from a hash of the block.
    private static int Count(int block, uint seed)
    {
        uint h = unchecked(((uint)block * 2654435761u) ^ (seed * 40503u));
        h ^= h >> 15;
        h = unchecked(h * 2246822519u);
        return 3 + (int)((h >> 13) % 6);
    }

    // The definitions, written plainly: spans cut at the multiples of 32 of the stream's positions inside each segment, a count and a magnitude per span,
    // the rules for silent, short and thin spans, the changes, the grid and the times.
    private sealed record Model(long[] Distribution, long Silent, long Thin, long Short, long Changes, long Moments, int Grid, long OnGrid, long Times, long Equal);

    private static Model Measure(int bits, params Segment[] segments)
    {
        int channels = segments[0].Channels.Length;
        long[] distribution = new long[33];
        long silent = 0, thin = 0, shortSpans = 0, changes = 0, times = 0, equal = 0;
        long[] phase = new long[2048];
        long moments = 0;
        foreach (Segment segment in segments)
        {
            int length = segment.Channels[0].Length;
            int[] last = Enumerable.Repeat(-1, channels).ToArray();
            for (long index = segment.Position >> 5; (index << 5) < segment.Position + length; index++)
            {
                long from = Math.Max(segment.Position, index << 5);
                long to = Math.Min(segment.Position + length, (index + 1) << 5);
                var counts = new List<int>();
                bool moved = false;
                for (int c = 0; c < channels; c++)
                {
                    int or = 0;
                    int magnitude = 0;
                    for (long p = from; p < to; p++)
                    {
                        int v = segment.Channels[c][(int)(p - segment.Position)];
                        or |= v;
                        magnitude |= v >= 0 ? v : ~v;
                    }

                    if (or == 0)
                    {
                        silent++;
                        counts.Add(-1);
                        continue;
                    }

                    int count = BitOperations.TrailingZeroCount(or);
                    counts.Add(count);
                    distribution[count]++;
                    bool isShort = to - from < 32;
                    shortSpans += isShort ? 1 : 0;
                    if ((32 - BitOperations.LeadingZeroCount((uint)magnitude)) - count < 4)
                    {
                        thin++;
                    }
                    else if (!isShort)
                    {
                        if (last[c] >= 0 && last[c] != count)
                        {
                            changes++;
                            moved = true;
                        }

                        last[c] = count;
                    }
                }

                if (moved)
                {
                    moments++;
                    phase[(int)(index % 2048)]++;
                }

                if (channels > 1 && !counts.Contains(-1))
                {
                    times++;
                    equal += counts.Distinct().Count() == 1 ? 1 : 0;
                }
            }
        }

        (int grid, long onGrid) = GridOf(phase, moments);
        return new Model(distribution, silent, thin, shortSpans, changes, moments, grid, onGrid, times, equal);
    }

    private static (int, long) GridOf(long[] phase, long moments)
    {
        if (moments < 20)
        {
            return (0, 0);
        }

        for (int length = 65536; length >= 256; length /= 2)
        {
            int n = length / 32;
            long[] folded = new long[n];
            for (int i = 0; i < 2048; i++)
            {
                folded[i % n] += phase[i];
            }

            int m = Array.IndexOf(folded, folded.Max());
            long hits = folded[m] + folded[(m + n - 1) % n] + folded[(m + 1) % n];
            if (hits * 20 >= moments * 19)
            {
                return (length, hits);
            }
        }

        return (0, 0);
    }

    private static void AssertEqual(Model model, LowBitsSpanFigures spans, string what = "")
    {
        Assert.True(model.Distribution.Take(spans.Distribution.Count).SequenceEqual(spans.Distribution), $"{what}: distribution");
        Assert.Equal(model.Distribution.Sum(), spans.Distribution.Sum());
        Assert.Equal(model.Distribution.Sum(), spans.Spans);
        Assert.Equal((model.Silent, model.Thin, model.Short, model.Changes), (spans.SilentSpans, spans.ThinSpans, spans.ShortSpans, spans.Changes));
        Assert.Equal((model.Moments, model.Grid, model.OnGrid), (spans.ChangeMoments, spans.Grid, spans.MomentsOnGrid));
        Assert.Equal((model.Times, model.Equal), (spans.Times, spans.TimesWithEqualCounts));
    }

    public static TheoryData<int, int, int> Cases()
    {
        var data = new TheoryData<int, int, int>();
        foreach (int channels in new[] { 1, 2, 3 })
        {
            foreach (int blockSize in new[] { 1, 7, 32, 33, 1000, 4096, 20000 })
            {
                data.Add(24, channels, blockSize);
            }
        }

        data.Add(16, 2, 4096);
        return data;
    }

    // A stream with silence, a change of phase and gaps at positions that are not multiples of 32: the figures are the model's, and the same for
    // every cut of the blocks.
    [Theory]
    [MemberData(nameof(Cases))]
    public void TheFiguresEqualTheModelForAnyCutOfTheBlocks(int bits, int channels, int blockSize)
    {
        int[][] first = Zeroed(channels, 30_000, 512, bits == 24 ? 40_000 : 3000, 5);
        for (int c = 0; c < channels; c++)
        {
            Array.Clear(first[c], 6000 + (c * 40), 700);
        }

        int[][] second = Zeroed(channels, 9_000, 256, bits == 24 ? 40_000 : 3000, 6, first: 77);
        int[][] third = Zeroed(channels, 777, 512, bits == 24 ? 40_000 : 3000, 7);
        Segment[] segments = [new(0, first), new(30_005, second), new(50_021, third)];

        LowBitsSpanFigures spans = Run(bits, blockSize, segments).Levels!.Spans!;
        AssertEqual(Measure(bits, segments), spans, $"{channels} channels, blocks of {blockSize}");
        Assert.True(spans.Changes > 0);
    }

    [Fact]
    public void ALossyWavLikeStreamHasItsGridAndEveryChangeOnIt()
    {
        LowBitsSpanFigures spans = Spans(24, 4096, Zeroed(2, 200_000, 512, 60_000, 11));

        Assert.Equal(512, spans.Grid);
        Assert.Equal(spans.ChangeMoments, spans.MomentsOnGrid);
        Assert.True(spans.Changes > 500);
        Assert.True(spans.SpansAboveLowBitsUnused > 0);
        Assert.InRange(spans.TimesWithEqualCounts * 100 / spans.Times, 5, 60);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(65536)]
    public void TheGridIsTheBlockLengthWhateverItsSize(int block)
    {
        Assert.Equal(block, Spans(24, 8192, Zeroed(1, Math.Max(40 * block, 100_000), block, 60_000, 12)).Grid);
    }

    [Fact]
    public void LinkedChannelsAgreeAtEveryTime()
    {
        LowBitsSpanFigures spans = Spans(24, 4096, Zeroed(2, 100_000, 512, 60_000, 13, isLinked: true));

        Assert.Equal(spans.Times, spans.TimesWithEqualCounts);
        Assert.True(spans.Times > 0);
    }

    [Fact]
    public void AStreamCutOffItsGridKeepsIt()
    {
        int[][] whole = Zeroed(2, 100_000, 512, 60_000, 14);
        int[][] cut = [.. whole.Select(c => c[100..])];

        Assert.Equal(512, Spans(24, 4096, cut).Grid);

        // The same samples at positions that start at 100 and at 5,000,000 have the same grid.
        Assert.Equal(512, Spans(24, 4096, whole, 100).Grid);
        Assert.Equal(512, Spans(24, 4096, whole, 5_000_000).Grid);
    }

    [Fact]
    public void TwoPhasesInOneStreamHaveNoGrid()
    {
        int[][] a = Zeroed(2, 60_000, 512, 60_000, 15);
        int[][] b = Zeroed(2, 60_000, 512, 60_000, 16, first: 236);
        int[][] joined = [.. a.Select((x, c) => x.Concat(b[c]).ToArray())];

        LowBitsSpanFigures spans = Spans(24, 4096, joined);

        Assert.True(spans.Changes > 100);
        Assert.Equal(0, spans.Grid);
    }

    [Fact]
    public void SixteenBitAudioInTwentyFourBitsHasOneCountAndNoChange()
    {
        int[] samples = [.. MeasurementSignals.Noise(50_000, 17).Select(v => (int)(v * 30_000) << 8)];
        LevelFigures levels = Run(24, 4096, new Segment(0, [samples, samples])).Levels!;

        Assert.Equal(8, levels.Stream.LowBitsUnused);
        Assert.Equal(0, levels.Spans!.SpansAboveLowBitsUnused);
        Assert.Equal(0, levels.Spans.Changes);
        Assert.Equal(0, levels.Spans.Grid);
        Assert.Equal(9, levels.Spans.Distribution.Count);
        Assert.Equal(levels.Spans.Spans, levels.Spans.Distribution[8]);
    }

    [Fact]
    public void AFullScaleSquareWaveHasNoChangeBecauseItsSpansAreThin()
    {
        // Spans of full-scale negative codes have a magnitude equal to their count, and the spans at the edge of a half period hold both signs.
        int[] square = [.. Enumerable.Range(0, 20_000).Select(i => (i / 50) % 2 == 0 ? 32767 : -32768)];
        LowBitsSpanFigures spans = Spans(16, 4096, [square]);

        Assert.Equal(0, spans.Changes);
        Assert.True(spans.ThinSpans > 0);
    }

    [Fact]
    public void ASpanOneBitAboveItsBlockByChanceIsNoChangeWhenItIsThin()
    {
        // Spans of tiny samples whose counts alternate (2, 4 and 6 have one low bit unused, 1, 2 and 3 none): thin spans take no part in the changes.
        int[] small = [.. Enumerable.Range(0, 3200).Select(i => (i / 32 % 2 == 0 ? 2 : 1) * ((i % 3) + 1))];
        LowBitsSpanFigures spans = Spans(16, 4096, [small]);

        Assert.Equal(0, spans.Changes);
        Assert.Equal(spans.Spans, spans.ThinSpans);
    }

    [Fact]
    public void ASilentSpanHasNoCountAndTheChangesSkipIt()
    {
        int[] samples = new int[32 * 12];
        for (int span = 0; span < 12; span++)
        {
            int count = span < 6 ? 4 : 6;
            for (int i = 0; i < 32; i++)
            {
                samples[(span * 32) + i] = ((i % 7) + 9) << count;
            }
        }

        Array.Clear(samples, 32 * 5, 32);
        LowBitsSpanFigures spans = Spans(16, 4096, [samples]);

        Assert.Equal(1, spans.SilentSpans);
        Assert.Equal(11, spans.Spans);
        Assert.Equal(1, spans.Changes);
    }

    [Fact]
    public void AGapEndsTheOpenSpanShortAndNoChangeIsCountedAcrossIt()
    {
        int[] a = [.. Enumerable.Range(0, 100).Select(i => (i % 7 + 9) << 4)];
        int[] b = [.. Enumerable.Range(0, 100).Select(i => (i % 7 + 9) << 6)];
        LowBitsSpanFigures spans = Run(16, 4096, new Segment(0, [a]), new Segment(1000, [b])).Levels!.Spans!;

        // 100 samples from 0: spans 0 to 2 whole and span 3 of 4 samples; 100 samples from 1,000: span 31 of 24 samples, spans 32 to 33 whole, span 34 of 12.
        Assert.Equal(8, spans.Spans);
        Assert.Equal(3, spans.ShortSpans);
        Assert.Equal(0, spans.Changes);
    }

    [Fact]
    public void ASampleCountThatIsNoMultipleOfTheSpanLeavesAShortLastSpan()
    {
        LowBitsSpanFigures spans = Spans(16, 4096, [[.. Enumerable.Range(0, 100).Select(i => 12 << 3)]]);

        Assert.Equal(4, spans.Spans);
        Assert.Equal(1, spans.ShortSpans);
    }

    [Fact]
    public void AStreamOfSilenceHasOnlySilentSpans()
    {
        LevelFigures levels = Run(16, 4096, new Segment(0, [new int[1000], new int[1000]])).Levels!;

        Assert.Equal(0, levels.Spans!.Spans);
        Assert.Equal(64, levels.Spans.SilentSpans);
        Assert.Empty(levels.Spans.Distribution);
        Assert.Equal(0, levels.Spans.Times);
    }

    [Fact]
    public void FloatsHaveNoSpanFigures()
    {
        var measurer = new StreamMeasurer(new PcmFormat(44100, 1, 32, SampleFormat.Float32), LevelsOnly, CancellationToken.None, _ => true);
        float[] floats = [.. MeasurementSignals.Noise(5000, 3).Select(v => (float)v)];
        measurer.Write(new PcmBlock(0, 5000, 1, 5000, floats));

        Assert.Null(measurer.Result(MeasurementStatus.Complete, null).Levels!.Spans);
    }

    [Fact]
    public void ThePositionsOfATrimmedStreamKeepTheGridOf32()
    {
        // 100 samples from position 8,000 (a multiple of 32): the same spans as from 0, with every span whole.
        int[][] data = Zeroed(1, 1024, 256, 60_000, 18);
        LowBitsSpanFigures atZero = Spans(24, 4096, data);
        LowBitsSpanFigures later = Spans(24, 4096, data, 8000);

        Assert.Equal(atZero.Distribution, later.Distribution);
        Assert.Equal(atZero.Changes, later.Changes);

        // From a position inside a span, the first span is short and the rest are on the grid of 32.
        LowBitsSpanFigures offset = Spans(24, 4096, data, 8005);
        Assert.Equal(2, offset.ShortSpans);
    }

    private static void AssertSame(LowBitsSpanFigures expected, LowBitsSpanFigures actual)
    {
        Assert.Equal(expected.Distribution, actual.Distribution);
        Assert.Equal(
            [expected.Spans, expected.SilentSpans, expected.ThinSpans, expected.ShortSpans, expected.SpansAboveLowBitsUnused, expected.Changes, expected.ChangeMoments, expected.Grid, expected.MomentsOnGrid, expected.Times, expected.TimesWithEqualCounts],
            [actual.Spans, actual.SilentSpans, actual.ThinSpans, actual.ShortSpans, actual.SpansAboveLowBitsUnused, actual.Changes, actual.ChangeMoments, actual.Grid, actual.MomentsOnGrid, actual.Times, actual.TimesWithEqualCounts]);
    }

    private static StreamMeasurer Measurer(int channels) => new(new PcmFormat(44100, channels, 24, SampleFormat.Integer), LevelsOnly, CancellationToken.None, _ => true);

    private static void Feed(StreamMeasurer measurer, int[][] data, long position)
    {
        int channels = data.Length;
        for (int start = 0; start < data[0].Length; start += 4096)
        {
            int n = Math.Min(4096, data[0].Length - start);
            int[] planar = new int[channels * n];
            for (int c = 0; c < channels; c++)
            {
                data[c].AsSpan(start, n).CopyTo(planar.AsSpan(c * n, n));
            }

            measurer.Write(new PcmBlock(position + start, n, channels, n, planar));
        }
    }

    // The stream's own trim (IDecodeSession.Trim): the samples before the start are not measured, and the spans stand where they would if the stream began there.
    [Fact]
    public void ATrimmedStartMeasuresLikeAStreamThatBeginsThere()
    {
        int[][] data = Zeroed(2, 30_000, 512, 60_000, 21);
        StreamMeasurer trimmed = Measurer(2);
        trimmed.Trim(1005, long.MaxValue);
        Feed(trimmed, data, 0);

        AssertSame(Spans(24, 4096, [.. data.Select(c => c[1005..])], 1005), trimmed.Result(MeasurementStatus.Complete, null).Levels!.Spans!);
    }

    // Withheld output (a decoder holds it back) is a jump in the positions: the open span ends short and no change is counted across it.
    [Fact]
    public void WithheldOutputIsAJumpThatEndsTheOpenSpan()
    {
        int[][] data = Zeroed(2, 40_000, 512, 60_000, 22);
        StreamMeasurer withheld = Measurer(2);
        Feed(withheld, [.. data.Select(c => c[..20_010])], 0);
        withheld.Withhold(20_010, 1000, WithholdReason.AfterGap);
        Feed(withheld, [.. data.Select(c => c[21_010..])], 21_010);

        LowBitsSpanFigures expected = Run(24, 4096, new Segment(0, [.. data.Select(c => c[..20_010])]), new Segment(21_010, [.. data.Select(c => c[21_010..])])).Levels!.Spans!;
        AssertSame(expected, withheld.Result(MeasurementStatus.Complete, null).Levels!.Spans!);
        Assert.True(expected.ShortSpans >= 2);
    }

    // One switch of bit usage at one time is one moment, however many channels change at it: no grid in any channel count.
    [Theory]
    [InlineData(2)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void OneSwitchInManyChannelsIsOneMomentAndNoGrid(int channels)
    {
        var data = new int[channels][];
        for (int c = 0; c < channels; c++)
        {
            double[] noise = MeasurementSignals.Noise(120_000, (uint)(100 + c));
            data[c] = [.. noise.Select((v, i) => i < 60_000 ? (int)(v * 4_000_000) & ~0xFF : (int)(v * 4_000_000))];
        }

        LowBitsSpanFigures spans = Spans(24, 4096, data);

        Assert.Equal(channels, spans.Changes);
        Assert.Equal(1, spans.ChangeMoments);
        Assert.Equal(0, spans.Grid);
        AssertEqual(Measure(24, new Segment(0, data)), spans);
    }

    // Bits above the declared width are no sample bits: a 16-bit stream of values that are multiples of 2^20 has only silent spans.
    [Fact]
    public void BitsBeyondTheDeclaredWidthAreNotCounted()
    {
        LevelFigures levels = Run(16, 4096, new Segment(0, [[.. Enumerable.Repeat(1 << 20, 64)]])).Levels!;

        Assert.Equal(0, levels.Spans!.Spans);
        Assert.Equal(2, levels.Spans.SilentSpans);
    }

    [Fact]
    public void TheEstimateHoldsTheDistributionAndThePhaseCounters()
    {
        Assert.InRange(LevelsMeter.EstimateBytes(1, SampleFormat.Integer, false), 16 * 1024, 20 * 1024);
    }
}
