// Reading derived from libmpcdec r495 (mpc_decoder.c, mpc_decoder_read_bitstream_sv8, and mpc_bits_reader.c), Copyright (c) 2005-2009 The Musepack Development Team, under the BSD 3-clause
// license. The license text is in THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;

namespace Encspect.Formats.Musepack;

// The syntax of an SV8 frame, which an audio packet holds one after the other with no length of their own (libmpcdec r495 mpc_decoder_read_bitstream_sv8):
//   - the number of used subbands: coded in a fixed number of bits in the first frame of a packet (a key frame) and as a change from the frame before after it;
//   - the subband resolutions from the highest subband down, each a difference from the one above, in one of two tables by that one's size; after them, where the stream
//     allows mid/side, how many coded subbands are mid/side and, where that is neither none nor all, which (an enumerative code over the coded subbands);
//   - the scalefactor selection of each coded subband (one code for both channels); the scalefactors, absolute in a packet's first frame and differences from the
//     previous frame's after it, and within a frame differences from the one before;
//   - the samples, 36 to a subband and channel: 3 values in one code for resolution 2, which of 18 are not 0 (an enumerative code) and their signs for resolution 1, 2 values in
//     one code for 3 and 4, one code each for 5 to 8 (in one of two tables by the size of the values just read), a code and plain bits for 9 to 15, nothing for 0 and -1, the
//     noise-substituted subbands whose values the decoder makes.
internal static class Sv8Syntax
{
    private static readonly CanonicalCode Bands = new(MusepackTables.Sv8Bands, MusepackTables.Sv8BandsSymbols);
    private static readonly CanonicalCode[] Resolutions = [new(MusepackTables.Sv8Res1, MusepackTables.Sv8Res1Symbols), new(MusepackTables.Sv8Res2, MusepackTables.Sv8Res2Symbols)];
    private static readonly CanonicalCode[] Selections = [new(MusepackTables.Sv8Scfi1, MusepackTables.Sv8Scfi1Symbols), new(MusepackTables.Sv8Scfi2, MusepackTables.Sv8Scfi2Symbols)];
    private static readonly CanonicalCode[] Differences = [new(MusepackTables.Sv8Dscf1, MusepackTables.Sv8Dscf1Symbols), new(MusepackTables.Sv8Dscf2, MusepackTables.Sv8Dscf2Symbols)];
    private static readonly CanonicalCode SingleOrPair = new(MusepackTables.Sv8Q1, MusepackTables.Sv8Q1Symbols);
    private static readonly CanonicalCode Wide = new(MusepackTables.Sv8Q9Up, MusepackTables.Sv8Q9UpSymbols);
    private static readonly CanonicalCode[][] Samples = Tables();

    // The thresholds that choose between the two tables of resolutions 2 and 5 to 8, by resolution.
    private static readonly int[] Thresholds = [0, 0, 3, 0, 0, 1, 3, 4, 8];

    private static CanonicalCode[][] Tables() =>
    [
        [new(MusepackTables.Sv8Q2A, MusepackTables.Sv8Q2ASymbols), new(MusepackTables.Sv8Q2B, MusepackTables.Sv8Q2BSymbols)],
        [new(MusepackTables.Sv8Q3, MusepackTables.Sv8Q3Symbols), new(MusepackTables.Sv8Q4, MusepackTables.Sv8Q4Symbols)],
        [new(MusepackTables.Sv8Q5A, MusepackTables.Sv8Q5ASymbols), new(MusepackTables.Sv8Q5B, MusepackTables.Sv8Q5BSymbols)],
        [new(MusepackTables.Sv8Q6A, MusepackTables.Sv8Q6ASymbols), new(MusepackTables.Sv8Q6B, MusepackTables.Sv8Q6BSymbols)],
        [new(MusepackTables.Sv8Q7A, MusepackTables.Sv8Q7ASymbols), new(MusepackTables.Sv8Q7B, MusepackTables.Sv8Q7BSymbols)],
        [new(MusepackTables.Sv8Q8A, MusepackTables.Sv8Q8ASymbols), new(MusepackTables.Sv8Q8B, MusepackTables.Sv8Q8BSymbols)],
    ];

