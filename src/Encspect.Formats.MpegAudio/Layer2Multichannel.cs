using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.MpegAudio;

// The MPEG-2 multichannel extension (ISO/IEC 13818-3 2.5) as an MPEG-1 Layer II frame carries it, in
// the bits after its samples (mc_extension, 2.5.1.12.1, mc_audio_data, 2.5.1.17): the multichannel header, a CRC, the composite status information,
// the LFE allocation, the bit allocation of the additional transmission channels T2 to T4 and
// their scfsi, which the CRC covers, then their scalefactors and samples and any multilingual
// channels. With an extension bitstream the data continues in that separate stream, after the
// base frame's bits minus n_ad_bytes of MPEG-1 ancillary data at its end, so side information
// that does not fit the base frame cannot be checked here. Read only for a stereo base frame.
internal sealed class Layer2Multichannel
{
    private static readonly string[] CenterNames = ["None", "Present", "Reserved", "Phantom"];
    private static readonly string[] SurroundNames = ["None", "Mono", "Stereo", "Second stereo programme"];

    // By surround, then whether a center is coded: the additional channels, the bits of
    // tc_allocation and of dyn_cross_mode, and the row of Predictors (ISO/IEC 13818-3 2.5.1.15 and
    // 2.5.2.15, mc_composite_status_info). The tables below follow the standard and the conformance
    // streams of ISO/IEC 13818-4; docs/development.md names the check.
    private static readonly (int Channels, int TcBits, int DynBits, int Prediction)[][] Shapes =
    [
        [(0, 0, 0, 3), (1, 2, 1, 2)],
        [(1, 2, 1, 2), (2, 3, 3, 1)],
        [(2, 2, 3, 1), (3, 3, 4, 0)],
        [(2, 0, 0, 3), (3, 2, 1, 2)],
    ];

    // The number of 2-bit predsi fields (npred, 13818-3 2.5.2.15) of a subband group with prediction, by dyn_cross_mode: rows
    // for 3/2, for 3/1 and 2/2, for 3/0, 2/1 and 3/0 + 2/0, and for 2/0 and 2/0 + 2/0.
    private static readonly int[][] Predictors =
    [
        [6, 4, 4, 4, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 0, 0],
        [4, 2, 2, 0, 0, 0, 0, 0],
        [2, 0],
        [0],
    ];

    // The subband group of each subband (13818-3 2.5.2.15, tc_sbgr_select): 0 to 7 alone, 8 and 9, 10 and 11, 12 to 15, then 16 and above.
    internal static readonly int[] Groups =
        [.. Enumerable.Range(0, 30).Select(sb => sb < 8 ? sb : sb < 10 ? 8 : sb < 12 ? 9 : sb < 16 ? 10 : 11)];

    // How a mode fills an additional channel: read from the bitstream, copied from a base channel
    // chosen by what the channel carries, copied from T0, T1, T2 or T3, or zero.
    private enum Source
    {
        Read,
        Base,
        T0,
        T1,
        T2,
        T3,
        Zero,
    }

    private const Source R = Source.Read;
    private const Source B = Source.Base;

    // 3/2, by dyn_cross_mode: T2, T3, T4.
    private static readonly Source[][] Modes32 =
    [
        [R, R, R],
        [R, R, Source.T1],
        [R, Source.T0, R],
        [B, R, R],
        [R, Source.T0, Source.T1],
        [B, R, Source.T1],
        [B, Source.T0, R],
        [B, Source.T0, Source.T1],
        [R, R, Source.T3],
        [R, Source.T2, R],
        [R, R, Source.T2],
        [R, Source.T2, Source.T1],
        [R, Source.T0, Source.T2],
        [B, R, Source.T3],
        [R, Source.T2, Source.T2],
        [Source.Zero, Source.Zero, Source.Zero],
    ];

    // 3/1 and 2/2, by dyn_cross_mode: T2, T3.
    private static readonly Source[][] Modes31 =
    [
        [R, R],
        [R, B],
        [B, R],
        [B, B],
        [R, Source.T2],
        [B, B],
        [B, B],
        [B, B],
    ];

