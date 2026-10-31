using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Encspect.Core;

namespace Encspect.Host.Measurement;

// The true peak of one stream (ITU-R BS.1770-5 Annex 2): the highest magnitude of the oversampled waveform, from the Annex's 48-tap
// four-phase FIR (Table "Phase 0" to "Phase 3"), which interpolates four values for each sample from the twelve samples up to it. Below
// 96 kHz all four phases run, from 96 kHz up to 192 kHz phases 0 and 2 (an oversampling of 2: the Annex gives no filter of its own, the
// two phases are the positions 1/8 and 5/8 of the same prototype lowpass), and from 192 kHz the sample peak is the figure. The sample
// peak always counts: the true peak is never below the highest sample.
//
// Each phase is summed in the order of the taps, with separate multiplications and additions and in element-wise vectors only (the two
// phases of a vector are independent lanes, never added to each other), so the figures are the same on every platform and for every cutting
// of blocks. A window of samples whose largest magnitude times the largest sum of absolute taps cannot exceed the peak found so far is not
// filtered: the result is the same, and in music with an early loud peak most windows are skipped.
internal sealed class TruePeakMeter
{
    // The most samples of a channel given to AddChannel at once.
    public const int MaxPiece = 16384;

    private const int Taps = 12;

    private const int History = Taps - 1;

    // Windows of this many outputs are skipped or filtered whole.
    private const int Window = 128;

    // The sum of the absolute taps of the phases 1 and 2 (2.0228...), rounded up, and the margin for the rounding of a sum of 12 products
    // (about 2^-49 relative, 2^-46 allowed): an output cannot exceed this times the largest of the 12 samples it reads.
    private const double TapBound = 2.03 * (1.0 + (1.0 / 70368744177664.0));

    // Table "Filter coefficients" of Annex 2 (format data), one row per tap and one column per phase.
    private static readonly double[] Phase0 = [0.0017089843750, 0.0109863281250, -0.0196533203125, 0.0332031250000, -0.0594482421875, 0.1373291015625, 0.9721679687500, -0.1022949218750, 0.0476074218750, -0.0266113281250, 0.0148925781250, -0.0083007812500];

    private static readonly double[] Phase1 = [-0.0291748046875, 0.0292968750000, -0.0517578125000, 0.0891113281250, -0.1665039062500, 0.4650878906250, 0.7797851562500, -0.2003173828125, 0.1015625000000, -0.0582275390625, 0.0330810546875, -0.0189208984375];

    private static readonly double[] Phase2 = [-0.0189208984375, 0.0330810546875, -0.0582275390625, 0.1015625000000, -0.2003173828125, 0.7797851562500, 0.4650878906250, -0.1665039062500, 0.0891113281250, -0.0517578125000, 0.0292968750000, -0.0291748046875];

    private static readonly double[] Phase3 = [-0.0083007812500, 0.0148925781250, -0.0266113281250, 0.0476074218750, -0.1022949218750, 0.9721679687500, 0.1373291015625, -0.0594482421875, 0.0332031250000, -0.0196533203125, 0.0109863281250, 0.0017089843750];

    private readonly int _factor;
    private readonly double[] _history;
    private double[] _window = [];
    private readonly double[] _peak;
    private readonly double[] _samplePeak;

    public TruePeakMeter(int measured, int rate)
    {
        _factor = FactorFor(rate);
        _history = new double[measured * History];
        _peak = new double[measured];
        _samplePeak = new double[measured];
    }

    // The oversampling for a sample rate, as the Annex puts it: 4 below 96 kHz, 2 from 96 kHz, and none from 192 kHz.
    public static int FactorFor(int rate) => rate < 96000 ? 4 : rate < 192000 ? 2 : 1;

    public static long EstimateBytes(int measured, int rate) => 8L * ((measured * (History + 2)) + (FactorFor(rate) > 1 ? History + MaxPiece : 0)) + 64;

    // For tests: false runs the scalar loop and skips nothing.
    internal bool UsesVectors { get; set; } = true;

    internal bool Skips { get; set; } = true;

    public int Oversampling => _factor;

    // The history starts again after a gap: the filter reads no sample from before it.
    public void Gap() => Array.Clear(_history);

    // The samples of one measured channel, finite and at most 1,024 times full scale.
    public void AddChannel(int channel, ReadOnlySpan<double> samples)
    {
        double samplePeak = _samplePeak[channel];
        for (int i = 0; i < samples.Length; i++)
        {
            double v = Math.Abs(samples[i]);
            samplePeak = v > samplePeak ? v : samplePeak;
        }

        _samplePeak[channel] = samplePeak;
        if (_factor == 1)
        {
            return;
        }

        // The twelve samples before the piece, then the piece: the window the filter reads (made with the first piece).
        if (_window.Length == 0)
        {
            _window = new double[History + MaxPiece];
        }

        Span<double> history = _history.AsSpan(channel * History, History);
        history.CopyTo(_window);
        samples.CopyTo(_window.AsSpan(History));
        Span<double> all = _window.AsSpan(0, History + samples.Length);
        double peak = _peak[channel];
        if (UsesVectors)
        {
            peak = Filter(all, _factor, peak, Skips);
        }
        else
        {
            peak = FilterScalar(all, _factor, peak);
        }

        _peak[channel] = peak;
        all.Slice(samples.Length, History).CopyTo(history);
    }

