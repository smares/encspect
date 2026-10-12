using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

[CollectionDefinition(nameof(MemoryTests), DisableParallelization = true)]
public class MemoryCollection;

// A long file's report keeps compact records of its frames, and the passes over them never hold
// every frame's decoded side information or nodes at once (plan, "Large inputs"). Measured over a
// stream built in memory, alone, since the heap is the whole process's. The bounds are generous
// for the compact records (130 bytes kept and 0.1 KB more heap per frame) and far below what the
// parser took when it kept an array per frame and listed every frame for each pass (490 bytes
// and 2.8 KB).
[Collection(nameof(MemoryTests))]
public class MemoryTests
{
    private const int Frames = 100_000;

    [Fact]
    public void ManyFramesStayCompact()
    {
        byte[] data = Layer3Frames(Frames);
        var analyzer = Analyzer.CreateDefault();
        analyzer.Analyze("warm-up.mp3", new MemoryStream(data[..(104 * 100)]));
        long before = GC.GetTotalMemory(forceFullCollection: true);

        // The heap after each collection while the analysis runs, without the free space the
        // collection left in it, whose amount depends on the machine's GC settings: what the
        // analysis holds at once.
        long peak = before;
        bool done = false;
        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.Any);
                Interlocked.Exchange(ref peak, Math.Max(Interlocked.Read(ref peak), info.HeapSizeBytes - info.FragmentedBytes));
                Thread.Sleep(1);
            }
        });
        sampler.Start();
        Report report = analyzer.Analyze("memory.mp3", new MemoryStream(data));
        Volatile.Write(ref done, true);
        sampler.Join();
        long kept = GC.GetTotalMemory(forceFullCollection: true) - before;

        Assert.Equal(NodeValue.Integer(Frames, "frames", "frame"), report.Root.Children.Single(c => c.Name == "MPEG Audio").Value);
        Assert.True(kept < Frames * 250L, $"The report keeps {kept / Frames} bytes per frame.");
        Assert.True(peak - before < Frames * 1024L, $"The heap grew by {(peak - before) / Frames} bytes per frame.");
        GC.KeepAlive(report);
        GC.KeepAlive(data);
    }

    // An Ogg stream of a codec no parser reads keeps a record and a segment table per page, and the
    // scan never holds the pages' nodes or data (64 bytes kept per page and a few more for its
    // segment table and place in its stream; the bounds leave room).
    [Fact]
    public void ManyPagesStayCompact()
    {
        const int pages = 100_000;
        var builder = new OggBuilder().Page(7, 0, 2, "unknown codec"u8.ToArray());
        byte[] packet = new byte[20];
        for (int i = 1; i < pages; i++)
        {
            builder.Page(7, i, i == pages - 1 ? (byte)4 : (byte)0, packet);
        }

        (Report report, long kept, long peak) = Measure("pages.ogg", builder.Bytes);

        Assert.Equal(NodeValue.Integer(pages, "pages", "page"), report.Root.Children.Single().Value);
        Assert.True(kept < pages * 150L, $"The report keeps {kept / pages} bytes per page.");
        Assert.True(peak < pages * 1024L, $"The heap grew by {peak / pages} bytes per page.");
        GC.KeepAlive(report);
    }

    // A Speex stream keeps a 16-byte record per packet, 4 bytes per frame and its per-packet series
    // (16 bytes), plus its share of the page records; the bounds leave room.
    [Fact]
    public void ManySpeexPacketsStayCompact()
    {
        const int packets = 200_000;
        byte[] frame = [0x08, 0, 0, 0, 0, 0x0F];
        var builder = new OggBuilder()
            .Page(9, 0, 2, OggBuilder.SpeexHeader(8000, 0))
            .Page(9, 0, 0, OggBuilder.VorbisComment("Encoded with Speex 1.2.1"));
        for (int done = 0; done < packets; done += 250)
        {
            builder.Page(9, ((done + 250) * 160L) - 40, done + 250 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(frame, 250)]);
        }

        (Report report, long kept, long peak) = Measure("speex.spx", builder.Bytes);

        Assert.Equal(packets * 160L / 8000.0 - (40 / 8000.0), report.Streams.Single().Duration, 9);
        Assert.True(kept < packets * 100L, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * 1024L, $"The heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // The heap the report keeps and the most it grew by while the analysis ran.
    private static (Report Report, long Kept, long Peak) Measure(string name, byte[] data)
    {
        var analyzer = Analyzer.CreateDefault();
        analyzer.Analyze("warm-up", new MemoryStream(data[..Math.Min(data.Length, 4096)]));
        long before = GC.GetTotalMemory(forceFullCollection: true);
        long peak = before;
        bool done = false;
        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                Interlocked.Exchange(ref peak, Math.Max(Interlocked.Read(ref peak), GC.GetGCMemoryInfo(GCKind.Any).HeapSizeBytes));
                Thread.Sleep(1);
            }
        });
        sampler.Start();
        Report report = analyzer.Analyze(name, new MemoryStream(data));
        Volatile.Write(ref done, true);
        sampler.Join();
        long kept = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(data);
        return (report, kept, peak - before);
    }

    // MPEG-1 Layer III stereo frames at 32 kbps and 44100 Hz, 104 bytes each, whose side
    // information gives the first granule's first channel 50 bytes of main data in its own frame
    // (part2_3_length 400 bits, starting at bit 20) and leaves 18 bytes of ancillary data.
    private static byte[] Layer3Frames(int count)
    {
        byte[] frame = [0xFF, 0xFB, 0x10, 0x00, 0x00, 0x00, 0x01, 0x90, .. new byte[96]];
        return Enumerable.Repeat(frame, count).SelectMany(b => b).ToArray();
    }
}
