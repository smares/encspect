using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Encspect.Formats.Mpeg;

// Bits of a byte span, most significant first, read past its end as zeros. Position counts bits from the start. Read stops at limit like minimp3's get_bits
// (a field that ends past the limit reads as 0); Peek and Skip are for the Huffman codes, which may run past a granule's end and are checked afterward.
internal ref struct BitSource(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private readonly int _limit = data.Length * 8;

    public int Position { get; set; }

    public readonly int Limit => _limit;

    public uint Read(int count)
    {
        int start = Position;
        Position += count;
        return count == 0 || Position > _limit ? 0 : Extract(start, count);
    }

    // The next count bits (at most 25) without consuming them.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint Peek(int count) => Extract(Position, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int count) => Position += count;

    private readonly uint Extract(int start, int count)
    {
        int first = start >> 3;
        ulong window;
        if (first >= 0 && first + 8 <= _data.Length)
        {
            window = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(first, 8));
        }
        else
        {
            window = 0;
            for (int i = 0; i < 8; i++)
            {
                int index = first + i;
                window = (window << 8) | (index >= 0 && index < _data.Length ? _data[index] : 0u);
            }
        }

        return (uint)((window << (start & 7)) >> (64 - count));
    }
}

// One granule and channel of Layer III side information in the form the entropy pass and the decoder read it (minimp3's L3_gr_info_t,
// ISO/IEC 11172-3 2.4.2.7 and ISO/IEC 13818-3 2.4.3.2).
internal struct GranuleSyntax
{
    public int Part23Length;
    public int BigValues;
    public int GlobalGain;
    public int ScalefacCompress;
    public int BlockType;
    public int MixedBlock;
    public int LongBandCount;
    public int ShortBandCount;
    public int Preflag;
    public int ScalefacScale;
    public int Count1Table;
    public int Scfsi;

    // The first entry of the band widths of this granule's rows in Layer3Tables (a row of the long, short or mixed table), and the table kind.
    public int BandRow;
    public BandKind Bands;

    public int Table0, Table1, Table2;
    public int Region0, Region1, Region2;
    public int SubblockGain0, SubblockGain1, SubblockGain2;

    public readonly int Table(int region) => region switch { 0 => Table0, 1 => Table1, _ => Table2 };

    public readonly int Region(int region) => region switch { 0 => Region0, 1 => Region1, _ => Region2 };

    public readonly int SubblockGain(int window) => window switch { 0 => SubblockGain0, 1 => SubblockGain1, _ => SubblockGain2 };

    public readonly bool IsShort => BlockType == Layer3Syntax.ShortBlockType;
}

internal enum BandKind
{
    Long,
    Short,
    Mixed,
}

// Why a frame cannot be walked, or what the walk found damaged in it.
[Flags]
internal enum WalkProblems
{
    None = 0,

    // big_values above 288 or the reserved block type 0 with window switching: the frame's side information breaks (minimp3 refuses it).
    Unreadable = 1,

    // The granules' part2_3_length together exceed the data the frame and the reservoir hold (minimp3 refuses it too).
    PartsExceedData = 2,

    // A granule's Huffman-coded values run past its part2_3_length.
    HuffmanOverrun = 4,
}

// The facts of one walked frame.
internal struct WalkFacts
{
    public WalkProblems Problems;

    // The highest line, in the 576-line scale, that the big-values and count1 regions of any granule and channel cover (0 where none codes a line).
    public int CodedLines;

    // Granules and channels whose Huffman data ran past their part2_3_length.
    public int Overruns;
}

// The syntax of a Layer III frame past its header: side information, scalefactors and the Huffman-coded spectrum, read once by the entropy pass of
// every analysis. With buffers it also stores what it reads (the layout is in LentLayout), so the decoder reads no coded bit itself.
// The code follows minimp3 (CC0 1.0, third_party/minimp3).
internal static class Layer3Syntax
{
    public const int ShortBlockType = 2;
    public const int StopBlockType = 3;

    // The most main_data_begin a frame states (MPEG-1: nine bits).
    public const int MaxReservoir = 511;

