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

    // [surround][center present]: additional channels, tc_allocation bits, dyn_cross_mode bits and
    // the row of the predictor table.
    private static readonly (int Channels, int TcBits, int DynBits, int Prediction)[][] Shapes =
    [
        [(0, 0, 0, 5), (1, 2, 1, 2)],
        [(1, 2, 1, 4), (2, 3, 3, 1)],
        [(2, 2, 3, 3), (3, 3, 4, 0)],
        [(2, 0, 0, 5), (3, 2, 1, 2)],
    ];

    // Predictors per subband group, by predictor table row and dynamic crosstalk mode.
    private static readonly int[][] Predictors =
    [
        [6, 4, 4, 4, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 0, 0],
        [4, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [4, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
    ];

    // Subband to subband group.
    private static readonly int[] Groups = [0, 1, 2, 3, 4, 5, 6, 7, 8, 8, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11];

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
                mc._allocation.Add((sb, ch, bit, value));
                return value;
            }

            Allocate(mc, allocation, sb, channels, dynBits, dynMode[g], secondStereo[g], tc[g], dynLeftRight, left, right, Transmit);
        }

        mc.ScfsiStart = bits.Position;
        for (int sb = 0; sb < Layer2AudioData.SubbandCount(table); sb++)
        {
            for (int ch = 2; ch < 2 + channels; ch++)
            {
                if (allocation[ch, sb] != 0)
                {
                    mc._scfsi.Add((sb, ch, bits.Position, bits.Read(2)));
                }
            }
        }

        mc.SideInfoEnd = bits.Position;
        mc.ComputedCrc = Crc16.ComputeBits(data, statusStart, mc.SideInfoEnd - statusStart, Crc16.ComputeBits(data, start, headerEnd - start));
        return mc;
    }

    // ISO/IEC 13818-3: which allocations of T2 to T4 a subband transmits, which it copies from the
    // base channels or another additional channel under dynamic crosstalk, and which are absent
    // (the center above subband 11 with phantom coding).
    private static void Allocate(Layer2Multichannel mc, int[,] a, int sb, int channels, int dynBits, int mode, int second,
        int tc, int fromRight, int left, int right, Func<int, int> transmit)
    {
        bool phantomHigh = mc.Center == 3 && sb >= 12;
        if (mode == 0)
        {
            for (int ch = 2; ch < 2 + channels; ch++)
            {
                bool shared = mc.Surround == 3 && second == 1 && ((mc.Center != 0 && ch == 4) || (mc.Center == 0 && ch == 3));
                a[ch, sb] = phantomHigh && ch == 2 ? 0 : shared ? a[ch - 1, sb] : transmit(ch);
            }

            return;
        }

        switch (dynBits)
        {
            case 1:
                a[2, sb] = phantomHigh ? 0 : tc == 1 ? left : tc == 2 || fromRight == 1 ? right : left;
                if (mc.Surround == 3)
                {
                    a[3, sb] = transmit(3);
                    a[4, sb] = second == 1 ? a[3, sb] : transmit(4);
                }

                break;
            case 3:
                a[2, sb] = phantomHigh ? 0 : mode is 1 or 4 ? transmit(2) : (mc.Surround == 2 || tc != 2) && fromRight == 0 ? left : right;
                a[3, sb] = mode == 2 ? transmit(3) : mode == 4 ? a[2, sb]
                    : (mc.Surround == 2 || tc is 4 or 5 || tc < 3) && fromRight == 1 ? right : left;
                break;
            case 4:
                a[2, sb] = phantomHigh ? 0 : mode switch
                {
                    1 or 2 or 4 or 8 or 9 or 10 or 11 or 12 or 14 => transmit(2),
                    3 or 5 or 6 or 7 or 13 => tc is 1 or 7 ? left : tc is 2 or 6 || fromRight == 1 ? right : left,
                    _ => 0,
                };
                a[3, sb] = mode switch
                {
                    1 or 3 or 5 or 8 or 10 or 13 => transmit(3),
                    2 or 4 or 6 or 7 or 12 => left,
                    9 or 11 or 14 => a[2, sb],
                    _ => 0,
                };
                a[4, sb] = mode switch
                {
                    2 or 3 or 6 or 9 => transmit(4),
                    1 or 4 or 5 or 7 or 11 => right,
                    10 or 12 or 14 => a[2, sb],
                    8 or 13 => a[3, sb],
                    _ => 0,
                };
                break;
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
            node.Add(Span("Data", fileOffset, CrcBit + 16, End, $"{(End - CrcBit - 16 + 7) / 8} bytes; the side information continues in the extension bitstream",
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
