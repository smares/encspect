using System.Buffers.Binary;

namespace Encspect.Tests;

// Writes FLAC streams for tests (RFC 9639): metadata blocks and frames of constant or verbatim subframes, the smallest frames there are,
// with the CRC-8 and CRC-16 the specification defines (bit by bit, apart from the analyzer's tables).
internal static class FlacBuilder
{
    public static byte[] Marker => "fLaC"u8.ToArray();

    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1;
            }
        }

        return (byte)crc;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b << 8;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
        }

        return (ushort)crc;
    }

    public static byte[] Block(int type, byte[] body, bool isLast = false) => [(byte)((isLast ? 0x80 : 0) | type), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, .. body];

    public static byte[] StreamInfo(int blockSize, int rate, int channels, int bits, long total, byte[]? md5 = null, int minFrame = 0, int maxFrame = 0)
    {
        byte[] body = new byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(body, (ushort)blockSize);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), (ushort)blockSize);
        body[4] = (byte)(minFrame >> 16);
        body[5] = (byte)(minFrame >> 8);
        body[6] = (byte)minFrame;
        body[7] = (byte)(maxFrame >> 16);
        body[8] = (byte)(maxFrame >> 8);
        body[9] = (byte)maxFrame;
        ulong packed = ((ulong)rate << 44) | ((ulong)(channels - 1) << 41) | ((ulong)(bits - 1) << 36) | (ulong)total;
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(10), packed);
        md5?.CopyTo(body, 18);
        return body;
    }

    // A frame of constant subframes (every channel holds value), with a frame number or, for variable block size, a sample number.
    public static byte[] ConstantFrame(long number, int blockSize, int rate, int channels, int bits, int value = 0, bool isVariable = false) =>
        Frame(number, blockSize, rate, channels, bits, isVariable, body =>
        {
            for (int c = 0; c < channels; c++)
            {
                Bits(body, 0, 8);
                Bits(body, (uint)(((long)value) & ((1L << bits) - 1)), bits);
            }
        });

    public static byte[] Frame(long number, int blockSize, int rate, int channels, int bits, bool isVariable, Action<List<bool>> subframes)
    {
        var header = new List<byte> { 0xFF, (byte)(0xF8 | (isVariable ? 1 : 0)), 0x7D, (byte)(((channels - 1) << 4) | (SizeCode(bits) << 1)) };
        header.AddRange(Number(number));
        header.AddRange(Short(blockSize - 1));
        header.AddRange(Short(rate));
        header.Add(Crc8([.. header]));
        var body = new List<bool>();
        subframes(body);
        return Finish([.. header], body);
    }

    private static byte[] Finish(byte[] header, List<bool> bits)
    {
        while (bits.Count % 8 != 0)
        {
            bits.Add(false);
        }

        byte[] data = [.. header, .. Pack(bits)];
        byte[] crc = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(crc, Crc16(data));
        return [.. data, .. crc];
    }

    public static void Bits(List<bool> bits, uint value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            bits.Add(((value >> i) & 1) != 0);
        }
    }

    private static byte[] Pack(List<bool> bits)
    {
        byte[] bytes = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bytes;
    }

    private static int SizeCode(int bits) => bits switch { 8 => 1, 12 => 2, 16 => 4, 20 => 5, 24 => 6, _ => 7 };

    private static byte[] Short(int value) => [(byte)(value >> 8), (byte)value];

    // The frame or sample number coded like a UTF-8 character that may take up to seven bytes.
    private static byte[] Number(long value)
    {
        if (value < 0x80)
        {
            return [(byte)value];
        }

        int count = value < 0x800 ? 2 : value < 0x10000 ? 3 : value < 0x200000 ? 4 : value < 0x4000000 ? 5 : value < 0x80000000L ? 6 : 7;
        var bytes = new byte[count];
        for (int i = count - 1; i > 0; i--)
        {
            bytes[i] = (byte)(0x80 | (value & 0x3F));
            value >>= 6;
        }

        bytes[0] = (byte)(((0xFF00 >> count) & 0xFF) | (int)value);
        return bytes;
    }

    // A stream of frames of constant subframes of 4,096 samples, 16 bits, stereo, 44.1 kHz, with a STREAMINFO block that counts them.
    public static byte[] Stream(int frames, byte[]? md5 = null, params byte[][] more)
    {
        byte[][] built = [.. Enumerable.Range(0, frames).Select(i => ConstantFrame(i, 4096, 44100, 2, 16, i & 0x7F))];
        return [.. Marker, .. Block(0, StreamInfo(4096, 44100, 2, 16, 4096L * frames, md5), isLast: more.Length == 0), .. more.SelectMany(b => b), .. built.SelectMany(f => f)];
    }

    public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}
