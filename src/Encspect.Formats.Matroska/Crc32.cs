using Encspect.Core;

namespace Encspect.Formats.Matroska;

// The CRC of the CRC-32 element: IEEE CRC-32 as in ISO 3309 and ITU-T V.42, with the initial value 0xFFFFFFFF, the result
// inverted, stored in little-endian order (RFC 8794 section 11.3.1).
internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        uint value = (uint)i;
        for (int bit = 0; bit < 8; bit++)
        {
            value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320 : value >> 1;
        }

        return value;
    }).ToArray();

    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            state = Table[(state ^ b) & 0xFF] ^ (state >> 8);
        }

        return state;
    }

    public static uint Of(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFF, data);

    // The CRC of the bytes [start, end) of the reader.
    public static uint Of(ByteReader reader, long start, long end)
    {
        uint state = 0xFFFFFFFF;
        byte[] buffer = new byte[1 << 16];
        reader.Position = start;
        for (long remaining = end - start; remaining > 0;)
        {
            int count = (int)Math.Min(buffer.Length, remaining);
            reader.Read(buffer.AsSpan(0, count));
            state = Update(state, buffer.AsSpan(0, count));
            remaining -= count;
        }

        return ~state;
    }
}
