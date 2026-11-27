using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Formats.Aac.Decoding;
using Encspect.Host;

namespace Encspect.Tests.Formats.Aac;

// The full parametric stereo decoder's additions to the baseline (ISO/IEC 14496-3 8.4.2, 8.6.4.6.2.2 and 8.6.4.6.3.2): the IPD and OPD codebooks, the phases the matrices take and mixing procedure Rb,
// on signals whose result follows from the formulas.
public class AacPsPhaseTests
{
    // Tables 8.B.20 and 8.B.21: each index's codeword, in the standard's order of columns df, dt.
    private static readonly string[][] Codewords =
    [
        ["1", "000", "0110", "0100", "0010", "0011", "0101", "0111"],
        ["1", "010", "0010", "00011", "00010", "0000", "0011", "011"],
        ["1", "001", "0110", "0100", "01111", "01110", "0101", "000"],
        ["1", "010", "0001", "00111", "00110", "0000", "0010", "011"],
    ];

    [Fact]
    public void TheCodebooksOfPhaseDifferencesDecodeTheStandardsCodewords()
    {
        for (int table = 0; table < 4; table++)
        {
            for (int symbol = 0; symbol < 8; symbol++)
            {
                // The codeword, then a run of ones that no codeword of these tables is a prefix of: the decoder consumes exactly the codeword.
                string word = Codewords[table][symbol];
                byte[] bytes = new byte[4];
                string padded = (word + "10101010101010101010101010101010")[..32];
                for (int i = 0; i < 32; i++)
                {
                    if (padded[i] == '1')
                    {
                        bytes[i / 8] |= (byte)(0x80 >> (i % 8));
                    }
                }

                var bits = new AacBits(bytes, 0, 32);
                int decoded = table switch
                {
                    0 => PsHuffman.DecodeIpdFrequency(ref bits),
                    1 => PsHuffman.DecodeIpdTime(ref bits),
                    2 => PsHuffman.DecodeOpdFrequency(ref bits),
                    _ => PsHuffman.DecodeOpdTime(ref bits),
                };
                Assert.Equal(symbol, decoded);
                Assert.Equal(word.Length, bits.Original);
            }
        }
    }

    [Fact]
    public void TheCodebooksAreCompletePrefixCodes()
    {
        foreach (string[] words in Codewords)
        {
            Assert.Equal(1.0, words.Sum(w => Math.Pow(2, -w.Length)));
            Assert.All(words, a => Assert.DoesNotContain(words, b => !ReferenceEquals(a, b) && b.StartsWith(a, StringComparison.Ordinal)));
        }
    }

    // Procedure Rb of an intensity difference of 0 dB (c = 1: alpha is a quarter of pi) and the coherence index k: mu = rho squared, so h11 = h12 = cos(gamma) and h22 = -h21 = sin(gamma) with
    // gamma = arctan(sqrt((1 - rho) / (1 + rho))), rho floored at 0.05.
    [Fact]
    public void MixingProcedureRbOfEqualChannelsFollowsTheFormulas()
    {
        float[] rhos = [1.0f, 0.937f, 0.84118f, 0.60092f, 0.36764f, 0.0f, -0.589f, -1.0f];
        for (int k = 0; k < 8; k++)
        {
            double rho = Math.Max(rhos[k], 0.05);
            double gamma = Math.Atan(Math.Sqrt((1 - rho) / (1 + rho)));
            float[] row = PsDecoder.CoarseMixB.AsSpan(((7 * 8) + k) * 4, 4).ToArray();
            Assert.Equal(Math.Cos(gamma), row[0], 1e-6);
            Assert.Equal(Math.Cos(gamma), row[1], 1e-6);
            Assert.Equal(-Math.Sin(gamma), row[2], 1e-6);
            Assert.Equal(Math.Sin(gamma), row[3], 1e-6);
        }
    }

