namespace Encspect.Formats.Mpeg;

// The MPEG audio error check: CRC-16 with polynomial 0x8005 (x^16 + x^15 + x^2 + 1), initial value
// 0xFFFF, most significant bit first (ISO/IEC 11172-3 annex A.2).
internal static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data, ushort crc = 0xFFFF)
    {
        foreach (byte b in data)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                bool top = (crc & 0x8000) != 0;
                crc = (ushort)(crc << 1);
                if (top ^ (((b >> bit) & 1) != 0))
                {
                    crc ^= 0x8005;
                }
            }
        }

        return crc;
    }

    // Over bits [startBit, startBit + count) of data, for coverage that does not end on a byte
    // boundary, such as the Layer II bit allocation and scfsi.
    public static ushort ComputeBits(byte[] data, int startBit, int count, ushort crc = 0xFFFF)
    {
        for (int i = startBit; i < startBit + count; i++)
        {
            int bit = i >> 3 < data.Length ? (data[i >> 3] >> (7 - (i & 7))) & 1 : 0;
            bool top = (crc & 0x8000) != 0;
            crc = (ushort)(crc << 1);
            if (top ^ (bit != 0))
            {
                crc ^= 0x8005;
            }
        }

        return crc;
    }
}
