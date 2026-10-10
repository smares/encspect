namespace Encspect.Core;

// Reads bit fields most significant bit first, the order MPEG and most audio formats store them
// in. Reading past the end yields zero bits and sets IsOverrun instead of throwing, so a parser can
// report a truncated structure as a finding.
public sealed class BitReader
{
    private readonly byte[] _data;

    public BitReader(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
    }

    // In bits from the start of the data.
    public int Position { get; private set; }

    public int Length => _data.Length * 8;

    public bool IsOverrun => Position > Length;

    public int Read(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 31);

        int value = 0;
        for (int i = 0; i < count; i++, Position++)
        {
            int bit = Position < Length ? (_data[Position >> 3] >> (7 - (Position & 7))) & 1 : 0;
            value = (value << 1) | bit;
        }

        return value;
    }
}