    // The columns of Rb are orthogonal and of the same length, whatever the difference: the pair's powers add up to twice the mono signal's (the matrices of Ra do the same).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixingProcedureRbKeepsThePowerOfTheSumOfBothChannels(bool fine)
    {
        float[] table = fine ? PsDecoder.FineMixB : PsDecoder.CoarseMixB;
        for (int at = 0; at < table.Length; at += 4)
        {
            double h11 = table[at];
            double h12 = table[at + 1];
            double h21 = table[at + 2];
            double h22 = table[at + 3];
            Assert.Equal(2.0, (h11 * h11) + (h12 * h12) + (h21 * h21) + (h22 * h22), 1e-5);
            Assert.Equal(0.0, (h11 * h21) + (h12 * h22), 1e-5);
        }
    }

    // An intensity difference of 0, coherence 1 and the phases settled (a frame's smoothing counts the two envelopes before it, so three frames of the same phases make them whole): the left channel
    // takes the overall phase, the right the overall phase less the inter-channel phase, as rotations of the mono signal.
    [Theory]
    [InlineData(4, 0, 1, 0, -1, 0)]
    [InlineData(0, 2, 0, 1, 0, 1)]
    [InlineData(2, 0, 1, 0, 0, -1)]
    [InlineData(2, 2, 0, 1, 1, 0)]
    public void TheMatricesTakeThePhasesOfTheIpdAndOpd(int ipd, int opd, int leftReFromRe, int leftReFromIm, int rightReFromRe, int rightReFromIm)
    {
        // The expectations name the real part of each channel as a combination of the input's real and imaginary parts; the imaginary part follows from the rotation by a multiple of a quarter turn
        // (a turn of j maps (re, im) to (-im, re), so the left channel's imaginary part takes re from the rotation's imaginary column).
        (float[] re, float[] im) = Run(iidMode: 1, ipd, opd, mixB: false, frames: 5, out PsDecoder ps);
        int row = 20 * 64;
        for (int band = 3; band <= 5; band++)
        {
            float inRe = re[row + band];
            float inIm = im[row + band];
            Assert.Equal((leftReFromRe * inRe) + (leftReFromIm * inIm * -1), ps.LeftRe[row + band], 1e-4f);
            Assert.Equal((leftReFromRe * inIm) + (leftReFromIm * inRe), ps.LeftIm[row + band], 1e-4f);
            Assert.Equal((rightReFromRe * inRe) + (rightReFromIm * inIm * -1), ps.RightRe[row + band], 1e-4f);
            Assert.Equal((rightReFromRe * inIm) + (rightReFromIm * inRe), ps.RightIm[row + band], 1e-4f);
        }

        // Above the bands that carry phases (parameter index 10 of the 20 is the last, at the fifth channel) nothing turns.
        for (int band = 12; band < 40; band++)
        {
            Assert.Equal(re[row + band], ps.LeftRe[row + band], 1e-4f);
            Assert.Equal(re[row + band], ps.RightRe[row + band], 1e-4f);
        }
    }

    // In the 10-band mode the five parameters cover 20-band parameters 0 to 9, so the fifth QMF channel (parameter 10) stays as it was.
    [Fact]
    public void TheBandsOfTheTenBandModeCarryFiveParameters()
    {
        (float[] re, _, PsDecoder ps) = RunWithMode(0, 4, 0);
        int row = 20 * 64;
        for (int band = 3; band <= 4; band++)
        {
            Assert.Equal(re[row + band], ps.LeftRe[row + band], 1e-4f);
            Assert.Equal(-re[row + band], ps.RightRe[row + band], 1e-4f);
        }

        Assert.Equal(re[row + 5], ps.RightRe[row + 5], 1e-4f);
    }

    // The differences across time add to the last envelope's indices modulo 8: 2 and then 6 is 0, and the phases die away.
    [Fact]
    public void TheDifferencesAcrossTimeAddModuloEight()
    {
        var ps = new PsDecoder();
        int[] data = Data(1, 2, 0, mixB: false);
        (float[] re, float[] im) = Signal();
        float[] ahead = new float[6 * 64];
        for (int frame = 0; frame < 4; frame++)
        {
            ps.Process(re, im, ahead, ahead, data, 64);
        }

        int row = 20 * 64;
        Assert.Equal(im[row + 4], ps.RightRe[row + 4], 1e-4f);
        data[SbrRecord.PsIpdTime] = 1;
        for (int b = 0; b < 17; b++)
        {
            data[SbrRecord.PsIpd + b] = 6;
        }

        for (int frame = 0; frame < 4; frame++)
        {
            ps.Process(re, im, ahead, ahead, data, 64);
            for (int b = 0; b < 17; b++)
            {
                data[SbrRecord.PsIpd + b] = 0;
            }
        }

        Assert.Equal(re[row + 4], ps.RightRe[row + 4], 1e-4f);
        Assert.Equal(im[row + 4], ps.RightIm[row + 4], 1e-4f);
    }

