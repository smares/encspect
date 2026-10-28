using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

public class MeasurementTests
{
    private static int[] Quantized(double[] values, double peak) => MeasurementSignals.ToIntegers(values.Select(v => v * peak).ToArray(), 16);

    private static double Peak(double[] values) => 0.5 / values.Max(Math.Abs);

    [Fact]
    public void ABrickwallAtSixteenKilohertzIsFoundWithinARowOfIt()
    {
        double[] signal = BandLimited.Lines(44100 * 3, 44100, 1486, 1.0);
        StreamMeasurements m = MeasurementSignals.Measure([Quantized(signal, Peak(signal))], 44100, 16, 4096);

        SpectralCutoff cutoff = m.Spectrum!.Cutoff;
        Assert.True(cutoff.IsFound);
        Assert.InRange(cutoff.Frequency, 16000 - 45, 16000 + 45);
        Assert.True(cutoff.StepTenthsOfDb > 600);
        Assert.Equal(cutoff.FramesWithSignal, cutoff.FramesShowing);
        Assert.True(cutoff.FramesWithSignal > 50);
        Assert.Equal(512, m.Spectrum.Combined.Length);
        Assert.Equal(m.Spectrum.Cutoff, m.Spectrum.ChannelCutoffs[0]);
    }

    [Fact]
    public void WhiteNoiseHasNoCutoff()
    {
        int[] noise = MeasurementSignals.ToIntegers(MeasurementSignals.Noise(44100 * 3, 5).Select(v => v * 0.3).ToArray(), 16);
        StreamMeasurements m = MeasurementSignals.Measure([noise], 44100, 16, 4096);

        Assert.False(m.Spectrum!.Cutoff.IsFound);
        Assert.True(m.Spectrum.Cutoff.StepTenthsOfDb < 100);
    }

    [Fact]
    public void ALowFrequencyChannelDoesNotSetTheCombinedCutoff()
    {
        // Five channels with a 16 kHz brickwall and a wideband LFE: without the LFE the combined spectrum shows the edge.
        double[] band = BandLimited.Lines(44100 * 2, 44100, 1486, 1.0);
        int[] limited = Quantized(band, Peak(band));
        int[] wide = MeasurementSignals.ToIntegers(MeasurementSignals.Noise(44100 * 2, 9).Select(v => v * 0.5).ToArray(), 16);
        Speaker[] layout = [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight];

        StreamMeasurements withLayout = MeasurementSignals.Measure([limited, limited, limited, wide, limited, limited], 44100, 16, 4096, layout: layout);
        StreamMeasurements without = MeasurementSignals.Measure([limited, limited, limited, wide, limited, limited], 44100, 16, 4096);

        Assert.True(withLayout.Spectrum!.Cutoff.IsFound);
        Assert.False(without.Spectrum!.Cutoff.IsFound);
        Assert.True(withLayout.Spectrum.ChannelCutoffs[3].StepTenthsOfDb < 100);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(1 << 17)]
    public void TheFiguresDoNotDependOnHowTheDecoderCutsBlocks(int blockSize)
    {
        double[] a = BandLimited.Lines(44100 * 2, 44100, 1000, 1.0);
        int[][] data = [Quantized(a, Peak(a)), MeasurementSignals.ToIntegers(MeasurementSignals.Noise(44100 * 2, 2).Select(v => v * 0.2).ToArray(), 16)];

        StreamMeasurements reference = MeasurementSignals.Measure(data, 44100, 16, 4096);
        StreamMeasurements other = MeasurementSignals.Measure(data, 44100, 16, blockSize);

        AssertSame(reference, other);
    }

    internal static void AssertSame(StreamMeasurements a, StreamMeasurements b)
    {
        Assert.Equal(a with { Spectrum = null, Levels = null, Spectrogram = null, Waveform = null, ChannelLayout = null }, b with { Spectrum = null, Levels = null, Spectrogram = null, Waveform = null, ChannelLayout = null });
        Assert.Equal(a.Levels?.Stream, b.Levels?.Stream);
        Assert.True((a.Levels?.Channels ?? []).SequenceEqual(b.Levels?.Channels ?? []));
        Assert.Equal(a.ChannelLayout ?? [], b.ChannelLayout ?? []);
        Assert.Equal(a.Spectrum!.Cutoff, b.Spectrum!.Cutoff);
        Assert.Equal(a.Spectrum.ChannelCutoffs, b.Spectrum.ChannelCutoffs);
        Assert.Equal(a.Spectrum.Frames, b.Spectrum.Frames);
        Assert.True(a.Spectrum.Combined.Span.SequenceEqual(b.Spectrum.Combined.Span));
        for (int c = 0; c < a.Spectrum.PerChannel.Count; c++)
        {
            Assert.True(a.Spectrum.PerChannel[c].Span.SequenceEqual(b.Spectrum.PerChannel[c].Span));
        }

        Assert.Equal(a.Spectrogram!.Columns, b.Spectrogram!.Columns);
        Assert.True(a.Spectrogram.Power.Span.SequenceEqual(b.Spectrogram.Power.Span));
        Assert.True(a.Waveform!.Minimum.Span.SequenceEqual(b.Waveform!.Minimum.Span));
        Assert.True(a.Waveform.Maximum.Span.SequenceEqual(b.Waveform.Maximum.Span));
    }

