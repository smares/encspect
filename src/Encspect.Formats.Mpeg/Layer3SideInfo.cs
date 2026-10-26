using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// A side-information field: its position in bits from the start of the side information, its
// width and its value.
internal readonly record struct BitField(int Bit, int Length, int Value);

// The side information of one granule and channel (ISO/IEC 11172-3 2.4.1.7 and 2.4.2.7).
internal sealed class Layer3Granule
{
    public int Start { get; set; }

    public int End { get; set; }

    public BitField Part23Length { get; set; }

    public BitField BigValues { get; set; }

    public BitField GlobalGain { get; set; }

    public BitField ScalefacCompress { get; set; }

    public BitField WindowSwitching { get; set; }

    public BitField BlockType { get; set; }

    public BitField MixedBlock { get; set; }

    public BitField[] TableSelect { get; set; } = [];

    public BitField[] SubblockGain { get; set; } = [];

    public BitField Region0Count { get; set; }

    public BitField Region1Count { get; set; }

    public BitField Preflag { get; set; }

    public BitField ScalefacScale { get; set; }

    public BitField Count1Table { get; set; }

    public bool IsSwitched => WindowSwitching.Value == 1;

    // Block type 0 with window switching is reserved; without window switching the block is normal.
    public int BlockTypeValue => IsSwitched ? BlockType.Value : 0;

    public bool IsShort => IsSwitched && BlockType.Value == 2;

    public bool IsMixed => IsSwitched && MixedBlock.Value == 1;
}

// Layer III side information: 17 or 32 bytes in MPEG-1, 9 or 17 in MPEG-2 and 2.5, which have one
// granule per frame, an 8-bit main_data_begin, fewer private bits, no scfsi, a 9-bit
// scalefac_compress and no preflag (ISO/IEC 13818-3 2.4.1.7).
internal sealed class Layer3SideInfo
{
    private static readonly string[] BlockTypes = ["Normal", "Start", "Short", "Stop"];

    // ISO/IEC 11172-3 2.4.2.7, scalefac_compress: slen1 and slen2.
    private static readonly (int Slen1, int Slen2)[] Slen =
    [
        (0, 0), (0, 1), (0, 2), (0, 3), (3, 0), (1, 1), (1, 2), (1, 3),
        (2, 1), (2, 2), (2, 3), (3, 1), (3, 2), (3, 3), (4, 2), (4, 3),
    ];

    // ISO/IEC 13818-3 2.4.3.2, nr_of_sfb_block: scalefactors per slen for each partition table,
    // for long, short and mixed blocks.
    private static readonly int[][][] NrOfSfb =
    [
        [[6, 5, 5, 5], [9, 9, 9, 9], [6, 9, 9, 9]],
        [[6, 5, 7, 3], [9, 9, 12, 6], [6, 9, 12, 6]],
        [[11, 10, 0, 0], [18, 18, 0, 0], [15, 18, 0, 0]],
        [[7, 7, 7, 0], [12, 12, 12, 0], [6, 15, 12, 0]],
        [[6, 6, 6, 3], [12, 9, 9, 6], [6, 12, 9, 6]],
        [[8, 8, 5, 0], [15, 12, 9, 0], [6, 18, 9, 0]],
    ];

    private Layer3SideInfo(MpegHeader header) => Header = header;

    public MpegHeader Header { get; }

    public BitField MainDataBegin { get; private set; }

    public BitField PrivateBits { get; private set; }

    public BitField[] Scfsi { get; private set; } = [];

    // [granule][channel]
    public Layer3Granule[][] Granules { get; private set; } = [];

    public IEnumerable<(int Granule, int Channel, Layer3Granule Info)> All =>
        Granules.SelectMany((row, gr) => row.Select((info, ch) => (gr, ch, info)));

