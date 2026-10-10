namespace Encspect.Tags.Id3;

// CRC-32 as in ISO 3309 (the zlib and PNG CRC): reflected polynomial 0xEDB88320, initial value
// and final XOR 0xFFFFFFFF. ID3v2.3 and 2.4 name ISO 3309 for the extended header CRC.
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    public const uint Initial = 0xFFFFFFFF;

    public static uint Append(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            state = Table[(state ^ b) & 0xFF] ^ (state >> 8);
        }

        return state;
    }

    public static uint Finish(uint state) => state ^ 0xFFFFFFFF;

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