    private static readonly string[] CarriesCenterSurround = ["CLRCCCRL"];
    private static readonly string[] CarriesCenterMonoSurround = ["CLRCCL--", "SSSLRR--"];
    private static readonly string[] CarriesStereoSurround = ["LLLL", "RRRR"];
    private static readonly string[] CarriesMonoSurround = ["SLR-"];
    private static readonly string[] CarriesOther = ["CLR-"];

    // What T2 and T3 carry for each tc_allocation (13818-3 2.5.2.15, tc_allocation): L for a left
    // or left surround channel, R for the right ones, any other letter for a channel that is neither
    // (center, surround, not assigned).
    private static string[] Carries(int surround, bool centerCoded) => (surround, centerCoded) switch
    {
        (2, true) => CarriesCenterSurround,
        (1, true) => CarriesCenterMonoSurround,
        (2, false) => CarriesStereoSurround,
        (1, false) => CarriesMonoSurround,
        _ => CarriesOther,
    };

    private static readonly Source[] PlanSecondCenterBase = [B, R, Source.T3];
    private static readonly Source[] PlanSecondCenterRead = [R, R, Source.T3];
    private static readonly Source[] PlanSecondCenterBaseUncopied = [B, R, R];
    private static readonly Source[] PlanSecondCenterUncopied = [R, R, R];
    private static readonly Source[] PlanSecondCopy = [R, Source.T2];
    private static readonly Source[] PlanSecondUncopied = [R, R];
    private static readonly Source[] PlanOneBase = [B];
    private static readonly Source[] PlanOneRead = [R];

    private readonly List<(string Name, int Bit, int Length, NodeValue Value, string Description)> _header = [];
    private readonly List<(string Name, int Bit, int Length, NodeValue Value, string Description)> _status = [];
    private readonly List<(int Subband, int Channel, int Bit, int Index)> _allocation = [];
    private readonly List<(int Subband, int Channel, int Bit, int Value)> _scfsi = [];
    private int[,] _indexes = new int[5, 30];
    private int[,] _sources = new int[5, 30];
    private int[,] _scfsiValues = new int[5, 30];

    private Layer2Multichannel()
    {
    }

    public int Start { get; private set; }

    public int End { get; private set; }

    public bool HasExtension { get; private set; }

    public int AncillaryBytes { get; private set; }

    public int Center { get; private set; }

    public int Surround { get; private set; }

    public bool HasLfe { get; private set; }

    // Whether dynamic crosstalk is on in 3/1 or 2/2, the configurations where the reference software's reading can differ from the text's.
    public bool HasCrosstalkChoice { get; private set; }

    // Whether the frame reads under the reference software's reading instead of ISO/IEC 13818-3 2.5.2.15: dyn_cross_LR names the base channel of
    // every copy in the cases the conformance streams leave open. Set only where the text's reading fails the multichannel CRC and this one holds it.
    public bool UsesFlagReading { get; private set; }

    public int DematrixProcedure { get; private set; }

    // What the decoder needs of the side information (docs/design/lossy-decoders.md, "MPEG-2 multichannel"): the allocation table, the number of additional
    // channels T2 to T4, the composite status information per subband group and the predictor selection of groups 0 to 7 (by predictor, 2 bits each).
    public int Table { get; private set; }

    public int AdditionalChannels { get; private set; }

    public int Subbands => Layer2AudioData.SubbandCount(Table);

    public bool HasDynamicCrosstalk { get; private set; }

    public int DynamicLeftRight { get; private set; }

    public int[] ChannelAllocation { get; private set; } = new int[12];

    public int[] DynamicMode { get; private set; } = new int[12];

    public int[] SecondStereo { get; private set; } = new int[12];

    public bool HasPrediction { get; private set; }

    public bool[] HasPredictionInGroup { get; } = new bool[8];

    public int[] PredictorCount { get; } = new int[8];

    public int[][] PredictorSelect { get; } = Enumerable.Range(0, 8).Select(_ => new int[6]).ToArray();

