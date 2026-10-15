namespace Encspect.Formats.Wma;

// Reads bits most significant first, as the Windows Media Audio coders pack them (FFmpeg's GetBitContext
// reads the same way). Reading past the end yields zero bits and moves the position past Length, so
// IsOverrun says that the data was too short, as FFmpeg's decoder reads zero padding there.
internal sealed class Bits(byte[] data, int length)
{
    public Bits(byte[] data)
        : this(data, data.Length * 8)
    {
    }

    public int Position { get; set; }

    // The bits the data holds.
    public int Length => length;

    public int Remaining => length - Position;

    public bool IsOverrun => Position > length;

    // The next count bits (up to 32) as the low bits of the result, without moving on.
    public uint Peek(int count)
    {
        int index = Position >> 3;
        ulong window = 0;
        for (int i = 0; i < 8; i++)
        {
            window = (window << 8) | (uint)(index + i < data.Length && index + i >= 0 ? data[index + i] : 0);
        }

        return (uint)((window << (Position & 7)) >> (64 - count));
    }

    public uint Read(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        uint value = Peek(count);
        Position += count;
        return value;
    }

    public int ReadBit() => (int)Read(1);

    public void Skip(int count) => Position += count;

    // Whether the bits from start up to end (not counting) are all zero.
    public bool IsZero(int start, int end)
    {
        for (int i = start; i < end; i += 32)
        {
            int count = Math.Min(32, end - i);
            int saved = Position;
            Position = i;
            uint value = Peek(count);
            Position = saved;
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    public void Align() => Position = (Position + 7) & ~7;

    // Copies count bits from one bit position of a buffer to another of another buffer, most significant first.
    public static void Copy(byte[] source, int sourceBit, byte[] target, int targetBit, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int s = sourceBit + i;
            int t = targetBit + i;
            if (((source[s >> 3] >> (7 - (s & 7))) & 1) != 0)
            {
                target[t >> 3] |= (byte)(1 << (7 - (t & 7)));
            }
            else
            {
                target[t >> 3] &= (byte)~(1 << (7 - (t & 7)));
            }
        }
    }
}
