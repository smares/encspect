namespace Encspect.Formats.Mpeg;

// The allocation and scalefactor syntax of Layers I and II in the form the entropy pass and the decoder read it (minimp3's L12_read_scale_info,
// CC0 1.0, third_party/minimp3; ISO/IEC 11172-3 2.4.1.5 and 2.4.1.6). One instance serves one stream's decoder: Read replaces what it holds.
internal sealed class Layer12Scale
{
    // The sample codes' limit: 12 time slots of 32 subbands in a Layer I frame, 36 in a Layer II frame.
    public const int Subbands = 32;

    // Quantizer of each subband and channel entry (subband * 2 + channel): 0 none, 2 to 16 the bits of each sample, 17 to 19 the grouped codes of 3, 5 and 9 levels.
    public byte[] BitAllocation { get; } = new byte[2 * Subbands];

    // The quantizer each entry's scalefactors are scaled for: the allocation as it was when they were read, which for the second channel above the intensity stereo bound is
    // the shared one (BitAllocation is 0 there, the samples being the first channel's).
    public byte[] ScalefactorAllocation { get; } = new byte[2 * Subbands];

    // The scalefactor indexes of each entry, one per twelve time slots (three per frame in Layer II, the same one three times in Layer I), plus one: 0 for none.
    public byte[] Scalefactor { get; } = new byte[2 * Subbands * 3];

    public int TotalBands { get; private set; }

    public int StereoBands { get; private set; }

    // Where the samples begin, in bits from the start of the frame.
    public int SampleStart { get; private set; }

    // The allocation codes of every table, mapped to the quantizer (minimp3's g_bitalloc_code_tab).
    private static readonly byte[] AllocationCodes =
    [
        0, 17, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        0, 17, 18, 3, 19, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 16,
        0, 17, 18, 3, 19, 4, 5, 16,
        0, 17, 18, 16,
        0, 17, 18, 19, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        0, 17, 18, 3, 19, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
        0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
    ];

    // Table offset, code width and band count of each group of subbands (minimp3's L12_subband_alloc_t).
    private static readonly (int Offset, int Width, int Bands)[] LayerOne = [(76, 4, 32)];
    private static readonly (int Offset, int Width, int Bands)[] LayerTwoMpeg2 = [(60, 4, 4), (44, 3, 7), (44, 2, 19)];
    private static readonly (int Offset, int Width, int Bands)[] LayerTwoMpeg1 = [(0, 4, 3), (16, 4, 8), (32, 3, 12), (40, 2, 7)];
    private static readonly (int Offset, int Width, int Bands)[] LayerTwoMpeg1LowRate = [(44, 4, 2), (44, 3, 10)];