    [Fact]
    public void LongStreamsMergeTheirColumnsInPairsAndKeepBetween512And1024()
    {
        // 1,100 hops of 2,048 samples at 44.1 kHz.
        int[] silence = new int[2048 * 1100];
        StreamMeasurements m = MeasurementSignals.Measure([silence], 44100, 16, 8192);

        Assert.Equal(2, m.Spectrogram!.HopsPerColumn);
        Assert.InRange(m.Spectrogram.Columns, 512, 1024);
        Assert.Equal(m.Spectrogram.Columns * 512, m.Spectrogram.Power.Length);
        Assert.Equal(m.Spectrogram.Columns, m.Waveform!.Columns);
    }

    [Fact]
    public void ABurstShowsInTheColumnOfItsTime()
    {
        int[] signal = new int[44100 * 4];
        int[] burst = MeasurementSignals.ToIntegers(MeasurementSignals.Noise(8192, 4).Select(v => v * 0.5).ToArray(), 16);
        burst.CopyTo(signal, 44100 * 2);
        StreamMeasurements m = MeasurementSignals.Measure([signal], 44100, 16, 4096);

        SpectrogramGrid grid = m.Spectrogram!;
        int hop = grid.HopSamples;
        int expected = 44100 * 2 / hop;
        int loudest = 0;
        for (int column = 0; column < grid.Columns; column++)
        {
            if (grid.Power.Span[(column * 512) + 100] > grid.Power.Span[(loudest * 512) + 100])
            {
                loudest = column;
            }
        }

        Assert.InRange(loudest, expected - 2, expected + 2);

        // The waveform's extremes at the burst, and nothing at the start.
        Assert.Equal(0, m.Waveform!.Maximum.Span[0]);
        Assert.True(m.Waveform.Maximum.Span[loudest] > 8000);
        Assert.True(m.Waveform.Minimum.Span[loudest] < -8000);
    }

    [Fact]
    public void AGapIsCountedAndLeavesColumnsWithoutData()
    {
        int[] first = MeasurementSignals.ToIntegers(MeasurementSignals.Noise(20000, 1).Select(v => v * 0.3).ToArray(), 16);
        int[] second = MeasurementSignals.ToIntegers(MeasurementSignals.Noise(20000, 2).Select(v => v * 0.3).ToArray(), 16);
        var format = new PcmFormat(44100, 1, 16, SampleFormat.Integer);
        var measurer = new StreamMeasurer(format, new MeasureOptions(), CancellationToken.None, _ => true);
        measurer.Write(new PcmBlock(0, first.Length, 1, first.Length, first));
        measurer.Write(new PcmBlock(60000, second.Length, 1, second.Length, second));
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal(40000, m.Samples);
        Assert.Equal(40000, m.LostSamples);
        Assert.Equal(1, m.Gaps);

        // Hops 10 to 28 lie in the gap.
        ReadOnlySpan<byte> power = m.Spectrogram!.Power.Span;
        Assert.Equal(0, power[(15 * 512) + 100]);
        Assert.NotEqual(0, power[(5 * 512) + 100]);
        Assert.NotEqual(0, power[(30 * 512) + 100]);
    }

    [Fact]
    public void ABlockOfNoSamplesLeavesNoGrids()
    {
        var measurer = new StreamMeasurer(new PcmFormat(44100, 1, 16, SampleFormat.Integer), new MeasureOptions(), CancellationToken.None, _ => true);
        measurer.Write(new PcmBlock(0, 0, 1, 0, Array.Empty<int>()));
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Null(measurer.Failure);
        Assert.Equal(0, m.Samples);
        Assert.Null(m.Spectrogram);
        Assert.Null(m.Waveform);
    }

    [Fact]
    public void AGapOfTwoToTheFortiethSamplesCostsNoLoop()
    {
        int[] block = MeasurementSignals.ToIntegers(MeasurementSignals.Noise(10000, 1), 16);
        var format = new PcmFormat(44100, 1, 16, SampleFormat.Integer);
        var measurer = new StreamMeasurer(format, new MeasureOptions(), CancellationToken.None, _ => true);
        measurer.Write(new PcmBlock(0, block.Length, 1, block.Length, block));
        measurer.Write(new PcmBlock(1L << 40, block.Length, 1, block.Length, block));
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal(20000, m.Samples);
        Assert.Equal((1L << 40) - 10000, m.LostSamples);
        Assert.Equal(1, m.Gaps);
        Assert.Null(measurer.Failure);
        Assert.InRange(m.Spectrogram!.Columns, 1, 1024);
    }

