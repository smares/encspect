using Encspect.Core;

namespace Encspect.Tests;

public class SniffWindowTests
{
    // Each sniff of a format that searches the file for its signature reads it through a window; one of a megabyte for a file of a few
    // kilobytes was a fifth of the time of a folder scan. The window of a short file holds the file, and must give the same bytes.
    private static void CheckWindow(byte[] data, Func<long, int, byte[]> window, Func<byte, long, long, long> indexOf)
    {
        var rng = new Random(data.Length);
        for (int i = 0; i < 60; i++)
        {
            long offset = rng.Next(-5, data.Length + 5);
            int want = rng.Next(0, 3) == 0 ? rng.Next(1, 100000) : rng.Next(1, 300);
            byte[] got = window(offset, want);
            if (offset < 0 || offset >= data.Length)
            {
                Assert.Empty(got);
                continue;
            }

            Assert.True(got.Length >= Math.Min(want, data.Length - offset), $"{got.Length} bytes at {offset} for {want} of {data.Length}");
            Assert.True(data.AsSpan((int)offset, got.Length).SequenceEqual(got));
        }

        for (int i = 0; i < 20; i++)
        {
            byte value = (byte)rng.Next(256);
            long from = rng.Next(-3, data.Length + 3);
            long limit = rng.Next(0, data.Length + 10);
            long expected = -1;
            for (long p = Math.Max(from, 0); p < Math.Min(limit, data.Length); p++)
            {
                if (data[p] == value)
                {
                    expected = p;
                    break;
                }
            }

            Assert.Equal(expected, indexOf(value, from, limit));
        }
    }

    public static TheoryData<int> WindowFileSizes => new() { 0, 1, 15, 16, 17, 1000, 70000, (1 << 20) - 1, 1 << 20, (1 << 20) + 5 };

    [Theory]
    [MemberData(nameof(WindowFileSizes))]
    public void FileWindowsOfTheSniffedFormatsGiveTheFileWhateverItsSize(int size)
    {
        byte[] data = new byte[size];
        new Random(size + 1).NextBytes(data);
        ByteReader Reader() => new(new MemoryStream(data));
        var flac = new Encspect.Formats.Flac.Source(Reader());
        CheckWindow(data, (o, w) => flac.Window(o, w).ToArray(), flac.IndexOf);
        var monkeys = new Encspect.Formats.MonkeysAudio.Source(Reader());
        CheckWindow(data, (o, w) => monkeys.Window(o, w).ToArray(), monkeys.IndexOf);
        var truehd = new Encspect.Formats.TrueHd.Source(Reader());
        CheckWindow(data, (o, w) => truehd.Window(o, w).ToArray(), truehd.IndexOf);
    }
}
