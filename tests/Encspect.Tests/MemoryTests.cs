using System.Runtime;
using Encspect.Core;
using Encspect.Host;
using Xunit.Abstractions;

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
public class MemoryTests(ITestOutputHelper output)
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

    // An MP4 file keeps a compact record per access unit and the arrays of its graphs, and reads its
    // sample tables into arrays of the entries the boxes hold, never the samples' bytes at once
    // (plan, "Large inputs"): 35 bytes kept per sample on linux-x64. The bound leaves room for a
    // platform that measures twice that, as macOS on arm64 did for the Ogg records, and stays below
    // what keeping a sample's node or bytes would cost.
    [Fact]
    public void ManySamplesStayCompact()
    {
        const int samples = 100_000;

        (Report report, long kept, long peak) = Measure("samples.m4a", Mp4Samples(samples));

        Assert.Equal(samples, Assert.Single(report.Streams).Samples / 1024);
        output.WriteLine($"{kept / samples} bytes kept and {peak / samples} bytes peak per sample");
        Assert.True(kept < samples * 80L, $"The report keeps {kept / samples} bytes per sample.");
        Assert.True(peak < samples * 1024L, $"The heap grew by {peak / samples} bytes per sample.");
        GC.KeepAlive(report);
    }

    // An Ogg stream of a codec no parser reads keeps a record and a segment table per page, and the
    // scan never holds the pages' nodes or data (64 bytes kept per page and a few more for its
    // segment table and place in its stream, 72 in all on linux-x64, where macOS on arm64
    // measured 120 before the measurement compacted the heap; the bounds leave room for both and
    // stay below what keeping a page's node or bytes would cost).
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
        Assert.True(peak < pages * 1024L, $"The heap grew by {peak / pages} bytes per page.");
        GC.KeepAlive(report);
    }

    // A Speex stream keeps an 8-byte record per packet, 4 bytes per frame and its per-packet series
    // (16 bytes, whose x values are also the packets' offsets), plus its share of the page records:
    // 30 bytes in all on linux-x64, where macOS on arm64 measured 67 before the measurement
    // compacted the heap. The bounds leave room for both and stay below what keeping a packet's
    // bytes or node would cost.
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
        output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * 80L, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * 1024L, $"The heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // A Vorbis stream keeps an 8-byte record per packet and its per-packet series (16 bytes, whose
    // x values are also the packets' offsets), plus its share of the page records: 26 bytes in all
    // on linux-x64. The bound leaves about twice that, as the Speex one does.
    [Fact]
    public void ManyVorbisPacketsStayCompact()
    {
        const int packets = 200_000;
        // Vorbis I, section 4.2.2: one channel at 44100 Hz, blocks of 256 and 2048 samples.
        byte[] identification = new byte[30];
        "\u0001vorbis"u8.CopyTo(identification);
        identification[11] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(identification.AsSpan(12), 44100);
        identification[28] = 0xB8;
        identification[29] = 1;
        byte[] comment = [0x03, .. "vorbis"u8, .. OggBuilder.VorbisComment("Encspect"), 0x01];
        var builder = new OggBuilder()
            .Page(5, 0, 2, identification)
            .Page(5, 0, 0, comment, MinimalVorbisSetup);
        byte[] block = [0x0E];
        for (int done = 0; done < packets; done += 250)
        {
            builder.Page(5, (done + 249) * 1024L, done + 250 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(block, 250)]);
        }

        (Report report, long kept, long peak) = Measure("vorbis.ogg", builder.Bytes);

        Assert.Equal((packets - 1) * 1024L, report.Streams.Single().Samples);
        output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * 60L, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * 1024L, $"The heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // An Opus stream keeps a 20-byte record per stream of each packet, 2 bytes per frame and its
    // per-packet series (16 bytes), plus its share of the page records: 40 bytes in all on
    // linux-x64. The bound leaves about twice that.
    [Fact]
    public void ManyOpusPacketsStayCompact()
    {
        const int packets = 200_000;
        byte[] head = [.. "OpusHead"u8, 1, 1, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0];
        var builder = new OggBuilder()
            .Page(6, 0, 2, head)
            .Page(6, 0, 0, [.. "OpusTags"u8, .. OggBuilder.VorbisComment("Encspect")]);
        byte[] packet = [0xF8, 0, 0, 0];
        for (int done = 0; done < packets; done += 250)
        {
            builder.Page(6, (done + 250) * 960L, done + 250 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(packet, 250)]);
        }

        (Report report, long kept, long peak) = Measure("opus.opus", builder.Bytes);

        Assert.Equal((packets * 960L) - 312, report.Streams.Single().Samples);
        output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * 90L, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * 1024L, $"The heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // The setup header of make_vorbis_opus_fixtures.py's minimal codec: one codebook, a floor of
    // type 1 without partitions, a residue without passes, one mapping, a short and a long mode.
    private static readonly byte[] MinimalVorbisSetup =
    [
        0x05, 0x76, 0x6F, 0x72, 0x62, 0x69, 0x73, 0x00, 0x42, 0x43, 0x56, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x80, 0x62, 0x40, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
    ];

    // The heap the report keeps and the most it grew by while the analysis ran.
    private static (Report Report, long Kept, long Peak) Measure(string name, byte[] data)
    {
        var analyzer = Analyzer.CreateDefault();
        analyzer.Analyze("warm-up", new MemoryStream(data[..Math.Min(data.Length, 4096)]));
        long before = Live();
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
        Report report = analyzer.Analyze(name, new MemoryStream(data));
        Volatile.Write(ref done, true);
        sampler.Join();
        long kept = Live() - before;
        GC.KeepAlive(data);
        return (report, kept, peak - before);
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

    // An M4A file of count silent AAC LC stereo access units of 6 bytes (a channel pair element with
    // max_sfb 0 and an END element), in one chunk after moov (ISO/IEC 14496-12 8.5 to 8.7).
    private static byte[] Mp4Samples(int count)
    {
        static byte[] Box(string type, params byte[][] parts)
        {
            byte[] payload = parts.SelectMany(p => p).ToArray();
            return [.. BigEndian(8 + payload.Length), .. System.Text.Encoding.ASCII.GetBytes(type), .. payload];
        }

        static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

        byte[] unit = [0x21, 0x00, 0x03, 0x20, 0x01, 0x9C];
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
