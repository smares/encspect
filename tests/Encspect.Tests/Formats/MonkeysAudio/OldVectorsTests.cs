using Encspect.Formats.MonkeysAudio.Decoding;

namespace Encspect.Tests;

// The filter of 16 taps of the old high predictor against a plain loop; the 128-bit, 256-bit and scalar entries are all reached, whatever the processor.
public sealed class OldVectorsTests
{
    private delegate int Taps(ReadOnlySpan<int> values, Span<int> weights, int direction);

    public static TheoryData<int> Seeds => [1, 2, 3, 4];

    private static int[] Fill(Random random, int length, int mode) => [.. Enumerable.Range(0, length).Select(_ => mode switch
    {
        0 => random.Next(-40000, 40000),
        1 => random.Next(int.MinValue, int.MaxValue),
        2 => random.Next(3) switch { 0 => int.MinValue, 1 => int.MaxValue, _ => 0 },
        _ => random.Next(-3, 4),
    })];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheFilterOf16TapsEqualsThePlainLoopOnEveryEntry(int seed)
    {
        Check(seed, OldVectors.Taps, OldVectors.HighTaps, OldVectors.HighTaps256, OldVectors.HighTaps128, OldVectors.HighTapsScalar, Reference);

        static int Reference(ReadOnlySpan<int> values, Span<int> weights, int direction)
        {
            int dot = 0;
            for (int j = 0; j < OldVectors.Taps; j++)
            {
                dot = unchecked(dot + (values[j] * weights[j]));
                int step = ((values[j] >> 30) & 2) - 1;
                weights[j] = unchecked(direction > 0 ? weights[j] + step : direction < 0 ? weights[j] - step : weights[j]);
            }

            return dot;
        }
    }

    private static void Check(int seed, int taps, Taps entry, Taps wide, Taps medium, Taps scalar, Taps reference)
    {
        var random = new Random(seed);
        for (int round = 0; round < 2000; round++)
        {
            int mode = round % 4;
            int[] values = Fill(random, taps + 3, mode);
            int[] weights = Fill(random, taps + 3, (round / 4) % 4);
            int direction = (round % 3) - 1;
            int[] expectedWeights = [.. weights];
            int expected = reference(values.AsSpan(1, taps), expectedWeights.AsSpan(2, taps), direction);
            foreach (Taps run in new[] { entry, wide, medium, scalar })
            {
                int[] w = [.. weights];
                Assert.Equal(expected, run(values.AsSpan(1, taps), w.AsSpan(2, taps), direction));
                Assert.Equal(expectedWeights, w);
            }
        }
    }
}
