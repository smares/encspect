using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Encspect.Core;

namespace Encspect.Host.Measurement;

// The tables and the transform of one FFT size: a real FFT of N points through a complex FFT of N / 2 and the split step of
// Sorensen et al. (IEEE Trans. ASSP 35(6), 1987), in double precision with a periodic Hann window, reduced to 512 rows of mean power
// (docs/design/spectral-analysis.md, "FFT" and "Window, size and hop"). Written for determinism: the order of every operation depends
// on N alone, no operation is fused, and the vector loop does the same lane-wise operations as the scalar one. An instance is
// immutable and built for each stream.
internal sealed class FftPlan
{
    public const int MinimumSize = 1024;

    public const int MaximumSize = 32768;

    // The smallest power of two at least 0.085 s of samples, from 1,024 to 32,768 points: the same time and frequency resolution at
    // every common rate (85 to 170 ms, 10.8 to 11.7 Hz a bin at the common rates).
    public static int SizeFor(int sampleRate)
    {
        long need = ((85L * sampleRate) + 999) / 1000;
        int size = MinimumSize;
        while (size < need && size < MaximumSize)
        {
            size *= 2;
        }

        return size;
    }

    private readonly int[] _reverse;
    private readonly double[] _stageCos;
    private readonly double[] _stageSin;
    private readonly double[] _splitCos;
    private readonly double[] _splitSin;
    private readonly double _scale;