    // The frame: isKey for the first frame of a packet; maxBand the stream header's value (libmpcdec's d->max_band); midSide whether the stream header allows mid/side.
    // Stops at the first problem; the facts are the frame's as far as they were read.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Walk(SubbandState state, MsbBits bits, bool isKey, int maxBand, bool midSide, ref WalkFacts facts)
    {
        int used;
        if (isKey)
        {
            used = LogDecode(bits, maxBand + 1);
        }
        else
        {
            if (!Bands.TryDecode(bits, out int change))
            {
                facts.Problems |= WalkProblems.Syntax;
                return;
            }

            used = state.LastUsedBands + change;
            if (used > 32)
            {
                used -= 33;
            }
        }

        state.LastUsedBands = used;
        int[] left = state.Resolution[0];
        int[] right = state.Resolution[1];
        if (used > 0)
        {
            if (!ReadResolutions(state, bits, used, midSide, out uint mask, out int flagged))
            {
                facts.Problems |= WalkProblems.Syntax;
                return;
            }

            facts.MidSide = mask;
            facts.Flagged = flagged;
        }

        for (int band = used; band <= maxBand && band < SubbandState.Bands; band++)
        {
            left[band] = 0;
            right[band] = 0;
        }

        int highest = 0;
        for (int band = 0; band < used; band++)
        {
            if (left[band] != 0 || right[band] != 0)
            {
                highest = band + 1;
            }

            facts.Noise += (left[band] == -1 ? 1 : 0) + (right[band] == -1 ? 1 : 0);
        }

        facts.UsedBands = highest;
        if (bits.IsOverrun)
        {
            facts.Problems |= WalkProblems.HeaderOverrun;
            return;
        }

        if (isKey)
        {
            Array.Fill(state.Differential[0], true);
            Array.Fill(state.Differential[1], true);
        }

        for (int band = 0; band < used; band++)
        {
            int count = -1;
            count += left[band] != 0 ? 1 : 0;
            count += right[band] != 0 ? 1 : 0;
            if (count >= 0)
            {
                if (!Selections[count].TryDecode(bits, out int selection))
                {
                    facts.Problems |= WalkProblems.Syntax;
                    return;
                }

                if (left[band] != 0)
                {
                    state.Selection[0][band] = selection >> (2 * count);
                }

                if (right[band] != 0)
                {
                    state.Selection[1][band] = selection & 3;
                }
            }
        }

        if (bits.IsOverrun)
        {
            facts.Problems |= WalkProblems.DataOverrun;
            return;
        }

        for (int band = 0; band < used; band++)
        {
            for (int channel = 0; channel < 2; channel++)
            {
                if (state.Resolution[channel][band] != 0 && !ReadScalefactors(state, channel, band, bits))
                {
                    facts.Problems |= WalkProblems.Syntax;
                    return;
                }
            }

            if (bits.IsOverrun)
            {
                facts.Problems |= WalkProblems.DataOverrun;
                return;
            }
        }

        for (int band = 0; band < used; band++)
        {
            for (int channel = 0; channel < 2; channel++)
            {
                if (!ReadSamples(state.Quantized[channel].AsSpan(band * SubbandState.SamplesPerBand, SubbandState.SamplesPerBand), state.Resolution[channel][band], bits))
                {
                    facts.Problems |= WalkProblems.Syntax;
                    return;
                }

                if (bits.IsOverrun)
                {
                    facts.Problems |= WalkProblems.DataOverrun;
                    return;
                }
            }
        }
    }

