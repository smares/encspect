namespace Encspect.Formats.Ogg;

// The page checksum (RFC 3533, section 6, "CRC_checksum"): a CRC-32 with the generator polynomial
// 0x04C11DB7, initial value 0, no reflection of input or output and no final XOR, over the whole
// page with its CRC field set to zero.
internal static class OggCrc
{
    private static readonly uint[] Table = BuildTable();

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = (crc << 8) ^ Table[(crc >> 24) ^ b];
        }

        return crc;
    }

    // The checksum of a page whose bytes are in page, with the CRC field (bytes 22 to 25) counted as
    // zero.
    public static uint OfPage(ReadOnlySpan<byte> page)
    {
        uint crc = Update(0, page[..22]);
        crc = Update(crc, [0, 0, 0, 0]);
        return Update(crc, page[26..]);
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int bit = 0; bit < 8; bit++)
            {
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            }

            table[i] = r;
        }

        return table;
    }
}
