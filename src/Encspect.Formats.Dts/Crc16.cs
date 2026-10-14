namespace Encspect.Formats.Dts;

// The CRC of ETSI TS 102 114 Annex B: the generator polynomial x^16 + x^12 + x^5 + 1 (CRC-CCITT),
// the register set to 0xFFFF before the first byte and bytes shifted in most significant bit first.
// Every header that carries a CRC16 is written so that the CRC over the bytes it covers, the CRC
// field included, comes to 0.
internal static class Crc16
{
    private static readonly ushort[] Table = Build();

    public static int Compute(ReadOnlySpan<byte> data)
    {
        int crc = 0xFFFF;
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
                r = (r & 0x8000) != 0 ? (r << 1) ^ 0x1021 : r << 1;
            }

            table[i] = (ushort)r;
        }

        return table;
    }
}