    // The resolutions from the highest used subband down (the left and the right channel's code of each subband one after the other) and then the mid/side flags.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool ReadResolutions(SubbandState state, MsbBits bits, int used, bool midSide, out uint mask, out int flagged)
    {
        mask = 0;
        flagged = 0;
        int[] left = state.Resolution[0];
        int[] right = state.Resolution[1];
        if (!Resolutions[0].TryDecode(bits, out int first) || !Resolutions[0].TryDecode(bits, out int second))
        {
            return false;
        }

        left[used - 1] = first > 15 ? first - 17 : first;
        right[used - 1] = second > 15 ? second - 17 : second;
        for (int band = used - 2; band >= 0; band--)
        {
            if (!Resolutions[left[band + 1] > 2 ? 1 : 0].TryDecode(bits, out int change))
            {
                return false;
            }

            int value = change + left[band + 1];
            left[band] = value > 15 ? value - 17 : value;
            if (!Resolutions[right[band + 1] > 2 ? 1 : 0].TryDecode(bits, out change))
            {
                return false;
            }

            value = change + right[band + 1];
            right[band] = value > 15 ? value - 17 : value;
        }

        int total = 0;
        for (int band = 0; band < used; band++)
        {
            total += left[band] != 0 || right[band] != 0 ? 1 : 0;
        }

        flagged = total;
        if (!midSide)
        {
            return true;
        }

        int count = LogDecode(bits, total);
        uint flags = 0;
        if (count > total)
        {
            return false;
        }

        if (count != 0 && count != total)
        {
            if (!TryEnumerate(bits, Math.Min(count, total - count), total, out flags))
            {
                return false;
            }
        }

        if (count * 2 > total)
        {
            flags = ~flags;
        }

        for (int band = used - 1; band >= 0; band--)
        {
            if (left[band] != 0 || right[band] != 0)
            {
                bool flag = (flags & 1) != 0;
                state.MidSideFlag[band] = flag;
                if (flag)
                {
                    mask |= 1u << band;
                }

                flags >>= 1;
            }
        }

        return true;
    }

