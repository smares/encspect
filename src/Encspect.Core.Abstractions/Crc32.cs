namespace Encspect.Core;

// CRC-32 with the reflected polynomial 0xEDB88320, the one TTA, Monkey's Audio, Matroska, Musepack and ID3v2 use.
// Matroska, Musepack and ID3v2 keep their own copies for now; moving them is a change of its own.
//
// Update works on the register: start it with Initial, feed the data in any number of pieces and take Finish of the
// result, which is the CRC of ISO 3309, zlib and PNG ("123456789" gives 0xCBF43926). A format that stores the
// register unchanged, or inverts it differently, uses Update alone.
public static class Crc32
{
    public const uint Initial = 0xFFFFFFFF;

    private static readonly uint[] Table = CreateTable();

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] table = Table;
        foreach (byte b in data)
        {
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    public static uint Finish(uint crc) => ~crc;

    public static uint Of(ReadOnlySpan<byte> data) => Finish(Update(Initial, data));

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320 : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
