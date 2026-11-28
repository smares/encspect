using System.Globalization;
using Encspect.Core;
namespace Encspect.Formats.Aac;

// The SBR header of one channel element (ISO/IEC 14496-3 4.4.2, sbr_header) with what its
// frequencies give: the number of SBR bands in the high and low resolution tables and of noise floor
// bands, which say how many values each frame's envelope and noise data codes (the frequency band
// tables of 4.6.18). A header holds until the next one; its absent fields take their defaults.
internal sealed record SbrHeader
{
    public int AmpRes { get; init; }

    public int StartFrequency { get; init; }

    public int StopFrequency { get; init; }

    public int XoverBand { get; init; }

    public int FrequencyScale { get; init; } = 2;

    public int AlterScale { get; init; } = 1;

    public int NoiseBands { get; init; } = 2;

    // Whether the header sends bs_header_extra_1's fields (frequency scale, alter scale and noise bands)
    // and bs_header_extra_2's (the limiter, interpolation and smoothing), or leaves them at their defaults.
    public bool HasHeaderExtra1 { get; init; }

    public bool HasHeaderExtra2 { get; init; }

    public int LimiterBands { get; init; } = 2;

    public int LimiterGains { get; init; } = 2;

    public int InterpolatedFrequency { get; init; } = 1;

    public int SmoothingMode { get; init; } = 1;

    // The frequency tables' figures for the SBR rate the header was read at, or the reason they
    // cannot be built.
    public SbrBands? Bands { get; init; }

    public string? BandError { get; init; }

    // Whether the fields that set the frequency tables are equal.
    public bool SameFrequencies(SbrHeader other) =>
        StartFrequency == other.StartFrequency && StopFrequency == other.StopFrequency && XoverBand == other.XoverBand
        && FrequencyScale == other.FrequencyScale && AlterScale == other.AlterScale && NoiseBands == other.NoiseBands;
}

// kx and k2: the first QMF band SBR rebuilds and the band after its last; the high and low
// resolution band counts and the noise floor bands.
internal readonly record struct SbrBands(int Kx, int K2, int High, int Low, int Noise);

// The frequency band tables of ISO/IEC 14496-3 4.6.18.3 for a header: the master table, and from the crossover band on the high resolution table, the low resolution table
// and the noise floor table, each as the QMF band edges (one more than its bands).
internal sealed record SbrTables(int Kx, int K2, int[] Master, int[] High, int[] Low, int[] Noise);

internal static partial class Sbr
{
    // The number of QMF bands (ISO/IEC 14496-3 4.6.18.3).
    private const int QmfBands = 64;

    // The frequency band tables of ISO/IEC 14496-3 4.6.18.3 for a header and the SBR output rate.
    public static (SbrBands? Bands, string? Error) Derive(SbrHeader header, int rate)
    {
        (SbrTables? tables, string? error) = Tables(header, rate);
        return tables is null ? (null, error) : (new SbrBands(tables.Kx, tables.K2, tables.High.Length - 1, tables.Low.Length - 1, tables.Noise.Length - 1), null);
    }

