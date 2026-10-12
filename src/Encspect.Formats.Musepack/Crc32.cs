namespace Encspect.Formats.Musepack;

// The CRC-32 of the SV8 stream header, which the specification names as PNG's (ISO 3309): reflected
// polynomial 0xEDB88320, initial value and final XOR 0xFFFFFFFF (musepack_src r495 common/crc32.c).
internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }

        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFF;
    }
}
