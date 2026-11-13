using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Ac3.Decoding;

// The inverse transforms of A/52 7.9.4: the 512-sample transform of a long block (a 128-point complex inverse FFT with twiddles) and the two 256-sample transforms of a
// block switched channel (64-point ones), each with the window of Table 7.33. The calling code overlaps the first half of the output with the delay of the block before.
// The tables come from DetMath and the butterflies are element-wise float operations in an order that depends only on the transform's size.
internal sealed class InverseTransform
{
    private const int N = 512;

    private readonly float[] _window = Quantizers.Window();
    private readonly float[] _cos1 = new float[N / 4];
    private readonly float[] _sin1 = new float[N / 4];
    private readonly float[] _cos2 = new float[N / 8];
    private readonly float[] _sin2 = new float[N / 8];
    private readonly float[] _fftCos128 = new float[N / 8];
    private readonly float[] _fftSin128 = new float[N / 8];
    private readonly float[] _fftCos64 = new float[N / 16];
    private readonly float[] _fftSin64 = new float[N / 16];
    private readonly int[] _reverse128 = new int[N / 4];
    private readonly int[] _reverse64 = new int[N / 8];
    private readonly float[] _re = new float[N / 4];
    private readonly float[] _im = new float[N / 4];
    private readonly float[] _yr = new float[N / 4];
    private readonly float[] _yi = new float[N / 4];
    private readonly float[] _yr2 = new float[N / 8];
    private readonly float[] _yi2 = new float[N / 8];

    public InverseTransform()
    {
        for (int k = 0; k < N / 4; k++)
        {
            // xcos1[k] = -cos(2 pi (8k + 1) / (8 N)), xsin1[k] = -sin(...).
            DetMath.SinCos((8 * k) + 1, 8 * N, out double sin, out double cos);
            _cos1[k] = (float)-cos;
            _sin1[k] = (float)-sin;
        }

        for (int k = 0; k < N / 8; k++)
        {
            // xcos2[k] = -cos(2 pi (8k + 1) / (4 N)).
            DetMath.SinCos((8 * k) + 1, 4 * N, out double sin, out double cos);
            _cos2[k] = (float)-cos;
            _sin2[k] = (float)-sin;
        }

        for (int j = 0; j < N / 8; j++)
        {
            DetMath.SinCos(j, N / 4, out double sin, out double cos);
            _fftCos128[j] = (float)cos;
            _fftSin128[j] = (float)sin;
        }

        for (int j = 0; j < N / 16; j++)
        {
            DetMath.SinCos(j, N / 8, out double sin, out double cos);
            _fftCos64[j] = (float)cos;
            _fftSin64[j] = (float)sin;
        }

        BitReversal(_reverse128);
        BitReversal(_reverse64);
    }

