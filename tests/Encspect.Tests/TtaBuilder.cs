using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Tests;

// Builds TTA1 streams byte by byte from the TTA format description (header, seek table, frames, each with a CRC-32), so a test changes one thing and
// nothing else. The frames hold bytes in place of coded samples: the analyzer compares CRC-32s and does not decode.
internal static class TtaBuilder
{
    public const int HeaderLength = 22;

    public static string FixturePath(string name) => Path.Combine(Fixtures.FixtureDirectory, "tta", name);

    public static byte[] Header(uint rate, int channels, int bits, long samples, int format = 1, uint? crc = null, string signature = "TTA1")
    {
        byte[] bytes = new byte[HeaderLength];
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)format);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)channels);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)bits);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), (uint)samples);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(18), crc ?? Crc32.Of(bytes.AsSpan(0, 18)));
        return bytes;
    }

    public static byte[] Table(IEnumerable<uint> sizes, uint? crc = null)
    {
        uint[] list = [.. sizes];
        byte[] bytes = new byte[(list.Length * 4) + 4];
        for (int i = 0; i < list.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), list[i]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(list.Length * 4), crc ?? Crc32.Of(bytes.AsSpan(0, list.Length * 4)));
        return bytes;
    }

    public static byte[] Frame(int payloadLength, int seed = 0, uint? crc = null)
    {
        byte[] bytes = new byte[payloadLength + 4];
        uint state = (uint)(0x5EED + (seed * 7919));
        for (int i = 0; i < payloadLength; i++)
        {
            state = (state * 1103515245) + 12345;
            bytes[i] = (byte)(state >> 16);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(payloadLength), crc ?? Crc32.Of(bytes.AsSpan(0, payloadLength)));
        return bytes;
    }

    public static long FrameLength(uint rate) => rate * 256L / 245;

    public static long FrameCount(uint rate, long samples) => (samples + FrameLength(rate) - 1) / FrameLength(rate);

    // A whole stream whose frames have the payload lengths given (a frame is 4 bytes longer).
    public static byte[] Stream(uint rate, int channels, int bits, long samples, IReadOnlyList<int> payloads)
    {
        byte[][] frames = [.. payloads.Select((n, i) => Frame(n, i))];
        return [.. Header(rate, channels, bits, samples), .. Table(frames.Select(f => (uint)f.Length)), .. frames.SelectMany(f => f)];
    }

    // A stream of the frames only, each of the length given in bytes with its CRC-32.
    public static byte[] Stream(uint rate, int channels, int bits, int frames, int payload = 200) =>
        Stream(rate, channels, bits, FrameLength(rate) * frames, [.. Enumerable.Repeat(payload, frames)]);

    // Frames of a CRC-32 and nothing else, 8,359 samples apiece at 8,000 Hz.
    public static byte[] SmallFrames(int count) => Stream(8000, 1, 16, count, 0);

    // The pieces of a stream: its header, the sizes in its seek table, and its frames with their CRC-32s.
    public static (byte[] Header, uint[] Sizes, List<byte[]> Frames) Parts(byte[] stream)
    {
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(10));
        uint samples = BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(14));
        int count = (int)FrameCount(rate, samples);
        uint[] sizes = [.. Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(HeaderLength + (4 * i))))];
        var frames = new List<byte[]>();
        int at = HeaderLength + (4 * count) + 4;
        foreach (uint size in sizes)
        {
            frames.Add(stream[at..(at + (int)size)]);
            at += (int)size;
        }

        return (stream[..HeaderLength], sizes, frames);
    }
}
