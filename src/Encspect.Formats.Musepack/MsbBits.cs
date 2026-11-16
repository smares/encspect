namespace Encspect.Formats.Musepack;

// Reads bits most significant first from a byte buffer whose end is followed by at least 4 zero bytes (the walkers' buffers are made so), the order of libmpcdec r495's
// mpc_bits_reader.h and, after the 32-bit words of SV4 to SV7 are swapped, of every stream version. Reading past Limit gives zero bits, and the walker checks Position
// against Limit where a frame or a packet ends. Position counts bits from the start of the buffer.
internal sealed class MsbBits
{
    private byte[] _data = [];

    public int Position { get; set; }

    // The bit just past the unit being read: the walker's check that a unit did not read beyond its end.
    public int Limit { get; set; }

    public void Reset(byte[] data, int position, int limit)
    {
        _data = data;
        Position = position;
        Limit = limit;
    }

    public bool IsOverrun => Position > Limit;

    // The next 16 bits without consuming them.
    public int Peek16()
    {
        int i = Position >> 3;
        int window = (_data[i] << 16) | (_data[i + 1] << 8) | _data[i + 2];
        return (window >> (8 - (Position & 7))) & 0xFFFF;
    }

    // The next 32 bits without consuming them.
    public uint Peek32()
    {
        int i = Position >> 3;
        ulong window = ((ulong)_data[i] << 32) | ((ulong)_data[i + 1] << 24) | ((ulong)_data[i + 2] << 16) | ((ulong)_data[i + 3] << 8) | _data[i + 4];
        return (uint)(window >> (8 - (Position & 7)));
    }

    public void Skip(int count) => Position += count;

    // 0 to 24 bits.
    public int Read(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        int i = Position >> 3;
        uint window = ((uint)_data[i] << 24) | ((uint)_data[i + 1] << 16) | ((uint)_data[i + 2] << 8) | _data[i + 3];
        int value = (int)((window << (Position & 7)) >> (32 - count));
        Position += count;
        return value;
    }

    // Up to 31 bits, as libmpcdec reads them.
    public int ReadLong(int count)
    {
        if (count <= 24)
        {
            return Read(count);
        }

        int high = Read(count - 16);
        return (high << 16) | Read(16);
    }
}
