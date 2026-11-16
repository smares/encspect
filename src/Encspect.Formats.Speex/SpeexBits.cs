namespace Encspect.Formats.Speex;

// Bits most significant first, zero bits past the end, as speex_bits_unpack_unsigned reads them once the data has overflowed (bits.c). A read that
// starts inside the data and ends past it returns the bits it has, where libspeex returns 0: such a frame is refused (IsOverrun) before its
// values are used, so the difference never shows.
internal ref struct SpeexBits(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; private set; }

    public readonly bool IsOverrun => Position > _data.Length * 8;

    public readonly int Peek() => Bit(Position);

    // At most 24 bits.
    public int Read(int count)
    {
        int position = Position;
        Position = position + count;
        if (count == 0)
        {
            return 0;
        }

        int index = position >> 3;
        uint window = 0;
        for (int i = 0; i < 4; i++)
        {
            window = (window << 8) | (index + i < _data.Length ? _data[index + i] : (uint)0);
        }

        return (int)((window << (position & 7)) >> (32 - count));
    }

    public void Advance(int count) => Position += count;

    private readonly int Bit(int position) => position < _data.Length * 8 ? (_data[position >> 3] >> (7 - (position & 7))) & 1 : 0;
}
