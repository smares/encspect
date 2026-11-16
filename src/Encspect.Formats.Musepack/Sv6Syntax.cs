// Reading derived from libmpcdec 1.2.6 (mpc_decoder.c, mpc_decoder_read_bitstream_sv6, and huffsv46.c), Copyright (c) 2005-2009 The Musepack Development Team, under the BSD 3-clause
// license. The license text is in THIRD-PARTY-NOTICES.md.

namespace Encspect.Formats.Musepack;

// The syntax of a frame of stream versions 4 to 6, which libmpcdec 1.2.6 reads with one function (mpc_decoder_read_bitstream_sv6), after the frame's 20-bit length:
//   - for each subband up to the header's maximum: the left channel's resolution (a code of the table of the subband's region, translated by Q_res), a mid/side flag where
//     the stream allows mid/side, and the right channel's resolution;
//   - for each subband up to the highest that carries data: the scalefactor selection and whether the scalefactors are differences, one code for both, for each coded channel;
//   - the scalefactors, differences (a code, as in SV7) or 6 bits each;
//   - the samples, 36 to a subband, the left and the right channel's value one after the other, a code for resolutions 1 to 7 and plain bits for 8 to 17, which follow
//     the codes of the subband.
internal static class Sv6Syntax
{
    private static readonly WideCode[] Regions = [new(MusepackTables.Sv6RegionA), new(MusepackTables.Sv6RegionB), new(MusepackTables.Sv6RegionC)];
    private static readonly WideCode Bundle = new(MusepackTables.Sv6ScfiBundle);
    private static readonly WideCode Difference = new(MusepackTables.Sv6Dscf);

    private static readonly WideCode?[] Samples =
    [
        null, new(MusepackTables.Sv6Samples1), new(MusepackTables.Sv6Samples2), new(MusepackTables.Sv6Samples3), new(MusepackTables.Sv6Samples4),
        new(MusepackTables.Sv6Samples5), new(MusepackTables.Sv6Samples6), new(MusepackTables.Sv6Samples7),
    ];

    // The resolutions and mid/side flags; used is the number of subbands up to the highest that carries data.
    public static void ReadHeader(SubbandState state, MsbBits bits, int maxBand, bool midSide, ref WalkFacts facts)
    {
        int[] left = state.Resolution[0];
        int[] right = state.Resolution[1];
        int highest = -1;
        int flagged = 0;
        uint mask = 0;
        for (int band = 0; band <= maxBand && band < SubbandState.Bands; band++)
        {
            WideCode table = Regions[band < 11 ? 0 : band <= 22 ? 1 : 2];
            left[band] = MusepackTables.Sv6Resolutions[(band * 16) + table.Decode(bits)];
            if (midSide)
            {
                state.MidSideFlag[band] = bits.Read(1) == 1;
            }

            right[band] = MusepackTables.Sv6Resolutions[(band * 16) + table.Decode(bits)];
            if (left[band] != 0 || right[band] != 0)
            {
                highest = band;
                flagged++;
                if (midSide && state.MidSideFlag[band])
                {
                    mask |= 1u << band;
                }
            }

            if (bits.IsOverrun)
            {
                facts.Problems |= WalkProblems.HeaderOverrun;
                break;
            }
        }

        facts.UsedBands = highest + 1;
        facts.MidSide = mask;
        facts.Flagged = flagged;
    }

    public static void ReadBody(SubbandState state, MsbBits bits, ref WalkFacts facts)
    {
        int used = facts.UsedBands;
        for (int band = 0; band < used; band++)
        {
            for (int channel = 0; channel < 2; channel++)
            {
                if (state.Resolution[channel][band] != 0)
                {
                    int value = Bundle.Decode(bits);
                    state.Selection[channel][band] = value >> 1;
                    state.Differential[channel][band] = (value & 1) != 0;
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
                if (state.Resolution[channel][band] != 0)
                {
                    ReadScalefactors(state.Scalefactor[channel].AsSpan(band * 3, 3), state.Selection[channel][band], state.Differential[channel][band], bits);
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
            int resolutionLeft = state.Resolution[0][band];
            int resolutionRight = state.Resolution[1][band];
            Span<int> left = state.Quantized[0].AsSpan(band * SubbandState.SamplesPerBand, SubbandState.SamplesPerBand);
            Span<int> right = state.Quantized[1].AsSpan(band * SubbandState.SamplesPerBand, SubbandState.SamplesPerBand);
            WideCode? tableLeft = resolutionLeft is >= 1 and <= 7 ? Samples[resolutionLeft] : null;
            WideCode? tableRight = resolutionRight is >= 1 and <= 7 ? Samples[resolutionRight] : null;
            for (int k = 0; k < 36 && (tableLeft is not null || tableRight is not null); k++)
            {
                if (tableLeft is not null)
                {
                    left[k] = tableLeft.Decode(bits);
                }

                if (tableRight is not null)
                {
                    right[k] = tableRight.Decode(bits);
                }
            }

            for (int k = 0; k < 36 && (resolutionLeft > 7 || resolutionRight > 7); k++)
            {
                if (resolutionLeft > 7)
                {
                    left[k] = bits.ReadLong(MusepackTables.ResolutionBits[resolutionLeft]) - MusepackTables.Offsets[resolutionLeft + 1];
                }

                if (resolutionRight > 7)
                {
                    right[k] = bits.ReadLong(MusepackTables.ResolutionBits[resolutionRight]) - MusepackTables.Offsets[resolutionRight + 1];
                }
            }

            if (bits.IsOverrun)
            {
                facts.Problems |= WalkProblems.DataOverrun;
                return;
            }
        }
    }

    private static void ReadScalefactors(Span<int> scalefactor, int selection, bool isDifference, MsbBits bits)
    {
        int Next(int previous) => isDifference ? previous + Difference.Decode(bits) : bits.Read(6);
        switch (selection)
        {
            case 1:
                scalefactor[0] = Next(scalefactor[2]);
                scalefactor[1] = Next(scalefactor[0]);
                scalefactor[2] = scalefactor[1];
                break;
            case 3:
                scalefactor[0] = Next(scalefactor[2]);
                scalefactor[1] = scalefactor[0];
                scalefactor[2] = scalefactor[1];
                break;
            case 2:
                scalefactor[0] = Next(scalefactor[2]);
                scalefactor[1] = scalefactor[0];
                scalefactor[2] = Next(scalefactor[1]);
                break;
            default:
                scalefactor[0] = Next(scalefactor[2]);
                scalefactor[1] = Next(scalefactor[0]);
                scalefactor[2] = Next(scalefactor[1]);
                break;
        }

        if (isDifference)
        {
            scalefactor[0] = SubbandState.Clamp(scalefactor[0]);
            scalefactor[1] = SubbandState.Clamp(scalefactor[1]);
            scalefactor[2] = SubbandState.Clamp(scalefactor[2]);
        }
    }
}