    // The allocation index of channel ch (0 and 1 the base channels, 2 to 4 the additional ones) in a subband, after copying, and the channel whose quantized
    // samples it takes: itself where they are transmitted, another channel where dynamic crosstalk copies them, -1 where it has none.
    public int AllocationIndex(int channel, int subband) => _indexes[channel, subband];

    public int SampleSource(int channel, int subband) => _sources[channel, subband];

    public int ScfsiValue(int channel, int subband) => _scfsiValues[channel, subband];

    // What ReadData read, in bits from the frame's start: the end of the multichannel data and whether it lies within the base frame.
    public int DataEnd { get; private set; }

    public bool DataFits => DataEnd <= End;

    // Prediction: the delay compensation and the coefficients (already (value - 127) / 32) of predictor px of a subband group, three taps each.
    public int[,] PredictionDelay { get; } = new int[8, 6];

    public float[,,] PredictionCoefficient { get; } = new float[8, 6, 3];

    // The scalefactor index of the LFE channel, and of each additional channel in each subband for each of three parts, plus one (0 for none).
    public int LfeScalefactor { get; private set; }

    public byte[,,] Scalefactor { get; } = new byte[3, 30, 3];

    public int MultilingualChannels { get; private set; }

    // The header values that stay the same from frame to frame in a multichannel stream.
    public (int, int, int, bool, int, int) HeaderKey => (AncillaryBytes, Center, Surround, HasLfe, DematrixProcedure, MultilingualChannels);

    public int CrcBit { get; private set; }

    public int StoredCrc { get; private set; }

    public int ComputedCrc { get; private set; }

    public int SideInfoEnd { get; private set; }

    public int LfeBit { get; private set; }

    public int LfeAllocation { get; private set; }

    public int AllocationStart { get; private set; }

    public int ScfsiStart { get; private set; }

    // Whether the multichannel header and its CRC lie within the base frame.
    public bool HeaderFits { get; private set; }

    // Whether n_ad_bytes declares more MPEG-1 ancillary data than the frame holds after the
    // multichannel header and CRC; the multichannel data then ends after the CRC.
    public bool HasAncillaryOverflow { get; private set; }

    // Whether the side information lies within the base frame, so its CRC can be checked.
    public bool Fits => SideInfoEnd <= End && !HasAncillaryOverflow;

    public bool IsCrcValid => Fits && StoredCrc == ComputedCrc;

    public string Configuration
    {
        get
        {
            int front = Center is 1 or 3 ? 3 : 2;
            string text = Surround == 3 ? $"{front}/0 + 2/0" : $"{front}/{Surround}";
            text += Center == 3 ? " (phantom center)" : string.Empty;
            return text + (HasLfe ? " + LFE" : string.Empty);
        }
    }

    // The speaker channels of the configuration, in the order Encspect reports them (the standard
    // fixes none): the base frame's left and right, the center (a reserved center value reads as
    // none, 13818-3 2.5.2.13), the surround channels or the second stereo program, whose two
    // channels feed no speakers of the first, then the LFE.
    public IReadOnlyList<Speaker> Layout
    {
        get
        {
            var speakers = new List<Speaker> { Speaker.FrontLeft, Speaker.FrontRight };
            if (Center is 1 or 3)
            {
                speakers.Add(Speaker.FrontCenter);
            }

            speakers.AddRange(Surround switch
            {
                1 => [Speaker.BackCenter],
                2 => [Speaker.BackLeft, Speaker.BackRight],
                3 => [Speaker.Discrete, Speaker.Discrete],
                _ => [],
            });
            if (HasLfe)
            {
                speakers.Add(Speaker.LowFrequency);
            }

            return speakers;
        }
    }

    // What decides the stream's channels: the configuration and the multilingual channels.
    public (bool, int, bool, int) ChannelKey => (Center is 1 or 3, Surround, HasLfe, MultilingualChannels);

