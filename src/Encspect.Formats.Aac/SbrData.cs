using System.Globalization;
using Encspect.Core;
namespace Encspect.Formats.Aac;

internal static partial class Sbr
{
    // The most envelopes one channel's frame holds, which sizes the per-channel buffers; it is also the
    // limit of a VARVAR frame (the other classes hold at most 4).
    private const int MaxEnvelopes = SbrRecord.MaxEnvelopes;

    // ceil(log2(envelopes + 1)): the bits of bs_pointer.
    private static readonly string[] FrameClassNames = ["FIXFIX", "FIXVAR", "VARFIX", "VARVAR"];
    private static readonly int[] PointerBits = [0, 1, 2, 2, 3, 3];

    // What ReadData gives for the parametric stereo extension's ID in a channel pair's SBR data.
    public const int PsInPair = -2;

    // sbr_data (ISO/IEC 14496-3 4.4.2): sbr_single_channel_element or sbr_channel_pair_element, then the extended data, whose extensions are read up to their ID: ID 2 is parametric
    // stereo (EXT_ID_PS), whose ps_data is read where the data is a single channel element's. Returns where the PS extension's ID lies, or -1, and the reason the data cannot be
    // read, or null. With a record, the values are stored in it as SbrRecord lays them out (the header's fields are the caller's).
    // Plain is whether the data uses none of the optional coding tools an encoder may choose: no extra data bits, no channel coupling, only FIXFIX and VARFIX frame classes, no
    // envelope or noise floor coded across time, no sinusoids and no extended data (see FaacSbr).
    public static (int PsAt, string? Error, bool Plain) ReadData(ref AacBits bits, SbrHeader header, bool pair, Span<int> output = default, Span<int> psState = default, scoped Span<int> figures = default)
    {
        bool plain = true;
        SbrBands bands = header.Bands!.Value;
        bool stores = !output.IsEmpty;
        Span<int> record = stores ? output : Scratch();
        Span<int> envelopes = stackalloc int[2];
        Span<int> noises = stackalloc int[2];
        Span<int> ampRes = stackalloc int[2];
        string? error;
        {
            record[SbrRecord.Pair] = pair ? 1 : 0;
            record[SbrRecord.Valid] = 0;
            record[SbrRecord.HasPs] = 0;
            record[SbrRecord.Coupling] = 0;
            record[SbrRecord.AmpRes] = header.AmpRes;
            record[SbrRecord.StartFrequency] = header.StartFrequency;
            record[SbrRecord.StopFrequency] = header.StopFrequency;
            record[SbrRecord.XoverBand] = header.XoverBand;
            record[SbrRecord.FrequencyScale] = header.FrequencyScale;
            record[SbrRecord.AlterScale] = header.AlterScale;
            record[SbrRecord.NoiseBands] = header.NoiseBands;
            record[SbrRecord.LimiterBands] = header.LimiterBands;
            record[SbrRecord.LimiterGains] = header.LimiterGains;
            record[SbrRecord.InterpolatedFrequency] = header.InterpolatedFrequency;
            record[SbrRecord.SmoothingMode] = header.SmoothingMode;
            for (int c = 0; c < 2; c++)
            {
                record[SbrRecord.FirstChannel + (c * SbrRecord.ChannelStride) + SbrRecord.EnvelopeCount] = 0;
            }
        }

        if (!pair)
        {
            if (bits.ReadBit())
            {
                bits.Skip(4);
                plain = false;
            }

            if ((error = Grid(ref bits, header, 0, envelopes, noises, ampRes, Channel(record, 0), ref plain)) is not null)
            {
                return (-1, error, false);
            }

            Directions(ref bits, 0, envelopes, noises, Channel(record, 0), ref plain);
            InverseFilter(ref bits, bands, Channel(record, 0));
            Envelope(ref bits, bands, false, envelopes[0], ampRes[0], Channel(record, 0));
            Noise(ref bits, bands, false, noises[0], Channel(record, 0));
            Harmonics(ref bits, bands, Channel(record, 0), ref plain);
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
                record[SbrRecord.Coupling] = 1;

                if ((error = Grid(ref bits, header, 0, envelopes, noises, ampRes, Channel(record, 0), ref plain)) is not null)
                {
                    return (-1, error, false);
                }

                envelopes[1] = envelopes[0];
                noises[1] = noises[0];
                ampRes[1] = ampRes[0];
                Channel(record, 0).CopyTo(Channel(record, 1));

                Directions(ref bits, 0, envelopes, noises, Channel(record, 0), ref plain);
                Directions(ref bits, 1, envelopes, noises, Channel(record, 1), ref plain);
                InverseFilter(ref bits, bands, Channel(record, 0));
                Channel(record, 0).Slice(SbrRecord.InverseFilterMode, 5).CopyTo(Channel(record, 1).Slice(SbrRecord.InverseFilterMode, 5));
                Envelope(ref bits, bands, false, envelopes[0], ampRes[0], Channel(record, 0));
                Noise(ref bits, bands, false, noises[0], Channel(record, 0));
                Envelope(ref bits, bands, true, envelopes[1], ampRes[1], Channel(record, 1));
                Noise(ref bits, bands, true, noises[1], Channel(record, 1));
            }
            else
            {
                if ((error = Grid(ref bits, header, 0, envelopes, noises, ampRes, Channel(record, 0), ref plain)) is not null
                    || (error = Grid(ref bits, header, 1, envelopes, noises, ampRes, Channel(record, 1), ref plain)) is not null)
                {
                    return (-1, error, false);
                }

                Directions(ref bits, 0, envelopes, noises, Channel(record, 0), ref plain);
                Directions(ref bits, 1, envelopes, noises, Channel(record, 1), ref plain);
                InverseFilter(ref bits, bands, Channel(record, 0));
                InverseFilter(ref bits, bands, Channel(record, 1));
                Envelope(ref bits, bands, false, envelopes[0], ampRes[0], Channel(record, 0));
                Envelope(ref bits, bands, false, envelopes[1], ampRes[1], Channel(record, 1));
                Noise(ref bits, bands, false, noises[0], Channel(record, 0));
                Noise(ref bits, bands, false, noises[1], Channel(record, 1));
            }

            Harmonics(ref bits, bands, Channel(record, 0), ref plain);
            Harmonics(ref bits, bands, Channel(record, 1), ref plain);
        }

