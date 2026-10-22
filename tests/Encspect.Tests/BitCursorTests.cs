using Encspect.Core;

namespace Encspect.Tests;

public class BitCursorTests
{
    private static byte[] RandomBytes(int seed, int length)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void ReadsLikeBitReaderMostSignificantFirstAtEveryLengthAndEnd()
    {
        for (int length = 0; length <= 40; length++)
        {
            byte[] data = RandomBytes(length, length);
            var random = new Random(length + 100);
            var cursor = new BitCursor(data);
            var reader = new BitReader(data);
            for (int step = 0; step < 60; step++)
            {
                int count = random.Next(0, 33);
                switch (random.Next(4))
                {
                    case 0:
                        Assert.Equal((uint)reader.ReadLong(count), cursor.Read(count));
                        break;
                    case 1:
                        int wide = random.Next(0, 65);
                        Assert.Equal((ulong)reader.ReadLong(wide), cursor.ReadLong(wide));
                        break;
                    case 2:
                        if (count > 0)
                        {
                            long expected = reader.ReadLong(count);
                            expected = (expected << (64 - count)) >> (64 - count);
                            Assert.Equal((int)expected, cursor.ReadSigned(count));
                        }

                        break;
                    default:
                        Assert.Equal(reader.ReadLong(1) == 1, cursor.ReadBit());
                        break;
                }

                Assert.Equal(reader.Position, cursor.Position);
                Assert.Equal(reader.IsOverrun, cursor.IsOverrun);
                Assert.Equal(reader.Length, cursor.Length);
            }
        }
    }

    [Fact]
    public void ReadsLikeBitReaderLeastSignificantFirstAtEveryLengthAndEnd()
    {
        for (int length = 0; length <= 40; length++)
        {
            byte[] data = RandomBytes(length + 500, length);
            var random = new Random(length + 700);
            var cursor = new LsbBitCursor(data);
            var reader = new BitReader(data, BitOrder.LeastSignificantFirst);
            for (int step = 0; step < 60; step++)
            {
                int count = random.Next(0, 33);
                switch (random.Next(4))
                {
                    case 0:
                        Assert.Equal((uint)reader.ReadLong(count), cursor.Read(count));
                        break;
                    case 1:
                        int wide = random.Next(0, 65);
                        Assert.Equal((ulong)reader.ReadLong(wide), cursor.ReadLong(wide));
                        break;
                    case 2:
                        if (count > 0)
                        {
                            long expected = reader.ReadLong(count);
                            expected = (expected << (64 - count)) >> (64 - count);
                            Assert.Equal((int)expected, cursor.ReadSigned(count));
                        }

                        break;
                    default:
                        Assert.Equal(reader.ReadLong(1) == 1, cursor.ReadBit());
                        break;
                }

                Assert.Equal(reader.Position, cursor.Position);
                Assert.Equal(reader.IsOverrun, cursor.IsOverrun);
            }
        }
    }

    [Fact]
    public void ReadsSignedValuesOfAnyWidth()
    {
        var cursor = new BitCursor([0b1000_0000, 0b0111_1111, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        Assert.Equal(-1, cursor.ReadSigned(1));
        Assert.Equal(0, cursor.ReadSigned(7));
        Assert.Equal(127, cursor.ReadSigned(8));
        Assert.Equal(-1, cursor.ReadSigned(32));
        Assert.Equal(-1L, cursor.ReadSignedLong(33));
        Assert.Equal(-1L, cursor.ReadSignedLong(14));

        var low = new LsbBitCursor([0b0000_0001, 0b1111_1110]);
        Assert.Equal(-1L, low.ReadSignedLong(1));
        Assert.Equal(0, low.ReadSigned(8));
        Assert.Equal(-1, low.ReadSigned(7));
    }

    [Fact]
    public void CountsRunsLikeReadingBitByBit()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            var random = new Random(seed);
            byte[] data = RandomBytes(seed, random.Next(0, 24));
            // Long runs of equal bits as well as noise.
            for (int i = 0; i < data.Length; i++)
            {
                if (random.Next(3) == 0)
                {
                    data[i] = (byte)(random.Next(2) * 0xFF);
                }
            }

            foreach (bool ones in new[] { false, true })
            {
                var cursor = new BitCursor(data);
                var lsb = new LsbBitCursor(data);
                var reference = new BitCursor(data);
                var lsbReference = new LsbBitCursor(data);
                while (!reference.IsOverrun && !cursor.IsOverrun)
                {
                    int limit = random.Next(0, 90);
                    int expected = Reference(ref reference, ones, limit);
                    int actual = ones ? cursor.CountOnes(limit) : cursor.CountZeros(limit);
                    Assert.Equal(expected, actual);
                    Assert.Equal(reference.Position, cursor.Position);
                    Assert.Equal(reference.IsOverrun, cursor.IsOverrun);

                    int expectedLsb = ReferenceLsb(ref lsbReference, ones, limit);
                    Assert.Equal(expectedLsb, ones ? lsb.CountOnes(limit) : lsb.CountZeros(limit));
                    Assert.Equal(lsbReference.Position, lsb.Position);
                    Assert.Equal(lsbReference.IsOverrun, lsb.IsOverrun);
                }
            }
        }
    }

