using System.Buffers.Binary;
using System.Text;
using Encspect.Core;

namespace Encspect.Tests;

// Builds OptimFROG files block by block from the author's specification (format.txt: the main block, HEAD, COMP with its CRC-32, TAIL, MD5 and RECV),
// so a test changes one thing and nothing else. The data blocks hold bytes in place of compressed audio: the analyzer compares CRC-32s and does not decode.
internal static class OptimFrogBuilder
{
    public static string FixturePath(string name) => Path.Combine(Fixtures.FixtureDirectory, "optimfrog", name);

    public static byte[] Block(string id, byte[] data)
    {
        byte[] bytes = new byte[8 + data.Length];
        Encoding.ASCII.GetBytes(id).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)data.Length);
        data.CopyTo(bytes, 8);
        return bytes;
    }

    // The main block's data: the total number of samples over all channels, the sample type, the channel configuration, the sample rate, the encoder ID,
    // the compression byte and, from 5.100, the minimum decoder version less 4500; extra adds bytes the specification leaves unknown.
    public static byte[] Main(long samples = 88200, int sampleType = 3, int channelConfiguration = 1, uint rate = 44100, int encoderId = 0x2582, int compression = 0x0A,
        int? minDecoder = 100, int extra = 0, string id = "OFR ", int? size = null)
    {
        var data = new List<byte>(21);
        data.AddRange(BitConverter.GetBytes(samples).AsSpan(0, 6).ToArray());
        data.Add((byte)sampleType);
        data.Add((byte)channelConfiguration);
        data.AddRange(BitConverter.GetBytes(rate));
        data.AddRange(BitConverter.GetBytes((ushort)encoderId));
        data.Add((byte)compression);
        if (minDecoder is { } minimum)
        {
            data.AddRange(BitConverter.GetBytes((ushort)minimum));
        }

        data.AddRange(Enumerable.Repeat((byte)0x5A, extra));
        byte[] bytes = Block(id, [.. data]);
        if (size is { } stated)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)stated);
        }

        return bytes;
    }

    // A data block with a payload of the given length; the CRC-32 is zlib's over the data after it unless crc is given.
    public static byte[] Comp(long samples, int payload = 100, int sampleType = 3, int channelConfiguration = 1, int reserved = 0x1042, int encoderId = 0x2582, uint? crc = null, int seed = 0)
    {
        byte[] data = new byte[14 + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)samples);
        data[8] = (byte)sampleType;
        data[9] = (byte)channelConfiguration;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)reserved);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)encoderId);
        uint state = (uint)(0x5EED + (seed * 7919));
        for (int i = 0; i < payload; i++)
        {
            state = (state * 1103515245) + 12345;
            data[14 + i] = (byte)(state >> 16);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data, crc ?? Crc32.Of(data.AsSpan(4)));
        return Block("COMP", data);
    }

    public static byte[] Head(byte[]? wav = null) => Block("HEAD", wav ?? Wav());

    public static byte[] Tail(byte[]? data = null) => Block("TAIL", data ?? []);

    public static byte[] Md5(int length = 16) => Block("MD5 ", Enumerable.Range(1, length).Select(i => (byte)i).ToArray());

    // The 44-byte header of a WAVE file of 16-bit audio; the data chunk states a length of samples * channels * 2 bytes.
    public static byte[] Wav(int channels = 2, uint rate = 44100, int samples = 44100, byte[]? beforeData = null, byte[]? format = null)
    {
        format ??= Format(channels, rate);
        byte[] header =
        [
            .. "RIFF"u8, .. BitConverter.GetBytes(36 + (samples * channels * 2)), .. "WAVE"u8,
            .. "fmt "u8, .. BitConverter.GetBytes(format.Length), .. format,
            .. beforeData ?? [],
            .. "data"u8, .. BitConverter.GetBytes(samples * channels * 2),
        ];
        return header;
    }

    public static byte[] Format(int channels, uint rate, int tag = 1, uint? mask = null, int cbSize = 22)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((ushort)tag));
        bytes.AddRange(BitConverter.GetBytes((ushort)channels));
        bytes.AddRange(BitConverter.GetBytes(rate));
        bytes.AddRange(BitConverter.GetBytes(rate * (uint)channels * 2));
        bytes.AddRange(BitConverter.GetBytes((ushort)(channels * 2)));
        bytes.AddRange(BitConverter.GetBytes((ushort)16));
        if (tag == 0xFFFE)
        {
            bytes.AddRange(BitConverter.GetBytes((ushort)cbSize));
            if (cbSize >= 22)
            {
                bytes.AddRange(BitConverter.GetBytes((ushort)16));
                bytes.AddRange(BitConverter.GetBytes(mask ?? 3));
                bytes.AddRange(new byte[16]);
            }
        }

        return [.. bytes];
    }

    public static byte[] Chunk(string id, byte[] data) => [.. Encoding.ASCII.GetBytes(id), .. BitConverter.GetBytes(data.Length), .. data, .. (data.Length % 2 == 1 ? new byte[1] : [])];

    // A whole file of a stereo 44.1 kHz stream with the data blocks given (each of 88,200 values by default, so 1 s).
    public static byte[] File(params byte[][] blocks) => [.. blocks.SelectMany(b => b)];

    public static byte[] Standard(int comps = 1, long valuesPerBlock = 88200, int payload = 100) =>
        File(Main(valuesPerBlock * comps), Head(), .. Enumerable.Range(0, comps).Select(i => Comp(valuesPerBlock, payload, seed: i)), Tail());

    // An ID3v1.1 tag: 128 bytes, the title and artist padded with NUL bytes.
    public static byte[] Id3v1(string title, string artist)
    {
        byte[] tag = new byte[128];
        Encoding.ASCII.GetBytes("TAG").CopyTo(tag, 0);
        Encoding.ASCII.GetBytes(title).CopyTo(tag, 3);
        Encoding.ASCII.GetBytes(artist).CopyTo(tag, 33);
        tag[127] = 255;
        return tag;
    }

    // The raw bytes of a CRC-32 for a data block built by hand.
    public static byte[] Bytes(params int[] values) => [.. values.Select(v => (byte)v)];
}
