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

internal static class Sbr
{
    // The most envelopes one channel's frame holds, which sizes the per-channel buffers; it is also the
    // limit of a VARVAR frame (the other classes hold at most 4).
    private const int MaxEnvelopes = 5;

    // ceil(log2(envelopes + 1)): the bits of bs_pointer.
    private static readonly int[] PointerBits = [0, 1, 2, 2, 3, 3];

    // The number of QMF bands (ISO/IEC 14496-3 4.6.18.3).
    private const int QmfBands = 64;

    // The frequency band tables of ISO/IEC 14496-3 4.6.18.3 for a header and the SBR output rate.
    public static (SbrBands? Bands, string? Error) Derive(SbrHeader header, int rate)
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
        int noise = Math.Max(1, Nearest(header.NoiseBands * Math.Log2((double)end / kx)));
        if (noise > 5)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"the header gives {noise} noise floor bands, more than 5"));
        }

        return (new SbrBands(kx, end, high, low, noise), null);
    }

    private static int Nearest(double value) => (int)Math.Floor(value + 0.5);

    // The widths of n bands from a to b on a logarithmic scale: the boundaries are the nearest integers to a * (b / a)^(j / n).
    private static int[] LogWidths(int a, int b, int n)
    {
        int[] widths = new int[n];
        int previous = a;
        double ratio = (double)b / a;
        for (int j = 1; j <= n; j++)
        {
            int boundary = j == n ? b : Nearest(a * Math.Pow(ratio, (double)j / n));
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
        int firstCount = 2 * Nearest(bandsPerOctave / 2.0 * Math.Log2((double)k1 / k0));
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
            int secondCount = 2 * Nearest(bandsPerOctave / 2.0 * Math.Log2((double)k2 / k1) / warp);
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

    // sbr_data (ISO/IEC 14496-3 4.4.2): sbr_single_channel_element or
    // sbr_channel_pair_element, then the extended data, whose extensions are read up to their ID:
    // ID 2 is parametric stereo (EXT_ID_PS). Returns where the PS extension's ID lies, or -1, and
    // the reason the data cannot be read, or null.
    // What ReadData gives for the parametric stereo extension's ID in a channel pair's SBR data.
    public const int PsInPair = -2;

    // Plain is whether the data uses none of the optional coding tools an encoder may choose: no extra
    // data bits, no channel coupling, only FIXFIX and VARFIX frame classes, no envelope or noise floor
    // coded across time, no sinusoids and no extended data (see FaacSbr).
    public static (int PsAt, string? Error, bool Plain) ReadData(ref AacBits bits, SbrHeader header, bool pair)
    {
        bool plain = true;
        SbrBands bands = header.Bands!.Value;
        Span<int> envelopes = stackalloc int[2];
        Span<int> noises = stackalloc int[2];
        Span<int> ampRes = stackalloc int[2];
        Span<byte> resolution = stackalloc byte[2 * MaxEnvelopes];
        Span<byte> timeEnvelope = stackalloc byte[2 * MaxEnvelopes];
        Span<byte> timeNoise = stackalloc byte[4];
        string? error;
        if (!pair)
        {
            if (bits.ReadBit())
            {
                bits.Skip(4);
                plain = false;
            }

            if ((error = Grid(ref bits, header, 0, envelopes, noises, ampRes, resolution, ref plain)) is not null)
            {
                return (-1, error, false);
            }

            Directions(ref bits, 0, envelopes, noises, timeEnvelope, timeNoise, ref plain);
            bits.Skip(2 * bands.Noise);
            Envelope(ref bits, bands, 0, false, envelopes, ampRes, resolution, timeEnvelope);
            Noise(ref bits, bands, 0, false, noises, timeNoise);
            Harmonics(ref bits, bands, ref plain);
        }
        else
        {
            if (bits.ReadBit())
            {
                bits.Skip(8);
                plain = false;
            }

            bool coupled = bits.ReadBit();
            if (coupled)
            {
                plain = false;
                if ((error = Grid(ref bits, header, 0, envelopes, noises, ampRes, resolution, ref plain)) is not null)
                {
                    return (-1, error, false);
                }

                envelopes[1] = envelopes[0];
                noises[1] = noises[0];
                ampRes[1] = ampRes[0];
                resolution[..MaxEnvelopes].CopyTo(resolution[MaxEnvelopes..]);
                Directions(ref bits, 0, envelopes, noises, timeEnvelope, timeNoise, ref plain);
                Directions(ref bits, 1, envelopes, noises, timeEnvelope, timeNoise, ref plain);
                bits.Skip(2 * bands.Noise);
                Envelope(ref bits, bands, 0, false, envelopes, ampRes, resolution, timeEnvelope);
                Noise(ref bits, bands, 0, false, noises, timeNoise);
                Envelope(ref bits, bands, 1, true, envelopes, ampRes, resolution, timeEnvelope);
                Noise(ref bits, bands, 1, true, noises, timeNoise);
            }
            else
            {
                if ((error = Grid(ref bits, header, 0, envelopes, noises, ampRes, resolution, ref plain)) is not null
                    || (error = Grid(ref bits, header, 1, envelopes, noises, ampRes, resolution, ref plain)) is not null)
                {
                    return (-1, error, false);
                }

                Directions(ref bits, 0, envelopes, noises, timeEnvelope, timeNoise, ref plain);
                Directions(ref bits, 1, envelopes, noises, timeEnvelope, timeNoise, ref plain);
                bits.Skip(4 * bands.Noise);
                Envelope(ref bits, bands, 0, false, envelopes, ampRes, resolution, timeEnvelope);
                Envelope(ref bits, bands, 1, false, envelopes, ampRes, resolution, timeEnvelope);
                Noise(ref bits, bands, 0, false, noises, timeNoise);
                Noise(ref bits, bands, 1, false, noises, timeNoise);
            }

            Harmonics(ref bits, bands, ref plain);
            Harmonics(ref bits, bands, ref plain);
        }

        int ps = -1;
        if (bits.ReadBit())
        {
            plain = false;
            int size = (int)bits.Read(4);
            if (size == 15)
            {
                size += (int)bits.Read(8);
            }

            int left = size * 8;
            if (left > 7)
            {
                // The first extension's ID; the extensions after it are not read, since the
                // parametric stereo data that ID 2 starts is not parsed.
                // Parametric stereo exists only in a single channel element's SBR data (4.4.2.8,
                // sbr_single_channel_element); in a pair's, ID 2 is reported, not taken for PS.
                int id = (int)bits.Read(2);
                if (id == 2)
                {
                    ps = pair ? PsInPair : bits.Original - 2;
                }

                left -= 2;
            }

            bits.Skip(left);
        }

        return (ps, null, plain);
    }

    // sbr_grid: the frame class, the envelopes and noise floors and each envelope's
    // frequency resolution.
    private static string? Grid(ref AacBits bits, SbrHeader header, int channel, scoped Span<int> envelopes, scoped Span<int> noises, scoped Span<int> ampRes, scoped Span<byte> resolution, ref bool plain)
    {
        int frameClass = (int)bits.Read(2);
        int count;
        ampRes[channel] = header.AmpRes;
        switch (frameClass)
        {
            case 0:
                count = 1 << (int)bits.Read(2);
                if (count > 4)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"an SBR frame of class FIXFIX with {count} envelopes, more than 4");
                }

                // The syntax table fixes the amplitude resolution of a single envelope at 1.5 dB steps.
                if (count == 1)
                {
                    ampRes[channel] = 0;
                }

                byte shared = (byte)bits.Read(1);
                for (int e = 0; e < count; e++)
                {
                    resolution[(channel * MaxEnvelopes) + e] = shared;
                }

                envelopes[channel] = count;
                noises[channel] = count > 1 ? 2 : 1;
                return null;
            case 1 or 2:
                plain &= frameClass == 2;
                bits.Skip(2);
                int relative = (int)bits.Read(2);
                count = relative + 1;
                bits.Skip(2 * relative);
                break;
            default:
                plain = false;
                bits.Skip(4);
                int lead = (int)bits.Read(2);
                int trail = (int)bits.Read(2);
                count = lead + trail + 1;
                if (count > MaxEnvelopes)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"an SBR frame of class VARVAR with {count} envelopes, more than {MaxEnvelopes}");
                }

                bits.Skip(2 * (lead + trail));
                break;
        }

        int pointer = (int)bits.Read(PointerBits[count]);
        if (pointer > count + 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"bs_pointer {pointer} points past the {Counts.Of(count, "envelope", "envelopes")} of the SBR frame");
        }

        // bs_freq_res of a FIXVAR frame is sent for the last envelope first (4.4.2.8, sbr_grid).
        for (int e = 0; e < count; e++)
        {
            resolution[(channel * MaxEnvelopes) + (frameClass == 1 ? count - 1 - e : e)] = (byte)bits.Read(1);
        }

        envelopes[channel] = count;
        noises[channel] = count > 1 ? 2 : 1;
        return null;
    }

    // sbr_dtdf: for each envelope and noise floor, whether it is coded across time.
    private static void Directions(ref AacBits bits, int channel, scoped Span<int> envelopes, scoped Span<int> noises, scoped Span<byte> timeEnvelope, scoped Span<byte> timeNoise, ref bool plain)
    {
        for (int e = 0; e < envelopes[channel]; e++)
        {
            timeEnvelope[(channel * MaxEnvelopes) + e] = (byte)bits.Read(1);
            plain &= timeEnvelope[(channel * MaxEnvelopes) + e] == 0;
        }

        for (int n = 0; n < noises[channel]; n++)
        {
            timeNoise[(channel * 2) + n] = (byte)bits.Read(1);
            plain &= timeNoise[(channel * 2) + n] == 0;
        }
    }

    // sbr_envelope: per envelope a start value and differences across frequency, or
    // differences across time, one per band of the envelope's resolution. A coupled pair's second
    // channel codes the balance, with its own codebooks.
    private static void Envelope(ref AacBits bits, SbrBands bands, int channel, bool balance, scoped Span<int> envelopes, scoped Span<int> ampRes, scoped Span<byte> resolution, scoped Span<byte> timeEnvelope)
    {
        bool coarse = ampRes[channel] != 0;
        int startBits = balance ? (coarse ? 5 : 6) : (coarse ? 6 : 7);
        Huffman time = balance ? (coarse ? Codebooks.SbrBalance30Time : Codebooks.SbrBalance15Time) : (coarse ? Codebooks.SbrEnvelope30Time : Codebooks.SbrEnvelope15Time);
        Huffman frequency = balance ? (coarse ? Codebooks.SbrBalance30Frequency : Codebooks.SbrBalance15Frequency)
            : (coarse ? Codebooks.SbrEnvelope30Frequency : Codebooks.SbrEnvelope15Frequency);
        for (int e = 0; e < envelopes[channel]; e++)
        {
            int count = resolution[(channel * MaxEnvelopes) + e] != 0 ? bands.High : bands.Low;
            if (timeEnvelope[(channel * MaxEnvelopes) + e] == 0)
            {
                bits.Skip(startBits);
                for (int band = 1; band < count; band++)
                {
                    frequency.Decode(ref bits);
                }
            }
            else
            {
                for (int band = 0; band < count; band++)
                {
                    time.Decode(ref bits);
                }
            }
        }
    }

    // sbr_noise: per noise floor, values for the noise floor bands.
    private static void Noise(ref AacBits bits, SbrBands bands, int channel, bool balance, scoped Span<int> noises, scoped Span<byte> timeNoise)
    {
        Huffman time = balance ? Codebooks.SbrNoiseBalanceTime : Codebooks.SbrNoiseTime;
        Huffman frequency = balance ? Codebooks.SbrBalance30Frequency : Codebooks.SbrEnvelope30Frequency;
        for (int n = 0; n < noises[channel]; n++)
        {
            if (timeNoise[(channel * 2) + n] == 0)
            {
                bits.Skip(5);
                for (int band = 1; band < bands.Noise; band++)
                {
                    frequency.Decode(ref bits);
                }
            }
            else
            {
                for (int band = 0; band < bands.Noise; band++)
                {
                    time.Decode(ref bits);
                }
            }
        }
    }

    // bs_add_harmonic_flag and sbr_sinusoidal_coding: a flag per high resolution band.
    private static void Harmonics(ref AacBits bits, SbrBands bands, ref bool plain)
    {
        if (bits.ReadBit())
        {
            plain = false;
            bits.Skip(bands.High);
        }
    }

    // A QMF band's lower edge in Hz at the SBR rate: 64 bands span half the rate.
    public static int BandFrequency(int band, int rate) => (int)Math.Round(band * rate / 128.0, MidpointRounding.AwayFromZero);
}
