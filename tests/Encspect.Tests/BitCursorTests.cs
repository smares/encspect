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

    // A mix of every operation against a model that keeps a bit position and reads one bit at a time, so the buffer of the cursors never
    // shows: reads, runs, skips and alignments in any order, at every length and past the end.
    [Fact]
    public void AMixOfOperationsAgreesWithABitByBitModel()
    {
        for (int seed = 0; seed < 400; seed++)
        {
            var random = new Random(seed);
            byte[] data = RandomBytes(seed, random.Next(0, 40));
            for (int i = 0; i < data.Length; i++)
            {
                if (random.Next(4) == 0)
                {
                    data[i] = (byte)(random.Next(2) * 0xFF);
                }
            }

            foreach (bool isLsb in new[] { false, true })
            {
                long position = 0;
                var msb = new BitCursor(data);
                var lsb = new LsbBitCursor(data);
                int length = data.Length * 8;
                bool Bit(long at) => at < length && at >= 0 && (isLsb ? (data[at >> 3] >> (int)(at & 7)) & 1 : (data[at >> 3] >> (7 - (int)(at & 7))) & 1) != 0;
                uint Value(int count)
                {
                    uint value = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (isLsb)
                        {
                            value |= (Bit(position + i) ? 1u : 0u) << i;
                        }
                        else
                        {
                            value = (value << 1) | (Bit(position + i) ? 1u : 0u);
                        }
                    }

                    return value;
                }

                for (int step = 0; step < 80; step++)
                {
                    switch (random.Next(7))
                    {
                        case 0:
                        case 1:
                            int count = random.Next(0, 33);
                            uint expected = Value(count);
                            position += count;
                            Assert.Equal(expected, isLsb ? lsb.Read(count) : msb.Read(count));
                            break;
                        case 2:
                            bool bit = Bit(position++);
                            Assert.Equal(bit, isLsb ? lsb.ReadBit() : msb.ReadBit());
                            break;
                        case 3:
                        case 4:
                            bool ones = random.Next(2) == 0;
                            int limit = random.Next(8) == 0 ? int.MaxValue : random.Next(0, 140);
                            int total = 0;
                            while (total < limit)
                            {
                                if (position >= length)
                                {
                                    position = Math.Max(position, length + 1L);
                                    break;
                                }

                                bool b = Bit(position++);
                                if (b != ones)
                                {
                                    break;
                                }

                                total++;
                            }

                            int actual = isLsb ? (ones ? lsb.CountOnes(limit) : lsb.CountZeros(limit)) : (ones ? msb.CountOnes(limit) : msb.CountZeros(limit));
                            Assert.Equal(total, actual);
                            break;
                        case 5:
                            int skip = random.Next(0, random.Next(2) == 0 ? 20 : 300);
                            position += skip;
                            if (isLsb)
                            {
                                lsb.Skip(skip);
                            }
                            else
                            {
                                msb.Skip(skip);
                            }

                            break;
                        default:
                            position = (position + 7) & ~7L;
                            if (isLsb)
                            {
                                lsb.AlignToByte();
                            }
                            else
                            {
                                msb.AlignToByte();
                            }

                            break;
                    }

                    Assert.Equal((int)position, isLsb ? lsb.Position : msb.Position);
                    Assert.Equal(position > length, isLsb ? lsb.IsOverrun : msb.IsOverrun);
                }
            }
        }
    }

    // A run of 63 or 64 equal bits ends exactly at, or just short of, the end of the buffer when the last bytes are loaded one at a time:
    // the counts, the data after the run and the zeros past the end must still match a model that reads one bit at a time.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunsOf63And64BitsNearTheEndAgreeWithABitByBitModel(bool isLsb)
    {
        for (int length = 1; length <= 24; length++)
        {
            int bits = length * 8;
            foreach (bool runBit in new[] { false, true })
            {
                foreach (int run in new[] { 31, 32, 33, 62, 63, 64, 65 })
                {
                    for (int start = 0; start + run <= bits + 2; start++)
                    {
                        foreach (int chunk in new[] { 1, 7, 8, 13, 16, 24, 32 })
                        {
                            var random = new Random(length * 1000 + start);
                            bool[] model = new bool[bits];
                            for (int i = 0; i < bits; i++)
                            {
                                model[i] = random.Next(2) == 0;
                            }

                            for (int i = start; i < Math.Min(bits, start + run); i++)
                            {
                                model[i] = runBit;
                            }

                            if (start + run < bits)
                            {
                                model[start + run] = !runBit;
                            }

                            byte[] data = new byte[length];
                            for (int i = 0; i < bits; i++)
                            {
                                if (model[i])
                                {
                                    data[i >> 3] |= (byte)(isLsb ? 1 << (i & 7) : 0x80 >> (i & 7));
                                }
                            }

                            RunScenario(data, model, isLsb, runBit, start, chunk);
                        }
                    }
                }
            }
        }
    }

    private static void RunScenario(byte[] data, bool[] model, bool isLsb, bool countOnes, int start, int chunk)
    {
        var msb = new BitCursor(data);
        var lsb = new LsbBitCursor(data);
        long position = 0;
        bool Bit(long at) => at < model.Length && model[at];

        while (position < start)
        {
            ReadAndCheck(ref msb, ref lsb, isLsb, model, ref position, (int)Math.Min(chunk, start - position));
        }

        int total = 0;
        while (true)
        {
            if (position >= model.Length)
            {
                position = Math.Max(position, model.Length + 1L);
                break;
            }

            if (Bit(position++) != countOnes)
            {
                break;
            }

            total++;
        }

        int actual = isLsb ? (countOnes ? lsb.CountOnes(int.MaxValue) : lsb.CountZeros(int.MaxValue)) : (countOnes ? msb.CountOnes(int.MaxValue) : msb.CountZeros(int.MaxValue));
        Assert.Equal(total, actual);
        Assert.Equal((int)position, isLsb ? lsb.Position : msb.Position);
        for (int i = 0; i < 4; i++)
        {
            ReadAndCheck(ref msb, ref lsb, isLsb, model, ref position, 32);
        }
    }

    private static void ReadAndCheck(ref BitCursor msb, ref LsbBitCursor lsb, bool isLsb, bool[] model, ref long position, int count)
    {
        uint expected = 0;
        for (int i = 0; i < count; i++)
        {
            bool bit = position + i < model.Length && model[position + i];
            expected = isLsb ? expected | ((bit ? 1u : 0u) << i) : (expected << 1) | (bit ? 1u : 0u);
        }

        position += count;
        Assert.Equal(expected, isLsb ? lsb.Read(count) : msb.Read(count));
    }

    // ReadRice reads the values the buffer holds whole and stops before the rest: after it, the cursor is where reading the same number of
    // values with CountZeros and Read would leave it, with the same position, bits left and overrun state, and the values and the flag for
    // 0xFFFFFFFF are the ones that reading gives. The cursor starts from a random prefix of the other readers, so ReadRice is entered with the
    // buffer in any state: empty, full, refilled byte by byte near the end, already past the end. The data are random, with long runs of
    // zeros, with runs of exactly 63 and 64 zeros before a one, and cut inside a code; the parameters include 0, 14 and 30 (the largest
    // FLAC has with its 5-bit escape of 31), and 31.
    [Fact]
    public void ReadRiceLeavesTheCursorWhereReadingTheSameValuesOneAtATimeDoes()
    {
        var rng = new Random(12345);
        long fastValues = 0;
        for (int iteration = 0; iteration < 60000; iteration++)
        {
            byte[] data = RiceData(rng);
            int parameter = rng.Next(3) == 0 ? rng.Next(0, 32) : rng.Next(5) switch { 0 => 0, 1 => 14, 2 => 30, _ => rng.Next(0, 15) };
            int count = rng.Next(0, 60);
            var fast = new BitCursor(data);
            var slow = new BitCursor(data);
            int operations = rng.Next(0, 6);
            for (int o = 0; o < operations; o++)
            {
                int n = rng.Next(0, 33);
                switch (rng.Next(5))
                {
                    case 0:
                        Assert.Equal(slow.Read(n), fast.Read(n));
                        break;
                    case 1:
                        Assert.Equal(slow.ReadBit(), fast.ReadBit());
                        break;
                    case 2:
                        Assert.Equal(slow.CountZeros(n), fast.CountZeros(n));
                        break;
                    case 3:
                        fast.Skip(n);
                        slow.Skip(n);
                        break;
                    default:
                        fast.AlignToByte();
                        slow.AlignToByte();
                        break;
                }
            }

            bool store = rng.Next(2) == 0;
            int[] stored = new int[count];
            bool minimum = false;
            int read = fast.ReadRice(parameter, count, store ? stored : default, ref minimum);
            fastValues += read;
            Assert.InRange(read, 0, count);
            uint limit = uint.MaxValue >> parameter;
            bool expectedMinimum = false;
            for (int i = 0; i < read; i++)
            {
                int quotient = slow.CountZeros(int.MaxValue);
                Assert.True((uint)quotient <= limit);
                uint folded = ((uint)quotient << parameter) | slow.Read(parameter);
                expectedMinimum |= folded == uint.MaxValue;
                if (store)
                {
                    Assert.Equal((int)(folded >> 1) ^ -(int)(folded & 1), stored[i]);
                }
            }

            Assert.Equal(expectedMinimum, minimum);
            Assert.Equal(slow.Position, fast.Position);
            Assert.Equal(slow.IsOverrun, fast.IsOverrun);
            Assert.Equal(slow.BitsLeft, fast.BitsLeft);

            // The rest one value at a time on both, as the residual walk does, then plain reads.
            for (int i = read; i < count; i++)
            {
                int quotientFast = fast.CountZeros(int.MaxValue);
                int quotientSlow = slow.CountZeros(int.MaxValue);
                Assert.Equal(quotientSlow, quotientFast);
                Assert.Equal(slow.IsOverrun, fast.IsOverrun);
                if (fast.IsOverrun || (uint)quotientFast > limit)
                {
                    break;
                }

                Assert.Equal(slow.Read(parameter), fast.Read(parameter));
            }

            for (int t = 0; t < 4; t++)
            {
                int n = rng.Next(0, 33);
                Assert.Equal(slow.Read(n), fast.Read(n));
                Assert.Equal(slow.Position, fast.Position);
            }
        }

        Assert.True(fastValues > 100000);
    }

    private static byte[] RiceData(Random rng)
    {
        int length = rng.Next(0, 90);
        byte[] data = new byte[length];
        int style = rng.Next(8);
        for (int i = 0; i < length; i++)
        {
            data[i] = style switch
            {
                0 => (byte)rng.Next(256),
                1 => (byte)(rng.Next(8) == 0 ? rng.Next(256) : 0),
                2 => (byte)(rng.Next(256) | 0x11),
                3 => 0xFF,
                4 => (byte)(1 << rng.Next(8)),
                _ => (byte)(rng.Next(3) == 0 ? 0 : rng.Next(256)),
            };
        }

        if (style >= 6 && length >= 12)
        {
            // A run of 63 or 64 zeros (a quotient the 64-bit buffer holds whole only just, or not at all) ended by a one bit, at a random bit.
            int zeros = rng.Next(2) == 0 ? 63 : 64;
            int start = rng.Next(0, Math.Min(8, length * 8 - zeros - 2));
            Array.Clear(data, start / 8, Math.Min(length - (start / 8), (zeros + 7) / 8 + 1));
            int one = start + zeros;
            if (one / 8 < length)
            {
                data[one / 8] |= (byte)(0x80 >> (one % 8));
            }
        }

        return data;
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