    // data holds the bits from start to end (in bits); baseAllocation the allocation index of the
    // base frame's two channels per subband; rate selects the allocation table.
    public static Layer2Multichannel Read(byte[] data, int start, int end, int[,] baseAllocation, int rate)
    {
        var mc = new Layer2Multichannel { Start = start };
        var bits = new BitReader(data);
        bits.Read(start % 8);
        for (int i = 0; i < start / 8; i++)
        {
            bits.Read(8);
        }

        int Field(List<(string, int, int, NodeValue, string)> list, string name, int length, Func<int, NodeValue> describe, string description)
        {
            int bit = bits.Position;
            int value = bits.Read(length);
            list.Add((name, bit, length, describe(value), description));
            return value;
        }

        mc.HasExtension = Field(mc._header, "HasExtensionBitstream", 1, Bool, Explain.McHasExtension) == 1;
        if (mc.HasExtension)
        {
            mc.AncillaryBytes = Field(mc._header, "Ancillary Bytes", 8, Integer, Explain.McAncillaryBytes);
        }

        mc.Center = Field(mc._header, "Center", 2, v => CenterNames[v], Explain.McCenter);
        mc.Surround = Field(mc._header, "Surround", 2, v => SurroundNames[v], Explain.McSurround);
        mc.HasLfe = Field(mc._header, "HasLfe", 1, Bool, Explain.McHasLfe) == 1;
        Field(mc._header, "Audio Mix", 1, Integer, Explain.McAudioMix);
        mc.DematrixProcedure = Field(mc._header, "Dematrix Procedure", 2, Integer, Explain.McDematrix);
        mc.MultilingualChannels = Field(mc._header, "Multilingual Channels", 3, Integer, Explain.McMultilingualChannels);
        Field(mc._header, "Multilingual Sample Rate", 1, v => v == 0 ? "Full" : "Half", Explain.McMultilingualRate);
        Field(mc._header, "Multilingual Layer", 1, v => v == 0 ? "II" : "I", Explain.McMultilingualLayer);
        Field(mc._header, "HasCopyrightIdentification", 1, Bool, Explain.McCopyrightBit);
        Field(mc._header, "IsCopyrightIdentificationStart", 1, Bool, Explain.McCopyrightStart);
        int headerEnd = bits.Position;
        mc.CrcBit = bits.Position;
        mc.StoredCrc = bits.Read(16);
        int statusStart = bits.Position;
        mc.HeaderFits = statusStart <= end;
        mc.HasAncillaryOverflow = end - (8 * mc.AncillaryBytes) < statusStart;
        mc.End = Math.Max(end - (8 * mc.AncillaryBytes), Math.Min(statusStart, end));

        (int channels, int tcBits, int dynBits, int predictionRow) = Shapes[mc.Surround][mc.Center & 1];
        mc.AdditionalChannels = channels;
        int tcSelect = bits.Read(1);
        int dynamic = Field(mc._status, "HasDynamicCrosstalk", 1, Bool, Explain.McHasDynamicCrosstalk);
        int prediction = Field(mc._status, "HasPrediction", 1, Bool, Explain.McHasPrediction);
        int tcBit = bits.Position;
        int[] tc = tcSelect == 1 ? Enumerable.Repeat(bits.Read(tcBits), 12).ToArray() : Enumerable.Range(0, 12).Select(_ => bits.Read(tcBits)).ToArray();
        mc._status.Add(("Channel Allocation", tcBit - 3, bits.Position - tcBit + 3,
            tcSelect == 1 ? Integer(tc[0]) : string.Join(", ", tc.Select(Number)), Explain.McChannelAllocation));

        int[] dynMode = new int[12];
        int[] secondStereo = new int[12];
        int dynLeftRight = 0;
        mc.ChannelAllocation = tc;
        mc.DynamicMode = dynMode;
        mc.SecondStereo = secondStereo;
        mc.HasDynamicCrosstalk = dynamic == 1;
        mc.HasPrediction = prediction == 1;
        if (dynamic == 1)
        {
            dynLeftRight = Field(mc._status, "IsDynamicCrosstalkFromRight", 1, Bool, Explain.McFromRight);
            mc.DynamicLeftRight = dynLeftRight;
            int bit = bits.Position;
            for (int g = 0; g < 12; g++)
            {
                dynMode[g] = bits.Read(dynBits);
                if (mc.Surround == 3)
                {
                    secondStereo[g] = bits.Read(1);
                }
            }

            mc._status.Add(("Dynamic Crosstalk Modes", bit, bits.Position - bit, string.Join(", ", dynMode.Select(Number)), Explain.McDynamicModes));
        }

        if (prediction == 1)
        {
            int bit = bits.Position;
            int[] flags = new int[8];
            for (int g = 0; g < 8; g++)
            {
                flags[g] = bits.Read(1);
                mc.HasPredictionInGroup[g] = flags[g] == 1;
                mc.PredictorCount[g] = flags[g] == 1 ? Predictors[predictionRow][dynMode[g]] : 0;
                for (int p = 0; p < mc.PredictorCount[g]; p++)
                {
                    mc.PredictorSelect[g][p] = bits.Read(2);
                }
            }

            mc._status.Add(("Prediction", bit, bits.Position - bit, string.Join(", ", flags.Select(Number)), Explain.McPrediction));
        }

        if (mc.HasLfe)
        {
            mc.LfeBit = bits.Position;
            mc.LfeAllocation = bits.Read(4);
        }

        int table = rate == 48000 ? 0 : 1;
        mc.Table = table;
        mc.AllocationStart = bits.Position;

        mc.HasCrosstalkChoice = dynamic == 1 && channels == 2 && mc.Surround != 3;
        void ReadAs(bool flagReading)
        {
            mc.UsesFlagReading = flagReading;
            mc.ReadAllocation(data, start, statusStart, headerEnd, table, channels, dynMode, secondStereo, tc, dynLeftRight, baseAllocation);
        }

        // The text's reading (13818-3 2.5.2.15) first; the reference software's only where the text's fails the CRC and it holds.
        ReadAs(false);
        if (mc.HasCrosstalkChoice && mc.StoredCrc != mc.ComputedCrc)
        {
            ReadAs(true);
            if (mc.StoredCrc != mc.ComputedCrc)
            {
                ReadAs(false);
            }
        }

        return mc;
    }

