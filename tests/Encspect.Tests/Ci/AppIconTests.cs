using System.IO.Compression;

namespace Encspect.Tests;

// The Windows icon is committed (scripts/make_icons.py writes it from the SVGs); this checks the file's structure and that
// the small entries come from the small form (the glass alone) and the large ones from the rounded tile.
public sealed class AppIconTests
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    [Fact]
    public void The_icon_holds_every_size_as_a_png_of_that_size()
    {
        List<(int Size, byte[] Png)> entries = ReadIco();

        Assert.Equal(Sizes, entries.Select(e => e.Size).ToArray());
        foreach ((int size, byte[] png) in entries)
        {
            Assert.True(png.AsSpan(0, 8).SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]), $"{size}: PNG signature");
            Assert.Equal(size, ReadInt(png, 16));
            Assert.Equal(size, ReadInt(png, 20));
        }
    }

    // A pixel near the corner (an eighth in for the large form, a sixteenth for the small one, clear of the circle's
    // antialiased edge): inside the rounded tile of the large form, outside the circle of the small one.
    [Fact]
    public void The_small_entries_are_the_glass_alone_and_the_large_ones_the_tile()
    {
        foreach ((int size, byte[] png) in ReadIco())
        {
            int probe = size <= 32 ? size / 16 : size / 8;
            Assert.Equal(size <= 32 ? 0 : 255, AlphaAt(png, probe, probe));
        }
    }

    private static List<(int Size, byte[] Png)> ReadIco()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "THIRD-PARTY-NOTICES.md")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        byte[] file = File.ReadAllBytes(Path.Combine(dir!, "src", "Encspect.Gui", "Assets", "Icon", "encspect.ico"));
        Assert.Equal(1, BitConverter.ToUInt16(file, 2));
        int count = BitConverter.ToUInt16(file, 4);
        var entries = new List<(int, byte[])>();
        for (int i = 0; i < count; i++)
        {
            int at = 6 + (16 * i);
            int size = file[at] == 0 ? 256 : file[at];
            int length = BitConverter.ToInt32(file, at + 8);
            int offset = BitConverter.ToInt32(file, at + 12);
            entries.Add((size, file[offset..(offset + length)]));
        }

        return entries;
    }

    private static int ReadInt(byte[] png, int at) => (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];

    // 8-bit RGBA, non-interlaced, as the renderer writes it.
    private static int AlphaAt(byte[] png, int x, int y)
    {
        int width = ReadInt(png, 16);
        Assert.Equal(8, png[24]);
        Assert.Equal(6, png[25]);
        Assert.Equal(0, png[28]);
        using var idat = new MemoryStream();
        for (int at = 8; at < png.Length;)
        {
            int length = ReadInt(png, at);
            if (png[at + 4] == (byte)'I' && png[at + 5] == (byte)'D' && png[at + 6] == (byte)'A' && png[at + 7] == (byte)'T')
            {
                idat.Write(png, at + 8, length);
            }

            at += 12 + length;
        }

        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        byte[] raw = inflated.ToArray();
        int stride = width * 4;
        var previous = new byte[stride];
        var row = new byte[stride];
        for (int line = 0; line <= y; line++)
        {
            int start = line * (stride + 1);
            byte filter = raw[start];
            for (int i = 0; i < stride; i++)
            {
                int a = i >= 4 ? row[i - 4] : 0;
                int b = previous[i];
                int c = i >= 4 ? previous[i - 4] : 0;
                int value = raw[start + 1 + i];
                value += filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                row[i] = (byte)value;
            }

            (previous, row) = (row, previous);
        }

        return previous[(x * 4) + 3];
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
