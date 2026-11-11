using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Vorbis.Decoding;

// The inverse MDCT of one block size (Vorbis I specification, section 4.3.7): n/2 coefficients to n samples,
//   y[i] = scale * sum over k of X[k] cos(2 pi / n * (i + 1/2 + n/4) * (k + 1/2)),
// computed as a DCT-IV of the coefficients by a complex FFT of n/4 points with pre and post twiddles, then unfolded to the n samples. The tables come from DetMath, the
// butterflies are element-wise float operations in an order that depends on n only, so the output is the same on every platform.
internal sealed class InverseMdct
{
    private readonly int _n;
    private readonly int _m;
    private readonly int _h;
    private readonly float[] _preCos;
    private readonly float[] _preSin;
    private readonly float[] _postCos;
    private readonly float[] _postSin;
    private readonly float[] _twiddleCos;
    private readonly float[] _twiddleSin;
    private readonly int[] _reverse;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly float[] _u;

    public InverseMdct(int n, float scale)
    {
        _n = n;
        _m = n / 2;
        _h = n / 4;
        Scale = scale;
        _preCos = new float[_h];
        _preSin = new float[_h];
        _postCos = new float[_h];
        _postSin = new float[_h];
        for (int j = 0; j < _h; j++)
        {
            // exp(-i pi j / M) = exp(-2 pi i j / (2M)); exp(-i pi (4j + 1) / (4M)) = exp(-2 pi i (4j + 1) / (8M)).
            DetMath.SinCos(j, 2 * _m, out double sin, out double cos);
            _preCos[j] = (float)cos;
            _preSin[j] = (float)sin;
            DetMath.SinCos((4 * j) + 1, 8 * _m, out sin, out cos);
            _postCos[j] = (float)cos;
            _postSin[j] = (float)sin;
        }

        int half = Math.Max(1, _h / 2);
        _twiddleCos = new float[half];
        _twiddleSin = new float[half];
        for (int j = 0; j < half; j++)
        {
            DetMath.SinCos(j, _h, out double sin, out double cos);
            _twiddleCos[j] = (float)cos;
            _twiddleSin[j] = (float)sin;
        }

        _reverse = new int[_h];
        int bits = System.Numerics.BitOperations.Log2((uint)_h);
        for (int i = 0; i < _h; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                if ((i & (1 << b)) != 0)
                {
                    r |= 1 << (bits - 1 - b);
                }
            }

            _reverse[i] = r;
        }

        _re = new float[_h];
        _im = new float[_h];
        _u = new float[_m];
    }

    public float Scale { get; }

    // Transforms the n/2 coefficients into the n samples of the block, unwindowed.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Transform(ReadOnlySpan<float> spectrum, Span<float> output)
    {
        int m = _m;
        int h = _h;
        float[] re = _re;
        float[] im = _im;
        for (int j = 0; j < h; j++)
        {
            float a = spectrum[2 * j];
            float b = spectrum[m - 1 - (2 * j)];
            float pc = _preCos[j];
            float ps = _preSin[j];
            int r = _reverse[j];
            re[r] = (a * pc) + (b * ps);
            im[r] = (b * pc) - (a * ps);
        }

        // Forward FFT of h points on bit-reversed input, in place, radix 2.
        for (int length = 2; length <= h; length <<= 1)
        {
            int half = length >> 1;
            int stride = h / length;
            for (int start = 0; start < h; start += length)
            {
                for (int j = 0; j < half; j++)
                {
                    float wc = _twiddleCos[j * stride];
                    float ws = _twiddleSin[j * stride];
                    int p = start + j;
                    int q = p + half;
                    float xr = (re[q] * wc) + (im[q] * ws);
                    float xi = (im[q] * wc) - (re[q] * ws);
                    re[q] = re[p] - xr;
                    im[q] = im[p] - xi;
                    re[p] += xr;
                    im[p] += xi;
                }
            }
        }

        float[] u = _u;
        for (int k = 0; k < h; k++)
        {
            float qc = _postCos[k];
            float qs = _postSin[k];
            float wr = (re[k] * qc) + (im[k] * qs);
            float wi = (im[k] * qc) - (re[k] * qs);
            u[2 * k] = wr;
            u[m - 1 - (2 * k)] = -wi;
        }

        float scale = Scale;
        int quarter = m / 2;
        for (int i = 0; i < quarter; i++)
        {
            output[i] = u[i + quarter] * scale;
        }

        for (int i = quarter; i < quarter + m; i++)
        {
            output[i] = -u[(3 * quarter) - 1 - i] * scale;
        }

        for (int i = quarter + m; i < _n; i++)
        {
            output[i] = -u[i - (3 * quarter)] * scale;
        }
    }
}

// The slope of a window (Vorbis I specification, section 1.3.2): sin(pi/2 * sin^2(pi * (i + 1/2) / (2 * length))) for the length samples that rise from 0 to 1.
internal static class WindowSlope
{
    public static float[] Create(int length)
    {
        var slope = new float[length];
        for (int i = 0; i < length; i++)
        {
            DetMath.SinCos((2 * i) + 1, 8 * length, out double sin, out _);
            slope[i] = (float)DetMath.Sin(Math.PI / 2 * sin * sin);
        }

        return slope;
    }
}