    private void ReadAllocation(byte[] data, int start, int statusStart, int headerEnd, int table, int channels, int[] dynMode,
        int[] secondStereo, int[] tc, int dynLeftRight, int[,] baseAllocation)
    {
        _allocation.Clear();
        _scfsi.Clear();
        Array.Clear(_scfsiValues);
        var bits = new BitReader(data);
        for (int skipped = 0; skipped < AllocationStart; skipped += 31)
        {
            bits.Read(Math.Min(31, AllocationStart - skipped));
        }

        var allocation = new int[5, 30];
        var sources = new int[5, 30];
        for (int sb = 0; sb < 30; sb++)
        {
            sources[1, sb] = 1;
            sources[2, sb] = sources[3, sb] = sources[4, sb] = -1;
        }

        for (int sb = 0; sb < Layer2AudioData.SubbandCount(table); sb++)
        {
            int width = Layer2AudioData.AllocationWidth(Layer2AudioData.Levels(table, sb));
            int g = Groups[sb];
            int left = sb < baseAllocation.GetLength(1) ? baseAllocation[0, sb] : 0;
            int right = sb < baseAllocation.GetLength(1) ? baseAllocation[1, sb] : 0;
            allocation[0, sb] = left;
            allocation[1, sb] = right;

            int Transmit(int ch)
            {
                int bit = bits.Position;
                int value = bits.Read(width);
                _allocation.Add((sb, ch, bit, value));
                return value;
            }

            Allocate(this, allocation, sources, sb, channels, dynMode[g], secondStereo[g], tc[g], dynLeftRight, Transmit);
        }

        _indexes = allocation;
        _sources = sources;

        ScfsiStart = bits.Position;
        for (int sb = 0; sb < Layer2AudioData.SubbandCount(table); sb++)
        {
            for (int ch = 2; ch < 2 + channels; ch++)
            {
                if (allocation[ch, sb] != 0)
                {
                    int value = bits.Read(2);
                    _scfsiValues[ch, sb] = value;
                    _scfsi.Add((sb, ch, bits.Position - 2, value));
                }
            }
        }

        SideInfoEnd = bits.Position;
        ComputedCrc = Crc16.ComputeBits(data, statusStart, SideInfoEnd - statusStart, Crc16.ComputeBits(data, start, headerEnd - start));
    }