    // The same tables with their band edges, which the decoder's reconstruction needs.
    public static (SbrTables? Tables, string? Error) Tables(SbrHeader header, int rate)
    {
        int row = rate switch
        {
            16000 => 0,
            22050 => 1,
            24000 => 2,
            32000 => 3,
            44100 or 48000 or 64000 => 4,
            88200 or 96000 or 128000 or 176400 or 192000 => 5,
            _ => -1,
        };
        if (row < 0)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"SBR has no frequency tables for an output rate of {rate} Hz"));
        }

        int baseFrequency = rate < 32000 ? 3000 : rate < 64000 ? 4000 : 5000;
        int startMin = Nearest(baseFrequency * 128.0 / rate);
        int stopMin = Nearest(baseFrequency * 256.0 / rate);
        int k0 = startMin + HuffmanData.SbrOffset[row][header.StartFrequency];
        int k2;
        if (header.StopFrequency <= 13)
        {
            int[] widths = LogWidths(stopMin, QmfBands, 13);
            Array.Sort(widths);
            k2 = stopMin;
            for (int i = 0; i < header.StopFrequency; i++)
            {
                k2 += widths[i];
            }
        }
        else
        {
            k2 = header.StopFrequency == 14 ? 2 * k0 : 3 * k0;
        }

        k2 = Math.Min(k2, QmfBands);
        int limit = rate <= 32000 ? 48 : rate == 44100 ? 35 : 32;
        if (k2 - k0 > limit)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"the SBR range spans {k2 - k0} QMF bands, more than the {limit} allowed at {rate} Hz"));
        }

        if (k0 >= k2)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"the SBR range would start at QMF band {k0} and end at {k2}"));
        }

        int[] master;
        string? error;
        if (header.FrequencyScale == 0)
        {
            (master, error) = LinearMaster(k0, k2, header.AlterScale);
        }
        else
        {
            (master, error) = LogarithmicMaster(k0, k2, header.FrequencyScale, header.AlterScale != 0);
        }

        if (error is not null)
        {
            return (null, error);
        }

        int bandCount = master.Length - 1;
        if (header.XoverBand >= bandCount)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"bs_xover_band {header.XoverBand} lies beyond the {Counts.Of(bandCount, "band", "bands")} of the master frequency table"));
        }

        int kx = master[header.XoverBand];
        int end = master[bandCount];
        if (end > QmfBands)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"the SBR range ends at QMF band {end}, beyond 64"));
        }

        if (kx > 32)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"the SBR range starts at QMF band {kx}, beyond 32"));
        }

        int high = bandCount - header.XoverBand;
        int low = (high + 1) / 2;
        int noise = Math.Max(1, Nearest(header.NoiseBands * DetMath.Log2((double)end / kx)));
        if (noise > 5)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"the header gives {noise} noise floor bands, more than 5"));
        }

        int patches = PatchCount(master, kx, rate);
        if (patches > MaxPatches)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"the frequency tables need {patches} patches, more than the {MaxPatches} the standard allows"));
        }

        int[] highTable = master[header.XoverBand..];
        int[] lowTable = new int[low + 1];
        lowTable[0] = highTable[0];
        for (int k = 1; k <= low; k++)
        {
            lowTable[k] = highTable[(high & 1) != 0 ? (2 * k) - 1 : 2 * k];
        }

        int[] noiseTable = new int[noise + 1];
        noiseTable[0] = lowTable[0];
        int at = 0;
        for (int k = 1; k <= noise; k++)
        {
            at += (low - at) / (noise + 1 - k);
            noiseTable[k] = lowTable[at];
        }

        return (new SbrTables(kx, end, master, highTable, lowTable, noiseTable), null);
    }

    // The most patches an SBR range may need (4.6.18.3.6). A header whose tables need more is built as Figure 4.48 gives them and reported (aac.sbr-patches-over-limit), since
    // reference streams (Coding Technologies' aacPlus decoder check streams) have six.
    public const int MaxPatches = 5;

    // The most patches Figure 4.48's loop can build: it stops at 39 (each patch is at least one QMF band wide and the SBR range is at most 63 bands, and the loop's guard stops it sooner).
    public const int FigureMaxPatches = 39;

    // The patches of Figure 4.48 (4.6.18.6.3) for a master table, kx and the SBR rate: how many there are (numPatches), the width of each (patchNumSubbands) and the first QMF band each
    // copies from (patchStartSubband). The decoder builds its patches from these and the parser checks their number against 4.6.18.3.6, so both see the same ones.
    public static (int Count, int[] Widths, int[] Sources) Patches(int[] master, int kx, int rate)
    {
        int count = master.Length - 1;
        int lsb = master[0];
        int end = master[count];
        int msb = lsb;
        int usb = kx;
        int patches = 0;
        int goal = (int)Math.Floor((2.048e6 / rate) + 0.5);
        int k = count;
        if (goal < end)
        {
            k = 0;
            for (int i = 0; master[i] < goal; i++)
            {
                k = i + 1;
            }
        }

        var widths = new int[40];
        var sources = new int[40];
        int sb;
        int guard = 0;
        do
        {
            int j = k;
            int odd;
            do
            {
                sb = master[j];
                odd = (sb - 2 + lsb) % 2;
                j--;
            }
            while (sb > lsb - 1 + msb - odd && j >= 0);

            int width = Math.Max(sb - usb, 0);
            widths[patches] = width;
            sources[patches] = lsb - odd - width;
            if (width > 0)
            {
                usb = sb;
                msb = sb;
                patches++;
            }
            else
            {
                msb = kx;
            }

            if (master[k] - sb < 3)
            {
                k = count;
            }
        }
        while (sb != end && ++guard < 32 && patches < 39);

        if (patches > 0 && widths[patches - 1] < 3 && patches > 1)
        {
            patches--;
        }

        return (patches, widths, sources);
    }

    public static int PatchCount(int[] master, int kx, int rate) => Patches(master, kx, rate).Count;

    private static int Nearest(double value) => (int)Math.Floor(value + 0.5);

    // The widths of n bands from a to b on a logarithmic scale: the boundaries are the nearest integers to a * (b / a)^(j / n).
    private static int[] LogWidths(int a, int b, int n)
    {
        int[] widths = new int[n];
        int previous = a;
        double ratio = (double)b / a;
        for (int j = 1; j <= n; j++)
        {
            int boundary = j == n ? b : Nearest(a * DetMath.Pow(ratio, (double)j / n));
            widths[j - 1] = boundary - previous;
            previous = boundary;
        }

        return widths;
    }

    private static int[] Boundaries(int start, int[] firstWidths, int[] secondWidths)
    {
        int[] table = new int[firstWidths.Length + secondWidths.Length + 1];
        table[0] = start;
        int at = 0;
        foreach (int width in firstWidths)
        {
            table[at + 1] = table[at] + width;
            at++;
        }

        foreach (int width in secondWidths)
        {
            table[at + 1] = table[at] + width;
            at++;
        }

        return table;
    }

    private static (int[] Master, string? Error) LinearMaster(int k0, int k2, int alterScale)
    {
        int span = k2 - k0;
        int step = alterScale != 0 ? 2 : 1;
        int count = alterScale != 0 ? 2 * ((span + 2) / 4) : 2 * (span / 2);
        if (count == 0)
        {
            return ([], "the SBR master frequency table is empty");
        }

        int[] widths = new int[count];
        Array.Fill(widths, step);
        int rest = span - (count * step);
        for (int i = 0; i < -rest; i++)
        {
            widths[i]--;
        }

        if (rest > 0)
        {
            widths[count - 1] += rest;
        }

        return (Boundaries(k0, widths, []), null);
    }

    private static (int[] Master, string? Error) LogarithmicMaster(int k0, int k2, int scale, bool warped)
    {
        int bandsPerOctave = scale == 1 ? 12 : scale == 2 ? 10 : 8;
        bool twoRegions = (double)k2 / k0 > 2.2449;
        int k1 = twoRegions ? 2 * k0 : k2;
        int firstCount = 2 * Nearest(bandsPerOctave / 2.0 * DetMath.Log2((double)k1 / k0));
        if (firstCount <= 0)
        {
            return ([], "the SBR master frequency table's first region is empty");
        }

        int[] first = LogWidths(k0, k1, firstCount);
        Array.Sort(first);
        if (first[0] <= 0)
        {
            return ([], "the SBR master frequency table has a band of no width");
        }

        int[] second = [];
        if (twoRegions)
        {
            double warp = warped ? 1.3 : 1.0;
            int secondCount = 2 * Nearest(bandsPerOctave / 2.0 * DetMath.Log2((double)k2 / k1) / warp);
            if (secondCount <= 0)
            {
                return ([], "the SBR master frequency table's second region is empty");
            }

            second = LogWidths(k1, k2, secondCount);
            Array.Sort(second);
            int firstMax = first[^1];
            if (second[0] < firstMax)
            {
                int shift = Math.Min(firstMax - second[0], (second[^1] - second[0]) / 2);
                second[0] += shift;
                second[^1] -= shift;
            }

            Array.Sort(second);
            if (second[0] <= 0)
            {
                return ([], "the SBR master frequency table has a band of no width");
            }
        }

        return (Boundaries(k0, first, second), null);
    }


    // A QMF band's lower edge in Hz at the SBR rate: 64 bands span half the rate.
    public static int BandFrequency(int band, int rate) => (int)Math.Round(band * rate / 128.0, MidpointRounding.AwayFromZero);
}
