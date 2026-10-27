using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// Synthetic signals and a way to run a measurer over them without a decoder.
internal static class MeasurementSignals
{
    // A linear congruential generator: the same noise on every platform.
    public static double[] Noise(int length, uint seed)
    {
        var values = new double[length];
        uint state = seed;
        for (int i = 0; i < length; i++)
        {
            state = unchecked((state * 1664525u) + 1013904223u);
            values[i] = (((state >> 8) / 16777216.0) * 2) - 1;
        }

        return values;
    }

    // A triangle wave with an integer period: no library function.
    public static double[] Triangle(int length, int period, double amplitude)
    {
        var values = new double[length];
        for (int i = 0; i < length; i++)
        {
            double phase = (double)(i % period) / period;
            values[i] = amplitude * (phase < 0.5 ? (4 * phase) - 1 : 3 - (4 * phase));
        }

        return values;
    }

    public static int[] ToIntegers(IReadOnlyList<double> values, int bits)
    {
        double full = Math.Pow(2, bits - 1);
        var result = new int[values.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (int)Math.Clamp(Math.Floor((values[i] * full) + 0.5), -full, full - 1);
        }

        return result;
    }

    public static StreamMeasurements Measure(int[][] channels, int rate, int bits, int blockSize, MeasureOptions? options = null, IReadOnlyList<Speaker>? layout = null, long firstPosition = 0)
    {
        var format = new PcmFormat(rate, channels.Length, bits, SampleFormat.Integer) { ChannelLayout = layout };
        var measurer = new StreamMeasurer(format, options ?? new MeasureOptions(), CancellationToken.None, _ => true);
        int length = channels[0].Length;
        int[] planar = new int[channels.Length * blockSize];
        for (int start = 0; start < length; start += blockSize)
        {
            int n = Math.Min(blockSize, length - start);
            for (int c = 0; c < channels.Length; c++)
            {
                channels[c].AsSpan(start, n).CopyTo(planar.AsSpan(c * n, n));
            }

            measurer.Write(new PcmBlock(firstPosition + start, n, channels.Length, n, planar.AsSpan(0, channels.Length * n)));
        }

        Assert.Null(measurer.Failure);
        return measurer.Result(MeasurementStatus.Complete, null);
    }
}

internal static class BandLimited
{
    // Lines at the bin centers of a 4,096-point frame up to a cutoff, with fixed pseudo-random phases, repeated: a signal whose every
    // frame has the same line spectrum, so the edge is as sharp as the bins. Level: the amplitude of each line.
    public static double[] Lines(int length, int rate, int highestBin, double amplitude, uint seed = 3)
    {
        double[] period = new double[4096];
        double[] phases = MeasurementSignals.Noise(highestBin + 1, seed);
        for (int bin = 20; bin <= highestBin; bin++)
        {
            double phase = phases[bin] * Math.PI;
            for (int n = 0; n < period.Length; n++)
            {
                period[n] += amplitude * Math.Cos((2 * Math.PI * ((long)bin * n % 4096) / 4096) + phase);
            }
        }

        var result = new double[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = period[i % 4096];
        }

        _ = rate;
        return result;
    }
}