    // Whether the channel at index i copies a base channel in a case the conformance streams of ISO/IEC 13818-4 leave open (they decide the others for the text's
    // reading): any copy under dyn_cross_LR 1, 3/1 with T3 carrying R under 0, and all of 2/2.
    private static bool IsUndecided(Layer2Multichannel mc, string[] carries, int i, int tc, int fromRight) =>
        mc.HasCrosstalkChoice && (fromRight == 1 || mc.Surround == 2 || (i == 1 && carries[1][tc] == 'R'));

    // Decides the allocation index of T2 to T4 in one subband, reading the fields the bitstream
    // carries in channel order; the rules are those of section 1 of
    // docs/design/mpeg-clean-room-rules.md. a holds the base channels' indexes in rows 0 and 1;
    // sources gets the channel whose quantized samples each additional channel takes.
    private static void Allocate(Layer2Multichannel mc, int[,] a, int[,] sources, int sb, int channels, int mode, int second,
        int tc, int fromRight, Func<int, int> transmit)
    {
        bool centerCoded = mc.Center is 1 or 3;
        Source[] plan;
        if (mc.Surround == 3)
        {
            // The second programme occupies the last two channels; its right channel may copy its left.
            plan = centerCoded
                ? (second == 1 ? (mode == 1 ? PlanSecondCenterBase : PlanSecondCenterRead) : (mode == 1 ? PlanSecondCenterBaseUncopied : PlanSecondCenterUncopied))
                : (second == 1 ? PlanSecondCopy : PlanSecondUncopied);
        }
        else if (channels == 3)
        {
            plan = Modes32[mode];
        }
        else if (channels == 2)
        {
            plan = Modes31[mode];
        }
        else
        {
            plan = mode == 1 ? PlanOneBase : PlanOneRead;
        }

        string[] carries = Carries(mc.Surround, centerCoded);
        for (int i = 0; i < channels; i++)
        {
            int from = -1;
            if (i == 0 && mc.Center == 3 && sb >= 12)
            {
                a[2, sb] = 0;
                continue;
            }

            switch (plan[i])
            {
                case Source.Read:
                    a[2 + i, sb] = transmit(2 + i);
                    sources[2 + i, sb] = 2 + i;
                    continue;
                case Source.Base:
                    from = mc.UsesFlagReading && IsUndecided(mc, carries, i, tc, fromRight) ? fromRight : carries[i][tc] switch
                    {
                        'L' => 0,
                        'R' => 1,
                        _ => fromRight,
                    };
                    break;
                case Source.T0:
                    from = 0;
                    break;
                case Source.T1:
                    from = 1;
                    break;
                case Source.T2:
                    from = 2;
                    break;
                case Source.T3:
                    from = 3;
                    break;
            }

            a[2 + i, sb] = from < 0 ? 0 : a[from, sb];
            sources[2 + i, sb] = from < 0 ? -1 : sources[from, sb];
        }
    }

    // The quantizer of an allocation's level count as the Layer I and II decoder numbers them: 2 to 16 the bits of each sample, 17 to 19 the grouped codes of 3, 5 and 9 levels.
    public static int Quantizer(int levels) => levels switch
    {
        3 => 17,
        5 => 18,
        9 => 19,
        _ => 32 - int.LeadingZeroCount(levels),
    };

