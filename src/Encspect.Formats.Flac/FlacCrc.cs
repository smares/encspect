using System.Runtime.CompilerServices;

namespace Encspect.Formats.Flac;

// The CRC-8 of a frame header (polynomial x^8 + x^2 + x + 1) and the CRC-16 of a whole frame (x^16 + x^15 + x^2 + 1), both
// with start value 0, most significant bit first and no final inversion (RFC 9639, sections 9.1.8 and 9.3).
internal static class FlacCrc
{
    private static readonly byte[] Table8 = BuildTable8();
    private static readonly ushort[][] Table16 = BuildTable16();

    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte b in data)
        {
            crc = Table8[crc ^ b];
        }

        return crc;
    }

    // Four bytes at a time: table k holds the CRC of a byte followed by k zero bytes.
    // Compiled optimized at the first call and not tiered (docs/development.md, "Hot loops and tiering").
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort[] t0 = Table16[0];
        ushort[] t1 = Table16[1];
        ushort[] t2 = Table16[2];
        ushort[] t3 = Table16[3];
        int crc = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            crc = t3[(crc >> 8) ^ data[i]] ^ t2[(crc & 0xFF) ^ data[i + 1]] ^ t1[data[i + 2]] ^ t0[data[i + 3]];
        }

        for (; i < data.Length; i++)
        {
            crc = t0[(crc >> 8) ^ data[i]] ^ ((crc << 8) & 0xFFFF);
        }

        return (ushort)crc;
    }

    private static byte[] BuildTable8()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1;
            }

            table[i] = (byte)crc;
        }

        return table;
    }

    private static ushort[][] BuildTable16()
    {
        var tables = new ushort[4][];
        tables[0] = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int crc = i << 8;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1;
            }

            tables[0][i] = (ushort)crc;
        }

        for (int k = 1; k < 4; k++)
        {
            tables[k] = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                int previous = tables[k - 1][i];
                tables[k][i] = (ushort)(tables[0][previous >> 8] ^ ((previous << 8) & 0xFFFF));
            }
        }

        return tables;
    }
}
