// Reading derived from libmpcdec r495 (mpc_decoder.c, mpc_decoder_read_bitstream_sv7), Copyright (c) 2005-2009 The Musepack Development Team, under the BSD 3-clause license.
// The license text is in THIRD-PARTY-NOTICES.md.

namespace Encspect.Formats.Musepack;

// The syntax of an SV7 frame's data after its 20-bit length, in the order libmpcdec r495 reads it (mpc_decoder_read_bitstream_sv7):
//   - the subband resolutions: subband 0 in 4 bits per channel, the others as a change from the subband below (the code of MusepackTables.Sv7Header, whose value 4 escapes
//     to 4 bits), and after each subband that either channel codes a mid/side flag where the stream header allows mid/side;
//   - the scalefactor selection (SCFI) of each coded subband and channel, a code of 4 values that says which of the 3 scalefactors repeat;
//   - the scalefactors, each a difference from the one before it (DSCF; 8 escapes to 6 bits), left channel then right, subband by subband;
//   - the samples, 36 to a subband and channel: bundles of 3 or 2 values in one code for resolutions 1 and 2, one code each for 3 to 7 (each in one of two tables the frame
//     chooses by a bit), plain bits for 8 to 17, nothing for resolution 0 and for -1, the noise-substituted subbands whose values the decoder makes.
internal static class Sv7Syntax
{
    private static readonly CanonicalCode Header = new(MusepackTables.Sv7Header, default);
    private static readonly CanonicalCode Selection = new(MusepackTables.Sv7Scfi, default);
    private static readonly CanonicalCode Difference = new(MusepackTables.Sv7Dscf, default);
    private static readonly CanonicalCode[][] Samples = Tables();

    private static CanonicalCode[][] Tables()
    {
        static CanonicalCode Table(ReadOnlySpan<int> triples) => new(triples, default);
        return
        [
            [Table(MusepackTables.Sv7Q1A), Table(MusepackTables.Sv7Q1B)],
            [Table(MusepackTables.Sv7Q2A), Table(MusepackTables.Sv7Q2B)],
            [Table(MusepackTables.Sv7Q3A), Table(MusepackTables.Sv7Q3B)],
            [Table(MusepackTables.Sv7Q4A), Table(MusepackTables.Sv7Q4B)],
            [Table(MusepackTables.Sv7Q5A), Table(MusepackTables.Sv7Q5B)],
            [Table(MusepackTables.Sv7Q6A), Table(MusepackTables.Sv7Q6B)],
            [Table(MusepackTables.Sv7Q7A), Table(MusepackTables.Sv7Q7B)],
        ];
    }

    // The resolutions and mid/side flags at the start of the frame. Reports the highest subband that carries data and the facts the figures use; stops at the first overrun
    // (bits.Position past bits.Limit). Where a resolution is out of range the header is read on, so the facts are the frame's, and the problem is reported.
    public static void ReadHeader(SubbandState state, MsbBits bits, int maxBand, bool midSide, ref WalkFacts facts)
    {
        int[] left = state.Resolution[0];
        int[] right = state.Resolution[1];
        left[0] = bits.Read(4);
        right[0] = bits.Read(4);
        int used = 0;
        uint mask = 0;
        int flagged = 0;
        int noise = 0;
        bool invalid = false;
        for (int band = 0; band <= maxBand; band++)
        {
            if (band > 0)
            {
                left[band] = Next(bits, left[band - 1]);
                right[band] = Next(bits, right[band - 1]);
            }

            invalid |= left[band] is < -1 or > 17 || right[band] is < -1 or > 17;
            noise += (left[band] == -1 ? 1 : 0) + (right[band] == -1 ? 1 : 0);
            if (left[band] != 0 || right[band] != 0)
            {
                flagged++;
                if (midSide)
                {
                    bool flag = bits.Read(1) == 1;
                    state.MidSideFlag[band] = flag;
                    if (flag)
                    {
                        mask |= 1u << band;
                    }
                }

                used = band + 1;
            }

            if (bits.IsOverrun)
            {
                facts.Problems |= WalkProblems.HeaderOverrun;
                break;
            }
        }

        facts.UsedBands = used;
        facts.MidSide = mask;
        facts.Flagged = flagged;
        facts.Noise = noise;
        if (invalid)
        {
            facts.Problems |= WalkProblems.BadResolution;
        }
    }

