using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Aac.Decoding;

// The QMF analysis and synthesis banks of SBR (ISO/IEC 14496-3 4.6.18.4) in single precision: 32 complex subbands over the core's samples, and 64 over the output's. The prototype filter
// is the standard's table (SbrTableData); the modulation is the sum of its definition (analysis 2 exp(i pi / 64 (k + 1/2)(2n - 1/2)), synthesis Re of exp(i pi / 128 (k + 1/2)(2n - 255))),
// folded by the symmetry of its 128 outputs, with the tables of DetMath, element by element in an order that depends on the bank alone. Following libxaac's ixheaacd_esbr_analysis_filt_block and ixheaacd_esbr_synthesis_filt_block (Apache License 2.0, Copyright (C) The Android Open Source Project; the notice
// is in THIRD-PARTY-NOTICES.md), whose fixed-point butterflies compute the same sums.
internal sealed class SbrQmf
{
    public const int Slots = 32;
    private const int AnalysisBands = 32;
    private const int SynthesisBands = 64;

    private static readonly float[] AnalysisCos = new float[AnalysisBands * 64];
    private static readonly float[] AnalysisSin = new float[AnalysisBands * 64];
    private static readonly float[] SynthesisCos = new float[SynthesisBands * 64];
    private static readonly float[] SynthesisSin = new float[SynthesisBands * 64];

    private readonly float[] _analysis = new float[320];
    private readonly float[] _synthesis = new float[1280];
    private readonly float[] _u = new float[64];
    private readonly float[] _a = new float[64];
    private readonly float[] _b = new float[64];
    private int _analysisAt;
    private int _synthesisAt;

    static SbrQmf()
    {
        for (int k = 0; k < AnalysisBands; k++)
        {
            for (int n = 0; n < 64; n++)
            {
                int index = (((2 * k) + 1) * ((4 * n) - 1)) % 512;
                if (index < 0)
                {
                    index += 512;
                }

                DetMath.SinCos(index, 512, out double sin, out double cos);
                AnalysisCos[(k * 64) + n] = (float)(2 * cos);
                AnalysisSin[(k * 64) + n] = (float)(2 * sin);
            }
        }

        for (int k = 0; k < SynthesisBands; k++)
        {
            for (int n = 0; n < 64; n++)
            {
                DetMath.SinCos((((2 * k) + 1) * ((2 * n) + 1)) % 512, 512, out double sin, out double cos);
                SynthesisCos[(k * 64) + n] = (float)cos;
                SynthesisSin[(k * 64) + n] = (float)sin;
            }
        }
    }

    public void Reset()
    {
        Array.Clear(_analysis);
        Array.Clear(_synthesis);
        _analysisAt = 0;
        _synthesisAt = 0;
    }

    // One slot of the analysis: 32 new samples (oldest first, at the scale of 16-bit samples) to the 32 subbands' real and imaginary values.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Analyze(ReadOnlySpan<float> samples, Span<float> re, Span<float> im)
    {
        _analysisAt = (_analysisAt + 320 - 32) % 320;
        for (int n = 0; n < 32; n++)
        {
            _analysis[(_analysisAt + n) % 320] = samples[31 - n];
        }

        float[] window = SbrTableData.Qmf;
        for (int m = 0; m < 64; m++)
        {
            float sum = 0;
            for (int j = 0; j < 5; j++)
            {
                int n = m + (64 * j);
                sum += _analysis[(_analysisAt + n) % 320] * window[2 * n];
            }

            _u[m] = sum;
        }

        for (int k = 0; k < AnalysisBands; k++)
        {
            float sumRe = 0;
            float sumIm = 0;
            int row = k * 64;
            for (int n = 0; n < 64; n++)
            {
                sumRe += _u[n] * AnalysisCos[row + n];
                sumIm += _u[n] * AnalysisSin[row + n];
            }

            re[k] = sumRe;
            im[k] = sumIm;
        }
    }

    // One slot of the synthesis: the 64 subbands' values to 64 samples (at the scale of 16-bit samples, times the bank's gain).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Synthesize(ReadOnlySpan<float> re, ReadOnlySpan<float> im, Span<float> output, int bands)
    {
        _synthesisAt = (_synthesisAt + 1280 - 128) % 1280;
        for (int n = 0; n < 64; n++)
        {
            _a[n] = 0;
            _b[n] = 0;
        }

        for (int k = 0; k < bands; k++)
        {
            float r = re[k];
            float i = im[k];
            int row = k * 64;
            for (int n = 0; n < 64; n++)
            {
                _a[n] += r * SynthesisCos[row + n];
                _b[n] += i * SynthesisSin[row + n];
            }
        }

        for (int n = 0; n < 64; n++)
        {
            _synthesis[(_synthesisAt + n) % 1280] = _b[n] - _a[n];
            _synthesis[(_synthesisAt + 127 - n) % 1280] = _a[n] + _b[n];
        }

        float[] window = SbrTableData.Qmf;
        for (int k = 0; k < 64; k++)
        {
            float sum = 0;
            for (int j = 0; j < 10; j++)
            {
                int block = ((_synthesisAt / 128) + j) % 10;
                sum += _synthesis[(block * 128) + ((j & 1) * 64) + k] * window[(64 * j) + k];
            }

            output[k] = sum;
        }
    }
}
