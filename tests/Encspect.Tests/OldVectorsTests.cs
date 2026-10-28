using Encspect.Formats.MonkeysAudio.Decoding;

namespace Encspect.Tests;

// The filters of 16 and 8 taps of the old predictors and the old bit reader against plain loops; the 128-bit, 256-bit and scalar entries are all reached, whatever the processor.
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

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheFilterOf8TapsEqualsThePlainLoopOnEveryEntry(int seed)
    {
        Check(seed, OldVectors.ShortTaps, OldVectors.EightTaps, OldVectors.EightTaps256, OldVectors.EightTaps128, OldVectors.EightTapsScalar, Reference);

        static int Reference(ReadOnlySpan<int> values, Span<int> weights, int direction)
        {
            int dot = 0;
            for (int j = 0; j < OldVectors.ShortTaps; j++)
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

    [Fact]
    public void TheBitReaderEqualsABitByBitReaderOnEveryPositionAndWidth()
    {
        var random = new Random(9);
        foreach (int dataLength in new[] { 1, 7, 8, 9, 15, 40, 41 })
        {
            foreach (int slack in new[] { 0, 5 })
            {
                byte[] data = new byte[dataLength + slack];
                random.NextBytes(data);
                for (int lengthBytes = 0; lengthBytes <= dataLength; lengthBytes++)
                {
                    for (long start = 0; start <= (lengthBytes * 8L) + 3; start++)
                    {
                        for (int bits = 0; bits <= 32; bits++)
                        {
                            var bitsReader = new OldBits();
                            bitsReader.Begin(data, lengthBytes, start);
                            uint expected = 0;
                            bool fits = bits == 0 || start + bits <= lengthBytes * 8L;
                            if (fits)
                            {
                                for (int b = 0; b < bits; b++)
                                {
                                    long at = start + b;
                                    expected = (expected << 1) | (uint)((data[at >> 3] >> (7 - (int)(at & 7))) & 1);
                                }

                                Assert.Equal(expected, bitsReader.Read(bits));
                                Assert.Equal(start + bits, bitsReader.Position);
                            }
                            else
                            {
                                Assert.Throws<InvalidDataException>(() => bitsReader.Read(bits));
                            }
                        }

                        var unary = new OldBits();
                        unary.Begin(data, lengthBytes, start);
                        long limit = lengthBytes * 8L;
                        long p = start;
                        while (p < limit && ((data[p >> 3] >> (7 - (int)(p & 7))) & 1) == 0)
                        {
                            p++;
                        }

                        if (p < limit)
                        {
                            Assert.Equal((int)(p - start), unary.ReadUnary());
                            Assert.Equal(p + 1, unary.Position);
                        }
                        else
                        {
                            Assert.Throws<InvalidDataException>(() => unary.ReadUnary());
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void AUnaryRunOfManyZerosIsReadAcrossBytes()
    {
        foreach (int zeros in new[] { 55, 56, 57, 63, 64, 65, 120, 200 })
        {
            foreach (int offset in new[] { 0, 3, 7 })
            {
                byte[] data = new byte[40];
                int one = offset + zeros;
                data[one >> 3] = (byte)(0x80 >> (one & 7));
                var reader = new OldBits();
                reader.Begin(data, 40, offset);
                Assert.Equal(zeros, reader.ReadUnary());
                Assert.Equal(one + 1, reader.Position);
            }
        }
    }
}
