using System.Buffers.Binary;

namespace Encspect.Tests;

// Takes WavPack blocks apart and puts them together again with sub-blocks changed, working the block
// checksum out again as libwavpack does (open_utils.c WavpackVerifySingleBlock), so a test changes
// one thing and nothing else.
internal static class WavPackBuilder
{
    public const int HeaderLength = 32;

    public static string FixturePath(string name) => Path.Combine(Fixtures.FixtureDirectory, "wavpack", name);

    // The blocks of a file of nothing else, by their sizes.
    public static List<byte[]> Blocks(string name) => Split(File.ReadAllBytes(FixturePath(name)));

    public static List<byte[]> Split(byte[] file)
    {
        var blocks = new List<byte[]>();
        for (int at = 0; at < file.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4)) + 8;
            blocks.Add(file[at..(at + length)]);
            at += length;
        }

        return blocks;
    }

    // The sub-blocks of a block, header included.
    public static List<byte[]> Parts(byte[] block)
    {
        var parts = new List<byte[]>();
        int at = HeaderLength;
        while (at + 2 <= block.Length)
        {
            int size = block[at + 1] << 1;
            int header = 2;
            if ((block[at] & 0x80) != 0)
            {
                size += (block[at + 2] << 9) + (block[at + 3] << 17);
                header = 4;
            }

            parts.Add(block[at..(at + header + size)]);
            at += header + size;
        }

        return parts;
    }

    public static int Function(byte[] part) => part[0] & 0x3F;

    // A sub-block with this function and data, padded to an even length.
    public static byte[] Sub(int function, byte[] data)
    {
        bool odd = data.Length % 2 != 0;
        byte[] padded = odd ? [.. data, 0] : data;
        int words = padded.Length / 2;
        int id = function | (odd ? 0x40 : 0) | (words > 255 ? 0x80 : 0);
        return words > 255 ? [(byte)id, (byte)words, (byte)(words >> 8), (byte)(words >> 16), .. padded] : [(byte)id, (byte)words, .. padded];
    }

    // The block with these sub-blocks; a checksum sub-block among them is worked out again.
    public static byte[] Join(byte[] block, IEnumerable<byte[]> parts)
    {
        byte[] body = [.. parts.SelectMany(p => p)];
        byte[] result = [.. block[..HeaderLength], .. body];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        List<byte[]> list = Parts(result);
        int at = HeaderLength;
        foreach (byte[] part in list)
        {
            if (Function(part) == 0x2F)
            {
                uint sum = uint.MaxValue;
                for (int i = 0; i < at; i += 2)
                {
                    sum = (sum * 3) + BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(i));
                }

                int header = (part[0] & 0x80) != 0 ? 4 : 2;
                if (part.Length - header == 4)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at + header), sum);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(at + header), (ushort)(sum ^ (sum >> 16)));
                }
            }

            at += part.Length;
        }

        return result;
    }

    public static byte[] SetFlags(byte[] block, uint set, uint clear = 0)
    {
        byte[] copy = (byte[])block.Clone();
        uint flags = (BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(24)) | set) & ~clear;
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(24), flags);
        return Join(copy, Parts(copy));
    }

    public static byte[] Replace(byte[] block, int function, byte[]? replacement)
    {
        IEnumerable<byte[]> parts = Parts(block);
        parts = replacement is null ? parts.Where(p => Function(p) != function) : parts.Select(p => Function(p) == function ? replacement : p);
        return Join(block, parts);
    }

    public static byte[] Concat(IEnumerable<byte[]> blocks) => [.. blocks.SelectMany(b => b)];
}
