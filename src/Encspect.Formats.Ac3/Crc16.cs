namespace Encspect.Formats.Ac3;

// The CRC of A/52 7.10.1: the generator polynomial x^16 + x^15 + x^2 + 1, registers reset to zero,
// the bits shifted in as they appear. crc1 and crc2 are written so that the register holds zero at
// the end of what they cover, so a CRC over that data, the CRC word included, of 0 verifies it.
internal static class Crc16
{
    private static readonly ushort[] Table = Build();

    public static int Compute(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc = ((crc << 8) ^ Table[((crc >> 8) ^ b) & 0xFF]) & 0xFFFF;
        }

        return crc;
    }

    private static ushort[] Build()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int r = i << 8;
            for (int bit = 0; bit < 8; bit++)
            {
                r = (r & 0x8000) != 0 ? (r << 1) ^ 0x8005 : r << 1;
            }

            table[i] = (ushort)r;
        }

        return table;
    }
}