    // Reads what follows the side information (ISO/IEC 13818-3 2.5.1.17, 2.5.2.17): the predictors, the LFE and the scalefactors, and where the samples end. With planes
    // (one per channel of stride entries, as Layer12Scale.ReadSamples lays them out: channels 2 to 4 the additional ones, 5 the LFE's twelve samples) it also reads
    // the sample codes, as the signed integers the decoder scales, provided that the data fit the base frame.
    public void ReadData(byte[] data, Span<int> planes = default, int stride = 0)
    {
        var bits = new BitReader(data);
        for (int skipped = 0; skipped < SideInfoEnd; skipped += 31)
        {
            bits.Read(Math.Min(31, SideInfoEnd - skipped));
        }

        Array.Clear(PredictionDelay);
        Array.Clear(PredictionCoefficient);
        Array.Clear(Scalefactor);
        LfeScalefactor = 0;
        if (HasPrediction)
        {
            for (int g = 0; g < 8; g++)
            {
                for (int px = 0; px < PredictorCount[g]; px++)
                {
                    int taps = PredictorSelect[g][px];
                    if (taps == 0)
                    {
                        continue;
                    }

                    PredictionDelay[g, px] = bits.Read(3);
                    for (int pci = 0; pci < taps; pci++)
                    {
                        PredictionCoefficient[g, px, pci] = (bits.Read(8) - 127) / 32f;
                    }
                }
            }
        }

        if (LfeAllocation != 0)
        {
            LfeScalefactor = bits.Read(6);
        }

        int limit = Subbands;
        for (int sb = 0; sb < limit; sb++)
        {
            for (int ch = 0; ch < AdditionalChannels; ch++)
            {
                if (_indexes[2 + ch, sb] == 0)
                {
                    continue;
                }

                int mask = 4 + ((19 >> _scfsiValues[2 + ch, sb]) & 3);
                int current = 0;
                for (int part = 0, m = 4; m != 0; m >>= 1, part++)
                {
                    if ((mask & m) != 0)
                    {
                        current = bits.Read(6) + 1;
                    }

                    Scalefactor[ch, sb, part] = (byte)current;
                }
            }
        }

        int start = bits.Position;
        long total = LfeAllocation != 0 ? 12 * (LfeAllocation + 1) : 0;
        for (int sb = 0; sb < limit; sb++)
        {
            for (int ch = 0; ch < AdditionalChannels; ch++)
            {
                if (_indexes[2 + ch, sb] != 0 && _sources[2 + ch, sb] == 2 + ch)
                {
                    total += 12 * Layer2AudioData.SampleBits(Layer2AudioData.Levels(Table, sb)[_indexes[2 + ch, sb]]);
                }
            }
        }

        DataEnd = (int)Math.Min(int.MaxValue, start + total);
        if (planes.IsEmpty || !DataFits || bits.IsOverrun)
        {
            return;
        }

        // Each of the twelve granules holds three samples of each channel and one LFE sample.
        for (int gr = 0; gr < 12; gr++)
        {
            if (LfeAllocation != 0)
            {
                int width = LfeAllocation + 1;
                planes[(5 * stride) + gr] = bits.Read(width) - ((1 << (width - 1)) - 1);
            }

            for (int sb = 0; sb < limit; sb++)
            {
                for (int ch = 0; ch < AdditionalChannels; ch++)
                {
                    int index = _indexes[2 + ch, sb];
                    if (index == 0 || _sources[2 + ch, sb] != 2 + ch)
                    {
                        continue;
                    }

                    int levels = Layer2AudioData.Levels(Table, sb)[index];
                    Span<int> destination = planes.Slice(((2 + ch) * stride) + ((((gr / 4) * 32) + sb) * 12) + ((gr % 4) * 3), 3);
                    if (levels is 3 or 5 or 9)
                    {
                        int code = bits.Read(Layer2AudioData.SampleBits(levels));
                        for (int k = 0; k < 3; k++, code /= levels)
                        {
                            destination[k] = (code % levels) - (levels / 2);
                        }
                    }
                    else
                    {
                        int width = Layer2AudioData.SampleBits(levels) / 3;
                        for (int k = 0; k < 3; k++)
                        {
                            destination[k] = bits.Read(width) - ((1 << (width - 1)) - 1);
                        }
                    }
                }
            }
        }
    }

