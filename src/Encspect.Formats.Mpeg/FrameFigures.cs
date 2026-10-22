using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// What a frame's kept audio data says about how it is coded, read straight from the bits at the positions the syntax gives
// (ISO/IEC 11172-3 2.4.1.5 to 2.4.1.7), without building the objects that make the frame's nodes: the series of a stream are
// derived from the frame records at the end, once for each frame, and a long file has many frames. Layer1AudioData,
// Layer2AudioData and Layer3SideInfo stay the readers of the nodes; the tests hold these figures equal to what they give.
internal static class FrameFigures
{
    public const int None = -1;

    // Bandwidth in Hz of the highest subband with an allocation (0 where none has), or None for Layer III; the bits of each
    // coded channel's own share of the frame's audio data, or None for a mono frame or a frame without a second channel.
    public readonly record struct Figure(int Bandwidth, long FirstBits, long SecondBits)
    {
        public bool HasShare => FirstBits >= 0 && SecondBits >= 0 && FirstBits + SecondBits > 0;

        public bool HasBandwidth => Bandwidth >= 0;

        public static Figure Empty { get; } = new(None, None, None);
    }

    // Layer III: part2_3_length of every granule and channel, summed per channel (2.4.1.7, 2.4.2.7). It counts the
    // scalefactors and the Huffman-coded values, not the header, the side information or the padding.
    public static Figure Layer3(ReadOnlySpan<byte> sideInfo, MpegHeader header)
    {
        if (header.IsMono)
        {
            return Figure.Empty;
        }

        long first = 0;
        long second = 0;
        for (int gr = 0; gr < Granules(header); gr++)
        {
            first += Bits(sideInfo, GranuleStart(header, gr, 0), 12);
            second += Bits(sideInfo, GranuleStart(header, gr, 1), 12);
        }

        return new Figure(None, first, second);
    }

    // The block type of one granule and channel as the index of Blocks in the parser: long (0), start (1), short (2), mixed (3),
    // stop (4), reserved (5); the same decision the Block Usage statistics make from window_switching_flag, block_type and
    // mixed_block_flag.
    public static byte BlockKind(ReadOnlySpan<byte> sideInfo, MpegHeader header, int granule, int channel)
    {
        int start = GranuleStart(header, granule, channel);
        int windowSwitching = start + 12 + 9 + 8 + (header.IsMpeg1 ? 4 : 9);
        if (Bits(sideInfo, windowSwitching, 1) == 0)
        {
            return 0;
        }

        return Bits(sideInfo, windowSwitching + 1, 2) switch
        {
            1 => 1,
            2 => Bits(sideInfo, windowSwitching + 3, 1) == 1 ? (byte)3 : (byte)2,
            3 => 4,
            _ => 5,
        };
    }

    public static int Granules(MpegHeader header) => header.IsMpeg1 ? 2 : 1;

    // The side information has a fixed layout: main_data_begin, private bits and, in MPEG-1, scfsi come first, then every
    // granule and channel takes 59 bits in MPEG-1 and 63 in MPEG-2 and 2.5 (a 9-bit scalefac_compress and no preflag).
    private static int GranuleStart(MpegHeader header, int granule, int channel)
    {
        int channels = header.IsMono ? 1 : 2;
        int lead = header.IsMpeg1 ? 9 + (channels == 1 ? 5 : 3) + (4 * channels) : 8 + (channels == 1 ? 1 : 2);
        return lead + (((granule * channels) + channel) * (header.IsMpeg1 ? 59 : 63));
    }

    // Layer I (2.4.1.5, 2.4.2.5): a 4-bit allocation per subband and channel, one per subband above the intensity stereo
    // bound; a 6-bit scalefactor per allocated subband and channel; twelve samples of allocation + 1 bits per allocated
    // subband and channel, one set above the bound. The share of a channel counts its allocations, scalefactors and samples;
    // the shared allocations and samples above the bound belong to neither channel.
    public static Figure Layer1(ReadOnlySpan<byte> data, MpegHeader header)
    {
        int channels = header.IsMono ? 1 : 2;
        int bound = header.ChannelModeBits == 1 ? (header.ModeExtension + 1) * 4 : 32;
        Span<int> allocation = stackalloc int[2 * 32];
        Span<long> bits = stackalloc long[2];
        int position = 32 + (header.IsProtected ? 16 : 0);
        for (int sb = 0; sb < 32; sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (sb >= bound && ch > 0)
                {
                    allocation[(ch * 32) + sb] = allocation[sb];
                    continue;
                }

                allocation[(ch * 32) + sb] = Bits(data, position, 4);
                position += 4;
                bits[ch] += sb < bound ? 4 : 0;
            }
        }

