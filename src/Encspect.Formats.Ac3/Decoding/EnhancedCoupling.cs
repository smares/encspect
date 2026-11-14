using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Ac3.Decoding;

// Enhanced coupling's reconstruction (ATSC A/52:2018 Annex E 3.5.5): the coupling channel of the previous, the current and the next block is made free of time domain aliasing,
// moved to a complex spectrum (the MDCT coefficients and their sine counterparts), rotated by each channel's angle and scaled by its amplitude, and brought back to coefficients
// of the current block. The random angles the specification asks for (chaos, and new values in every block for a channel with a transient) are drawn from generators with fixed
// seeds, so every platform decodes the same samples.
internal sealed class EnhancedCoupling
{
    private const int N = 512;

    // Table E3.10: ampbnd = ecplampmanttab / 32 >> ecplampexptab, 0 for the code 31.
    private static readonly float[] Amplitude = BuildAmplitude();

    // Table E3.12: the chaos scaling of each code, in sevenths.
    private const float ChaosStep = 1.0f / 7.0f;

    private readonly float[] _window = Quantizers.Window();
    private readonly float[] _cos3 = new float[N];
    private readonly float[] _sin3 = new float[N];
    private readonly float[] _y = new float[N / 2];
    private readonly float[] _fftCos = new float[N / 2];
    private readonly float[] _fftSin = new float[N / 2];
    private readonly int[] _reverse = new int[N];
    private readonly float[] _re = new float[N];
    private readonly float[] _im = new float[N];
    private readonly float[] _zr = new float[N / 2];
    private readonly float[] _zi = new float[N / 2];
    private readonly float[] _amplitude = new float[256];
    private readonly float[] _angle = new float[256];
    private readonly float[] _chaos = new float[256];
    private readonly float[] _random = new float[256];
    private readonly float[] _bandAngle = new float[22];
    private readonly float[] _bandChaos = new float[22];
    private readonly float[] _bandAmplitude = new float[22];
    private readonly float[] _bandRandom = new float[22];
    private readonly int[] _bandBins = new int[22];

    // The random angle of each bin of each channel for blocks without a transient: made once, kept for every block of every frame (3.5.5.3).
    private readonly float[][] _fixedRandom = new float[5][];
    private uint _noise = 0x3C6EF35F;

    public EnhancedCoupling()
    {
        for (int n = 0; n < N; n++)
        {
            double angle = Math.PI * n / N;
            _cos3[n] = (float)DetMath.Cos(angle);
            _sin3[n] = (float)-DetMath.Sin(angle);
        }

        for (int k = 0; k < N / 2; k++)
        {
            _y[k] = (float)DetMath.Cos(2.0 * Math.PI * ((N / 4) + 0.5) / N * (k + 0.5));
            DetMath.SinCos(k, N, out double sin, out double cos);
            _fftCos[k] = (float)cos;
            _fftSin[k] = (float)sin;
        }

        InverseTransform.BitReversal(_reverse);
        uint seed = 0x2F6B1D93;
        for (int ch = 0; ch < 5; ch++)
        {
            _fixedRandom[ch] = new float[256];
            for (int bin = 0; bin < 256; bin++)
            {
                _fixedRandom[ch][bin] = Uniform(ref seed);
            }
        }
    }

    // Restarts the generator of the transients' angles, as after a gap.
    public void Reset() => _noise = 0x3C6EF35F;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float Uniform(ref uint state)
    {
        state = unchecked((state * 1664525u) + 1013904223u);
        return ((int)state >> 8) * (1.0f / 8388608.0f);
    }

    private static float[] BuildAmplitude()
    {
        var table = new float[32];
        for (int code = 0; code < 31; code++)
        {
            int[] mantissas = [0x20, 0x1B, 0x17, 0x13, 0x10];
            int mantissa = code < 5 ? mantissas[code] : mantissas[((code - 1) % 4) + 1];
            int exponent = code < 5 ? 0 : ((code - 1) / 4);
            table[code] = mantissa / 32.0f * (float)DetMath.PowerOfTwo(-exponent);
        }

        return table;
    }

    // The complex spectrum of the non-aliased coupling channel of the current block: previous, current and next are the windowed outputs of the inverse transform (7.9.4.1, steps 1
    // to 5) of the coupling channel's coefficients of those blocks, or empty where the block does not have enhanced coupling.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Analyze(ReadOnlySpan<float> previous, ReadOnlySpan<float> current, ReadOnlySpan<float> next)
    {
        float[] re = _re;
        float[] im = _im;
        for (int n = 0; n < N; n++)
        {
            float signal;
            if (n < N / 2)
            {
                signal = (previous.IsEmpty ? 0 : previous[n + (N / 2)]) + current[n];
            }
            else
            {
                signal = current[n] + (next.IsEmpty ? 0 : next[n - (N / 2)]);
            }

            // The factor of 2 of 7.9.4.1's step 6 (the headroom the encoder takes off) that 3.5.5.1 leaves out: with it an amplitude of 1 and an angle of 0 return the coupling channel's
            // own coefficients, which is what the transform pair is for; without it every channel would come back at half.
            float windowed = 2.0f * signal * (n < N / 2 ? _window[n] : _window[N - 1 - n]);
            int r = _reverse[n];
            re[r] = windowed * _cos3[n];
            im[r] = -(windowed * _sin3[n]);
        }

        InverseTransform.Fft(re, im, N, _fftCos, _fftSin);
        for (int k = 0; k < N / 2; k++)
        {
            _zr[k] = re[k] * (1.0f / N);
            _zi[k] = -im[k] * (1.0f / N);
        }
    }

