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

    // An MP4 file keeps a compact record per access unit and the arrays of its graphs, and reads its
    // sample tables into arrays of the entries the boxes hold, never the samples' bytes at once
    // (plan, "Large inputs"): bounds of 150 bytes kept and 1 KB more heap per sample, about three
    // times what it takes.
    [Fact]
    public void ManySamplesStayCompact()
    {
        const int samples = 100_000;
        byte[] data = Mp4Samples(samples);
        var analyzer = Analyzer.CreateDefault();
        analyzer.Analyze("warm-up.m4a", new MemoryStream(Mp4Samples(100)));
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
        Report report = analyzer.Analyze("memory.m4a", new MemoryStream(data));
        Volatile.Write(ref done, true);
        sampler.Join();
        long kept = GC.GetTotalMemory(forceFullCollection: true) - before;

        Assert.Equal(samples, Assert.Single(report.Streams).Samples / 1024);
        Assert.True(kept < samples * 150L, $"The report keeps {kept / samples} bytes per sample.");
        Assert.True(peak - before < samples * 1024L, $"The heap grew by {(peak - before) / samples} bytes per sample.");
        GC.KeepAlive(report);
        GC.KeepAlive(data);
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