    // Up to limit matching bits, then the terminator is consumed if the limit allowed it to be seen; the end of the data
    // reads as the terminator, one bit past it.
    private static int Reference(ref BitCursor bits, bool ones, int limit)
    {
        int count = 0;
        while (count < limit)
        {
            if (bits.Position >= bits.Length)
            {
                bits.Skip(bits.Position == bits.Length ? 1 : 0);
                return count;
            }

            if (bits.ReadBit() != ones)
            {
                return count;
            }

            count++;
        }

        return count;
    }

    private static int ReferenceLsb(ref LsbBitCursor bits, bool ones, int limit)
    {
        int count = 0;
        while (count < limit)
        {
            if (bits.Position >= bits.Length)
            {
                bits.Skip(bits.Position == bits.Length ? 1 : 0);
                return count;
            }

            if (bits.ReadBit() != ones)
            {
                return count;
            }

            count++;
        }

        return count;
    }

    [Fact]
    public void ACountStopsAtTheLimitWithoutTakingTheTerminator()
    {
        var cursor = new BitCursor([0b0000_0100, 0xFF]);
        Assert.Equal(3, cursor.CountZeros(3));
        Assert.Equal(3, cursor.Position);
        Assert.Equal(2, cursor.CountZeros(10));
        Assert.Equal(6, cursor.Position);
        Assert.Equal(0, cursor.CountOnes(2));
        Assert.Equal(7, cursor.Position);
        Assert.Equal(0, cursor.CountZeros(0));
    }

    [Fact]
    public void ACountThatReachesTheEndOverruns()
    {
        var zeros = new BitCursor([0x00, 0x00]);
        Assert.Equal(16, zeros.CountZeros(100));
        Assert.True(zeros.IsOverrun);

        var ones = new BitCursor([0xFF]);
        Assert.Equal(8, ones.CountOnes(100));
        Assert.True(ones.IsOverrun);

        var exact = new BitCursor([0x01]);
        Assert.Equal(7, exact.CountZeros(100));
        Assert.False(exact.IsOverrun);
        Assert.Equal(8, exact.Position);
    }

    [Fact]
    public void CountsAHugeRunInTimeOfItsBytes()
    {
        byte[] data = new byte[1 << 20];
        var cursor = new BitCursor(data);
        Assert.Equal(data.Length * 8, cursor.CountZeros(int.MaxValue));
        Assert.True(cursor.IsOverrun);
    }

    [Fact]
    public void ACountPastTheEndNeverMovesThePositionBackward()
    {
        var cursor = new BitCursor([0x00, 0x00]);
        cursor.Skip(1000);
        Assert.Equal(0, cursor.CountZeros(5));
        Assert.Equal(0, cursor.CountOnes(5));
        Assert.Equal(1000, cursor.Position);
        Assert.True(cursor.IsOverrun);

        var lsb = new LsbBitCursor([0x00, 0x00]);
        lsb.Skip(1000);
        Assert.Equal(0, lsb.CountZeros(5));
        Assert.Equal(0, lsb.CountOnes(5));
        Assert.Equal(1000, lsb.Position);
    }

    [Fact]
    public void SkipAndAlignMoveThePosition()
    {
        var cursor = new BitCursor(RandomBytes(3, 4));
        cursor.Skip(3);
        cursor.AlignToByte();
        Assert.Equal(8, cursor.Position);
        cursor.AlignToByte();
        Assert.Equal(8, cursor.Position);
        cursor.Skip(1000);
        Assert.True(cursor.IsOverrun);
        Assert.Equal(0u, cursor.Read(32));

        var lsb = new LsbBitCursor(RandomBytes(4, 4));
        lsb.Skip(9);
        lsb.AlignToByte();
        Assert.Equal(16, lsb.Position);
    }

    [Fact]
    public void RefusesWidthsOutOfRange()
    {
        var cursor = new BitCursor([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitCursor([1]).Read(33));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitCursor([1]).Read(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitCursor([1]).ReadLong(65));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitCursor([1]).ReadSigned(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LsbBitCursor([1]).Read(33));
        Assert.Equal(0x0102u, cursor.Read(16));
    }
}