    private static int Next(MsbBits bits, int previous)
    {
        Header.TryDecode(bits, out int change);
        return change != 4 ? previous + change : bits.Read(4);
    }

    // The scalefactor selections, scalefactors and samples, after ReadHeader found no problem.
    public static void ReadBody(SubbandState state, MsbBits bits, ref WalkFacts facts)
    {
        int used = facts.UsedBands;
        for (int band = 0; band < used; band++)
        {
            for (int channel = 0; channel < 2; channel++)
            {
                if (state.Resolution[channel][band] != 0)
                {
                    Selection.TryDecode(bits, out int selection);
                    state.Selection[channel][band] = selection;
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
                    ReadScalefactors(state.Scalefactor[channel].AsSpan(band * 3, 3), state.Selection[channel][band], bits);
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
                ReadSamples(state.Quantized[channel].AsSpan(band * SubbandState.SamplesPerBand, SubbandState.SamplesPerBand), state.Resolution[channel][band], bits);
                if (bits.IsOverrun)
                {
                    facts.Problems |= WalkProblems.DataOverrun;
                    return;
                }
            }
        }
    }

    // Each scalefactor of a subband and channel: after a selection of 1, the first and the second follow the one before them and the third repeats the second; 3, one value
    // repeated; 2, the first repeated and a third; 0, three.
    private static void ReadScalefactors(Span<int> scalefactor, int selection, MsbBits bits)
    {
        switch (selection)
        {
            case 1:
                scalefactor[0] = Value(bits, scalefactor[2]);
                scalefactor[1] = Value(bits, scalefactor[0]);
                scalefactor[2] = scalefactor[1];
                break;
            case 3:
                scalefactor[0] = Value(bits, scalefactor[2]);
                scalefactor[1] = scalefactor[0];
                scalefactor[2] = scalefactor[1];
                break;
            case 2:
                scalefactor[0] = Value(bits, scalefactor[2]);
                scalefactor[1] = scalefactor[0];
                scalefactor[2] = Value(bits, scalefactor[1]);
                break;
            default:
                scalefactor[0] = Value(bits, scalefactor[2]);
                scalefactor[1] = Value(bits, scalefactor[0]);
                scalefactor[2] = Value(bits, scalefactor[1]);
                break;
        }

        scalefactor[0] = SubbandState.Clamp(scalefactor[0]);
        scalefactor[1] = SubbandState.Clamp(scalefactor[1]);
        scalefactor[2] = SubbandState.Clamp(scalefactor[2]);
    }

    private static int Value(MsbBits bits, int previous)
    {
        Difference.TryDecode(bits, out int change);
        return change != 8 ? previous + change : bits.Read(6);
    }

    private static void ReadSamples(Span<int> values, int resolution, MsbBits bits)
    {
        switch (resolution)
        {
            case 1:
                {
                    CanonicalCode table = Samples[0][bits.Read(1)];
                    for (int k = 0; k < 36; k += 3)
                    {
                        table.TryDecode(bits, out int index);
                        values[k] = (index % 3) - 1;
                        values[k + 1] = ((index / 3) % 3) - 1;
                        values[k + 2] = (index / 9) - 1;
                    }

                    break;
                }

            case 2:
                {
                    CanonicalCode table = Samples[1][bits.Read(1)];
                    for (int k = 0; k < 36; k += 2)
                    {
                        table.TryDecode(bits, out int index);
                        values[k] = (index % 5) - 2;
                        values[k + 1] = (index / 5) - 2;
                    }

                    break;
                }

            case >= 3 and <= 7:
                {
                    CanonicalCode table = Samples[resolution - 1][bits.Read(1)];
                    for (int k = 0; k < 36; k++)
                    {
                        table.TryDecode(bits, out int value);
                        values[k] = value;
                    }

                    break;
                }

            case >= 8 and <= 17:
                {
                    int width = MusepackTables.ResolutionBits[resolution];
                    int offset = MusepackTables.Offsets[resolution + 1];
                    for (int k = 0; k < 36; k++)
                    {
                        values[k] = bits.ReadLong(width) - offset;
                    }

                    break;
                }
        }
    }
}
