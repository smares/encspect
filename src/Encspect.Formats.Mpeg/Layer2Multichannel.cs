using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// The MPEG-2 multichannel extension (ISO/IEC 13818-3) as an MPEG-1 Layer II frame carries it, in
// the bits after its samples: the multichannel header, a CRC, the composite status information,
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
    // tc_allocation and of dyn_cross_mode, and the row of Predictors (ISO/IEC 13818-3, the syntax
    // of mc_composite_status_info). The tables below follow the standard and the conformance
    // streams of ISO/IEC 13818-4; docs/development.md names the check.
    private static readonly (int Channels, int TcBits, int DynBits, int Prediction)[][] Shapes =
    [
        [(0, 0, 0, 3), (1, 2, 1, 2)],
        [(1, 2, 1, 2), (2, 3, 3, 1)],
        [(2, 2, 3, 1), (3, 3, 4, 0)],
        [(2, 0, 0, 3), (3, 2, 1, 2)],
    ];

    // The number of 2-bit predsi fields of a subband group with prediction, by dyn_cross_mode: rows
    // for 3/2, for 3/1 and 2/2, for 3/0, 2/1 and 3/0 + 2/0, and for 2/0 and 2/0 + 2/0.
    private static readonly int[][] Predictors =
    [
        [6, 4, 4, 4, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 0, 0],
        [4, 2, 2, 0, 0, 0, 0, 0],
        [2, 0],
        [0],
    ];

    // The subband group of each subband: 0 to 7 alone, 8 and 9, 10 and 11, 12 to 15, then 16 and above.
    private static readonly int[] Groups =
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

    // What T2 and T3 carry for each tc_allocation (13818-3, semantics of tc_allocation): L for a left
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

    // Whether dynamic crosstalk is on in 3/1 or 2/2, where a copied channel's base channel has two readings.
    public bool HasCrosstalkChoice { get; private set; }

    // Whether the frame reads under the flag reading, in which dyn_cross_LR names the base channel of
    // every copy in the cases the conformance streams leave open, instead of the channel's side.
    public bool UsesFlagReading { get; private set; }

    public int DematrixProcedure { get; private set; }

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

    // The speaker channels of the configuration, as the decoder outputs them (ISO/IEC 13818-3): the
    // base frame's left and right, the center (a reserved center value reads as none), the
    // surround channels or the second stereo program, whose two channels feed no speakers of the
    // first, then the LFE.
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
        if (dynamic == 1)
        {
            dynLeftRight = Field(mc._status, "IsDynamicCrosstalkFromRight", 1, Bool, Explain.McFromRight);
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
                for (int p = 0; p < (flags[g] == 1 ? Predictors[predictionRow][dynMode[g]] : 0); p++)
                {
                    bits.Read(2);
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
        mc.AllocationStart = bits.Position;

        // In 3/1 and 2/2 with dynamic crosstalk the standard's text and the ISO reference software
        // differ in which base channel a copied channel takes in some cases that no conformance
        // stream decides (docs/design/mpeg-clean-room-rules.md, section 1). The frame is read under
        // the text first; if its CRC does not hold, under the flag reading, which stays only if
        // its CRC holds.
        mc.HasCrosstalkChoice = dynamic == 1 && channels == 2 && mc.Surround != 3;
        void ReadAs(bool flagReading)
        {
            mc.UsesFlagReading = flagReading;
            mc.ReadAllocation(data, start, statusStart, headerEnd, table, channels, dynMode, secondStereo, tc, dynLeftRight, baseAllocation);
        }

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
        var bits = new BitReader(data);
        for (int skipped = 0; skipped < AllocationStart; skipped += 31)
        {
            bits.Read(Math.Min(31, AllocationStart - skipped));
        }

        var allocation = new int[5, 30];
        for (int sb = 0; sb < Layer2AudioData.SubbandCount(table); sb++)
        {
            int width = Layer2AudioData.AllocationWidth(Layer2AudioData.Levels(table, sb));
            int g = Groups[sb];
            int left = sb < baseAllocation.GetLength(1) ? baseAllocation[0, sb] : 0;
            int right = sb < baseAllocation.GetLength(1) ? baseAllocation[1, sb] : 0;

            int Transmit(int ch)
            {
                int bit = bits.Position;
                int value = bits.Read(width);
                _allocation.Add((sb, ch, bit, value));
                return value;
            }

            Allocate(this, allocation, sb, channels, dynMode[g], secondStereo[g], tc[g], dynLeftRight, left, right, Transmit);
        }

        ScfsiStart = bits.Position;
        for (int sb = 0; sb < Layer2AudioData.SubbandCount(table); sb++)
        {
            for (int ch = 2; ch < 2 + channels; ch++)
            {
                if (allocation[ch, sb] != 0)
                {
                    _scfsi.Add((sb, ch, bits.Position, bits.Read(2)));
                }
            }
        }

        SideInfoEnd = bits.Position;
        ComputedCrc = Crc16.ComputeBits(data, statusStart, SideInfoEnd - statusStart, Crc16.ComputeBits(data, start, headerEnd - start));
    }

    // Whether the channel at index i copies a base channel in a case the conformance streams leave
    // open: any copy under dyn_cross_LR 1, 3/1 with T3 carrying R under 0, and all of 2/2.
    private static bool IsUndecided(Layer2Multichannel mc, string[] carries, int i, int tc, int fromRight) =>
        mc.HasCrosstalkChoice && (fromRight == 1 || mc.Surround == 2 || (i == 1 && carries[1][tc] == 'R'));

    // Decides the allocation index of T2 to T4 in one subband, reading the fields the bitstream
    // carries in channel order; the rules are those of section 1 of
    // docs/design/mpeg-clean-room-rules.md.
    private static void Allocate(Layer2Multichannel mc, int[,] a, int sb, int channels, int mode, int second,
        int tc, int fromRight, int left, int right, Func<int, int> transmit)
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
            int value;
            if (i == 0 && mc.Center == 3 && sb >= 12)
            {
                value = 0;
            }
            else
            {
                value = plan[i] switch
                {
                    Source.Read => transmit(2 + i),
                    Source.Base => mc.UsesFlagReading && IsUndecided(mc, carries, i, tc, fromRight) ? (fromRight == 1 ? right : left) : carries[i][tc] switch
                    {
                        'L' => left,
                        'R' => right,
                        _ => fromRight == 1 ? right : left,
                    },
                    Source.T0 => left,
                    Source.T1 => right,
                    Source.T2 => a[2, sb],
                    Source.T3 => a[3, sb],
                    _ => 0,
                };
            }

            a[2 + i, sb] = value;
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
            Node field = status.Add(Span(name, fileOffset, bit, bit + length, value, description));
            if (HasCrosstalkChoice && name == "Dynamic Crosstalk Modes")
            {
                field.Add(Span("Base Channel Source", fileOffset, bit, bit + length, UsesFlagReading ? "Named by the flag" : "On the channel's side", Explain.McCrosstalkSource));
            }
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