    // The coefficients of one coupled channel in the enhanced coupling range, from the spectrum Analyze made and the channel's parameters per band: amplitude code, angle code and
    // chaos code (ecplamp | ecplangle << 5 | ecplchaos << 11), whether the channel is the first in coupling and has a transient, and the band structure (a bit per sub-band, set
    // where it joins the band before), and the dither of the bins the coupling channel codes with no bits (already drawn, zero elsewhere), which is scaled by the bin's amplitude.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Channel(Span<float> spectrum, ReadOnlySpan<int> parameters, int channel, bool isFirst, bool hasTransient, bool interpolate, int begin, int end, int joined, ReadOnlySpan<float> dither)
    {
        int bands = 0;
        int lastBin = 0;
        for (int sub = begin; sub < end; sub++)
        {
            int low = Tables.EnhancedCouplingSubBandStart[sub];
            int high = Tables.EnhancedCouplingSubBandStart[sub + 1];
            if (sub == begin || ((joined >> sub) & 1) == 0)
            {
                _bandBins[bands++] = 0;
            }

            _bandBins[bands - 1] += high - low;
            for (int bin = low; bin < high; bin++)
            {
                _bandIndexOfBin[bin] = bands - 1;
            }

            lastBin = high;
        }

        int start = Tables.EnhancedCouplingSubBandStart[begin];
        for (int band = 0; band < bands; band++)
        {
            int packed = parameters[band];
            int amplitudeCode = packed & 31;
            int angleCode = (packed >> 5) & 63;
            int chaosCode = (packed >> 11) & 7;
            float amplitude = Amplitude[amplitudeCode];
            float chaos = isFirst ? 0 : -chaosCode * ChaosStep;
            if (!hasTransient && !isFirst)
            {
                amplitude *= 1 + (0.38f * chaos);
            }

            _bandAmplitude[band] = amplitude;
            _bandChaos[band] = chaos;
            _bandAngle[band] = isFirst ? 0 : (angleCode < 32 ? angleCode : angleCode - 64) * (1.0f / 32.0f);
            _bandRandom[band] = hasTransient ? Uniform(ref _noise) : 0;
        }

        for (int bin = start; bin < lastBin; bin++)
        {
            int band = _bandIndexOfBin[bin];
            _amplitude[bin] = _bandAmplitude[band];
            _chaos[bin] = _bandChaos[band];
            _angle[bin] = _bandAngle[band];
            _random[bin] = hasTransient ? _bandRandom[band] : _fixedRandom[channel][bin];
        }

        if (interpolate && !isFirst)
        {
            Interpolate(bands, start);
        }

        for (int bin = start; bin < lastBin; bin++)
        {
            float angle = _angle[bin] + (_chaos[bin] * _random[bin]);
            angle = angle < -1.0f ? angle + 2.0f : angle >= 1.0f ? angle - 2.0f : angle;
            double radians = Math.PI * angle;
            float cos = (float)DetMath.Cos(radians) * _amplitude[bin];
            float sin = (float)DetMath.Sin(radians) * _amplitude[bin];
            float zr = (_zr[bin] * cos) - (_zi[bin] * sin);
            float zi = (_zi[bin] * cos) + (_zr[bin] * sin);
            spectrum[bin] = (-2.0f * ((_y[bin] * zr) + (_y[(N / 2) - 1 - bin] * zi))) + (dither[bin] * _amplitude[bin]);
        }
    }

    private readonly int[] _bandIndexOfBin = new int[256];

    // Band angles to bin angles by linear interpolation between the bands' centers (3.5.5.3), wrapped to -1 to 1 (pi radians) as the pseudo code does.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Interpolate(int bands, int start)
    {
        if (bands < 2)
        {
            return;
        }

        int bin = 0;
        float y = 0;
        float slope = 0;
        int currentBins = 0;
        for (int band = 1; band < bands; band++)
        {
            int previousBins = _bandBins[band - 1];
            currentBins = _bandBins[band];
            float previous = _bandAngle[band - 1];
            float current = _bandAngle[band];
            while (current - previous > 1.0f)
            {
                current -= 2.0f;
            }

            while (previous - current > 1.0f)
            {
                current += 2.0f;
            }

            slope = (current - previous) / ((currentBins + previousBins) / 2.0f);
            if (band == 1 && previousBins > 1)
            {
                int count;
                if (previousBins % 2 == 0)
                {
                    y = previous - (slope / 2);
                    bin = (previousBins / 2) - 1;
                }
                else
                {
                    y = previous - slope;
                    bin = (previousBins - 3) / 2;
                }

                count = bin + 1;
                for (int j = 0; j < count; j++)
                {
                    float unwrapped = y;
                    _angle[start + bin--] = Wrap(y);
                    y = unwrapped - slope;
                }

                bin = count;
            }

            int upper;
            if (previousBins % 2 == 0)
            {
                y = previous + (slope / 2);
                upper = (currentBins / 2) + (previousBins / 2);
            }
            else
            {
                y = previous;
                upper = (currentBins / 2) + ((previousBins + 1) / 2);
            }

            for (int j = 0; j < upper; j++)
            {
                float unwrapped = y;
                _angle[start + bin++] = Wrap(y);
                y = unwrapped + slope;
            }
        }

        int last = currentBins % 2 == 0 ? currentBins / 2 : (currentBins / 2) + 1;
        for (int j = 0; j < last; j++)
        {
            float unwrapped = y;
            _angle[start + bin++] = Wrap(y);
            y = unwrapped + slope;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float Wrap(float value)
    {
        while (value > 1.0f)
        {
            value -= 2.0f;
        }

        while (value < -1.0f)
        {
            value += 2.0f;
        }

        return value;
    }
}
