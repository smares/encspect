namespace Encspect.Core;

public enum BitOrder
{
    // MPEG, FLAC and most audio formats: fields start at the most significant bit of a byte.
    MostSignificantFirst,

    // Vorbis (Vorbis I specification, section 2.1.4): fields start at the least significant bit
    // of a byte, and the first bit read is the value's least significant bit.
    LeastSignificantFirst,
}

// Reads bit fields in either bit order. Reading past the end yields zero bits and sets IsOverrun
// instead of throwing, so a parser can report a truncated structure as a finding.
public sealed class BitReader
{
    private readonly ReadOnlyMemory<byte> _data;

    // Positions are counted in an int, which limits the data to 256 MiB; a parser reads a frame or
    // a packet at a time.
    public BitReader(ReadOnlyMemory<byte> data, BitOrder order = BitOrder.MostSignificantFirst)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, int.MaxValue / 8, nameof(data));

        _data = data;
        Order = order;
    }

    public BitReader(byte[] data, BitOrder order = BitOrder.MostSignificantFirst)
        : this(new ReadOnlyMemory<byte>(data ?? throw new ArgumentNullException(nameof(data))), order)
    {
    }

    // Positions map back to the file through the bytes' map: FileOffsetOf, and Node.FromBits with
    // Source for a field read here.
    public BitReader(MappedBytes bytes, BitOrder order = BitOrder.MostSignificantFirst)
        : this((bytes ?? throw new ArgumentNullException(nameof(bytes))).Data, order)
    {
        Source = bytes;
    }

    public BitOrder Order { get; }

    // The mapped bytes this reader reads, or null when it was given bare bytes.
    public MappedBytes? Source { get; }

    // In bits from the start of the data.
    public int Position { get; private set; }

    public int Length => _data.Length * 8;

    public bool IsOverrun => Position > Length;

    // The file offset of the byte that holds the bit at bitPosition; Length maps to the end of the
    // source. Only for a reader over mapped bytes.
    public long FileOffsetOf(int bitPosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitPosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitPosition, Length);
        if (Source is null)
        {
            throw new InvalidOperationException("A reader over bare bytes has no file offsets.");
        }

        return Source.OffsetOf(bitPosition / 8);
    }

    // Up to 31 bits, so the value is never negative.
    public int Read(int count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 31);
        return (int)ReadLong(count);
    }

    // Up to 64 bits; a 64-bit value comes back as its two's complement bit pattern.
    public long ReadLong(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 64);

        ReadOnlySpan<byte> data = _data.Span;
        ulong value = 0;
        int done = 0;
        while (done < count)
        {
            int position = Position;
            int bit = position & 7;
            int take = Math.Min(8 - bit, count - done);
            int index = position >> 3;
            int current = index < data.Length ? data[index] : 0;
            int mask = (1 << take) - 1;

            if (Order == BitOrder.MostSignificantFirst)
            {
                value = (value << take) | (uint)((current >> (8 - bit - take)) & mask);
            }
            else
            {
                value |= (ulong)((current >> bit) & mask) << done;
            }

            done += take;
            Position = position + take;
        }

        return (long)value;
    }
}
