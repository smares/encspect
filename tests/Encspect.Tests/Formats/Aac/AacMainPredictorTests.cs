using System.Security.Cryptography;
using Encspect.Core;
using Encspect.Formats.Aac.Decoding;

namespace Encspect.Tests.Formats.Aac;

// AAC Main's predictor bank (ISO/IEC 13818-7 13.3.2 and 13.3.3) against what the specification fixes: the rounding of the estimate and of b / VAR, the truncation of the states, the
// reset groups of Table 63, the flush below 1e-30, and its output for a scripted input, whose hash must be the same on every platform.
public class AacMainPredictorTests
{
    private const string ScriptedHash = "3b4bd8d912d4f5e767ce6ce8620209b1caca9945d54a0e4711e9911b4754d9d0";

    private static float Bits(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    [Fact]
    public void TheTableOfBOverVarHoldsTheRoundedRatiosTheSpecificationBuilds()
    {
        float[] mantissas = MainPredictor.Mantissas;
        float[] exponents = MainPredictor.Exponents;

        Assert.Equal(0.953125f, mantissas[0]);
        Assert.All(mantissas, m => Assert.InRange(m, 0.47f, 0.9532f));
        Assert.All(mantissas, m => Assert.Equal(0u, BitConverter.SingleToUInt32Bits(m) & 0xFFFFu));
        Assert.Equal(0f, exponents[0]);
        Assert.Equal(0f, exponents[127]);
        Assert.Equal(0.5f, exponents[128]);
        Assert.Equal(0.25f, exponents[129]);
        Assert.Equal(0f, exponents[255]);
    }

    [Theory]
    [InlineData(0x3F800000u, 0x3F800000u)]
    [InlineData(0x3F807FFFu, 0x3F800000u)]
    [InlineData(0x3F808000u, 0x3F810000u)]
    [InlineData(0x3F80FFFFu, 0x3F810000u)]
    [InlineData(0x3FFFFFFFu, 0x40000000u)]
    [InlineData(0xBF808000u, 0xBF810000u)]
    [InlineData(0x00008000u, 0x00010000u)]
    public void TheEstimateRoundsToASevenBitMantissaWithHalvesAwayFromZero(uint input, uint expected) =>
        Assert.Equal(expected, BitConverter.SingleToUInt32Bits(MainPredictor.RoundInfinity(Bits(input))));

    [Fact]
    public void AStateKeepsTheUpper16BitsOfItsWord() =>
        Assert.Equal(0x3F81u << 16, BitConverter.SingleToUInt32Bits(MainPredictor.Truncate(Bits(0x3F81FFFFu))));

    [Fact]
    public void AGroupResetsEveryThirtiethLineAndLeavesTheOthers()
    {
        var predictor = new MainPredictor(100);
        var modes = new byte[100];
        for (int round = 0; round < 6; round++)
        {
            var spectrum = new float[100];
            for (int i = 0; i < 100; i++)
            {
                spectrum[i] = (1000 + (37 * i)) * (round % 2 == 0 ? 1 : 0.5f);
            }

            predictor.Process(spectrum, modes);
        }

        // Group 3 holds the lines 2, 32, 62 and 92. A line that uses prediction on a spectrum of zero returns its estimate, which is zero where the predictor is in its initial state.
        predictor.ResetGroup(3);
        modes.AsSpan().Fill(1);
        var estimates = new float[100];
        predictor.Process(estimates, modes);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(i % 30 == 2, estimates[i] == 0f);
        }
    }

    [Fact]
    public void ANoiseLineResetsItsPredictorAndKeepsTheSpectrum()
    {
        var predictor = new MainPredictor(4);
        var spectrum = new float[] { 1000, 2000, 3000, 4000 };
        var modes = new byte[4];
        for (int round = 0; round < 5; round++)
        {
            predictor.Process(spectrum, modes);
        }

        modes[1] = 2;
        var noisy = new float[] { 1000, 123.5f, 3000, 4000 };
        predictor.Process(noisy, modes);

        Assert.Equal(123.5f, noisy[1]);
    }

    [Fact]
    public void ValuesBeyondTheLimitStayFiniteWhateverTheStreamCodes()
    {
        var predictor = new MainPredictor(8);
        var spectrum = new float[8];
        var modes = new byte[8];
        modes.AsSpan().Fill(1);
        for (int round = 0; round < 2000; round++)
        {
            for (int i = 0; i < 8; i++)
            {
                spectrum[i] = round % 3 == 0 ? 7.2e16f : -7.2e16f;
            }

            predictor.Process(spectrum, modes);
            Assert.All(spectrum, v => Assert.True(float.IsFinite(v)));
        }
    }

    [Fact]
    public void TheOutputForAScriptedInputIsTheSameOnEveryPlatform()
    {
        var predictor = new MainPredictor(672);
        var spectrum = new float[1024];
        var modes = new byte[672];
        uint state = 12345;
        var bytes = new List<byte>();
        for (int frame = 0; frame < 300; frame++)
        {
            for (int i = 0; i < 1024; i++)
            {
                state = (state * 1664525u) + 1013904223u;
                double tone = 3000 * DetMath.Sin((i + 1) * 0.01 * (frame + 20) / 20.0);
                spectrum[i] = (float)(tone + (((state >> 12) / (double)(1 << 20)) - 0.5) * 400) / (1 + (i / 50));
            }

            for (int i = 0; i < 672; i++)
            {
                modes[i] = (byte)(i % 7 == 3 ? 0 : i % 41 == 5 ? 2 : 1);
            }

            if (frame % 30 == 29)
            {
                predictor.Flush();
            }

            predictor.Process(spectrum, modes);
            predictor.ResetGroup(frame % 30 + 1);
            foreach (float v in spectrum.AsSpan(0, 672))
            {
                bytes.AddRange(BitConverter.GetBytes(v));
            }
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
        Assert.True(hash == ScriptedHash, $"The output hashes to {hash}.");
    }

    [Fact]
    public void TheTrustAfterALossFollowsTheGroupsAndTheHistory()
    {
        var trust = new DependencyTrust(2);

        // A stream's start is synchronized.
        Assert.False(trust.Depends(0, DependencyTrust.AllGroups, 700, 0));

        trust.Lose(10 * 1024, true);
        Assert.True(trust.IsOverlapUntrusted);
        Assert.True(trust.Depends(0, 1u << 4, -1, 10 * 1024));
        Assert.False(trust.Depends(0, 0, -1, 10 * 1024));
        Assert.True(trust.Depends(1, 0, 0, 10 * 1024));

        trust.Settle(true, true, 11 * 1024);
        trust.Resynchronize(0, 5);
        Assert.False(trust.Depends(0, 1u << 4, -1, 11 * 1024));
        Assert.True(trust.Depends(0, 1u << 5, -1, 11 * 1024));

        // The history is right again once the lag reaches no further back than the last withheld block ends.
        trust.Settle(false, false, 12 * 1024);
        Assert.False(trust.IsOverlapUntrusted);
        Assert.True(trust.Depends(1, 0, 1, 11 * 1024));
        Assert.False(trust.Depends(1, 0, 1024, 12 * 1024 + 1024 + 1));
        trust.ResynchronizeAll(0);
        Assert.False(trust.Depends(0, DependencyTrust.AllGroups, -1, 20 * 1024));
    }
}