    // A frame without phases after frames with them takes the real matrices at once where it holds no envelope, and a frame that holds the phases (an extension without envelopes) keeps them.
    [Fact]
    public void AnEnvelopeLessFrameKeepsOrDropsThePhases()
    {
        var ps = new PsDecoder();
        int[] data = Data(1, 4, 0, mixB: false);
        (float[] re, float[] im) = Signal();
        float[] ahead = new float[6 * 64];
        for (int frame = 0; frame < 4; frame++)
        {
            ps.Process(re, im, ahead, ahead, data, 64);
        }

        int row = 20 * 64;
        Assert.Equal(-re[row + 4], ps.RightRe[row + 4], 1e-4f);
        int[] held = Data(1, 4, 0, mixB: false);
        held[SbrRecord.PsEnvelopes] = 0;
        ps.Process(re, im, ahead, ahead, held, 64);
        Assert.Equal(-re[row + 4], ps.RightRe[row + 4], 1e-4f);
        held[SbrRecord.PsEnableIpdopd] = 0;
        ps.Process(re, im, ahead, ahead, held, 64);
        Assert.Equal(re[row + 4], ps.RightRe[row + 4], 1e-4f);
    }

    // The flag of phase differences is the block's own, whether or not the walk stores its values for a decoder: after blocks with phases, blocks without the extension count as having none.
    [Fact]
    public void ABlockWithoutTheExtensionHasNoPhasesWhetherOrNotItIsDecoded()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "adts", "ps-phase-then-none.aac"));
        foreach (bool decode in new[] { false, true })
        {
            var options = new AnalysisOptions { Decoding = decode ? new DecodeOptions { MaxWork = long.MaxValue, Output = _ => new Encspect.Tests.Decoding.DecoderWorkTests.NullSink() } : null };
            Report report = Analyzer.CreateDefault().Analyze("ps-phase-then-none.aac", new MemoryStream(file), options);
            Node phase = Fixtures.Resolve(report.Streams.Single().Nodes[0], "Statistics/SBR/Parametric Stereo/Parametric Stereo Phase").Single();
            Assert.Equal("6 raw data blocks (50.0%)", phase.Children.Single(c => c.Name == "Phase differences").Value!.ToString());
        }
    }

    // Phases held through a frame without envelopes (Table 8.52) are the basis of the next frame's differences across time, and its smoothing history.
    [Fact]
    public void HeldPhasesAreTheBasisOfTheNextFramesDifferences()
    {
        var ps = new PsDecoder();
        int[] data = Data(1, 4, 0, mixB: false);
        (float[] re, float[] im) = Signal();
        float[] ahead = new float[6 * 64];
        for (int frame = 0; frame < 4; frame++)
        {
            ps.Process(re, im, ahead, ahead, data, 64);
        }

        int[] held = Data(1, 4, 0, mixB: false);
        held[SbrRecord.PsEnvelopes] = 0;
        ps.Process(re, im, ahead, ahead, held, 64);
        int[] next = Data(1, 0, 0, mixB: false);
        next[SbrRecord.PsIpdTime] = 1;
        next[SbrRecord.PsOpdTime] = 1;
        for (int frame = 0; frame < 4; frame++)
        {
            ps.Process(re, im, ahead, ahead, next, 64);
        }

        int row = 20 * 64;
        Assert.Equal(-re[row + 4], ps.RightRe[row + 4], 1e-4f);
    }

    // The envelope that VAR_BORDERS appends after the last border holds the matrices of the envelope before it: the last region of the frame does not turn on.
    [Fact]
    public void TheAppendedEnvelopeHoldsTheLastMatrices()
    {
        var ps = new PsDecoder();
        int[] data = Data(1, 2, 0, mixB: false);
        data[SbrRecord.PsFrameClass] = 1;
        data[SbrRecord.PsBorders + 1] = 16;
        float[] re = new float[32 * 64];
        float[] im = new float[32 * 64];
        Array.Fill(re, 1f);
        Array.Fill(im, 0.5f);
        float[] ahead = new float[6 * 64];

        // Alternating phases, so that the history is not constant: a second smoothing of the appended envelope would differ from the held matrices.
        for (int frame = 0; frame < 6; frame++)
        {
            data[SbrRecord.PsIpd] = frame % 2 == 0 ? 2 : 6;
            ps.Process(re, im, ahead, ahead, data, 64);
        }

        Assert.Equal(ps.RightRe[20 * 64 + 4], ps.RightRe[30 * 64 + 4], 1e-4f);
        Assert.Equal(ps.RightIm[20 * 64 + 4], ps.RightIm[30 * 64 + 4], 1e-4f);
    }

    // Until the first ps_data the output is the mono signal in both channels, and from it the matrices start at zero (8.6.4.6.4 a): the first slot is nearly silent and the gains ramp up.
    [Fact]
    public void TheMatricesOfTheFirstFrameStartAtZero()
    {
        var ps = new PsDecoder();
        int[] data = Data(1, 0, 0, mixB: false);
        data[SbrRecord.PsEnableIpdopd] = 0;
        float[] re = new float[32 * 64];
        float[] im = new float[32 * 64];
        Array.Fill(re, 1f);
        float[] ahead = new float[6 * 64];
        ps.Process(re, im, ahead, ahead, data, 64);
        Assert.True(Math.Abs(ps.LeftRe[(0 * 64) + 20]) < 0.1f);
        Assert.Equal(1f, ps.LeftRe[(31 * 64) + 20], 0.05f);
    }

    private (float[] Re, float[] Im, PsDecoder Ps) RunWithMode(int iidMode, int ipd, int opd)
    {
        (float[] re, float[] im) = Run(iidMode, ipd, opd, mixB: false, frames: 5, out PsDecoder ps);
        return (re, im, ps);
    }

    private static (float[] Re, float[] Im) Run(int iidMode, int ipd, int opd, bool mixB, int frames, out PsDecoder ps)
    {
        ps = new PsDecoder();
        int[] data = Data(iidMode, ipd, opd, mixB);
        (float[] re, float[] im) = Signal();
        float[] ahead = new float[6 * 64];
        for (int frame = 0; frame < frames; frame++)
        {
            ps.Process(re, im, ahead, ahead, data, 64);
        }

        return (re, im);
    }

    private static (float[] Re, float[] Im) Signal()
    {
        float[] re = new float[32 * 64];
        float[] im = new float[32 * 64];
        for (int i = 0; i < re.Length; i++)
        {
            re[i] = ((i % 3) - 1f) + 0.5f;
            im[i] = ((i % 5) - 2f) + 0.25f;
        }

        return (re, im);
    }

    // One envelope, intensity difference 0 and coherence index 0 in every band, and the phases of every band: ipd and opd as the first index with no differences after it.
    private static int[] Data(int iidMode, int ipd, int opd, bool mixB)
    {
        int[] data = new int[SbrRecord.PsEnd];
        data[SbrRecord.PsHasHeader] = 1;
        data[SbrRecord.PsEnableIid] = 1;
        data[SbrRecord.PsIidMode] = iidMode;
        data[SbrRecord.PsEnableIcc] = 1;
        data[SbrRecord.PsIccMode] = iidMode;
        data[SbrRecord.PsMixingB] = mixB ? 1 : 0;
        data[SbrRecord.PsFrameClass] = 0;
        data[SbrRecord.PsEnvelopes] = 1;
        data[SbrRecord.PsEnableIpdopd] = 1;
        data[SbrRecord.PsIpd] = ipd;
        data[SbrRecord.PsOpd] = opd;
        return data;
    }
}