    // Reads the scalefactors of one subband and channel: the first absolute (7 bits less 6) in a packet's first frame and a difference from the last scalefactor of the frame
    // before after it; the second and third, unless the selection repeats the one before, differences from it. Differences wrap in 7 bits.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool ReadScalefactors(SubbandState state, int channel, int band, MsbBits bits)
    {
        Span<int> scalefactor = state.Scalefactor[channel].AsSpan(band * 3, 3);
        int selection = state.Selection[channel][band];
        if (state.Differential[channel][band])
        {
            scalefactor[0] = bits.Read(7) - 6;
            state.Differential[channel][band] = false;
        }
        else
        {
            if (!Differences[1].TryDecode(bits, out int change))
            {
                return false;
            }

            if (change == 64)
            {
                change += bits.Read(6);
            }

            scalefactor[0] = ((scalefactor[2] - 25 + change) & 127) - 6;
        }

        for (int part = 0; part < 2; part++)
        {
            if (((selection << part) & 2) == 0)
            {
                if (!Differences[0].TryDecode(bits, out int change))
                {
                    return false;
                }

                if (change == 31)
                {
                    change = 64 + bits.Read(6);
                }

                scalefactor[part + 1] = ((scalefactor[part] - 25 + change) & 127) - 6;
            }
            else
            {
                scalefactor[part + 1] = scalefactor[part];
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool ReadSamples(Span<int> values, int resolution, MsbBits bits)
    {
        switch (resolution)
        {
            case 0 or -1:
                return true;
            case 1:
                for (int k = 0; k < 36;)
                {
                    int end = k + 18;
                    if (!SingleOrPair.TryDecode(bits, out int count) || count is < 0 or > 18)
                    {
                        return false;
                    }

                    uint index = 0;
                    if (count is > 0 and < 18 && !TryEnumerate(bits, count <= 9 ? count : 18 - count, 18, out index))
                    {
                        return false;
                    }

                    if (count > 9)
                    {
                        index = ~index;
                    }

                    for (; k < end; k++)
                    {
                        values[k] = 0;
                        if ((index & (1u << 17)) != 0)
                        {
                            values[k] = (bits.Read(1) << 1) - 1;
                        }

                        index <<= 1;
                    }
                }

                return true;
            case 2:
                {
                    int index = 2 * Thresholds[2];
                    for (int k = 0; k < 36; k += 3)
                    {
                        if (!Samples[0][index > Thresholds[2] ? 1 : 0].TryDecode(bits, out int code) || code is < 0 or > 124)
                        {
                            return false;
                        }

                        int a = (code % 5) - 2;
                        int b = ((code / 5) % 5) - 2;
                        int c = (code / 25) - 2;
                        values[k] = a;
                        values[k + 1] = b;
                        values[k + 2] = c;
                        index = (index >> 1) + Math.Abs(a) + Math.Abs(b) + Math.Abs(c);
                    }

                    return true;
                }

            case 3 or 4:
                {
                    CanonicalCode table = Samples[1][resolution - 3];
                    for (int k = 0; k < 36; k += 2)
                    {
                        if (!table.TryDecode(bits, out int pair))
                        {
                            return false;
                        }

                        // Two signed nibbles in one byte, the first in the low four bits.
                        values[k] = (sbyte)(pair << 4) >> 4;
                        values[k + 1] = (sbyte)pair >> 4;
                    }

                    return true;
                }

            case >= 5 and <= 8:
                {
                    int threshold = Thresholds[resolution];
                    int index = 2 * threshold;
                    CanonicalCode[] tables = Samples[resolution - 3];
                    for (int k = 0; k < 36; k++)
                    {
                        if (!tables[index > threshold ? 1 : 0].TryDecode(bits, out int value))
                        {
                            return false;
                        }

                        values[k] = value;
                        index = (index >> 1) + Math.Abs(value);
                    }

                    return true;
                }

            case >= 9 and <= 17:
                {
                    int offset = MusepackTables.Offsets[resolution + 1];
                    for (int k = 0; k < 36; k++)
                    {
                        if (!Wide.TryDecode(bits, out int code))
                        {
                            return false;
                        }

                        int value = code & 0xFF;
                        if (resolution != 9)
                        {
                            value = (value << (resolution - 9)) | bits.Read(resolution - 9);
                        }

                        values[k] = value - offset;
                    }

                    return true;
                }

            default:
                return false;
        }
    }

    // libmpcdec's mpc_bits_enum_dec: which k of n positions are set, as a bit mask, from an enumerative code. False where the code names no combination.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryEnumerate(MsbBits bits, int k, int n, out uint mask)
    {
        mask = 0;
        if (k < 1 || k > 16 || n < 1 || n > 32)
        {
            return false;
        }

        int row = (k - 1) * 32;
        int length = MusepackTables.EnumerationLengths[row + n - 1];
        if (length < 1)
        {
            return false;
        }

        int code = bits.ReadLong(length - 1);
        int lost = MusepackTables.EnumerationLost[row + n - 1];
        if (code >= lost)
        {
            code = ((code << 1) | bits.Read(1)) - lost;
        }

        do
        {
            if (n < 1)
            {
                return false;
            }

            n--;
            int count = MusepackTables.EnumerationCounts[row + n];
            if (code >= count)
            {
                mask |= 1u << n;
                code -= count;
                row -= 32;
                k--;
            }
        }
        while (k > 0);

        return true;
    }

    private static int LogDecode(MsbBits bits, int max)
    {
        if (max == 0)
        {
            return 0;
        }

        int value = 0;
        int length = MusepackTables.LogLengths[max - 1];
        if (length > 1)
        {
            value = bits.Read(length - 1);
        }

        int lost = MusepackTables.LogLost[max - 1];
        if (value >= lost)
        {
            value = ((value << 1) | bits.Read(1)) - lost;
        }

        return value;
    }
}
