namespace Encspect.Formats.Aac;

// The ADTS CRC (ISO/IEC 14496-3 1.A.4.3, which refers to ISO/IEC 11172-3 2.4.3.1): CRC-16 with the
// polynomial 0x8005 and the initial value 0xFFFF, over bit fields of any length, most significant
// bit first.
internal struct Crc16()
{
    public ushort Value { get; private set; } = 0xFFFF;

    public void Add(ReadOnlySpan<byte> data, int startBit, int count)
    {
        ushort crc = Value;
        for (int bit = startBit; bit < startBit + count; bit++)
        {
            int input = (data[bit >> 3] >> (7 - (bit & 7))) & 1;
            bool top = (crc & 0x8000) != 0;
            crc <<= 1;
            if (top ^ (input != 0))
            {
                crc ^= 0x8005;
            }
        }

        Value = crc;
    }
}