    private static void BitReversal(int[] table)
    {
        int bits = System.Numerics.BitOperations.Log2((uint)table.Length);
        for (int i = 0; i < table.Length; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                if ((i & (1 << b)) != 0)
                {
                    r |= 1 << (bits - 1 - b);
                }
            }

            table[i] = r;
        }
    }

    // The windowed 512 samples of one long block from its 256 coefficients (7.9.4.1).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Long(ReadOnlySpan<float> spectrum, Span<float> x)
    {
        const int quarter = N / 4;
        float[] re = _re;
        float[] im = _im;
        for (int k = 0; k < quarter; k++)
        {
            float a = spectrum[(N / 2) - (2 * k) - 1];
            float b = spectrum[2 * k];
            int r = _reverse128[k];
            re[r] = (a * _cos1[k]) - (b * _sin1[k]);
            im[r] = (b * _cos1[k]) + (a * _sin1[k]);
        }

        Fft(re, im, quarter, _fftCos128, _fftSin128);
        float[] yr = _yr;
        float[] yi = _yi;
        for (int n = 0; n < quarter; n++)
        {
            yr[n] = (re[n] * _cos1[n]) - (im[n] * _sin1[n]);
            yi[n] = (im[n] * _cos1[n]) + (re[n] * _sin1[n]);
        }

        float[] w = _window;
        for (int n = 0; n < N / 8; n++)
        {
            x[2 * n] = -yi[(N / 8) + n] * w[2 * n];
            x[(2 * n) + 1] = yr[(N / 8) - n - 1] * w[(2 * n) + 1];
            x[(N / 4) + (2 * n)] = -yr[n] * w[(N / 4) + (2 * n)];
            x[(N / 4) + (2 * n) + 1] = yi[(N / 4) - n - 1] * w[(N / 4) + (2 * n) + 1];
            x[(N / 2) + (2 * n)] = -yr[(N / 8) + n] * w[(N / 2) - (2 * n) - 1];
            x[(N / 2) + (2 * n) + 1] = yi[(N / 8) - n - 1] * w[(N / 2) - (2 * n) - 2];
            x[(3 * N / 4) + (2 * n)] = yi[n] * w[(N / 4) - (2 * n) - 1];
            x[(3 * N / 4) + (2 * n) + 1] = -yr[(N / 4) - n - 1] * w[(N / 4) - (2 * n) - 2];
        }
    }

    // The windowed 512 samples of a block switched channel from its 256 coefficients, interleaved from two transforms of 128 (7.9.4.2).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Short(ReadOnlySpan<float> spectrum, Span<float> x)
    {
        const int eighth = N / 8;
        float[] re = _re;
        float[] im = _im;
        float[] w = _window;
        for (int half = 0; half < 2; half++)
        {
            // X1[k] = X[2k], X2[k] = X[2k + 1].
            for (int k = 0; k < eighth; k++)
            {
                float a = spectrum[(2 * ((N / 4) - (2 * k) - 1)) + half];
                float b = spectrum[(2 * (2 * k)) + half];
                int r = _reverse64[k];
                re[r] = (a * _cos2[k]) - (b * _sin2[k]);
                im[r] = (b * _cos2[k]) + (a * _sin2[k]);
            }

            Fft(re, im, eighth, _fftCos64, _fftSin64);
            float[] yr = half == 0 ? _yr : _yr2;
            float[] yi = half == 0 ? _yi : _yi2;
            for (int n = 0; n < eighth; n++)
            {
                yr[n] = (re[n] * _cos2[n]) - (im[n] * _sin2[n]);
                yi[n] = (im[n] * _cos2[n]) + (re[n] * _sin2[n]);
            }
        }

        float[] yr1 = _yr;
        float[] yi1 = _yi;
        float[] yr2 = _yr2;
        float[] yi2 = _yi2;
        for (int n = 0; n < eighth; n++)
        {
            x[2 * n] = -yi1[n] * w[2 * n];
            x[(2 * n) + 1] = yr1[eighth - n - 1] * w[(2 * n) + 1];
            x[(N / 4) + (2 * n)] = -yr1[n] * w[(N / 4) + (2 * n)];
            x[(N / 4) + (2 * n) + 1] = yi1[eighth - n - 1] * w[(N / 4) + (2 * n) + 1];
            x[(N / 2) + (2 * n)] = -yr2[n] * w[(N / 2) - (2 * n) - 1];
            x[(N / 2) + (2 * n) + 1] = yi2[eighth - n - 1] * w[(N / 2) - (2 * n) - 2];
            x[(3 * N / 4) + (2 * n)] = yi2[n] * w[(N / 4) - (2 * n) - 1];
            x[(3 * N / 4) + (2 * n) + 1] = -yr2[eighth - n - 1] * w[(N / 4) - (2 * n) - 2];
        }
    }

    // z[n] = sum over k of Z[k] (cos(2 pi k n / size) + j sin(2 pi k n / size)), in place on bit-reversed input, radix 2.
    private static void Fft(float[] re, float[] im, int size, float[] cos, float[] sin)
    {
        for (int length = 2; length <= size; length <<= 1)
        {
            int half = length >> 1;
            int stride = size / length;
            for (int start = 0; start < size; start += length)
            {
                for (int j = 0; j < half; j++)
                {
                    float wc = cos[j * stride];
                    float ws = sin[j * stride];
                    int p = start + j;
                    int q = p + half;
                    float xr = (re[q] * wc) - (im[q] * ws);
                    float xi = (im[q] * wc) + (re[q] * ws);
                    re[q] = re[p] - xr;
                    im[q] = im[p] - xi;
                    re[p] += xr;
                    im[p] += xi;
                }
            }
        }
    }
}
