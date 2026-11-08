using Encspect.Formats.MonkeysAudio.Decoding;

namespace Encspect.Tests;

// The loops of the neural network filters against plain loops, on every length from 0 to 300 (so every remainder of 8, 16 and 32), at
// misaligned offsets, with extreme values and with lengths that differ. Each loop is reached through its public entry, which takes the widest
// path of the processor, and through the 128-bit entry, which a processor with AVX2 would not reach from the public one.
public sealed class NnVectorsTests
{
    private const int MaxLength = 300;
    private const int Padding = 8;

    public static TheoryData<int> Seeds => [1, 2, 3];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheDotProductOf16BitValuesEqualsTheScalarSumOnEveryLength(int seed)
    {
        var random = new Random(seed);
        foreach (short[] values in Fills16(random))
        {
            for (int length = 0; length <= MaxLength; length++)
            {
                int offset = length % 4;
                short[] a = Filled16(random, values, length + Padding);
                short[] b = Filled16(random, values, length + Padding);
                var left = a.AsSpan(offset, length);
                var right = b.AsSpan(offset, length);
                int expected = 0;
                for (int i = 0; i < length; i++)
                {
                    expected = unchecked(expected + (left[i] * right[i]));
                }

                Assert.Equal(expected, NnVectors.Dot(left, right));
                Assert.Equal(expected, NnVectors.Dot128(left, right));

                // The shorter of two spans sets the length.
                Assert.Equal(expected, NnVectors.Dot(a.AsSpan(offset, length + 3), right));
                Assert.Equal(expected, NnVectors.Dot128(left, b.AsSpan(offset, length + 5)));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheDotProductOf32BitValuesEqualsTheScalarSumOnEveryLength(int seed)
    {
        var random = new Random(seed);
        foreach (int[] values in Fills32(random))
        {
            for (int length = 0; length <= MaxLength; length++)
            {
                int offset = length % 4;
                int[] a = Filled32(random, values, length + Padding);
                int[] b = Filled32(random, values, length + Padding);
                var left = a.AsSpan(offset, length);
                var right = b.AsSpan(offset, length);
                long expected = 0;
                for (int i = 0; i < length; i++)
                {
                    expected += unchecked(left[i] * right[i]);
                }

                Assert.Equal(expected, NnVectors.Dot(left, right));
                Assert.Equal(expected, NnVectors.Dot(a.AsSpan(offset, length + 3), right));
                Assert.Equal(expected, NnVectors.Dot(left, b.AsSpan(offset, length + 5)));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheStepOf16BitWeightsEqualsTheScalarStepInBothDirections(int seed)
    {
        var random = new Random(seed);
        foreach (short[] values in Fills16(random))
        {
            for (int length = 0; length <= MaxLength; length++)
            {
                foreach (bool isSubtract in new[] { false, true })
                {
                    int offset = length % 4;
                    short[] weights = Filled16(random, values, length + Padding);
                    short[] delta = Filled16(random, values, length + Padding);
                    short[] expected = [.. weights];
                    for (int i = 0; i < length; i++)
                    {
                        expected[offset + i] = unchecked((short)(weights[offset + i] + (isSubtract ? -delta[offset + i] : delta[offset + i])));
                    }

                    short[] wide = [.. weights];
                    NnVectors.AddOrSubtract(wide.AsSpan(offset, length), delta.AsSpan(offset, length + 3), isSubtract);
                    Assert.Equal(expected, wide);

                    short[] narrow = [.. weights];
                    NnVectors.AddOrSubtract128(narrow.AsSpan(offset, length), delta.AsSpan(offset, length + 5), isSubtract);
                    Assert.Equal(expected, narrow);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheStepOf32BitWeightsEqualsTheScalarStepInBothDirections(int seed)
    {
        var random = new Random(seed);
        foreach (int[] values in Fills32(random))
        {
            for (int length = 0; length <= MaxLength; length++)
            {
                foreach (bool isSubtract in new[] { false, true })
                {
                    int offset = length % 4;
                    int[] weights = Filled32(random, values, length + Padding);
                    int[] delta = Filled32(random, values, length + Padding);
                    int[] expected = [.. weights];
                    for (int i = 0; i < length; i++)
                    {
                        expected[offset + i] = unchecked(weights[offset + i] + (isSubtract ? -delta[offset + i] : delta[offset + i]));
                    }

                    int[] wide = [.. weights];
                    NnVectors.AddOrSubtract(wide.AsSpan(offset, length + 2), delta.AsSpan(offset, length), isSubtract);
                    Assert.Equal(expected, wide);

                    int[] narrow = [.. weights];
                    NnVectors.AddOrSubtract128(narrow.AsSpan(offset, length), delta.AsSpan(offset, length + 5), isSubtract);
                    Assert.Equal(expected, narrow);
                }
            }
        }
    }

    // Pools to draw from: random values, the extremes alone (so -32768 times -32768 pairs and wrapping sums come up in every position), and a mix.
    private static short[][] Fills16(Random random) =>
    [
        [.. Enumerable.Range(0, 64).Select(_ => (short)random.Next(short.MinValue, short.MaxValue + 1))],
        [short.MinValue],
        [short.MaxValue],
        [short.MinValue, short.MaxValue, 0, -1, 1],
    ];

    private static int[][] Fills32(Random random) =>
    [
        [.. Enumerable.Range(0, 64).Select(_ => random.Next(int.MinValue, int.MaxValue))],
        [int.MinValue],
        [int.MaxValue],
        [int.MinValue, int.MaxValue, 0, -1, 1, 65536, -65536],
    ];

    private static short[] Filled16(Random random, short[] pool, int length) =>
        [.. Enumerable.Range(0, length).Select(_ => pool[random.Next(pool.Length)])];

    private static int[] Filled32(Random random, int[] pool, int length) =>
        [.. Enumerable.Range(0, length).Select(_ => pool[random.Next(pool.Length)])];
}
