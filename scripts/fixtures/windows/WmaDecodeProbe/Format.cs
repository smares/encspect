using System.Buffers.Binary;
using System.Text;

namespace WmaDecodeProbe;

public static class Format
{
    public static string Hr(int hresult) => "0x" + ((uint)hresult).ToString("X8");

    public static byte[] Wfx(int tag, int channels, int rate, int bits)
    {
        var b = new byte[18];
        int align = channels * bits / 8;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), (ushort)tag);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)(rate * align));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), (ushort)align);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), (ushort)bits);
        return b;
    }

    public static Dictionary<string, object?> DescribeWfx(byte[] b)
    {
        var d = new Dictionary<string, object?>();
        if (b.Length < 16)
        {
            d["error"] = "format block of " + b.Length + " bytes";
            d["blockAlign"] = 0;
            return d;
        }

        int tag = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(0, 2));
        d["tag"] = tag;
        d["channels"] = (int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2, 2));
        d["sampleRate"] = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4, 4));
        d["blockAlign"] = (int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(12, 2));
        d["bitsPerSample"] = (int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(14, 2));
        d["bytes"] = b.Length;
        if (tag == 0xFFFE && b.Length >= 40)
        {
            d["subFormatTag"] = (int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(24, 2));
        }

        return d;
    }

    public static string Hex(byte[] bytes, int max = 64)
    {
        var s = new StringBuilder();
        foreach (var x in bytes.Take(max))
        {
            s.Append(x.ToString("x2"));
        }

        return s.ToString();
    }
}
