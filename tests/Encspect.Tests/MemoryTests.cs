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

    // MPEG-1 Layer III stereo frames at 32 kbps and 44100 Hz, 104 bytes each, whose side
    // information gives the first granule's first channel 50 bytes of main data in its own frame
    // (part2_3_length 400 bits, starting at bit 20) and leaves 18 bytes of ancillary data.
    private static byte[] Layer3Frames(int count)
    {
        byte[] frame = [0xFF, 0xFB, 0x10, 0x00, 0x00, 0x00, 0x01, 0x90, .. new byte[96]];
        return Enumerable.Repeat(frame, count).SelectMany(b => b).ToArray();
    }
}
