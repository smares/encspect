using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Vorbis.Decoding;

// The floor curves (Vorbis I specification, sections 6.2.3 and 7.2.4), multiplied into the channel's spectrum. Floor 1 follows libvorbis 1.3.7 (lib/floor1.c
// floor1_inverse2, render_line, BSD-3-Clause, Copyright (c) 2002-2020 Xiph.Org Foundation; the notice is in THIRD-PARTY-NOTICES.md); floor 0 computes the curve of
// lib/lsp.c vorbis_lsp_to_curve, the exact version libvorbis compiles, in double with DetMath; the gain's exponent is limited to the range of a float.
internal static class Floors
{
    // floor1_inverse2: the line between each pair of used posts in the order of their positions, as the integer steps of render_line, each scaling the spectrum by the amplitude
    // the step stands for. fit holds the unwrapped values (bit 15 set where a post is unused).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ApplyFloor1(Floor1Model floor, ReadOnlySpan<int> fit, Span<float> spectrum, int half)
    {
        float[] table = FloorTable.Amplitude;
        int hx = 0;
        int lx = 0;
        int ly = Math.Clamp(fit[0] * floor.Multiplier, 0, 255);
        for (int j = 1; j < floor.Posts; j++)
        {
            int current = floor.ForwardIndex[j];
            int hy = fit[current] & 0x7FFF;
            if (hy == fit[current])
            {
                hx = floor.PostList[current];
                hy = Math.Clamp(hy * floor.Multiplier, 0, 255);
                RenderLine(half, lx, hx, ly, hy, spectrum, table);
                lx = hx;
                ly = hy;
            }
        }

        float last = table[ly];
        for (int j = hx; j < half; j++)
        {
            spectrum[j] *= last;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void RenderLine(int n, int x0, int x1, int y0, int y1, Span<float> d, float[] table)
    {
        int dy = y1 - y0;
        int adx = x1 - x0;
        int ady = Math.Abs(dy);
        int baseStep = dy / adx;
        int sy = dy < 0 ? baseStep - 1 : baseStep + 1;
        int x = x0;
        int y = y0;
        int err = 0;
        ady -= Math.Abs(baseStep * adx);
        if (n > x1)
        {
            n = x1;
        }

        if (x < n)
        {
            d[x] *= table[y];
        }

        while (++x < n)
        {
            err += ady;
            if (err >= adx)
            {
                err -= adx;
                y += sy;
            }
            else
            {
                y += baseStep;
            }

            d[x] *= table[y];
        }
    }
}

// Floor 0 (Vorbis I specification, section 6): the curve from the amplitude and the line spectral pair coefficients the packet gave, on a Bark scale mapped to the spectrum.
internal sealed class Floor0Curve(Floor0Model model, float[][] vectors, int[] dimensions)
{
    private readonly Dictionary<int, int[]> _maps = [];

    // The coefficients (the entries' vectors, each vector's values offset by the last value of the one before) and the curve over half samples, multiplied into the spectrum.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Apply(ReadOnlySpan<int> packet, Span<float> spectrum, int half)
    {
        int amplitudeRaw = packet[1];
        int bookNumber = packet[2];
        int entries = packet[3];
        int order = model.Order;
        double maximum = (1L << model.AmplitudeBits) - 1;
        double amplitude = (float)(amplitudeRaw / maximum * model.AmplitudeOffset);
        float[] book = vectors[bookNumber];
        int width = dimensions[bookNumber];
        double[] coefficients = _coefficients ??= new double[order + width + 1];
        Array.Clear(coefficients);
        int count = 0;
        for (int e = 0; e < entries && count < order; e++)
        {
            int entry = packet[4 + e];
            for (int k = 0; k < width && count < order; k++)
            {
                coefficients[count++] = book[(entry * width) + k];
            }
        }

        double last = 0;
        for (int j = 0; j < order;)
        {
            int end = Math.Min(order, j + width);
            for (; j < end; j++)
            {
                coefficients[j] += last;
            }

            last = coefficients[j - 1];
        }

        int[] map = MapFor(half);
        double wdel = Math.PI / model.BarkMapSize;
        double[] cosines = _cosines ??= new double[order];
        for (int i = 0; i < order; i++)
        {
            cosines[i] = 2 * DetMath.Cos(Math.Clamp(coefficients[i], 0, Math.PI));
        }

        // vorbis_lsp_to_curve as libvorbis 1.3.7 compiles it (lsp.c leaves FLOAT_LOOKUP and INT_LOOKUP undefined): the exact version, whose gain is not clamped above 0 dB.
        int index = 0;
        while (index < half)
        {
            int k = map[index];
            double p = 0.5;
            double q = 0.5;
            double w = 2 * DetMath.Cos(wdel * k);
            int j = 1;
            for (; j < order; j += 2)
            {
                q *= w - cosines[j - 1];
                p *= w - cosines[j];
            }

            if (j == order)
            {
                q *= w - cosines[j - 1];
                p *= p * (4.0 - (w * w));
                q *= q;
            }
            else
            {
                p *= p * (2.0 - w);
                q *= q * (2.0 + w);
            }

            // The exponent is limited where a float would overflow (a gain is clamped to the range of its representation).
            double exponent = Math.Clamp(((amplitude / Math.Sqrt(Math.Max(p + q, 1e-300))) - model.AmplitudeOffset) * 0.11512925, -87.0, 87.0);
            float value = (float)DetMath.Exp(exponent);
            spectrum[index] *= value;
            while (++index < half && map[index] == k)
            {
                spectrum[index] *= value;
            }
        }
    }

    private double[]? _cosines;
    private double[]? _coefficients;

    // floor0_map_lazy_init: the Bark number of each spectrum line, a line past the Nyquist frequency's number held at the last.
    private int[] MapFor(int half)
    {
        if (_maps.TryGetValue(half, out int[]? map))
        {
            return map;
        }

        map = new int[half + 1];
        double scale = model.BarkMapSize / ToBark(model.Rate / 2.0);
        for (int j = 0; j < half; j++)
        {
            int value = (int)Math.Floor(ToBark(model.Rate / 2.0 / half * j) * scale);
            map[j] = Math.Min(value, model.BarkMapSize - 1);
        }

        map[half] = -1;
        _maps[half] = map;
        return map;
    }

    private static double ToBark(double n) => (13.1 * Atan(0.00074 * n)) + (2.24 * Atan(n * n * 1.85e-8)) + (1e-4 * n);

    // arctan x for x >= 0, from the argument-halving identity and the series, in operations that do not depend on the platform.
    private static double Atan(double x)
    {
        if (x > 1)
        {
            return (Math.PI / 2) - Atan(1 / x);
        }

        double reduced = x / (1 + Math.Sqrt(1 + (x * x)));
        reduced = reduced / (1 + Math.Sqrt(1 + (reduced * reduced)));
        double square = reduced * reduced;
        double term = reduced;
        double sum = reduced;
        for (int i = 1; i < 12; i++)
        {
            term *= -square;
            sum += term / ((2 * i) + 1);
        }

        return 4 * sum;
    }
}
