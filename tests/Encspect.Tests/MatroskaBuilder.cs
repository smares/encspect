using System.Buffers.Binary;
using System.Text;

namespace Encspect.Tests;

// Writes small Matroska files byte by byte for the tests that need what no fixture holds: sizes and counts past the parser's limits.
internal static class MatroskaBuilder
{
    public static byte[] Element(uint id, params byte[][] children) => Element(id, Concat(children));

    public static byte[] Element(uint id, byte[] payload) => Concat([IdBytes(id), Size(payload.Length), payload]);

    // An element that states the size it is given, whatever it holds.
    public static byte[] WithSize(uint id, long size, byte[] payload) => Concat([IdBytes(id), Size(size), payload]);

    public static byte[] Unsigned(uint id, ulong value)
    {
        int length = 1;
        while (length < 8 && value >> (8 * length) != 0)
        {
            length++;
        }

        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(value >> (8 * (length - 1 - i)));
        }

        return Element(id, bytes);
    }

    public static byte[] Text(uint id, string value) => Element(id, Encoding.ASCII.GetBytes(value));

    public static byte[] Void(int length) => Element(0xEC, new byte[length]);

    public static byte[] Header(string docType = "matroska") =>
        Element(0x1A45DFA3, Unsigned(0x4286, 1), Unsigned(0x42F7, 1), Unsigned(0x42F2, 4), Unsigned(0x42F3, 8), Text(0x4282, docType), Unsigned(0x4287, 4), Unsigned(0x4285, 2));

    // One stereo Opus track at 48 kHz with a minimal OpusHead.
    public static byte[] OpusTracks(bool isHeaderStripped = false)
    {
        byte[] head = new byte[19];
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 0);
        head[8] = 1;
        head[9] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), 312);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48000);
        byte[] rate = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(rate, 48000);
        return Element(0x1654AE6B, Element(0xAE, Unsigned(0xD7, 1), Unsigned(0x73C5, 1), Unsigned(0x83, 2), Text(0x86, "A_OPUS"), Element(0x63A2, head),
            Element(0xE1, Element(0xB5, rate), Unsigned(0x9F, 2)),
            isHeaderStripped
                ? Element(0x6D80, Element(0x6240, Unsigned(0x5031, 0), Unsigned(0x5032, 1), Unsigned(0x5033, 0),
                    Element(0x5034, Unsigned(0x4254, 3), Element(0x4255, new byte[] { 0xF8 }))))
                : []));
    }

    public static byte[] SimpleBlock(int track, int timestamp, byte[] frame)
    {
        byte[] header = [(byte)(0x80 | track), (byte)(timestamp >> 8), (byte)timestamp, 0x80];
        return Element(0xA3, Concat([header, frame]));
    }

    // A SimpleBlock of fixed-size lacing that states 256 frames of no bytes: seven bytes of file.
    public static byte[] EmptyLacedBlock() => Element(0xA3, [0x81, 0x00, 0x00, 0x84, 0xFF]);

    public static byte[] Cluster(long timestamp, params byte[][] children) => Element(0x1F43B675, Concat([Unsigned(0xE7, (ulong)timestamp), .. children]));

    public static byte[] Segment(params byte[][] children) => Concat([[0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], .. children]);

    public static byte[] File(params byte[][] children) => Concat([Header(), Segment(children)]);


    // A file of Opus blocks in Clusters of the given size.
    public static byte[] OpusBlocks(int blocks, int blocksPerCluster)
    {
        var stream = new MemoryStream();
        stream.Write(Header());
        stream.Write([0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        stream.Write(OpusTracks());
        byte[] frame = [0xFC, 0xFF, 0xFE];
        for (int done = 0; done < blocks; done += blocksPerCluster)
        {
            var list = new List<byte[]>();
            for (int i = 0; i < Math.Min(blocksPerCluster, blocks - done); i++)
            {
                list.Add(SimpleBlock(1, i & 0x7FFF, frame));
            }

            stream.Write(Cluster(done * 20L, [.. list]));
        }

        return stream.ToArray();
    }

    // A Segment of unknown size with two stereo-less Opus tracks (1 and 2), each with the given number of blocks of 20 ms in one Cluster.
    public static byte[] TwoOpusTracksSegment(int blocks)
    {
        byte[] head = new byte[19];
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 0);
        head[8] = 1;
        head[9] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), 312);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48000);
        byte[] rate = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(rate, 48000);
        byte[] Track(uint number) => Element(0xAE, Unsigned(0xD7, number), Unsigned(0x73C5, number), Unsigned(0x83, 2), Text(0x86, "A_OPUS"), Element(0x63A2, head),
            Unsigned(0x56AA, 6_500_000), Element(0xE1, Element(0xB5, rate), Unsigned(0x9F, 2)));
        byte[] frame = [0xFC, 0xFF, 0xFE];
        var items = new List<byte[]>();
        for (int i = 0; i < blocks; i++)
        {
            items.Add(SimpleBlock(1, i * 20, frame));
            items.Add(SimpleBlock(2, i * 20, frame));
        }

        return Segment(Element(0x1654AE6B, Track(1), Track(2)), Cluster(0, [.. items]));
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var stream = new MemoryStream();
        foreach (byte[] part in parts)
        {
            stream.Write(part);
        }

        return stream.ToArray();
    }

    private static byte[] IdBytes(uint id)
    {
        int length = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(id >> (8 * (length - 1 - i)));
        }

        return bytes;
    }

    private static byte[] Size(long size)
    {
        int length = 1;
        while (size >= (1L << (7 * length)) - 1)
        {
            length++;
        }

        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(size >> (8 * (length - 1 - i)));
        }

        bytes[0] |= (byte)(0x80 >> (length - 1));
        return bytes;
    }
}