    // The highest interpolated output so far (no sample peak), for the tests that hold the loops to each other.
    internal double InterpolatedPeak(int channel) => _peak[channel];

    // The highest magnitude so far, as the double itself, for the tests that pin the raw output.
    internal double RawPeak(int channel) => Math.Max(_peak[channel], _samplePeak[channel]);

    // The highest magnitude in tenths of a dB of a channel: the larger of the sample peak and the interpolated ones.
    public int Tenths(int channel)
    {
        double peak = Math.Max(_peak[channel], _samplePeak[channel]);
        return peak <= 0 ? MeasurementLimits.NoLevel : DetMath.Tenths(peak * peak);
    }

    public TruePeakFigures Build(int measured)
    {
        int[] channels = new int[measured];
        int highest = MeasurementLimits.NoLevel;
        for (int c = 0; c < measured; c++)
        {
            channels[c] = Tenths(c);
            highest = Math.Max(highest, channels[c]);
        }

        return new TruePeakFigures { Oversampling = _factor, StreamTenths = highest, ChannelTenths = channels };
    }

    // The outputs for every sample after the first History ones of the window: phase p of the sample n is the sum over the taps k of
    // h[p][k] x[n - k], in the order of k. The result is the larger of peak and the highest magnitude.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double Filter(ReadOnlySpan<double> x, int factor, double peak, bool skips)
    {
        int outputs = x.Length - History;
        Vector128<double> maximum = Vector128.Create(peak);
        for (int start = 0; start < outputs; start += Window)
        {
            int count = Math.Min(Window, outputs - start);
            if (skips)
            {
                double largest = 0;
                foreach (double v in x.Slice(start, count + History))
                {
                    double a = Math.Abs(v);
                    largest = a > largest ? a : largest;
                }

                if (largest * TapBound <= peak)
                {
                    continue;
                }
            }

            if (factor == 4)
            {
                for (int n = start + History; n < start + History + count; n++)
                {
                    Vector128<double> a = Vector128<double>.Zero;
                    Vector128<double> b = Vector128<double>.Zero;
                    for (int k = 0; k < Taps; k++)
                    {
                        Vector128<double> v = Vector128.Create(x[n - k]);
                        a += Pair01[k] * v;
                        b += Pair23[k] * v;
                    }

                    maximum = Vector128.Max(maximum, Vector128.Max(Vector128.Abs(a), Vector128.Abs(b)));
                }
            }
            else
            {
                // Two samples at a time: one sum is a chain of 12 dependent additions, and a single chain makes this loop as slow per sample as the
                // four phases' two chains; each output keeps its own sum in the order of the taps.
                int end = start + History + count;
                int n = start + History;
                for (; n + 1 < end; n += 2)
                {
                    Vector128<double> a = Vector128<double>.Zero;
                    Vector128<double> b = Vector128<double>.Zero;
                    for (int k = 0; k < Taps; k++)
                    {
                        Vector128<double> pair = Pair02[k];
                        a += pair * Vector128.Create(x[n - k]);
                        b += pair * Vector128.Create(x[n + 1 - k]);
                    }

                    maximum = Vector128.Max(maximum, Vector128.Max(Vector128.Abs(a), Vector128.Abs(b)));
                }

                for (; n < end; n++)
                {
                    Vector128<double> a = Vector128<double>.Zero;
                    for (int k = 0; k < Taps; k++)
                    {
                        a += Pair02[k] * Vector128.Create(x[n - k]);
                    }

                    maximum = Vector128.Max(maximum, Vector128.Abs(a));
                }
            }

            peak = Math.Max(maximum.GetElement(0), maximum.GetElement(1));
        }

        return Math.Max(peak, Math.Max(maximum.GetElement(0), maximum.GetElement(1)));
    }

    // The same outputs by scalar arithmetic, with no skipping, for the test that holds the vector loop to it.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double FilterScalar(ReadOnlySpan<double> x, int factor, double peak)
    {
        double[][] phases = factor == 4 ? [Phase0, Phase1, Phase2, Phase3] : [Phase0, Phase2];
        for (int n = History; n < x.Length; n++)
        {
            foreach (double[] phase in phases)
            {
                double sum = 0;
                for (int k = 0; k < Taps; k++)
                {
                    sum += phase[k] * x[n - k];
                }

                peak = Math.Max(peak, Math.Abs(sum));
            }
        }

        return peak;
    }

    // The taps of two phases side by side, one vector for each tap.
    private static readonly Vector128<double>[] Pair01 = Pairs(Phase0, Phase1);

    private static readonly Vector128<double>[] Pair23 = Pairs(Phase2, Phase3);

    private static readonly Vector128<double>[] Pair02 = Pairs(Phase0, Phase2);

    private static Vector128<double>[] Pairs(double[] first, double[] second)
    {
        var result = new Vector128<double>[Taps];
        for (int k = 0; k < Taps; k++)
        {
            result[k] = Vector128.Create(first[k], second[k]);
        }

        return result;
    }
}