    // The row of the band tables for the sample rate of a header (minimp3's sr_idx after its adjustment): 0 and 1 for the MPEG-2.5 rates 11.025, 12 and 8 kHz,
    // 2 to 4 for the MPEG-2 rates, 5 to 7 for MPEG-1.
    public static int BandRowOf(MpegHeader header)
    {
        int index = header.SampleRateIndex + ((header.IsMpeg1 ? 6 : header.VersionBits == 2 ? 3 : 0));
        return index == 0 ? 0 : index - 1;
    }

    // Reads the side information that follows the header and the CRC. frame starts at the header. Returns main_data_begin, or -1 where the side information
    // breaks (the frame cannot be decoded). granules receives granule 0 channel 0, granule 0 channel 1, granule 1 channel 0 ... and count how many.
    public static int ReadSideInfo(ReadOnlySpan<byte> frame, MpegHeader header, Span<GranuleSyntax> granules, out int count, out int partsTotal)
    {
        var bits = new BitSource(frame) { Position = 32 + (header.IsProtected ? 16 : 0) };
        int channels = header.IsMono ? 1 : 2;
        int granuleCount = header.IsMpeg1 ? 2 : 1;
        count = granuleCount * channels;
        partsTotal = 0;
        int mainDataBegin;
        Span<int> scfsi = stackalloc int[2];
        if (header.IsMpeg1)
        {
            mainDataBegin = (int)bits.Read(9);
            bits.Read(channels == 1 ? 5 : 3);
            for (int ch = 0; ch < channels; ch++)
            {
                scfsi[ch] = (int)bits.Read(4);
            }
        }
        else
        {
            mainDataBegin = (int)bits.Read(8);
            bits.Read(channels == 1 ? 1 : 2);
        }

        int row = BandRowOf(header);
        for (int gr = 0; gr < granuleCount; gr++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                ref GranuleSyntax g = ref granules[(gr * channels) + ch];
                g = default;
                g.Part23Length = (int)bits.Read(12);
                partsTotal += g.Part23Length;
                g.BigValues = (int)bits.Read(9);
                if (g.BigValues > 288)
                {
                    return -1;
                }

                g.GlobalGain = (int)bits.Read(8);
                g.ScalefacCompress = (int)bits.Read(header.IsMpeg1 ? 4 : 9);
                g.Bands = BandKind.Long;
                g.BandRow = row;
                g.LongBandCount = 22;
                g.ShortBandCount = 0;
                uint tables;
                if (bits.Read(1) != 0)
                {
                    g.BlockType = (int)bits.Read(2);
                    if (g.BlockType == 0)
                    {
                        return -1;
                    }

                    g.MixedBlock = (int)bits.Read(1);
                    g.Region0 = 7;
                    g.Region1 = 255;
                    if (g.BlockType == ShortBlockType)
                    {
                        if (g.MixedBlock == 0)
                        {
                            g.Region0 = 8;
                            g.Bands = BandKind.Short;
                            g.LongBandCount = 0;
                            g.ShortBandCount = 39;
                        }
                        else
                        {
                            g.Bands = BandKind.Mixed;
                            g.LongBandCount = header.IsMpeg1 ? 8 : 6;
                            g.ShortBandCount = 30;
                        }
                    }

                    tables = bits.Read(10) << 5;
                    g.SubblockGain0 = (int)bits.Read(3);
                    g.SubblockGain1 = (int)bits.Read(3);
                    g.SubblockGain2 = (int)bits.Read(3);
                }
                else
                {
                    tables = bits.Read(15);
                    g.Region0 = (int)bits.Read(4);
                    g.Region1 = (int)bits.Read(3);
                    g.Region2 = 255;
                }

                g.Table0 = (int)(tables >> 10);
                g.Table1 = (int)((tables >> 5) & 31);
                g.Table2 = (int)(tables & 31);
                g.Preflag = header.IsMpeg1 ? (int)bits.Read(1) : g.ScalefacCompress >= 500 ? 1 : 0;
                g.ScalefacScale = (int)bits.Read(1);
                g.Count1Table = (int)bits.Read(1);
            }
        }

