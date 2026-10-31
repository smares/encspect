// The loop below is copied from libwavpack 5.6.0 (unpack_floats.c), Copyright (c) 1998 - 2022 David Bryant, under the BSD 3-clause license.
// The license text is in THIRD-PARTY-NOTICES.md.

using Encspect.Formats.WavPack.Decoding;

namespace Encspect.Tests;

// The closed form of the exponent loop of libwavpack's float_values (unpack_floats.c 70-71 and 118-119) against the loop, ported unchanged:
// the same value, exponent and shift count for every input, so the output and the CRC over the floats are the same
// (docs/design/decoders.md, "What each decoder counts", WavPack, floats).
public class WavPackFloatTests
{
    // The loop as libwavpack has it.
    private static void Loop(ref int value, ref int exponent, ref int shiftCount)
    {
        while ((value & 0x800000) == 0 && --exponent != 0)
        {
            shiftCount++;
            value = unchecked((int)((uint)value << 1));
        }
    }

    private static void AssertSame(int value, int exponent)
    {
        int v1 = value, e1 = exponent, s1 = 0;
        int v2 = value, e2 = exponent, s2 = 0;

        Loop(ref v1, ref e1, ref s1);
        PcmBlockDecoder.Normalize(ref v2, ref e2, ref s2);

        Assert.True((v1, e1, s1) == (v2, e2, s2), $"value 0x{value:X8} exponent {exponent}: the loop gives ({v1:X8}, {e1}, {s1}) and the closed form ({v2:X8}, {e2}, {s2}).");
    }

    private static IEnumerable<int> Edges()
    {
        yield return 0;
        yield return 1;
        yield return -1;
        yield return int.MinValue;
        yield return int.MaxValue;
        yield return 0x7FFFFF;
        yield return 0x800000;
        yield return 0x800001;
        yield return 0xFFFFFF;
        yield return 0x1000000;
        yield return 0x1000001;
        yield return 0x3FFFFFF;
        for (int bit = 0; bit < 32; bit++)
        {
            int one = 1 << bit;
            yield return one;
            yield return one - 1;
            yield return one + 1;
            yield return ~one;
            yield return -one;
            yield return one | (one >> 1);
        }
    }

    [Fact]
    public void TheClosedFormGivesWhatTheLoopGivesForEveryExponentAndTheValuesAroundTheEdges()
    {
        foreach (int value in Edges())
        {
            for (int exponent = 1; exponent <= 255; exponent++)
            {
                AssertSame(value, exponent);
            }
        }
    }

    [Fact]
    public void TheClosedFormGivesWhatTheLoopGivesForAValueThatBecomesZeroOnlyAfterTheShift()
    {
        // 1 shifted up by 31 is int.MinValue, which negating leaves negative: it is not 0 at the start, and the first shift of the loop makes it
        // 0, after which the bit 0x800000 can never be set.
        int shifted = unchecked((int)(1u << 31));
        Assert.Equal(int.MinValue, shifted);
        for (int exponent = 1; exponent <= 255; exponent++)
        {
            AssertSame(unchecked(-shifted), exponent);
            AssertSame(shifted, exponent);

            int value = unchecked(-shifted), left = exponent, count = 0;
            PcmBlockDecoder.Normalize(ref value, ref left, ref count);
            Assert.Equal(0, left);
            Assert.Equal(exponent - 1, count);
            Assert.Equal(exponent >= 2 ? 0 : int.MinValue, value);
        }
    }

    [Fact]
    public void TheClosedFormGivesWhatTheLoopGivesForSmallAndRandomValues()
    {
        for (int value = -70_000; value <= 70_000; value++)
        {
            AssertSame(value, 1 + (Math.Abs(value) % 255));
            AssertSame(value, 255);
            AssertSame(value, 24);
        }

        var random = new Random(31);
        for (int i = 0; i < 400_000; i++)
        {
            // Values with few significant bits at a random place, which is where the loop's branches differ.
            int bits = random.Next(1, 33);
            uint value = (uint)random.NextInt64() & (uint.MaxValue >> (32 - bits));
            value <<= random.Next(0, 33 - bits);
            AssertSame(unchecked((int)value), random.Next(1, 256));
        }
    }
}