        for (int sb = 0; sb < 32; sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (allocation[(ch * 32) + sb] != 0)
                {
                    bits[ch] += 6;
                }
            }
        }

        for (int sb = 0; sb < Math.Min(bound, 32); sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                int value = allocation[(ch * 32) + sb];
                bits[ch] += value == 0 ? 0 : 12 * (value + 1);
            }
        }

        return Result(header, allocation, 32, bits, channels);
    }

    // Layer II (2.4.1.6, 2.4.2.6): an allocation per subband and channel, one per subband above the bound, of the width the
    // allocation table gives; scfsi and one to three scalefactors per allocated subband and channel; twelve groups of three
    // samples, whose bits follow from the allocation alone (a codeword of 5, 7 or 10 bits for 3, 5 or 9 levels, else three
    // samples of equal width). Scfsi and scalefactors are coded per channel above the bound too.
    public static Figure Layer2(ReadOnlySpan<byte> data, MpegHeader header)
    {
        int table = Layer2AudioData.TableFor(header);
        int subbands = Layer2AudioData.SubbandCount(table);
        int channels = header.IsMono ? 1 : 2;
        int bound = header.ChannelModeBits == 1 ? Math.Min((header.ModeExtension + 1) * 4, subbands) : subbands;
        Span<int> allocation = stackalloc int[2 * 32];
        Span<long> bits = stackalloc long[2];
        int position = 32 + (header.IsProtected ? 16 : 0);
        for (int sb = 0; sb < subbands; sb++)
        {
            int width = Layer2AudioData.AllocationWidth(Layer2AudioData.Levels(table, sb));
            for (int ch = 0; ch < channels; ch++)
            {
                if (sb >= bound && ch > 0)
                {
                    allocation[(ch * 32) + sb] = allocation[sb];
                    continue;
                }

                allocation[(ch * 32) + sb] = Bits(data, position, width);
                position += width;
                bits[ch] += sb < bound ? width : 0;
            }
        }

        // The scfsi fields of all subbands and channels come one after the other, then the scalefactors they count, so only the
        // 2 bits of each are read here.
        ReadOnlySpan<int> scalefactorCounts = [3, 2, 1, 2];
        for (int sb = 0; sb < subbands; sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (allocation[(ch * 32) + sb] != 0)
                {
                    int scfsi = Bits(data, position, 2);
                    position += 2;
                    bits[ch] += 2 + (6 * scalefactorCounts[scfsi]);
                }
            }
        }

        for (int sb = 0; sb < bound; sb++)
        {
            int[] levels = Layer2AudioData.Levels(table, sb);
            for (int ch = 0; ch < channels; ch++)
            {
                int count = levels[allocation[(ch * 32) + sb]];
                bits[ch] += count == 0 ? 0 : 12 * Layer2AudioData.SampleBits(count);
            }
        }

        return Result(header, allocation, subbands, bits, channels);
    }

    private static Figure Result(MpegHeader header, ReadOnlySpan<int> allocation, int subbands, ReadOnlySpan<long> bits, int channels)
    {
        int highest = -1;
        for (int sb = 0; sb < subbands; sb++)
        {
            if (allocation[sb] != 0 || (channels == 2 && allocation[32 + sb] != 0))
            {
                highest = sb;
            }
        }

        // The upper edge of a subband: each is a 64th of the sample rate wide, rounded half to even to whole hertz.
        int hertz = highest < 0 ? 0 : (int)Rounding.HalfEven((long)(highest + 1) * header.SampleRate, 64);
        return channels == 2 ? new Figure(hertz, bits[0], bits[1]) : new Figure(hertz, None, None);
    }

    // Bit position counts from the start of data, most significant bit first; bits past the end read as 0.
    public static int Bits(ReadOnlySpan<byte> data, int position, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++, position++)
        {
            int index = position >> 3;
            int bit = index < data.Length ? (data[index] >> (7 - (position & 7))) & 1 : 0;
            value = (value << 1) | bit;
        }

        return value;
    }
}

internal static class Rounding
{
    // For a non-negative numerator and a positive denominator.
    public static long HalfEven(long numerator, long denominator)
    {
        (long quotient, long rest) = Math.DivRem(numerator, denominator);
        if (rest * 2 > denominator || (rest * 2 == denominator && quotient % 2 != 0))
        {
            quotient++;
        }

        return quotient;
    }
}
