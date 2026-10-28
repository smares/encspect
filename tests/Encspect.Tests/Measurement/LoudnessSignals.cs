using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// Synthetic signals for the loudness and true-peak tests, built from the descriptions in EBU Tech 3341 and 3342 (the EBU's own test files are
// not in the repository), and a way to measure them in blocks of any size.
internal static class LoudnessSignals
{
    public static double Amplitude(double dbfs) => Math.Pow(10, dbfs / 20);

    // A sine with the given peak level, whole samples at the given rate.
    public static double[] Tone(int rate, double seconds, double peakDbfs, double frequency = 1000, double phaseDegrees = 0)
    {
        int length = (int)Math.Round(seconds * rate);
        double amplitude = Amplitude(peakDbfs);
        var tone = new double[length];
        for (int i = 0; i < length; i++)
        {
            tone[i] = amplitude * Math.Sin((2 * Math.PI * frequency * i / rate) + (phaseDegrees * Math.PI / 180));
        }

        return tone;
    }

    public static double[] Silence(int rate, double seconds) => new double[(int)Math.Round(seconds * rate)];

    public static double[] Join(params double[][] parts) => [.. parts.SelectMany(p => p)];

    // The same signal in every channel.
    public static double[][] Stereo(double[] signal) => [signal, signal];

    public static StreamMeasurements Measure(double[][] channels, int rate, IReadOnlyList<Speaker>? layout = null, int blockSize = 4800, MeasurementKinds kinds = MeasurementKinds.Loudness | MeasurementKinds.TruePeak, long firstPosition = 0, params (int Start, int Length)[] removed)
    {
        var format = new PcmFormat(rate, channels.Length, 32, SampleFormat.Float64) { ChannelLayout = layout };
        var measurer = new StreamMeasurer(format, new MeasureOptions { Include = kinds }, CancellationToken.None, _ => true);
        int length = channels[0].Length;
        double[] planar = new double[channels.Length * blockSize];
        for (int start = 0; start < length; start += blockSize)
        {
            int n = Math.Min(blockSize, length - start);
            if (removed.Any(r => start >= r.Start && start < r.Start + r.Length))
            {
                continue;
            }

            for (int c = 0; c < channels.Length; c++)
            {
                channels[c].AsSpan(start, n).CopyTo(planar.AsSpan(c * n, n));
            }

            measurer.Write(new PcmBlock(firstPosition + start, n, channels.Length, n, planar.AsSpan(0, channels.Length * n)));
        }

        Assert.Null(measurer.Failure);
        return measurer.Result(MeasurementStatus.Complete, null);
    }

    // Tenths against an expected figure and a tolerance of the document, in whole tenths so that no rounding of a double decides.
    public static void AssertNear(int tenths, double expected, double tolerance, string what)
    {
        int low = (int)Math.Round((expected - tolerance) * 10);
        int high = (int)Math.Round((expected + tolerance) * 10);
        Assert.True(tenths >= low && tenths <= high, $"{what}: {tenths / 10.0:F1}, expected {expected:F1} within {tolerance:F1}");
    }

    public static void AssertNear(int tenths, double expected, double above, double below, string what)
    {
        int low = (int)Math.Round((expected - below) * 10);
        int high = (int)Math.Round((expected + above) * 10);
        Assert.True(tenths >= low && tenths <= high, $"{what}: {tenths / 10.0:F1}, expected {expected:F1} +{above:F1}/-{below:F1}");
    }
}