    public int Part23Total
    {
        get
        {
            int total = 0;
            foreach (Layer3Granule[] row in Granules)
            {
                foreach (Layer3Granule g in row)
                {
                    total += g.Part23Length.Value;
                }
            }

            return total;
        }
    }

    public int MainDataLength => (Part23Total + 7) / 8;

    public static int Size(MpegHeader header) => header.IsMpeg1 ? (header.IsMono ? 17 : 32) : (header.IsMono ? 9 : 17);

    public static Layer3SideInfo Read(byte[] data, MpegHeader header)
    {
        var bits = new BitReader(data);
        BitField Field(int length) => new(bits.Position, length, bits.Read(length));

        int channels = header.IsMono ? 1 : 2;
        var info = new Layer3SideInfo(header);
        if (header.IsMpeg1)
        {
            info.MainDataBegin = Field(9);
            info.PrivateBits = Field(channels == 1 ? 5 : 3);
            info.Scfsi = new BitField[channels];
            for (int ch = 0; ch < channels; ch++)
            {
                info.Scfsi[ch] = Field(4);
            }
        }
        else
        {
            info.MainDataBegin = Field(8);
            info.PrivateBits = Field(channels == 1 ? 1 : 2);
        }

        info.Granules = new Layer3Granule[header.IsMpeg1 ? 2 : 1][];
        for (int gr = 0; gr < info.Granules.Length; gr++)
        {
            info.Granules[gr] = new Layer3Granule[channels];
            for (int ch = 0; ch < channels; ch++)
            {
                var g = new Layer3Granule
                {
                    Start = bits.Position,
                    Part23Length = Field(12),
                    BigValues = Field(9),
                    GlobalGain = Field(8),
                    ScalefacCompress = Field(header.IsMpeg1 ? 4 : 9),
                    WindowSwitching = Field(1),
                };
                if (g.IsSwitched)
                {
                    g.BlockType = Field(2);
                    g.MixedBlock = Field(1);
                    g.TableSelect = [Field(5), Field(5)];
                    g.SubblockGain = [Field(3), Field(3), Field(3)];
                }
                else
                {
                    g.TableSelect = [Field(5), Field(5), Field(5)];
                    g.Region0Count = Field(4);
                    g.Region1Count = Field(3);
                }

                if (header.IsMpeg1)
                {
                    g.Preflag = Field(1);
                }

                g.ScalefacScale = Field(1);
                g.Count1Table = Field(1);
                g.End = bits.Position;
                info.Granules[gr][ch] = g;
            }
        }

        return info;
    }

    // The bits the scalefactors of one granule and channel take, the part2 of part2_3_length.
    public int Part2Bits(int gr, int ch)
    {
        Layer3Granule g = Granules[gr][ch];
        if (Header.IsMpeg1)
        {
            (int slen1, int slen2) = Slen[g.ScalefacCompress.Value];
            if (g.IsShort)
            {
                return ((g.IsMixed ? 17 : 18) * slen1) + (18 * slen2);
            }

            // scfsi bands 0-5, 6-10, 11-15 and 16-20; granule 1 reuses the scalefactors of granule 0
            // for each band whose scfsi bit is set.
            int reused = gr == 1 ? Scfsi[ch].Value : 0;
            return (((reused >> 3) & 1) == 0 ? 6 * slen1 : 0) + (((reused >> 2) & 1) == 0 ? 5 * slen1 : 0)
                + (((reused >> 1) & 1) == 0 ? 5 * slen2 : 0) + ((reused & 1) == 0 ? 5 * slen2 : 0);
        }

        bool intensityRight = ch == 1 && Header.ChannelModeBits == 1 && (Header.ModeExtension & 1) == 1;
        (int s1, int s2, int s3, int s4, int table) = LsfSlen(g.ScalefacCompress.Value, intensityRight);
        int[] counts = NrOfSfb[table][g.IsShort ? (g.IsMixed ? 2 : 1) : 0];
        return (s1 * counts[0]) + (s2 * counts[1]) + (s3 * counts[2]) + (s4 * counts[3]);
    }

