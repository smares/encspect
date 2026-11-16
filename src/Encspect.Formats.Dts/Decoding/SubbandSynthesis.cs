using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Dts.Decoding;

// The 32-band synthesis filter bank of one channel (C.3.6): the cosine modulation of the subband samples of each slot into a vector of 32, a history of 16 such vectors and the 512-tap
// filter of FILTS, summed into 32 samples a slot, with the 32 partial sums of the second half carried to the next slot. All in single precision, in the order of the pseudo-code.
internal sealed class SubbandSynthesis
{
    public const int Bands = 32;
    private const int Taps = 512;

    // C.3.6 PreCalCosMod: 16 x 16 values of cos((2i + 1)(2k + 1) pi / 64), 16 x 16 of cos(i(2k + 1) pi / 32), then 16 and 16 scale factors.
    private static readonly float[] CosMod = CreateCosMod();
    private static readonly float[] Perfect = [.. FilterTables.PerfectReconstruction.ToArray().Select(v => (float)v)];
    private static readonly float[] NonPerfect = [.. FilterTables.NonPerfectReconstruction.ToArray().Select(v => (float)v)];

    private readonly float[] _x = new float[Taps];
    private readonly float[] _z = new float[2 * Bands];
    private readonly float[] _in = new float[Bands];

    public void Reset()
    {
        Array.Clear(_x);
        Array.Clear(_z);
    }

    private static float[] CreateCosMod()
    {
        var table = new float[(2 * 256) + 32];
        int j = 0;
        for (int k = 0; k < 16; k++)
        {
            for (int i = 0; i < 16; i++)
            {
                table[j++] = (float)DetMath.Cos((((2 * i) + 1) * ((2 * k) + 1)) * Math.PI / 64);
            }
        }

        for (int k = 0; k < 16; k++)
        {
            for (int i = 0; i < 16; i++)
            {
                table[j++] = (float)DetMath.Cos(i * ((2 * k) + 1) * Math.PI / 32);
            }
        }

        for (int k = 0; k < 16; k++)
        {
            table[j++] = (float)(0.25 / (2 * DetMath.Cos(((2 * k) + 1) * Math.PI / 128)));
        }

        for (int k = 0; k < 16; k++)
        {
            table[j++] = (float)(-0.25 / (2 * DetMath.Sin(((2 * k) + 1) * Math.PI / 128)));
        }

        return table;
    }

    // slots slots of the subband samples subbands[band * stride + slot] (the bands from active up are zero), to 32 * slots samples in output.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Run(ReadOnlySpan<float> subbands, int stride, int slots, int active, bool isPerfect, Span<float> output)
    {
        float[] coefficients = isPerfect ? Perfect : NonPerfect;
        float[] x = _x;
        float[] z = _z;
        float[] xin = _in;
        Span<float> sum = stackalloc float[16];
        Span<float> difference = stackalloc float[16];
        for (int slot = 0; slot < slots; slot++)
        {
            for (int band = 0; band < Bands; band++)
            {
                xin[band] = band < active ? subbands[(band * stride) + slot] : 0f;
            }

            int j = 0;
            for (int k = 0; k < 16; k++)
            {
                float a = 0f;
                for (int i = 0; i < 16; i++)
                {
                    a += (xin[2 * i] + xin[(2 * i) + 1]) * CosMod[j++];
                }

                sum[k] = a;
            }

            for (int k = 0; k < 16; k++)
            {
                float b = 0f;
                for (int i = 0; i < 16; i++)
                {
                    b += (i > 0 ? xin[2 * i] + xin[(2 * i) - 1] : xin[0]) * CosMod[j++];
                }

                difference[k] = sum[k] - b;
                sum[k] += b;
            }

            // The newest vector goes to the front of the history.
            for (int i = Taps - 1; i >= Bands; i--)
            {
                x[i] = x[i - Bands];
            }

            for (int k = 0; k < 16; k++)
            {
                x[k] = CosMod[j + k] * sum[k];
            }

            for (int k = 0; k < 16; k++)
            {
                x[Bands - k - 1] = CosMod[j + 16 + k] * difference[k];
            }

            for (int i = 0, k = Bands - 1; i < Bands; i++, k--)
            {
                float acc = z[i];
                for (int m = 0; m < Taps; m += 2 * Bands)
                {
                    acc += coefficients[i + m] * (x[i + m] - x[m + k]);
                }

                z[i] = acc;
            }

            for (int i = 0, k = Bands - 1; i < Bands; i++, k--)
            {
                float acc = z[Bands + i];
                for (int m = 0; m < Taps; m += 2 * Bands)
                {
                    acc += coefficients[Bands + i + m] * (-x[i + m] - x[m + k]);
                }

                z[Bands + i] = acc;
            }

            Span<float> destination = output.Slice(slot * Bands, Bands);
            for (int i = 0; i < Bands; i++)
            {
                destination[i] = z[i];
            }

            for (int i = 0; i < Bands; i++)
            {
                z[i] = z[i + Bands];
                z[i + Bands] = 0f;
            }
        }
    }
}
