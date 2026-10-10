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
}
