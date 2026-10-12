using System.Runtime;
using Encspect.Core;
using Encspect.Host;
using Xunit.Abstractions;

namespace Encspect.Tests;

[CollectionDefinition(nameof(MemoryTests), DisableParallelization = true)]
public class MemoryCollection;

// A long file's report keeps compact records of its frames, and the passes over them never hold
// every frame's decoded side information or nodes at once (plan, "Large inputs"). Measured over a
// stream built in memory, alone, since the heap is the whole process's: what the report keeps,
// and the most the analysis holds live at once (Measure). The peak bounds are about twice what
// linux-x64 measures, so they hold on other platforms, and below what keeping each packet's bytes
// costs: with that regression the Speex and MP4 peaks rose from about 35 and 56 bytes per packet
// to 125 and 161. The MPEG bounds are far below what the parser took when it kept an array per
// frame and listed every frame for each pass (490 bytes kept and 2.8 KB of heap).
[Collection(nameof(MemoryTests))]
public class MemoryTests(ITestOutputHelper output)
{
    private const int Frames = 100_000;

    private const int SampleInterval = 2;

    [Fact]
    public void ManyFramesStayCompact()
    {
        byte[] data = Layer3Frames(Frames);

        (Report report, long kept, long peak) = Measure("memory.mp3", data, data[..(104 * 100)]);

        Assert.Equal(NodeValue.Integer(Frames, "frames", "frame"), report.Root.Children.Single(c => c.Name == "MPEG Audio").Value);
        output.WriteLine($"{kept / Frames} bytes kept and {peak / Frames} bytes peak per frame");
        Assert.True(kept < Frames * 250L, $"The report keeps {kept / Frames} bytes per frame.");
        Assert.True(peak < Frames * 350L, $"The live heap grew by {peak / Frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // An MP4 file keeps a compact record per access unit and the arrays of its graphs, and reads its
    // sample tables into arrays of the entries the boxes hold, never the samples' bytes at once
    // (plan, "Large inputs"): 35 bytes kept per sample on linux-x64 and a peak of 56. The bounds
    // leave room for a platform that measures twice that, as macOS on arm64 did for the Ogg
    // records, and stay below what keeping a sample's node or bytes would cost.
    [Fact]
    public void ManySamplesStayCompact()
    {
        const int samples = 100_000;

        (Report report, long kept, long peak) = Measure("samples.m4a", Mp4Samples(samples));

        Assert.Equal(samples, Assert.Single(report.Streams).Samples / 1024);
        output.WriteLine($"{kept / samples} bytes kept and {peak / samples} bytes peak per sample");
        Assert.True(kept < samples * 80L, $"The report keeps {kept / samples} bytes per sample.");
        Assert.True(peak < samples * 112L, $"The live heap grew by {peak / samples} bytes per sample.");
        GC.KeepAlive(report);
    }

    // An Ogg stream of a codec no parser reads keeps a record and a segment table per page, and the
    // scan never holds the pages' nodes or data (64 bytes kept per page and a few more for its
    // segment table and place in its stream, 72 in all on linux-x64, where macOS on arm64
    // measured 120 before the measurement compacted the heap, and a peak of 70 to 85; the bounds
    // leave room for both and stay below what keeping a page's node or bytes would cost).
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
        output.WriteLine($"{kept / pages} bytes kept and {peak / pages} bytes peak per page");
        Assert.True(kept < pages * 150L, $"The report keeps {kept / pages} bytes per page.");
        Assert.True(peak < pages * 170L, $"The live heap grew by {peak / pages} bytes per page.");
        GC.KeepAlive(report);
    }

    // A Speex stream keeps an 8-byte record per packet, 4 bytes per frame and its per-packet series
    // (16 bytes, whose x values are also the packets' offsets), plus its share of the page records:
    // 30 bytes in all on linux-x64, where macOS on arm64 measured 67 before the measurement
    // compacted the heap, and a peak of 30 to 50, where macOS on arm64 measured 80. The bounds
    // leave room for both and stay below what keeping a packet's bytes or node would cost (a peak
    // of 125 on linux-x64).
    [Fact]
    public void ManySpeexPacketsStayCompact()
    {
        const int packets = 200_000;
        // A narrowband frame of submode 7 (492 bits, 24.6 kbps) and the terminator, 63 bytes.
        byte[] frame = [0x38, .. new byte[60], 0x07, 0x80];
        var builder = new OggBuilder()
            .Page(9, 0, 2, OggBuilder.SpeexHeader(8000, 0))
            .Page(9, 0, 0, OggBuilder.VorbisComment("Encoded with Speex 1.2.1"));
        for (int done = 0; done < packets; done += 250)
        {
            builder.Page(9, ((done + 250) * 160L) - 40, done + 250 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(frame, 250)]);
        }

        (Report report, long kept, long peak) = Measure("speex.spx", builder.Bytes);

        Assert.Equal(packets * 160L / 8000.0 - (40 / 8000.0), report.Streams.Single().Duration, 9);
        output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * 80L, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * 110L, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // The heap the report keeps, and the most the analysis held live at once: while it runs, a
    // sampler thread forces a blocking full collection every few milliseconds and reads the heap
    // that survived it. Reading the collector's last figures instead measures nothing where no
    // collection happens during a short analysis. The forced collections slow the analysis down,
    // so the files stay small.
    private static (Report Report, long Kept, long Peak) Measure(string name, byte[] data, byte[]? warmUp = null)
    {
        var analyzer = Analyzer.CreateDefault();
        analyzer.Analyze("warm-up", new MemoryStream(warmUp ?? data[..Math.Min(data.Length, 4096)]));
        long before = Live();
        long peak = before;
        bool done = false;
        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                Volatile.Write(ref peak, Math.Max(Volatile.Read(ref peak), GC.GetTotalMemory(forceFullCollection: false)));
                Thread.Sleep(SampleInterval);
            }
        });
        sampler.Start();
        Report report = analyzer.Analyze(name, new MemoryStream(data));
        Volatile.Write(ref done, true);
        sampler.Join();
        long kept = Live() - before;
        GC.KeepAlive(data);
        return (report, kept, Volatile.Read(ref peak) - before);
    }

    // The live heap after a full, compacting collection that also compacts the large object heap,
    // where series and record chunks live, so the space their growth left behind does not count.
    private static long Live()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    // An M4A file of count silent AAC LC stereo access units of 100 bytes (a channel pair element with
    // max_sfb 0 and an END element, then zero bytes no decoder reads, so that keeping a sample's
    // bytes would show), in one chunk after moov (ISO/IEC 14496-12 8.5 to 8.7).
    private static byte[] Mp4Samples(int count)
    {
        static byte[] Box(string type, params byte[][] parts)
        {
            byte[] payload = parts.SelectMany(p => p).ToArray();
            return [.. BigEndian(8 + payload.Length), .. System.Text.Encoding.ASCII.GetBytes(type), .. payload];
        }

        static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

        byte[] unit = [0x21, 0x00, 0x03, 0x20, 0x01, 0x9C, .. new byte[94]];
        byte[] Moov(int mdatPayload)
        {
            byte[] esds = Box("esds", [0, 0, 0, 0, 0x03, 25, 0, 1, 0, 0x04, 17, 0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x05, 2, 0x12, 0x10, 0x06, 1, 2]);
            byte[] mp4a = Box("mp4a", new byte[6], [0, 1], new byte[8], [0, 2, 0, 16, 0, 0, 0, 0, 0xAC, 0x44, 0, 0], esds);
            byte[] stsz = Box("stsz", [0, 0, 0, 0], BigEndian(0), BigEndian(count), Enumerable.Range(0, count).SelectMany(_ => BigEndian(unit.Length)).ToArray());
            byte[] stbl = Box("stbl",
                Box("stsd", [0, 0, 0, 0], BigEndian(1), mp4a),
                Box("stts", [0, 0, 0, 0], BigEndian(1), BigEndian(count), BigEndian(1024)),
                Box("stsc", [0, 0, 0, 0], BigEndian(1), BigEndian(1), BigEndian(count), BigEndian(1)),
                stsz,
                Box("stco", [0, 0, 0, 0], BigEndian(1), BigEndian(mdatPayload)));
            byte[] mdia = Box("mdia",
                Box("mdhd", [0, 0, 0, 0], new byte[8], BigEndian(44100), BigEndian(count * 1024), [0x55, 0xC4, 0, 0]),
                Box("hdlr", [0, 0, 0, 0], new byte[4], System.Text.Encoding.ASCII.GetBytes("soun"), new byte[13]),
                Box("minf", Box("smhd", new byte[8]), Box("stbl", stbl[8..])));
            byte[] tkhd = Box("tkhd", [0, 0, 0, 3], new byte[8], BigEndian(1), new byte[4], BigEndian(count * 1024 / 44100 * 1000), new byte[60]);
            byte[] mvhd = Box("mvhd", [0, 0, 0, 0], new byte[8], BigEndian(1000), BigEndian(count * 1024 / 44100 * 1000), new byte[80]);
            return Box("moov", mvhd, Box("trak", tkhd, mdia));
        }

        byte[] ftyp = Box("ftyp", System.Text.Encoding.ASCII.GetBytes("M4A "), new byte[4], System.Text.Encoding.ASCII.GetBytes("M4A isom"));
        int moovLength = Moov(0).Length;
        byte[] moov = Moov(ftyp.Length + moovLength + 8);
        byte[] mdat = [.. BigEndian(8 + (count * unit.Length)), .. "mdat"u8, .. Enumerable.Repeat(unit, count).SelectMany(u => u)];
        return [.. ftyp, .. moov, .. mdat];
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