    // Reads the allocation and scalefactors of the frame (frame starts at the header). Returns false where the data ends inside them.
    public bool Read(MpegHeader header, ReadOnlySpan<byte> frame)
    {
        var bits = new BitSource(frame) { Position = 32 + (header.IsProtected ? 16 : 0) };
        int mode = header.ChannelModeBits;
        int stereoBands = mode == 3 ? 0 : mode == 1 ? (header.ModeExtension << 2) + 4 : 32;
        (int Offset, int Width, int Bands)[] allocation;
        int bands;
        if (header.Layer == 1)
        {
            allocation = LayerOne;
            bands = 32;
        }
        else if (!header.IsMpeg1)
        {
            allocation = LayerTwoMpeg2;
            bands = 30;
        }
        else
        {
            int kbps = header.Bitrate >> (mode != 3 ? 1 : 0);
            if (kbps == 0)
            {
                kbps = 192;
            }

            allocation = LayerTwoMpeg1;
            bands = 27;
            if (kbps < 56)
            {
                allocation = LayerTwoMpeg1LowRate;
                bands = header.SampleRateIndex == 2 ? 12 : 8;
            }
            else if (kbps >= 96 && header.SampleRateIndex != 1)
            {
                bands = 30;
            }
        }

        TotalBands = bands;
        StereoBands = Math.Min(stereoBands, bands);
        Array.Clear(BitAllocation);
        Array.Clear(Scalefactor);

        int group = -1;
        int next = 0;
        int width = 0;
        int offset = 0;
        for (int i = 0; i < bands; i++)
        {
            if (i == next)
            {
                group++;
                next += allocation[group].Bands;
                width = allocation[group].Width;
                offset = allocation[group].Offset;
            }

            byte ba = AllocationCodes[offset + (int)bits.Read(width)];
            BitAllocation[2 * i] = ba;
            if (i < StereoBands)
            {
                ba = AllocationCodes[offset + (int)bits.Read(width)];
            }

            BitAllocation[(2 * i) + 1] = StereoBands != 0 ? ba : (byte)0;
        }

        Span<byte> codes = stackalloc byte[2 * Subbands];
        for (int i = 0; i < 2 * bands; i++)
        {
            codes[i] = BitAllocation[i] != 0 ? (header.Layer == 1 ? (byte)2 : (byte)bits.Read(2)) : (byte)6;
        }

        for (int i = 0; i < 2 * bands; i++)
        {
            int ba = BitAllocation[i];
            ScalefactorAllocation[i] = (byte)ba;
            int mask = ba != 0 ? 4 + ((19 >> codes[i]) & 3) : 0;
            int current = 0;
            for (int part = 0, m = 4; m != 0; m >>= 1, part++)
            {
                if ((mask & m) != 0)
                {
                    current = (int)bits.Read(6) + 1;
                }

                Scalefactor[(i * 3) + part] = (byte)(ba != 0 ? current : 0);
            }
        }

        for (int i = StereoBands; i < bands; i++)
        {
            BitAllocation[(2 * i) + 1] = 0;
        }

        SampleStart = bits.Position;
        return bits.Position <= bits.Limit;
    }

    // Reads the sample codes of the frame into planes (one per channel, PerChannel entries each) as the signed integers minimp3 turns into floats: the code minus
    // half its range, or for a grouped quantizer the digit minus the middle level. The layout of a plane is (part * 32 + subband) * 12 + time slot, with one part in Layer I
    // and three in Layer II. Subbands above the intensity stereo bound repeat in the second channel. Returns false where the samples run past the frame.
    public bool ReadSamples(MpegHeader header, ReadOnlySpan<byte> frame, Span<int> planes, int stride)
    {
        var bits = new BitSource(frame) { Position = SampleStart };
        int layer = header.Layer;
        int groupSize = layer == 1 ? 1 : 3;
        int parts = layer == 1 ? 1 : 3;
        int channels = header.IsMono ? 1 : 2;
        for (int call = 0; call < 3; call++)
        {
            for (int j = 0; j < 4; j++)
            {
                for (int i = 0; i < 2 * TotalBands; i++)
                {
                    int ba = BitAllocation[i];
                    if (ba == 0)
                    {
                        continue;
                    }

                    int channel = i & 1;
                    int band = i >> 1;
                    int part = layer == 1 ? 0 : call;
                    int slot = layer == 1 ? (call * 4) + j : j * groupSize;
                    Span<int> destination = planes.Slice((channel * stride) + (((part * Subbands) + band) * 12) + slot, groupSize);
                    if (ba < 17)
                    {
                        int half = (1 << (ba - 1)) - 1;
                        for (int k = 0; k < groupSize; k++)
                        {
                            destination[k] = (int)bits.Read(ba) - half;
                        }
                    }
                    else
                    {
                        uint mod = (uint)((2 << (ba - 17)) + 1);
                        uint code = bits.Read((int)(mod + 2 - (mod >> 3)));
                        for (int k = 0; k < groupSize; k++, code /= mod)
                        {
                            destination[k] = (int)(code % mod) - (int)(mod / 2);
                        }
                    }
                }
            }
        }

        if (channels == 2)
        {
            for (int part = 0; part < parts; part++)
            {
                for (int band = StereoBands; band < TotalBands; band++)
                {
                    int from = ((part * Subbands) + band) * 12;
                    planes.Slice(from, 12).CopyTo(planes.Slice(stride + from, 12));
                }
            }
        }

        return bits.Position <= bits.Limit;
    }
}
