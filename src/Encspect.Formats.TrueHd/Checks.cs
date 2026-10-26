using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Encspect.Formats.TrueHd;

// The checks of a stream (docs/design/truehd-format.md, "Checksums and check values"). The CRCs are the remainder of the message without
// the zero bits a standard CRC appends, which equals a standard CRC over all but the last 8 or 16 bits, XORed with those bits.
internal static class Checks
{
    private static readonly ushort[] Table16 = BuildTable16(0x002D);
    private static readonly byte[] Table8Restart = BuildTable8(0x1D);
    private static readonly byte[] Table8Substream = BuildTable8(0x63);

    // Table k is the byte step applied k times (CRC tables are linear), so eight bytes take eight lookups that do not wait for each other.
    private static readonly byte[][] Slices8Substream = BuildSlices(Table8Substream);

    private static ushort[] BuildTable16(ushort polynomial)
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int register = i << 8;
            for (int bit = 0; bit < 8; bit++)
            {
                register = (register & 0x8000) != 0 ? ((register << 1) ^ polynomial) & 0xFFFF : (register << 1) & 0xFFFF;
            }

            table[i] = (ushort)register;
        }

        return table;
    }

    private static byte[] BuildTable8(byte polynomial)
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int register = i;
            for (int bit = 0; bit < 8; bit++)
            {
                register = (register & 0x80) != 0 ? ((register << 1) ^ polynomial) & 0xFF : (register << 1) & 0xFF;
            }

            table[i] = (byte)register;
        }

        return table;
    }

    private static byte[][] BuildSlices(byte[] table)
    {
        var slices = new byte[9][];
        slices[1] = table;
        for (int k = 2; k <= 8; k++)
        {
            slices[k] = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                slices[k][i] = table[slices[k - 1][i]];
            }
        }

        return slices;
    }

    // The standard CRC-16 with the generator 0x002D, register starting at 0.
    private static ushort Standard16(ReadOnlySpan<byte> data)
    {
        int register = 0;
        foreach (byte b in data)
        {
            register = ((register << 8) & 0xFFFF) ^ Table16[(register >> 8) ^ b];
        }

        return (ushort)register;
    }

    // The major sync's check over all of it but its last two bytes: a standard CRC-16 over all but the four last bytes, XORed with the two
    // before the check. The sync is at least four bytes.
    public static ushort MajorSync(ReadOnlySpan<byte> sync)
    {
        return (ushort)(Standard16(sync[..^4]) ^ BinaryPrimitives.ReadUInt16BigEndian(sync[^4..]));
    }

    // Whether the parity of the unit header and the substream directory comes out right: the XOR of their bytes folds to 0xF.
    public static bool ParityHolds(ReadOnlySpan<byte> header, ReadOnlySpan<byte> directory)
    {
        int x = Xor(header) ^ Xor(directory);
        return ((x >> 4) ^ (x & 0xF)) == 0xF;
    }

    // The restart header's CRC-8 over bitCount bits that start at startBit of data (generator 0x1D, register starting at 0, no appended zeros).
    public static byte RestartHeader(ReadOnlySpan<byte> data, int startBit, int bitCount)
    {
        int register = 0;
        for (int i = 0; i < bitCount; i++)
        {
            int at = startBit + i;
            int bit = (data[at >> 3] >> (7 - (at & 7))) & 1;
            int top = register & 0x80;
            register = (register << 1) & 0xFF;
            if (top != 0)
            {
                register ^= 0x1D;
            }

            register ^= bit;
        }

        return (byte)register;
    }

    // The substream's parity byte holds when the XOR of all bytes before it, XORed with it, is 0xA9. body is the bytes before it.
    public static bool SubstreamParityHolds(ReadOnlySpan<byte> body, byte parity)
    {
        return (parity ^ Xor(body)) == 0xA9;
    }

    // The CRC-8 of the substream's bytes before the parity byte: a standard CRC-8 (generator 0x63, register starting at 0x3C) over all but the
    // last of them, XORed with that last byte. body is at least one byte.
    public static byte Substream(ReadOnlySpan<byte> body)
    {
        int register = 0x3C;
        ReadOnlySpan<byte> data = body[..^1];
        byte[][] slices = Slices8Substream;
        int i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            ReadOnlySpan<byte> eight = data.Slice(i, 8);
            register = slices[8][register ^ eight[0]] ^ slices[7][eight[1]] ^ slices[6][eight[2]] ^ slices[5][eight[3]]
                ^ slices[4][eight[4]] ^ slices[3][eight[5]] ^ slices[2][eight[6]] ^ slices[1][eight[7]];
        }

        for (; i < data.Length; i++)
        {
            register = Table8Substream[register ^ data[i]];
        }

        return (byte)(register ^ body[^1]);
    }

    // The XOR of all the bytes, a word at a time.
    private static int Xor(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(data);
        ulong x = 0;
        foreach (ulong word in words)
        {
            x ^= word;
        }

        x ^= x >> 32;
        x ^= x >> 16;
        x ^= x >> 8;
        int result = (int)(x & 0xFF);
        foreach (byte b in data[(words.Length * 8)..])
        {
            result ^= b;
        }

        return result;
    }

    // The standard CRC-8 with the generator 0x1D, for the tests of the vectors in the description.
    internal static byte Standard8Restart(ReadOnlySpan<byte> data)
    {
        int register = 0;
        foreach (byte b in data)
        {
            register = Table8Restart[register ^ b];
        }

        return (byte)register;
    }

    internal static ushort Standard16Public(ReadOnlySpan<byte> data) => Standard16(data);
}