        // scfsi of the second granule's channel, cleared where either granule of that channel is short (a short block never reuses scalefactors).
        // minimp3 keeps the private bits in the register it takes the first granule's scfsi from; no granule reuses scalefactors before the first, so it is 0 here.
        if (header.IsMpeg1)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                bool isShort = granules[ch].IsShort || granules[channels + ch].IsShort;
                granules[channels + ch].Scfsi = isShort ? 0 : scfsi[ch];
            }
        }

        if (bits.Position > bits.Limit)
        {
            return -1;
        }

        return mainDataBegin;
    }

    private static ReadOnlySpan<byte> Bands(in GranuleSyntax gr)
    {
        (byte[] table, int width) = gr.Bands switch
        {
            BandKind.Short => (Layer3Tables.ShortBands, 40),
            BandKind.Mixed => (Layer3Tables.MixedBands, 40),
            _ => (Layer3Tables.LongBands, 23),
        };
        return table.AsSpan(gr.BandRow * width, width);
    }

    // The widths of a granule's scalefactor bands in the order the Huffman data comes in, ending in 0 (short blocks: each band once per window).
    public static ReadOnlySpan<byte> BandWidths(in GranuleSyntax gr) => Bands(gr);

    // Reads the scalefactors of one granule and channel (minimp3's L3_read_scalefactors and the partition choice of L3_decode_scalefactors) into
    // scalefactors (the values as coded) and positions (the same, with the intensity stereo illegal value marked 255 as minimp3 keeps it), which
    // carry over to the next granule through scfsi. Both are 40 bytes: the bands and three zeros.
    public static void ReadScalefactors(ref BitSource bits, MpegHeader header, in GranuleSyntax gr, int channel, bool isIntensityStereo, Span<byte> scalefactors, Span<byte> positions)
    {
        int partitionRow = (gr.ShortBandCount != 0 ? 1 : 0) + (gr.LongBandCount == 0 ? 1 : 0);
        ReadOnlySpan<byte> partitions = Layer3Tables.Partitions.AsSpan(partitionRow * 28, 28);
        Span<int> sizes = stackalloc int[4];
        int scfsi = gr.Scfsi;
        if (header.IsMpeg1)
        {
            int part = Layer3Tables.Mpeg1SlenCode[gr.ScalefacCompress];
            sizes[0] = sizes[1] = part >> 2;
            sizes[2] = sizes[3] = part & 3;
        }
        else
        {
            int intensity = isIntensityStereo && channel != 0 ? 1 : 0;
            int sfc = gr.ScalefacCompress >> intensity;
            int k = intensity * 3 * 4;
            int modulusProduct;
            for (; sfc >= 0 && k + 4 <= Layer3Tables.Moduli.Length; sfc -= modulusProduct, k += 4)
            {
                modulusProduct = 1;
                for (int i = 3; i >= 0; i--)
                {
                    int modulus = Layer3Tables.Moduli[k + i];
                    sizes[i] = sfc / modulusProduct % modulus;
                    modulusProduct *= modulus;
                }
            }

            partitions = partitions[Math.Min(k, 24)..];
            scfsi = -16;
        }

        int offset = 0;
        for (int i = 0; i < 4 && partitions[i] != 0; i++, scfsi *= 2)
        {
            int count = partitions[i];
            if (offset + count > 36)
            {
                break;
            }

            if ((scfsi & 8) != 0)
            {
                positions.Slice(offset, count).CopyTo(scalefactors.Slice(offset, count));
            }
            else
            {
                int width = sizes[i];
                if (width == 0)
                {
                    scalefactors.Slice(offset, count).Clear();
                    positions.Slice(offset, count).Clear();
                }
                else
                {
                    int maxScalefactor = scfsi < 0 ? (1 << width) - 1 : -1;
                    for (int j = 0; j < count; j++)
                    {
                        int s = (int)bits.Read(width);
                        positions[offset + j] = (byte)(s == maxScalefactor ? 255 : s);
                        scalefactors[offset + j] = (byte)s;
                    }
                }
            }

            offset += count;
        }

        scalefactors[offset] = scalefactors[offset + 1] = scalefactors[offset + 2] = 0;
    }

    // Reads the Huffman-coded values of one granule and channel into values (576 entries, zero outside what the regions code), following
    // minimp3's L3_huffman. limit is the bit position where the granule's part2_3_length ends. Returns the number of lines the big-values and
    // count1 regions code, in the order the data comes in.
    public static int ReadSpectrum(ref BitSource bits, in GranuleSyntax gr, Span<int> values, int limit, out bool isOverrun)
    {
        ReadOnlySpan<byte> bands = Bands(gr);
        ReadOnlySpan<short> codebooks = Layer3Tables.Codebooks;
        int band = 0;
        int region = 0;
        int dst = 0;
        int pairsLeft = gr.BigValues;
        while (pairsLeft > 0 && region < 3)
        {
            int table = gr.Table(region);
            int bandCount = gr.Region(region);
            region++;
            ReadOnlySpan<short> codebook = codebooks[Layer3Tables.TableOffsets[table]..];
            int linbits = Layer3Tables.LinBits[table];
            int np;
            do
            {
                np = bands[band++] / 2;
                if (np == 0)
                {
                    pairsLeft = 0;
                    break;
                }

                int pairs = Math.Min(pairsLeft, np);
                while (pairs-- > 0)
                {
                    int width = 5;
                    int leaf = codebook[(int)bits.Peek(width)];
                    while (leaf < 0)
                    {
                        bits.Skip(width);
                        width = leaf & 7;
                        leaf = codebook[(int)bits.Peek(width) - (leaf >> 3)];
                    }

                    bits.Skip(leaf >> 8);
                    for (int j = 0; j < 2; j++, dst++, leaf >>= 4)
                    {
                        int magnitude = leaf & 0x0F;
                        if (linbits != 0 && magnitude == 15)
                        {
                            magnitude += (int)bits.Peek(linbits);
                            bits.Skip(linbits);
                        }

                        if (magnitude != 0)
                        {
                            values[dst] = bits.Peek(1) != 0 ? -magnitude : magnitude;
                            bits.Skip(1);
                        }
                    }
                }
            }
            while ((pairsLeft -= np) > 0 && --bandCount >= 0);
        }

        isOverrun = bits.Position > limit;
        ReadOnlySpan<byte> count1 = gr.Count1Table != 0 ? Layer3Tables.Count1Table1 : Layer3Tables.Count1Table0;
        // np counts pairs of lines left in the band the quadruples are in; the quadruple loop starts in the band the big-values loop ended in.
        int remaining = 1 - pairsLeft;
        for (; dst + 4 <= 576; dst += 4)
        {
            int leaf = count1[(int)bits.Peek(4)];
            if ((leaf & 8) == 0)
            {
                int extra = leaf & 3;
                leaf = count1[(leaf >> 3) + (extra == 0 ? 0 : (int)((bits.Peek(4 + extra) & ((1u << extra) - 1))))];
            }

            bits.Skip(leaf & 7);
            if (bits.Position > limit)
            {
                break;
            }

            if (--remaining == 0)
            {
                remaining = bands[band++] / 2;
                if (remaining == 0)
                {
                    break;
                }
            }

            Quad(ref bits, values, dst, leaf, 0);
            Quad(ref bits, values, dst, leaf, 1);
            if (--remaining == 0)
            {
                remaining = bands[band++] / 2;
                if (remaining == 0)
                {
                    dst += 2;
                    break;
                }
            }

            Quad(ref bits, values, dst, leaf, 2);
            Quad(ref bits, values, dst, leaf, 3);
        }

        bits.Position = limit;
        return dst;
    }

    private static void Quad(ref BitSource bits, Span<int> values, int dst, int leaf, int index)
    {
        if ((leaf & (128 >> index)) != 0)
        {
            values[dst + index] = bits.Peek(1) != 0 ? -1 : 1;
            bits.Skip(1);
        }
    }
}
