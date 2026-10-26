using Encspect.Core;

namespace Encspect.Tests;

public class SniffWindowTests
{
    // A parser reads a file through a window (its Source). SourceWindow holds a file of up to 128 KiB whole instead of a megabyte; the window of
    // a file under a megabyte must still return everything from the offset to the end of the file, as the megabyte did, and for any file the
    // same bytes. Offsets are relative to the start of the reader, which lies prefix bytes into the stream.
    private static void CheckWindow(byte[] data, int prefix, int size, Func<long, int, byte[]> window, Func<byte, long, long, long> indexOf, bool returnsTheRest)
    {
        var rng = new Random(size + prefix);
        for (int i = 0; i < 80; i++)
        {
            long offset = rng.Next(-5, size + 5);
            int want = rng.Next(0, 3) == 0 ? rng.Next(1, 100000) : rng.Next(1, 300);
            byte[] got = window(prefix + offset, want);
            if (offset < 0 || offset >= size)
            {
                Assert.Empty(got);
                continue;
            }

            if (returnsTheRest)
            {
                Assert.Equal(size - offset, got.Length);
            }
            else
            {
                Assert.True(got.Length >= Math.Min(want, size - offset), $"{got.Length} bytes at {offset} for {want} of {size}");
            }

            Assert.True(data.AsSpan(prefix + (int)offset, got.Length).SequenceEqual(got));
        }

        for (int i = 0; i < 20; i++)
        {
            byte value = (byte)rng.Next(256);
            long from = rng.Next(-3, size + 3);
            long limit = rng.Next(0, size + 10);
            long expected = -1;
            for (long p = Math.Max(from, 0); p < Math.Min(limit, size); p++)
            {
                if (data[prefix + p] == value)
                {
                    expected = p;
                    break;
                }
            }

            long found = indexOf(value, prefix + from, prefix + limit);
            Assert.Equal(expected, found < 0 ? -1 : found - prefix);
        }
    }

    public static TheoryData<int, int> WindowCases()
    {
        var cases = new TheoryData<int, int>();
        foreach (int size in new[] { 0, 1, 15, 16, 17, 1000, 70000, 131071, 131072, 131073, (1 << 20) - 1, 1 << 20, (1 << 20) + 5 })
        {
            cases.Add(size, 0);
        }

        // After a leading tag the reader is a slice of the stream.
        foreach (int size in new[] { 1000, 131072, 131073, 300000 })
        {
            cases.Add(size, 777);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(WindowCases))]
    public void FileWindowsOfTheParsersGiveTheFileWhateverItsSize(int size, int prefix)
    {
        byte[] data = new byte[prefix + size + 33];
        new Random(size + prefix + 1).NextBytes(data);
        ByteReader Reader() => new ByteReader(new MemoryStream(data)).Slice(prefix, size);
        bool returnsTheRest = size < 1 << 20;
        var flac = new Encspect.Formats.Flac.Source(Reader());
        CheckWindow(data, prefix, size, (o, w) => flac.Window(o, w).ToArray(), flac.IndexOf, returnsTheRest);
        var monkeys = new Encspect.Formats.MonkeysAudio.Source(Reader());
        CheckWindow(data, prefix, size, (o, w) => monkeys.Window(o, w).ToArray(), monkeys.IndexOf, returnsTheRest);
        var truehd = new Encspect.Formats.TrueHd.Source(Reader());
        CheckWindow(data, prefix, size, (o, w) => truehd.Window(o, w).ToArray(), truehd.IndexOf, returnsTheRest);
    }

    // TrueHD's tests lower the window below the size of the file to put boundaries between units.
    [Theory]
    [InlineData(70000, 4096)]
    [InlineData(100000, 100)]
    [InlineData(300000, 4096)]
    public void ATrueHdWindowBelowTheSizeOfTheFileGivesTheSameBytes(int size, int window)
    {
        byte[] data = new byte[size];
        new Random(size).NextBytes(data);
        var truehd = new Encspect.Formats.TrueHd.Source(new ByteReader(new MemoryStream(data)), window);
        CheckWindow(data, 0, size, (o, w) => truehd.Window(o, w).ToArray(), truehd.IndexOf, returnsTheRest: false);
    }
}