    // ISO/IEC 13818-3 2.4.3.2: slen1 to slen4 and the scalefactor partition table.
    private static (int Slen1, int Slen2, int Slen3, int Slen4, int Table) LsfSlen(int sfc, bool intensityRight)
    {
        if (!intensityRight)
        {
            if (sfc < 400)
            {
                return ((sfc >> 4) / 5, (sfc >> 4) % 5, (sfc % 16) >> 2, sfc % 4, 0);
            }

            if (sfc < 500)
            {
                int x = sfc - 400;
                return ((x >> 2) / 5, (x >> 2) % 5, x % 4, 0, 1);
            }

            int y = sfc - 500;
            return (y / 3, y % 3, 0, 0, 2);
        }

        int i = sfc >> 1;
        if (i < 180)
        {
            return (i / 36, i % 36 / 6, i % 36 % 6, 0, 3);
        }

        if (i < 244)
        {
            int x = i - 180;
            return ((x % 64) >> 4, (x % 16) >> 2, x % 4, 0, 4);
        }

        int z = i - 244;
        return (z / 3, z % 3, 0, 0, 5);
    }

    // reservoirFill: the free bytes after the previous frame's main data that main_data_begin can
    // reach (at most 511, or 255 in MPEG-2 and 2.5), the bit reservoir this frame can draw on. It
    // is not stored in the file, so its node points at main_data_begin, which it bounds.
    public Node ToNode(long offset, int reservoirFill)
    {
        Node node = new("Side Info", offset, Size(Header)) { Kind = NodeKind.SideInfo, Description = Explain.SideInfo, Topic = Topics.GranulesAndFrames };
        Add(node, "Main Data Begin", MainDataBegin, NodeValue.Integer(MainDataBegin.Value, "bytes"), Explain.MainDataBegin, Topics.BitReservoir);
        Add(node, "Reservoir Fill", MainDataBegin, NodeValue.Integer(reservoirFill, "bytes"), Explain.ReservoirFill, Topics.BitReservoir);
        Add(node, "Private Bits", PrivateBits, NodeValue.Integer(PrivateBits.Value), Explain.PrivateBits);
        if (Scfsi.Length > 0)
        {
            Node scfsi = node.Add(Span("SCFSI", offset, Scfsi[0].Bit, Scfsi[^1].Bit + Scfsi[^1].Length, null, Explain.Scfsi, Topics.Scfsi));
            for (int ch = 0; ch < Scfsi.Length; ch++)
            {
                Add(scfsi, $"Channel {ch}", Scfsi[ch], Convert.ToString(Scfsi[ch].Value, 2).PadLeft(4, '0'), Explain.ScfsiChannel);
            }
        }

        for (int gr = 0; gr < Granules.Length; gr++)
        {
            Layer3Granule[] row = Granules[gr];
            Node granule = node.Add(Span($"Granule {gr}", offset, row[0].Start, row[^1].End, null, Explain.Granule, Topics.GranulesAndFrames));
            for (int ch = 0; ch < row.Length; ch++)
            {
                AddGranule(granule.Add(Span($"Channel {ch}", offset, row[ch].Start, row[ch].End, null, Explain.GranuleChannel)), row[ch]);
            }
        }

        return node;

        void Add(Node parent, string name, BitField field, NodeValue value, string description, string? topic = null) =>
            parent.Add(Span(name, offset, field.Bit, field.Bit + field.Length, value, description, topic));

        void AddGranule(Node parent, Layer3Granule g)
        {
            Add(parent, "Part 2 and 3 Length", g.Part23Length, NodeValue.Integer(g.Part23Length.Value, "bits"), Explain.Part23Length, Topics.HuffmanCoding);
            Add(parent, "Big Values", g.BigValues, NodeValue.Integer(g.BigValues.Value), Explain.BigValues, Topics.HuffmanCoding);
            Add(parent, "Global Gain", g.GlobalGain, NodeValue.Integer(g.GlobalGain.Value), Explain.GlobalGain, Topics.GlobalGain);
            Add(parent, "Scalefactor Compress", g.ScalefacCompress, NodeValue.Integer(g.ScalefacCompress.Value), Explain.ScalefacCompress, Topics.Scalefactors);
            if (g.IsSwitched)
            {
                parent.Add(BlockTypeSpan(offset, g, g.BlockType.Value == 0 ? "Reserved" : BlockTypes[g.BlockType.Value]));
                Add(parent, "IsMixedBlock", g.MixedBlock, NodeValue.Boolean(g.MixedBlock.Value != 0), Explain.IsMixedBlock, Topics.BlockSwitching);
                parent.Add(TableSelectSpan(offset, g, List(g.TableSelect)));
                parent.Add(Span("Subblock Gain", offset, g.SubblockGain[0].Bit, g.SubblockGain[^1].Bit + 3, List(g.SubblockGain), Explain.SubblockGain, Topics.GlobalGain));
            }
            else
            {
                parent.Add(BlockTypeSpan(offset, g, BlockTypes[0]));
                parent.Add(TableSelectSpan(offset, g, List(g.TableSelect)));
                Add(parent, "Region 0 Count", g.Region0Count, NodeValue.Integer(g.Region0Count.Value), Explain.Region0Count, Topics.HuffmanCoding);
                Add(parent, "Region 1 Count", g.Region1Count, NodeValue.Integer(g.Region1Count.Value), Explain.Region1Count, Topics.HuffmanCoding);
            }

            if (Header.IsMpeg1)
            {
                Add(parent, "HasPreflag", g.Preflag, NodeValue.Boolean(g.Preflag.Value != 0), Explain.HasPreflag, Topics.ScalefacScaleAndPreflag);
            }

            Add(parent, "HasCoarseScalefactors", g.ScalefacScale, NodeValue.Boolean(g.ScalefacScale.Value != 0), Explain.HasCoarseScalefactors, Topics.ScalefacScaleAndPreflag);
            Add(parent, "Count1 Table", g.Count1Table, g.Count1Table.Value == 0 ? "A" : "B", Explain.Count1Table, Topics.HuffmanCoding);
        }
    }

