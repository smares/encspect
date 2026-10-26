namespace Encspect.Tests;

// WMA's bit reader reads the eight bytes at the position as one word; the byte loop it replaced is the reference.
public class WmaBitsPeekTests
{
    // WMA's Bits.Peek read the eight bytes at the position one at a time, zero outside the data.
    private static uint PeekOneByteAtATime(byte[] data, int position, int count)
    {
        int index = position >> 3;
        ulong window = 0;
        for (int i = 0; i < 8; i++)
        {
            window = (window << 8) | (uint)(index + i < data.Length && index + i >= 0 ? data[index + i] : 0);
        }

        return (uint)((window << (position & 7)) >> (64 - count));
    }

    [Fact]
    public void WmaPeekEqualsTheByteLoopAtEveryPositionAroundBothEndsAndEveryWidth()
    {
        var rng = new Random(777);
        for (int length = 0; length <= 24; length++)
        {
            byte[] data = new byte[length];
            rng.NextBytes(data);
            var bits = new Encspect.Formats.Wma.Bits(data);
            for (int position = -80; position <= (length * 8) + 80; position++)
            {
                bits.Position = position;
                for (int count = 0; count <= 32; count++)
                {
                    Assert.Equal(PeekOneByteAtATime(data, position, count), bits.Peek(count));
                }
            }
        }
    }

    [Fact]
    public void WmaPeekEqualsTheByteLoopOnRandomBuffersAndLengthsShorterThanTheData()
    {
        var rng = new Random(778);
        for (int iteration = 0; iteration < 3000; iteration++)
        {
            byte[] data = new byte[rng.Next(0, 200)];
            rng.NextBytes(data);
            var bits = new Encspect.Formats.Wma.Bits(data, rng.Next(0, (data.Length * 8) + 1));
            for (int step = 0; step < 20; step++)
            {
                int count = rng.Next(1, 33);
                int position = rng.Next(-8, (data.Length * 8) + 40);
                bits.Position = position;
                Assert.Equal(PeekOneByteAtATime(data, position, count), bits.Peek(count));
            }
        }
    }
}