        record[SbrRecord.Valid] = 1;
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
                // The first extension's ID; the extensions after it are not read.
                // Parametric stereo exists only in a single channel element's SBR data (4.4.2.8,
                // sbr_single_channel_element); in a pair's, ID 2 is reported, not taken for PS.
                int id = (int)bits.Read(2);
                left -= 2;
                if (id == 2)
                {
                    ps = pair ? PsInPair : bits.Original - 2;
                    if (!pair && !psState.IsEmpty)
                    {
                        left -= ReadPs(ref bits, left, record.Slice(SbrRecord.Ps), psState);
                        record[SbrRecord.HasPs] = record[SbrRecord.Ps + SbrRecord.PsEnvelopes] >= 0 ? 1 : 0;
                    }
                }
            }

            bits.Skip(Math.Max(0, left));
        }

        if (figures.Length >= Figures)
        {
            figures[FigureFrameClass] = record[SbrRecord.FirstChannel + SbrRecord.FrameClass];
            figures[FigureEnvelopes] = record[SbrRecord.FirstChannel + SbrRecord.EnvelopeCount];
            figures[FigureFrameClass + 2] = pair ? record[SbrRecord.FirstChannel + SbrRecord.ChannelStride + SbrRecord.FrameClass] : -1;
            figures[FigureEnvelopes + 2] = pair ? record[SbrRecord.FirstChannel + SbrRecord.ChannelStride + SbrRecord.EnvelopeCount] : -1;
            bool hasPs = record[SbrRecord.HasPs] == 1;
            figures[FigurePsEnvelopes] = hasPs ? record[SbrRecord.Ps + SbrRecord.PsEnvelopes] : -1;
            figures[FigurePsIidMode] = hasPs && record[SbrRecord.Ps + SbrRecord.PsEnableIid] != 0 ? record[SbrRecord.Ps + SbrRecord.PsIidMode] : -1;
            figures[FigurePsIccMode] = hasPs && record[SbrRecord.Ps + SbrRecord.PsEnableIcc] != 0 ? record[SbrRecord.Ps + SbrRecord.PsIccMode] : -1;
            figures[FigurePsMixing] = hasPs ? record[SbrRecord.Ps + SbrRecord.PsMixingB] : -1;
            figures[FigurePsPhase] = hasPs ? record[SbrRecord.Ps + SbrRecord.PsEnableIpdopd] : -1;
        }

        return (ps, null, plain);
    }

    // What ReadData states of the payload in its figures: the frame class and envelope count of each channel (-1 for a second channel a single channel element does not have), and where
    // the payload carries ps_data its envelope count, the IID and ICC modes (-1 where there is none or the parameter is not enabled), whether the coherence mode selects mixing procedure Rb and whether the extension carries IPD and OPD (-1 where there is no ps_data).
    public const int Figures = 9;
    public const int FigureFrameClass = 0;
    public const int FigureEnvelopes = 1;
    public const int FigurePsEnvelopes = 4;
    public const int FigurePsIidMode = 5;
    public const int FigurePsIccMode = 6;
    public const int FigurePsMixing = 7;
    public const int FigurePsPhase = 8;

    // Where a payload that is not stored is read to (the read needs the values of its own channels): a buffer for each thread, cleared for each payload, so that nothing is allocated.
    [ThreadStatic]
    private static int[]? _scratch;

    private static Span<int> Scratch()
    {
        int[] scratch = _scratch ??= new int[SbrRecord.Ps + SbrRecord.PsEnd];
        Array.Clear(scratch);
        return scratch;
    }

    private static Span<int> Channel(Span<int> record, int channel) =>
        record.Slice(SbrRecord.FirstChannel + (channel * SbrRecord.ChannelStride), SbrRecord.ChannelStride);

    // sbr_grid: the frame class, the envelopes and noise floors, and each envelope's frequency resolution; with a record, the borders as the decoder uses them (libxaac's
    // ixheaacd_sbr_time_freq_grid_info; the borders count in time slots of two QMF columns, 16 to a frame).
    private static string? Grid(ref AacBits bits, SbrHeader header, int channel, scoped Span<int> envelopes, scoped Span<int> noises, scoped Span<int> ampRes, Span<int> record, ref bool plain)
    {
        int frameClass = (int)bits.Read(2);
        int count;
        ampRes[channel] = header.AmpRes;
        Span<int> border = stackalloc int[MaxEnvelopes + 1];
        Span<int> noiseBorder = stackalloc int[3];
        Span<int> resolution = stackalloc int[MaxEnvelopes];
        int transient = -1;
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

                int shared = (int)bits.Read(1);
                for (int e = 0; e < count; e++)
                {
                    resolution[e] = shared;
                    border[e] = e * (16 / count);
                }

                border[count] = 16;
                noiseBorder[0] = 0;
                noiseBorder[1] = count == 1 ? 16 : 8;
                noiseBorder[2] = 16;
                envelopes[channel] = count;
                noises[channel] = count > 1 ? 2 : 1;
                Store(record, frameClass, count, noises[channel], ampRes[channel], transient, border, noiseBorder, resolution);
                return null;
            case 1:
                {
                    plain &= false;
                    int relative = (int)bits.Read(2);
                    int variable = (int)bits.Read(2);
                    count = variable + 1;
                    int edge = relative + 16;
                    border[0] = 0;
                    border[count] = edge;
                    for (int k = variable; k > 0; k--)
                    {
                        edge -= ((int)bits.Read(2) * 2) + 2;
                        border[k] = edge;
                    }

                    int pointer = (int)bits.Read(PointerBits[count]);
                    if (pointer > count + 1)
                    {
                        return string.Create(CultureInfo.InvariantCulture, $"bs_pointer {pointer} points past the {Counts.Of(count, "envelope", "envelopes")} of the SBR frame");
                    }

                    // bs_freq_res of a FIXVAR frame is sent for the last envelope first (4.4.2.8, sbr_grid).
                    for (int e = count - 1; e >= 0; e--)
                    {
                        resolution[e] = (int)bits.Read(1);
                    }

                    transient = pointer != 0 ? count + 1 - pointer : -1;
                    noiseBorder[1] = pointer is 0 or 1 ? border[variable] : border[transient];
                    noises[channel] = count > 1 ? 2 : 1;
                    noiseBorder[0] = border[0];
                    noiseBorder[noises[channel]] = border[count];
                    break;
                }

            case 2:
                {
                    int relative = (int)bits.Read(2);
                    int variable = (int)bits.Read(2);
                    count = variable + 1;
                    int edge = relative;
                    border[0] = edge;
                    for (int k = 1; k <= variable; k++)
                    {
                        edge += ((int)bits.Read(2) * 2) + 2;
                        border[k] = edge;
                    }

                    border[count] = 16;
                    int pointer = (int)bits.Read(PointerBits[count]);
                    if (pointer > count + 1)
                    {
                        return string.Create(CultureInfo.InvariantCulture, $"bs_pointer {pointer} points past the {Counts.Of(count, "envelope", "envelopes")} of the SBR frame");
                    }

                    transient = pointer is 0 or 1 ? -1 : pointer - 1;
                    for (int e = 0; e < count; e++)
                    {
                        resolution[e] = (int)bits.Read(1);
                    }

                    noiseBorder[1] = pointer switch
                    {
                        0 => border[1],
                        1 => border[variable],
                        _ => border[transient],
                    };
                    noises[channel] = count > 1 ? 2 : 1;
                    noiseBorder[0] = border[0];
                    noiseBorder[noises[channel]] = border[count];
                    plain &= true;
                    break;
                }

            default:
                {
                    plain = false;
                    int lead = (int)bits.Read(2);
                    int trail = (int)bits.Read(2);
                    int leadBorders = (int)bits.Read(2);
                    int trailBorders = (int)bits.Read(2);
                    count = leadBorders + trailBorders + 1;
                    if (count > MaxEnvelopes)
                    {
                        return string.Create(CultureInfo.InvariantCulture, $"an SBR frame of class VARVAR with {count} envelopes, more than {MaxEnvelopes}");
                    }

                    // The lead border and the trail border: bs_var_bord_0 and bs_var_bord_1, then the relative borders (bs_num_rel_0 and bs_num_rel_1).
                    int edge = lead;
                    border[0] = edge;
                    for (int k = 1; k <= leadBorders; k++)
                    {
                        edge += ((int)bits.Read(2) * 2) + 2;
                        border[k] = edge;
                    }

                    edge = trail + 16;
                    border[count] = edge;
                    for (int k = 0; k < trailBorders; k++)
                    {
                        edge -= ((int)bits.Read(2) * 2) + 2;
                        border[count - 1 - k] = edge;
                    }

                    int pointer = (int)bits.Read(PointerBits[count]);
                    if (pointer > count + 1)
                    {
                        return string.Create(CultureInfo.InvariantCulture, $"bs_pointer {pointer} points past the {Counts.Of(count, "envelope", "envelopes")} of the SBR frame");
                    }

                    transient = pointer != 0 ? count + 1 - pointer : -1;
                    for (int e = 0; e < count; e++)
                    {
                        resolution[e] = (int)bits.Read(1);
                    }

                    noiseBorder[0] = lead;
                    if (count == 1)
                    {
                        noiseBorder[1] = trail + 16;
                        noises[channel] = 1;
                    }
                    else
                    {
                        noiseBorder[1] = pointer is 0 or 1 ? border[count - 1] : border[transient];
                        noiseBorder[2] = trail + 16;
                        noises[channel] = 2;
                    }

                    break;
                }
        }

        // 4.6.18.3.3: the envelope borders rise strictly, from 0..3 at the start to 16..19 at the end of the frame, which also keeps every border inside the 40 time slots the decoder holds.
        bool ordered = border[0] is >= 0 and <= 3 && border[count] is >= 16 and <= 19;
        for (int k = 1; ordered && k <= count; k++)
        {
            ordered = border[k] > border[k - 1];
        }

        if (!ordered)
        {
            return string.Create(CultureInfo.InvariantCulture, $"envelope borders of an SBR frame of class {FrameClassNames[frameClass]} that do not rise strictly from the start of the frame to its end");
        }

        envelopes[channel] = count;
        Store(record, frameClass, count, noises[channel], ampRes[channel], transient, border, noiseBorder, resolution);
        return null;
    }

    private static void Store(Span<int> record, int frameClass, int count, int noises, int ampRes, int transient, ReadOnlySpan<int> border, ReadOnlySpan<int> noiseBorder, ReadOnlySpan<int> resolution)
    {
        record[SbrRecord.FrameClass] = frameClass;
        record[SbrRecord.EnvelopeCount] = count;
        record[SbrRecord.NoiseCount] = noises;
        record[SbrRecord.ChannelAmpRes] = ampRes;
        record[SbrRecord.Transient] = transient;
        for (int i = 0; i <= count; i++)
        {
            record[SbrRecord.Borders + i] = border[i];
        }

        for (int i = 0; i <= noises; i++)
        {
            record[SbrRecord.NoiseBorders + i] = noiseBorder[i];
        }

        for (int i = 0; i < count; i++)
        {
            record[SbrRecord.FrequencyResolution + i] = resolution[i];
        }
    }

    // sbr_dtdf: for each envelope and noise floor, whether it is coded across time.
    private static void Directions(ref AacBits bits, int channel, scoped Span<int> envelopes, scoped Span<int> noises, Span<int> record, ref bool plain)
    {
        for (int e = 0; e < envelopes[channel]; e++)
        {
            int direction = (int)bits.Read(1);
            plain &= direction == 0;
            record[SbrRecord.EnvelopeTime + e] = direction;
        }

        for (int n = 0; n < noises[channel]; n++)
        {
            int direction = (int)bits.Read(1);
            plain &= direction == 0;
            record[SbrRecord.NoiseTime + n] = direction;
        }
    }

    // sbr_invf: bs_invf_mode of each noise floor band.
    private static void InverseFilter(ref AacBits bits, SbrBands bands, Span<int> record)
    {
        for (int b = 0; b < bands.Noise; b++)
        {
            record[SbrRecord.InverseFilterMode + b] = (int)bits.Read(2);
        }
    }

    // sbr_envelope: per envelope a start value and differences across frequency, or
    // differences across time, one per band of the envelope's resolution. A coupled pair's second
    // channel codes the balance, with its own codebooks.
    private static void Envelope(ref AacBits bits, SbrBands bands, bool balance, int envelopes, int ampRes, Span<int> record)
    {
        bool coarse = ampRes != 0;
        int startBits = balance ? (coarse ? 5 : 6) : (coarse ? 6 : 7);
        Huffman time = balance ? (coarse ? Codebooks.SbrBalance30Time : Codebooks.SbrBalance15Time) : (coarse ? Codebooks.SbrEnvelope30Time : Codebooks.SbrEnvelope15Time);
        Huffman frequency = balance ? (coarse ? Codebooks.SbrBalance30Frequency : Codebooks.SbrBalance15Frequency)
            : (coarse ? Codebooks.SbrEnvelope30Frequency : Codebooks.SbrEnvelope15Frequency);
        for (int e = 0; e < envelopes; e++)
        {
            int count = record[SbrRecord.FrequencyResolution + e] != 0 ? bands.High : bands.Low;
            Span<int> values = record.Slice(SbrRecord.Envelope + (e * SbrRecord.EnvelopeStride), SbrRecord.EnvelopeStride);
            if (record[SbrRecord.EnvelopeTime + e] == 0)
            {
                values[0] = (int)bits.Read(startBits);
                for (int band = 1; band < count; band++)
                {
                    values[band] = frequency.Decode(ref bits) - frequency.Offset;
                }
            }
            else
            {
                for (int band = 0; band < count; band++)
                {
                    values[band] = time.Decode(ref bits) - time.Offset;
                }
            }
        }
    }

    // sbr_noise: per noise floor, values for the noise floor bands.
    private static void Noise(ref AacBits bits, SbrBands bands, bool balance, int noises, Span<int> record)
    {
        Huffman time = balance ? Codebooks.SbrNoiseBalanceTime : Codebooks.SbrNoiseTime;
        Huffman frequency = balance ? Codebooks.SbrBalance30Frequency : Codebooks.SbrEnvelope30Frequency;
        for (int n = 0; n < noises; n++)
        {
            Span<int> values = record.Slice(SbrRecord.Noise + (n * SbrRecord.NoiseStride), SbrRecord.NoiseStride);
            if (record[SbrRecord.NoiseTime + n] == 0)
            {
                values[0] = (int)bits.Read(5);
                for (int band = 1; band < bands.Noise; band++)
                {
                    values[band] = frequency.Decode(ref bits) - frequency.Offset;
                }
            }
            else
            {
                for (int band = 0; band < bands.Noise; band++)
                {
                    values[band] = time.Decode(ref bits) - time.Offset;
                }
            }
        }
    }

    // bs_add_harmonic_flag and sbr_sinusoidal_coding: a flag per high resolution band.
    private static void Harmonics(ref AacBits bits, SbrBands bands, Span<int> record, ref bool plain)
    {
        bool present = bits.ReadBit();
        record[SbrRecord.HasHarmonics] = present ? 1 : 0;

        if (!present)
        {
            return;
        }

        plain = false;
        for (int band = 0; band < bands.High; band++)
        {
            record[SbrRecord.Harmonics + band] = bits.ReadBit() ? 1 : 0;
        }
    }

    // ps_data (ISO/IEC 14496-3 8.4.2, Table 8.9; decoded as 8.6.4): the header's modes (kept in state across frames, since a frame without a header leaves them as they were), the envelopes' borders, and the IID and
    // ICC parameters of each envelope as coded. Returns the bits read; the record then holds the modes in force. A coding the decoder does not take leaves no envelopes (-1).
    private static int ReadPs(ref AacBits bits, int left, Span<int> ps, Span<int> state)
    {
        int start = bits.Original;
        ps[SbrRecord.PsEnvelopes] = -1;
        bool header = bits.ReadBit();
        ps[SbrRecord.PsHasHeader] = header ? 1 : 0;
        if (header)
        {
            bool enableIid = bits.ReadBit();
            int iidMode = enableIid ? (int)bits.Read(3) : 0;
            bool enableIcc = bits.ReadBit();
            int iccMode = enableIcc ? (int)bits.Read(3) : 0;
            state[0] = enableIid ? 1 : 0;
            state[1] = iidMode > 2 ? iidMode - 3 : iidMode;
            state[2] = iidMode > 2 ? 1 : 0;
            state[3] = enableIcc ? 1 : 0;
            state[4] = iccMode > 2 ? iccMode - 3 : iccMode;
            state[5] = bits.ReadBit() ? 1 : 0;
            state[6] = 1;
            state[7] = iccMode > 2 ? 1 : 0;
        }

        ps[SbrRecord.PsEnableIid] = state[0];
        ps[SbrRecord.PsIidMode] = state[1];
        ps[SbrRecord.PsIidFine] = state[2];
        ps[SbrRecord.PsEnableIcc] = state[3];
        ps[SbrRecord.PsIccMode] = state[4];
        ps[SbrRecord.PsMixingB] = state[7];
        if (state[6] == 0 || state[1] > 2 || state[4] > 2)
        {
            // No header yet, or a mode above the 34 bands of mode 2 (the coding the standard marks reserved).
            return bits.Original - start;
        }

        int frameClass = bits.ReadBit() ? 1 : 0;
        int index = (int)bits.Read(2);
        int envelopes;
        ps[SbrRecord.PsBorders] = 0;
        if (frameClass == 0)
        {
            envelopes = index switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        }
        else
        {
            envelopes = index + 1;
            for (int e = 1; e <= envelopes; e++)
            {
                ps[SbrRecord.PsBorders + e] = (int)bits.Read(5) + 1;
            }
        }

        ps[SbrRecord.PsFrameClass] = frameClass;
        int iidBands = BandCount(state[1]);
        int iccBands = BandCount(state[4]);
        if (state[0] != 0)
        {
            for (int e = 0; e < envelopes; e++)
            {
                int time = (int)bits.Read(1);
                ps[SbrRecord.PsIidTime + e] = time;
                for (int b = 0; b < iidBands; b++)
                {
                    ps[SbrRecord.PsIid + (e * SbrRecord.PsStride) + b] = state[2] != 0
                        ? (time != 0 ? PsHuffman.DecodeIidFineTime(ref bits) : PsHuffman.DecodeIidFineFrequency(ref bits))
                        : (time != 0 ? PsHuffman.DecodeIidTime(ref bits) : PsHuffman.DecodeIidFrequency(ref bits));
                }
            }
        }

        if (state[3] != 0)
        {
            for (int e = 0; e < envelopes; e++)
            {
                int time = (int)bits.Read(1);
                ps[SbrRecord.PsIccTime + e] = time;
                for (int b = 0; b < iccBands; b++)
                {
                    ps[SbrRecord.PsIcc + (e * SbrRecord.PsStride) + b] = time != 0 ? PsHuffman.DecodeIccTime(ref bits) : PsHuffman.DecodeIccFrequency(ref bits);
                }
            }
        }

        // enable_ext of the last header: the extension's size and bytes. Extension 0 holds the IPD and OPD (Tables 8.10, 8.13 and 8.14), which the decoder applies; any other ID defines nothing
        // and ends what is read of the extension.
        if (state[5] != 0 && left - (bits.Original - start) >= 4)
        {
            int count = (int)bits.Read(4);
            if (count == 15)
            {
                count += (int)bits.Read(8);
            }

            int available = Math.Min(count * 8, Math.Max(0, left - (bits.Original - start)));
            int end = bits.Original + available;
            if (available > 7 && bits.Read(2) == 0 && bits.ReadBit())
            {
                ReadPhase(ref bits, envelopes, state[1] switch { 0 => 5, 1 => 11, _ => 17 }, ps);
                ps[SbrRecord.PsEnableIpdopd] = bits.Original <= end ? 1 : 0;
            }

            bits.Skip(Math.Max(0, end - bits.Original));
        }

        ps[SbrRecord.PsEnvelopes] = envelopes;
        return bits.Original - start;
    }

    // The IPD and OPD of every envelope (ipd_data and opd_data): the direction flag and the symbols of the codebook of that direction.
    private static void ReadPhase(ref AacBits bits, int envelopes, int parameters, Span<int> ps)
    {
        for (int e = 0; e < envelopes; e++)
        {
            int time = bits.ReadBit() ? 1 : 0;
            ps[SbrRecord.PsIpdTime + e] = time;
            for (int b = 0; b < parameters; b++)
            {
                ps[SbrRecord.PsIpd + (e * SbrRecord.PsPhaseStride) + b] = time != 0 ? PsHuffman.DecodeIpdTime(ref bits) : PsHuffman.DecodeIpdFrequency(ref bits);
            }

            time = bits.ReadBit() ? 1 : 0;
            ps[SbrRecord.PsOpdTime + e] = time;
            for (int b = 0; b < parameters; b++)
            {
                ps[SbrRecord.PsOpd + (e * SbrRecord.PsPhaseStride) + b] = time != 0 ? PsHuffman.DecodeOpdTime(ref bits) : PsHuffman.DecodeOpdFrequency(ref bits);
            }
        }
    }

    private static int BandCount(int mode) => mode switch { 0 => 10, 1 => 20, _ => 34 };
}