    // A node covering the bytes that hold bits [startBit, endBit) of the side information.
    public static Node Span(string name, long offset, int startBit, int endBit, NodeValue? value = null, string? description = null, string? topic = null) =>
        Node.FromBits(name, offset, startBit, endBit, value, description: description, topic: topic);

    // The block type node covers window_switching_flag and, where it is set, block_type.
    public static Node BlockTypeSpan(long offset, Layer3Granule g, NodeValue? value = null) =>
        g.IsSwitched
            ? Span("Block Type", offset, g.WindowSwitching.Bit, g.BlockType.Bit + g.BlockType.Length, value, BlockTypeText(g.BlockType.Value), Topics.BlockSwitching)
            : Span("Block Type", offset, g.WindowSwitching.Bit, g.WindowSwitching.Bit + g.WindowSwitching.Length, value, Explain.BlockNormal, Topics.BlockSwitching);

    // One node covers every table_select of a granule and channel.
    public static Node TableSelectSpan(long offset, Layer3Granule g, NodeValue? value = null) =>
        Span("Table Select", offset, g.TableSelect[0].Bit, g.TableSelect[^1].Bit + 5, value, Explain.TableSelect, Topics.HuffmanCoding);

    private static string BlockTypeText(int blockType) => blockType switch
    {
        1 => Explain.BlockStart,
        2 => Explain.BlockShort,
        3 => Explain.BlockStop,
        _ => Explain.BlockReserved,
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string List(BitField[] fields) => string.Join(", ", fields.Select(f => Number(f.Value)));
}