    public FftPlan(int size)
    {
        if (size is < MinimumSize or > MaximumSize || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        Size = size;
        Half = size / 2;
        BinsPerRow = size / (MeasurementLimits.Rows * 2);
        Window = new double[size];
        for (int i = 0; i < size; i++)
        {
            DetMath.SinCos(i, size, out _, out double cos);
            Window[i] = 0.5 - (0.5 * cos);
        }

        _splitCos = new double[Half + 1];
        _splitSin = new double[Half + 1];
        for (int k = 0; k <= Half; k++)
        {
            DetMath.SinCos(k, size, out _splitSin[k], out _splitCos[k]);
        }

        _stageCos = new double[Half];
        _stageSin = new double[Half];
        for (int h = 1; h < Half; h *= 2)
        {
            int step = Half / h;
            for (int j = 0; j < h; j++)
            {
                _stageCos[h - 1 + j] = _splitCos[j * step];
                _stageSin[h - 1 + j] = _splitSin[j * step];
            }
        }

        int bits = 0;
        while ((1 << bits) < Half)
        {
            bits++;
        }

        _reverse = new int[Half];
        for (int i = 0; i < Half; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _reverse[i] = r;
        }

        // The bin a full-scale sine is centered on reads 1 (0 dB) once the window's coherent gain is taken out: |X| = N / 4 for it, and the
        // transform here is twice X; a row averages its bins, so the sine, whose Hann main lobe holds 1.5 times the center bin's power in
        // three bins, reads 1.5 / BinsPerRow in its row (-4.3 dB at 4 bins, -7.3 at 8, -10.3 at 16). Kept so that noise reads as a mean power
        // per bin; docs/design/spectral-analysis.md. A power of two, so the factor is exact.
        _scale = 4.0 / ((double)size * size * BinsPerRow);
    }

    public int Size { get; }

    // For tests: false runs the scalar loop of every stage, which must give the same bits as the vector one.
    internal bool UsesVectors { get; set; } = true;

    public int Half { get; }

    public int BinsPerRow { get; }

    public double[] Window { get; }

    // The bytes the tables take, for the memory bound.
    public long Bytes => (long)Size * 8 + (Half * 4L) + (Half * 8L * 2) + (((long)Half + 1) * 8 * 2) + (Half * 8L * 2);

    // The scratch arrays of one transform (the real and imaginary parts of the N / 2 complex values).
    public double[] NewScratch() => new double[Half];

    // Windows N samples, transforms them and writes the 512 rows of mean power, each normalized as the average of its bins relative to
    // the bin a full-scale sine is centered on. samples holds N values; re and im each N / 2 of scratch.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Rows(ReadOnlySpan<double> samples, double[] re, double[] im, Span<double> rows)
    {
        if (samples.Length < Size || re.Length < Half || im.Length < Half || rows.Length < MeasurementLimits.Rows)
        {
            throw new ArgumentException("The buffers are too short for the transform.");
        }

        int m = Half;
        ref double x = ref MemoryMarshal.GetReference(samples);
        ref double window = ref MemoryMarshal.GetArrayDataReference(Window);
        ref int reverse = ref MemoryMarshal.GetArrayDataReference(_reverse);
        ref double reRef = ref MemoryMarshal.GetArrayDataReference(re);
        ref double imRef = ref MemoryMarshal.GetArrayDataReference(im);

        // The windowed pairs of samples, bit-reversed into place as the complex input z[n] = x[2n] + i x[2n + 1].
        for (int i = 0; i < m; i++)
        {
            int target = Unsafe.Add(ref reverse, i);
            int at = 2 * i;
            Unsafe.Add(ref reRef, target) = Unsafe.Add(ref x, at) * Unsafe.Add(ref window, at);
            Unsafe.Add(ref imRef, target) = Unsafe.Add(ref x, at + 1) * Unsafe.Add(ref window, at + 1);
        }

        // Stage of half-size 1: the twiddle is 1.
        for (int b = 0; b < m; b += 2)
        {
            double ar = Unsafe.Add(ref reRef, b);
            double ai = Unsafe.Add(ref imRef, b);
            double br = Unsafe.Add(ref reRef, b + 1);
            double bi = Unsafe.Add(ref imRef, b + 1);
            Unsafe.Add(ref reRef, b) = ar + br;
            Unsafe.Add(ref imRef, b) = ai + bi;
            Unsafe.Add(ref reRef, b + 1) = ar - br;
            Unsafe.Add(ref imRef, b + 1) = ai - bi;
        }

        ref double stageCos = ref MemoryMarshal.GetArrayDataReference(_stageCos);
        ref double stageSin = ref MemoryMarshal.GetArrayDataReference(_stageSin);
        for (int h = 2; h < m; h *= 2)
        {
            Stage(ref reRef, ref imRef, ref Unsafe.Add(ref stageCos, h - 1), ref Unsafe.Add(ref stageSin, h - 1), m, h, UsesVectors && Vector128.IsHardwareAccelerated);
        }

        SplitRows(ref reRef, ref imRef, rows);
    }

    // The butterflies of one stage with the half-size h >= 2: j runs over the contiguous twiddles of the stage. Two lanes at a time in
    // a Vector128 where the hardware has it, the same operations lane by lane as the scalar loop.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Stage(ref double re, ref double im, ref double cos, ref double sin, int m, int h, bool usesVectors)
    {
        for (int b = 0; b < m; b += 2 * h)
        {
            int j = 0;
            if (usesVectors)
            {
                for (; j < h; j += 2)
                {
                    int e = b + j;
                    int o = e + h;
                    Vector128<double> c = Vector128.LoadUnsafe(ref cos, (nuint)j);
                    Vector128<double> s = Vector128.LoadUnsafe(ref sin, (nuint)j);
                    Vector128<double> or = Vector128.LoadUnsafe(ref re, (nuint)o);
                    Vector128<double> oi = Vector128.LoadUnsafe(ref im, (nuint)o);
                    Vector128<double> er = Vector128.LoadUnsafe(ref re, (nuint)e);
                    Vector128<double> ei = Vector128.LoadUnsafe(ref im, (nuint)e);
                    Vector128<double> tr = Vector128.Add(Vector128.Multiply(c, or), Vector128.Multiply(s, oi));
                    Vector128<double> ti = Vector128.Subtract(Vector128.Multiply(c, oi), Vector128.Multiply(s, or));
                    Vector128.StoreUnsafe(Vector128.Add(er, tr), ref re, (nuint)e);
                    Vector128.StoreUnsafe(Vector128.Add(ei, ti), ref im, (nuint)e);
                    Vector128.StoreUnsafe(Vector128.Subtract(er, tr), ref re, (nuint)o);
                    Vector128.StoreUnsafe(Vector128.Subtract(ei, ti), ref im, (nuint)o);
                }
            }

            for (; j < h; j++)
            {
                int e = b + j;
                int o = e + h;
                double c = Unsafe.Add(ref cos, j);
                double s = Unsafe.Add(ref sin, j);
                double or = Unsafe.Add(ref re, o);
                double oi = Unsafe.Add(ref im, o);
                double er = Unsafe.Add(ref re, e);
                double ei = Unsafe.Add(ref im, e);
                double tr = (c * or) + (s * oi);
                double ti = (c * oi) - (s * or);
                Unsafe.Add(ref re, e) = er + tr;
                Unsafe.Add(ref im, e) = ei + ti;
                Unsafe.Add(ref re, o) = er - tr;
                Unsafe.Add(ref im, o) = ei - ti;
            }
        }
    }

    // The split step: from Z, the transform of the N / 2 complex values, the real signal's bins 0 to N / 2 - 1, as |2 X|^2, summed
    // per row (in bin order) and scaled.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void SplitRows(ref double re, ref double im, Span<double> rows)
    {
        int m = Half;
        int bins = BinsPerRow;
        ref double splitCos = ref MemoryMarshal.GetArrayDataReference(_splitCos);
        ref double splitSin = ref MemoryMarshal.GetArrayDataReference(_splitSin);
        for (int row = 0; row < MeasurementLimits.Rows; row++)
        {
            double sum = 0;
            int first = row * bins;
            for (int k = first; k < first + bins; k++)
            {
                int other = k == 0 ? 0 : m - k;
                double zr1 = Unsafe.Add(ref re, k);
                double zi1 = Unsafe.Add(ref im, k);
                double zr2 = Unsafe.Add(ref re, other);
                double zi2 = Unsafe.Add(ref im, other);
                double a = zr1 + zr2;
                double b = zi1 - zi2;
                double c = zi1 + zi2;
                double d = zr2 - zr1;
                double cos = Unsafe.Add(ref splitCos, k);
                double sin = Unsafe.Add(ref splitSin, k);
                double xr = a + ((cos * c) + (sin * d));
                double xi = b + ((cos * d) - (sin * c));
                sum += (xr * xr) + (xi * xi);
            }

            rows[row] = sum * _scale;
        }
    }
}