    [Fact]
    public void TheGridCountsHopsFromWhereTheStreamStartsNotFromZero()
    {
        // Frame numbers that start at sample 2^24: from position 0 that would be 8,192 hops of empty columns, then merged to 513 columns.
        int[] block = MeasurementSignals.ToIntegers(MeasurementSignals.Noise(20000, 1), 16);
        var measurer = new StreamMeasurer(new PcmFormat(44100, 1, 16, SampleFormat.Integer), new MeasureOptions(), CancellationToken.None, _ => true);
        measurer.Write(new PcmBlock((1L << 24) + 700, block.Length, 1, block.Length, block));
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((1L << 24) + 700, m.FirstPosition);
        Assert.Equal(1, m.Spectrogram!.HopsPerColumn);
        Assert.InRange(m.Spectrogram.Columns, 1, 12);
        Assert.NotEqual(0, m.Spectrogram.Power.Span[100]);
    }

    [Fact]
    public void APositionThatGoesBackStopsTheMeasuring()
    {
        int[] block = new int[5000];
        var measurer = new StreamMeasurer(new PcmFormat(44100, 1, 16, SampleFormat.Integer), new MeasureOptions(), CancellationToken.None, _ => true);
        measurer.Write(new PcmBlock(0, 5000, 1, 5000, block));
        measurer.Write(new PcmBlock(1000, 5000, 1, 5000, block));

        Assert.True(measurer.HasPositionJumped);
        Assert.Equal(1000, measurer.StopPosition);
        Assert.Null(measurer.Failure);
        Assert.Equal(5000, measurer.Samples);
    }

    [Fact]
    public void ABlockWithAnotherChannelCountOrFormatStopsTheMeasuring()
    {
        int[] block = new int[10000];
        float[] floats = new float[10000];
        var measurer = new StreamMeasurer(new PcmFormat(44100, 2, 16, SampleFormat.Integer), new MeasureOptions(), CancellationToken.None, _ => true);
        measurer.Write(new PcmBlock(0, 3000, 2, 3000, block.AsSpan(0, 6000)));
        measurer.Write(new PcmBlock(3000, 3000, 2, 3000, floats.AsSpan(0, 6000)));

        Assert.True(measurer.HasFormatChanged);
        Assert.Equal(3000, measurer.StopPosition);

        var other = new StreamMeasurer(new PcmFormat(44100, 2, 16, SampleFormat.Integer), new MeasureOptions(), CancellationToken.None, _ => true);
        other.Write(new PcmBlock(0, 3000, 1, 3000, block.AsSpan(0, 3000)));
        Assert.True(other.HasFormatChanged);
    }

    [Fact]
    public void OnlyTheFirstThirtyTwoChannelsAreMeasuredWithinTheMemoryEstimate()
    {
        var format = new PcmFormat(44100, 65535, 16, SampleFormat.Integer);
        long estimate = StreamMeasurer.EstimateBytes(format, new MeasureOptions());
        Assert.InRange(estimate, 1, 30L << 20);

        int[] data = new int[65535 * 8];
        long before = GC.GetAllocatedBytesForCurrentThread();
        var measurer = new StreamMeasurer(format, new MeasureOptions(), CancellationToken.None, _ => true);
        measurer.Write(new PcmBlock(0, 8, 65535, 8, data));
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(65535, m.Channels);
        Assert.Equal(32, m.ChannelsMeasured);
        Assert.Equal(32, m.Spectrum!.PerChannel.Count);
        Assert.True(allocated < estimate, $"allocated {allocated:N0}, estimated {estimate:N0}");
    }

    [Fact]
    public void FloatsThatAreNaNInfiniteOrHugeAreCountedAndClamped()
    {
        float[] floats = new float[8192];
        for (int i = 0; i < floats.Length; i++)
        {
            floats[i] = (i % 7) switch { 0 => float.NaN, 1 => float.PositiveInfinity, 2 => 3e30f, 3 => -3e30f, 4 => 1e-40f, _ => 0.25f };
        }

        var measurer = new StreamMeasurer(new PcmFormat(44100, 1, 32, SampleFormat.Float32), new MeasureOptions(), CancellationToken.None, _ => true);
        measurer.Write(new PcmBlock(0, 8192, 1, 8192, floats));
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Null(measurer.Failure);
        Assert.True(measurer.HasClamped);
        Assert.Equal(floats.Count(f => !float.IsFinite(f)), measurer.NonFiniteSamples);
        Assert.All(m.Spectrum!.Combined.ToArray(), v => Assert.True(v < 1000));
    }
}
