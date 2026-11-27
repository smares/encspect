using Encspect.Formats.Aac;
using Encspect.Formats.Aac.Decoding;

namespace Encspect.Tests.Formats.Aac;

// The parametric stereo decoder's parameter mapping and resets (ISO/IEC 14496-3 8.6.4.6.1, 8.A.3), on their own and without a file.
public class AacPsDecoderTests
{
    private const int Slots = 32;
    private const int Bands = 64;

    // Table 8.45: the 34 bands take the 20-band parameters, bands 1 and 4 the integer averages of two (8.6.4.6.1), division truncating toward zero as in ANSI C.
    [Fact]
    public void Parameters_of_20_bands_map_to_34_with_the_averages_of_Table_8_45()
    {
        var table = new int[6, 34];
        int[] values = [-7, 6, 3, -4, 5, -1, 2, 7, -6, 0, 1, -2, 4, -3, 7, 5, -5, 2, 6, -7];
        for (int i = 0; i < 20; i++)
        {
            table[0, i] = values[i];
        }

        PsDecoder.Spread(table, 0);

        int[] wider = [0, 0, 1, 2, 2, 3, 4, 4, 5, 5, 6, 7, 8, 8, 9, 9, 10, 11, 12, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18, 18, 18, 18, 19, 19];
        for (int i = 0; i < 34; i++)
        {
            int expected = i switch { 1 => (values[0] + values[1]) / 2, 4 => (values[2] + values[3]) / 2, _ => values[wider[i]] };
            Assert.Equal(expected, table[0, i]);
        }

        Assert.Equal(0, table[0, 1]);
        Assert.Equal(0, table[0, 4]);
    }

    private static int[] Parameters(int iidMode, int iccMode)
    {
        var ps = new int[400];
        ps[SbrRecord.PsEnvelopes] = 1;
        ps[SbrRecord.PsEnableIid] = 1;
        ps[SbrRecord.PsIidMode] = iidMode;
        ps[SbrRecord.PsEnableIcc] = 1;
        ps[SbrRecord.PsIccMode] = iccMode;
        ps[SbrRecord.PsIid] = 3;
        ps[SbrRecord.PsIcc] = 2;
        return ps;
    }

    // A frame of the QMF domain with noise in the slots from 0 up to (not including) noiseSlots, zero after them: the decorrelator's delay lines hold the noise's last 14 slots at the end.
    private static (float[] Re, float[] Im) Frame(int noiseSlots, int seed)
    {
        var re = new float[Slots * Bands];
        var im = new float[Slots * Bands];
        var random = new Random(seed);
        for (int l = 0; l < noiseSlots; l++)
        {
            for (int k = 0; k < 40; k++)
            {
                re[(l * Bands) + k] = (float)((random.NextDouble() * 2) - 1);
                im[(l * Bands) + k] = (float)((random.NextDouble() * 2) - 1);
            }
        }

        return (re, im);
    }

    private static void Process(PsDecoder decoder, (float[] Re, float[] Im) frame, int[]? ps) =>
        decoder.Process(frame.Re, frame.Im, new float[6 * Bands], new float[6 * Bands], ps ?? [], Bands);

    // 8.A.3: the frame after one without ps_data starts from reset decorrelator states. Without the reset the delay lines of the frame without parameters (noise to its slot 25) come out
    // in the next frame, which has no input of its own.
    [Fact]
    public void The_decorrelator_is_reset_after_a_frame_without_parameters()
    {
        var decoder = new PsDecoder();
        Process(decoder, Frame(26, 1), Parameters(1, 1));
        Process(decoder, Frame(26, 2), null);
        Assert.True(decoder.HasDecorrelatorState);

        Process(decoder, Frame(0, 3), Parameters(1, 1));

        Assert.False(decoder.HasDecorrelatorState);
    }

    // A frame after one with parameters keeps the states (the test above is not a test of an always-empty decorrelator).
    [Fact]
    public void The_decorrelator_keeps_its_states_between_frames_with_parameters()
    {
        var decoder = new PsDecoder();
        Process(decoder, Frame(26, 1), Parameters(1, 1));
        Process(decoder, Frame(0, 3), Parameters(1, 1));

        Assert.True(decoder.HasDecorrelatorState);
    }

    // Table 8.47 and 8.6.4.6.1: a change of the number of stereo bands resets the state variables, the delay lines included.
    [Fact]
    public void The_decorrelator_is_reset_when_the_number_of_stereo_bands_changes()
    {
        var decoder = new PsDecoder();
        Process(decoder, Frame(26, 1), Parameters(1, 1));
        Assert.True(decoder.HasDecorrelatorState);

        Process(decoder, Frame(0, 3), Parameters(2, 2));

        Assert.False(decoder.HasDecorrelatorState);
    }
}
