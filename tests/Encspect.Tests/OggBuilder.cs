using System.Buffers.Binary;

namespace Encspect.Tests;

// Writes Ogg pages for tests (RFC 3533, section 6), with the CRC-32 the specification defines:
// polynomial 0x04C11DB7, initial value 0, no reflection, over the page with the CRC field zeroed.
internal sealed class OggBuilder
{
    private static readonly uint[] Table = BuildTable();

    private readonly List<byte> _data = [];
    private readonly Dictionary<uint, uint> _sequences = [];

    public byte[] Bytes => [.. _data];

    public int Length => _data.Count;

    // One page of whole packets; flags 2 marks the first page of a stream, 4 the last.
    public OggBuilder Page(uint serial, long granule, byte flags, params byte[][] packets)
    {
        var lacing = new List<byte>();
        foreach (byte[] packet in packets)
        {
            for (int left = packet.Length; ; left -= 255)
            {
                lacing.Add((byte)Math.Min(left, 255));
                if (left < 255)
                {
                    break;
                }
            }
        }

        uint sequence = _sequences.GetValueOrDefault(serial);
        _sequences[serial] = sequence + 1;
        byte[] header = new byte[27 + lacing.Count];
        "OggS"u8.CopyTo(header);
        header[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), sequence);
        header[26] = (byte)lacing.Count;
        lacing.CopyTo(header, 27);
        byte[] page = [.. header, .. packets.SelectMany(p => p)];
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc(page));
        _data.AddRange(page);
        return this;
    }

    public OggBuilder Raw(params byte[] bytes)
    {
        _data.AddRange(bytes);
        return this;
    }

    public static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc = (crc << 8) ^ Table[(crc >> 24) ^ b];
        }

        return crc;
    }

    // speex_header.h: "Speex   ", a version string of 20 bytes, then 13 little-endian integers.
    public static byte[] SpeexHeader(int rate, int mode, int framesPerPacket = 1)
    {
        var header = new byte[80];
        "Speex   1.2.1"u8.CopyTo(header);
        int[] fields = [1, 80, rate, mode, 4, 1, -1, 160 << mode, 0, framesPerPacket, 0, 0, 0];
        for (int i = 0; i < fields.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28 + (4 * i)), fields[i]);
        }

        return header;
    }

    public static byte[] VorbisComment(string vendor, params string[] comments)
    {
        var data = new List<byte>();
        void Text(string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            data.AddRange(BitConverter.GetBytes((uint)bytes.Length));
            data.AddRange(bytes);
        }

        Text(vendor);
        data.AddRange(BitConverter.GetBytes((uint)comments.Length));
        foreach (string comment in comments)
        {
            Text(comment);
        }

        return [.. data];
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