    // fileOffset is where data starts in the file.
    public Node ToNode(long fileOffset, int rate)
    {
        Node node = Span("Multichannel", fileOffset, Start, End, Configuration, Explain.Multichannel, Topics.Multichannel);
        Node header = node.Add(Span("Header", fileOffset, Start, CrcBit, null, Explain.McHeader));
        foreach ((string name, int bit, int length, NodeValue value, string description) in _header)
        {
            header.Add(Span(name, fileOffset, bit, bit + length, value, description));
        }

        Node crc = node.Add(Span("CRC", fileOffset, CrcBit, CrcBit + 16, NodeValue.Hex(StoredCrc, 4), Explain.McCrc, Topics.Crc));
        if (HasAncillaryOverflow)
        {
            return node;
        }

        if (!Fits)
        {
            node.Add(Span("Data", fileOffset, CrcBit + 16, End, $"{Counts.Of((End - CrcBit - 16 + 7) / 8, "byte", "bytes")}; the side information continues in the extension bitstream",
                Explain.McDataContinued));
            return node;
        }

        crc.Add(Span("Computed", fileOffset, CrcBit, CrcBit + 16, NodeValue.Hex(ComputedCrc, 4), Explain.ComputedCrc));
        Node status = node.Add(Span("Composite Status", fileOffset, CrcBit + 16, HasLfe ? LfeBit : AllocationStart, null, Explain.McStatus));
        foreach ((string name, int bit, int length, NodeValue value, string description) in _status.OrderBy(s => s.Bit))
        {
            status.Add(Span(name, fileOffset, bit, bit + length, value, description));
        }

        if (HasLfe)
        {
            node.Add(Span("LFE Allocation", fileOffset, LfeBit, LfeBit + 4, LfeAllocation == 0 ? "None" : NodeValue.Integer(LfeAllocation + 1, "bits"), Explain.McLfeAllocation));
        }

        int table = rate == 48000 ? 0 : 1;
        Node allocation = node.Add(Span("Bit Allocation", fileOffset, AllocationStart, ScfsiStart, null, Explain.McAllocation));
        foreach (IGrouping<int, (int Subband, int Channel, int Bit, int Index)> subband in _allocation.GroupBy(a => a.Subband))
        {
            int width = Layer2AudioData.AllocationWidth(Layer2AudioData.Levels(table, subband.Key));
            Node group = allocation.Add(Span($"Subband {subband.Key}", fileOffset, subband.First().Bit, subband.Last().Bit + width, null, Explain.Subband));
            foreach ((int _, int channel, int bit, int index) in subband)
            {
                int levels = Layer2AudioData.Levels(table, subband.Key)[index];
                group.Add(Span($"Channel {channel}", fileOffset, bit, bit + width, levels == 0 ? "None" : NodeValue.Integer(levels, "levels"), Explain.McChannelLevels));
            }
        }

        Node scfsi = node.Add(Span("SCFSI", fileOffset, ScfsiStart, SideInfoEnd, null, Explain.McScfsi));
        foreach (IGrouping<int, (int Subband, int Channel, int Bit, int Value)> subband in _scfsi.GroupBy(s => s.Subband))
        {
            Node group = scfsi.Add(Span($"Subband {subband.Key}", fileOffset, subband.First().Bit, subband.Last().Bit + 2, null, Explain.Subband));
            foreach ((int _, int channel, int bit, int value) in subband)
            {
                group.Add(Span($"Channel {channel}", fileOffset, bit, bit + 2, Integer(value), Explain.McScfsiChannel));
            }
        }

        int dataStart = (SideInfoEnd + 7) / 8;
        node.Add(new Node("Data", fileOffset + dataStart, Math.Max((End / 8) - dataStart, 0), NodeValue.Integer(Math.Max((End / 8) - dataStart, 0), "bytes"))
        {
            Description = Explain.McData,
        });
        return node;
    }

    private static Node Span(string name, long fileOffset, int startBit, int endBit, NodeValue? value, string description, string? topic = null) =>
        Layer2AudioData.Span(name, fileOffset, startBit, endBit, value, description: description, topic: topic);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static NodeValue Integer(int value) => NodeValue.Integer(value);

    private static NodeValue Bool(int value) => NodeValue.Boolean(value != 0);
}
