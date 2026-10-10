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

    public int Part23Total => All.Sum(g => g.Info.Part23Length.Value);

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
            info.Scfsi = Enumerable.Range(0, channels).Select(_ => Field(4)).ToArray();
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
            int[] groups = [6 * slen1, 5 * slen1, 5 * slen2, 5 * slen2];
            int reused = gr == 1 ? Scfsi[ch].Value : 0;
            return groups.Where((_, i) => ((reused >> (3 - i)) & 1) == 0).Sum();
        }

        bool intensityRight = ch == 1 && Header.ChannelModeBits == 1 && (Header.ModeExtension & 1) == 1;
        (int[] slen, int table) = LsfSlen(g.ScalefacCompress.Value, intensityRight);
        int kind = g.IsShort ? (g.IsMixed ? 2 : 1) : 0;
        return slen.Zip(NrOfSfb[table][kind], (s, n) => s * n).Sum();
    }

    // ISO/IEC 13818-3 2.4.3.2: slen1 to slen4 and the scalefactor partition table.
    private static (int[] Slen, int Table) LsfSlen(int sfc, bool intensityRight)
    {
        if (!intensityRight)
        {
            if (sfc < 400)
            {
                return ([(sfc >> 4) / 5, (sfc >> 4) % 5, (sfc % 16) >> 2, sfc % 4], 0);
            }

            if (sfc < 500)
            {
                int x = sfc - 400;
                return ([(x >> 2) / 5, (x >> 2) % 5, x % 4, 0], 1);
            }

            int y = sfc - 500;
            return ([y / 3, y % 3, 0, 0], 2);
        }

        int i = sfc >> 1;
        if (i < 180)
        {
            return ([i / 36, i % 36 / 6, i % 36 % 6, 0], 3);
        }

        if (i < 244)
        {
            int x = i - 180;
            return ([(x % 64) >> 4, (x % 16) >> 2, x % 4, 0], 4);
        }

        int z = i - 244;
        return ([z / 3, z % 3, 0, 0], 5);
    }

    // reservoirFill: the free bytes after the previous frame's main data that main_data_begin can
    // reach (at most 511, or 255 in MPEG-2 and 2.5), the bit reservoir this frame can draw on. It
    // is not stored in the file, so its node points at main_data_begin, which it bounds.
    public Node ToNode(long offset, int reservoirFill)
    {
        Node node = new("Side Info", offset, Size(Header));
        Add(node, "Main Data Begin", MainDataBegin, $"{MainDataBegin.Value} bytes");
        Add(node, "Reservoir Fill", MainDataBegin, $"{reservoirFill} bytes");
        Add(node, "Private Bits", PrivateBits, Number(PrivateBits.Value));
        if (Scfsi.Length > 0)
        {
            Node scfsi = node.Add(Span("SCFSI", offset, Scfsi[0].Bit, Scfsi[^1].Bit + Scfsi[^1].Length));
            for (int ch = 0; ch < Scfsi.Length; ch++)
            {
                Add(scfsi, $"Channel {ch}", Scfsi[ch], Convert.ToString(Scfsi[ch].Value, 2).PadLeft(4, '0'));
            }
        }

        for (int gr = 0; gr < Granules.Length; gr++)
        {
            Layer3Granule[] row = Granules[gr];
            Node granule = node.Add(Span($"Granule {gr}", offset, row[0].Start, row[^1].End));
            for (int ch = 0; ch < row.Length; ch++)
            {
                AddGranule(granule.Add(Span($"Channel {ch}", offset, row[ch].Start, row[ch].End)), row[ch]);
            }
        }

        return node;

        void Add(Node parent, string name, BitField field, string value) =>
            parent.Add(Span(name, offset, field.Bit, field.Bit + field.Length, value));

        void AddGranule(Node parent, Layer3Granule g)
        {
            Add(parent, "Part 2 and 3 Length", g.Part23Length, $"{g.Part23Length.Value} bits");
            Add(parent, "Big Values", g.BigValues, Number(g.BigValues.Value));
            Add(parent, "Global Gain", g.GlobalGain, Number(g.GlobalGain.Value));
            Add(parent, "Scalefactor Compress", g.ScalefacCompress, Number(g.ScalefacCompress.Value));
            if (g.IsSwitched)
            {
                string type = g.BlockType.Value == 0 ? "Reserved" : BlockTypes[g.BlockType.Value];
                parent.Add(Span("Block Type", offset, g.WindowSwitching.Bit, g.BlockType.Bit + g.BlockType.Length, type));
                Add(parent, "IsMixedBlock", g.MixedBlock, Bool(g.MixedBlock.Value));
                parent.Add(Span("Table Select", offset, g.TableSelect[0].Bit, g.TableSelect[^1].Bit + 5, List(g.TableSelect)));
                parent.Add(Span("Subblock Gain", offset, g.SubblockGain[0].Bit, g.SubblockGain[^1].Bit + 3, List(g.SubblockGain)));
            }
            else
            {
                Add(parent, "Block Type", g.WindowSwitching, BlockTypes[0]);
                parent.Add(Span("Table Select", offset, g.TableSelect[0].Bit, g.TableSelect[^1].Bit + 5, List(g.TableSelect)));
                Add(parent, "Region 0 Count", g.Region0Count, Number(g.Region0Count.Value));
                Add(parent, "Region 1 Count", g.Region1Count, Number(g.Region1Count.Value));
            }

            if (Header.IsMpeg1)
            {
                Add(parent, "HasPreflag", g.Preflag, Bool(g.Preflag.Value));
            }

            Add(parent, "HasCoarseScalefactors", g.ScalefacScale, Bool(g.ScalefacScale.Value));
            Add(parent, "Count1 Table", g.Count1Table, g.Count1Table.Value == 0 ? "A" : "B");
        }
    }

    // A node covering the bytes that hold bits [startBit, endBit) of the side information.
    public static Node Span(string name, long offset, int startBit, int endBit, string? value = null) =>
        new(name, offset + (startBit / 8), ((endBit - 1) / 8) - (startBit / 8) + 1, value);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Bool(int value) => value != 0 ? "true" : "false";

    private static string List(BitField[] fields) => string.Join(", ", fields.Select(f => Number(f.Value)));
}
