using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Formats.TrueHd;
using Encspect.Formats.WavPack;
using Encspect.Host;
using Xunit.Abstractions;

namespace Encspect.Tests;

// The measuring helpers of the memory tests, which run in the one non-parallel collection (see MemoryTests).
public abstract class MemoryTestBase(ITestOutputHelper output) : IDisposable
{
    protected ITestOutputHelper Output { get; } = output;

    protected const int SampleInterval = 2;

    private readonly SharedPoolGuard _pools = new();

    public void Dispose()
    {
        _pools.Check();
    }

    protected const int Frames = 100_000;

    // What the further units add to the coding series, from the series' own sizes: the codes and count bytes of the category series and 8 bytes
    // for each point of the numeric series, which shares the bitrate series' x values.
    protected static void CheckCodingGrowth(Report few, Report many, int fewer, int units, int categoryBytes, int numericBytes, string[]? names = null)
    {
        StreamInfo small = Assert.Single(few.Streams);
        StreamInfo large = Assert.Single(many.Streams);
        Assert.Equal(names ?? ["Block Usage", "Block Sizes", "Stereo Coding"], large.CategorySeries.Select(c => c.Name));
        long grown = large.CategorySeries.Sum(c => c.StorageBytes) - small.CategorySeries.Sum(c => c.StorageBytes);
        long index = large.CategorySeries.Count * 4L * ((units - fewer) / 1024 + 1);
        Assert.True(grown <= (units - fewer) * (long)categoryBytes + index, $"The category series grow by {grown / (double)(units - fewer):F3} bytes per further unit.");

        Series bandwidth = large.Series.Single(s => s.Name == "Coded Bandwidth");
        Assert.Equal(units, bandwidth.Values.Length);
        long numeric = BackingBytes(bandwidth.Values) - BackingBytes(small.Series.Single(s => s.Name == "Coded Bandwidth").Values);
        Assert.True(numeric <= (units - fewer) * (long)numericBytes, $"The coded bandwidth grows by {numeric / (double)(units - fewer):F2} bytes per further unit.");
        Series bitrate = large.Series.Single(s => s.Name == "Bitrate");
        Assert.True(MemoryMarshal.TryGetArray(bandwidth.X, out ArraySegment<double> own)
            && MemoryMarshal.TryGetArray(bitrate.X, out ArraySegment<double> shared) && ReferenceEquals(own.Array, shared.Array),
            "The coded bandwidth copies the bitrate series' x values although every unit has a value.");
    }

    // The bytes of the array behind a series, not only of the part it shows, so a slack kept at the end counts.
    // The compression ratio of a lossless stream (docs/design/coding-graphs.md, "Memory budget"): 8 bytes for each unit of its own values, and the bitrate series' x values,
    // shared, so that it costs nothing more; its per-second form is as short as the bitrate's.
    protected static void CheckCompressionRatio(StreamInfo stream)
    {
        Series bitrate = stream.Series.Single(s => s.Name == "Bitrate");
        Series ratio = stream.Series.Single(s => s.Name == "Compression Ratio");
        Assert.Equal(bitrate.X.Length, ratio.Values.Length);
        Assert.True(MemoryMarshal.TryGetArray(ratio.X, out ArraySegment<double> own) && MemoryMarshal.TryGetArray(bitrate.X, out ArraySegment<double> shared) && ReferenceEquals(own.Array, shared.Array),
            "The compression ratio copies the bitrate series' x values.");
        Assert.Equal(bitrate.X.Length * 8L, BackingBytes(ratio.Values));
        Assert.Equal(stream.Series.Single(s => s.Name == "Bitrate Over Time").Values.Length, stream.Series.Single(s => s.Name == "Compression Ratio Over Time").Values.Length);
    }

    protected static long BackingBytes(ReadOnlyMemory<double> values) =>
        MemoryMarshal.TryGetArray(values, out ArraySegment<double> segment) ? segment.Array!.Length * 8L : values.Length * 8L;

    // Frames of ffmpeg's encoder at 96 kbit/s, repeated.
    protected static byte[] AdtsFrames(int count)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "ffmpeg-adts.aac"));
        var stream = new MemoryStream();
        int frames = 0;
        while (frames < count)
        {
            for (int at = 0; at + 7 <= source.Length && frames < count; frames++)
            {
                int length = ((source[at + 3] & 3) << 11) | (source[at + 4] << 3) | (source[at + 5] >> 5);
                stream.Write(source, at, length);
                at += length;
            }
        }

        return stream.ToArray();
    }

    protected Report Bounded(string name, byte[] data, long bound, string code, bool expectFinding = true)
    {
        (Report report, long kept, long peak) = Measure(name, data, data[..Math.Min(data.Length, 4096)]);
        Output.WriteLine($"{name}: {kept / 1_000_000} MB kept and {peak / 1_000_000} MB peak for {data.Length / 1_000_000} MB");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        if (expectFinding)
        {
            Assert.Contains(report.Findings, f => f.Code == code);
        }

        Assert.True(peak < bound, $"{name}: the live heap grew by {peak / 1_000_000} MB.");
        GC.KeepAlive(report);
        return report;
    }

    // What the runtime is configured with, for reading a failure on a runner.
    protected static string RuntimeSettings() =>
        $"{Environment.ProcessorCount} processors, server GC {GCSettings.IsServerGC}, " +
        string.Join(", ", GC.GetConfigurationVariables().Where(v => v.Key is "GCServer" or "GCConcurrent" or "GCHeapCount" or "GCgen0size" or "GCgen0MaxBudget" or "GCRegionSize" or "GCConserveMemory" or "GCHeapHardLimit").Select(v => $"{v.Key}={v.Value}"));

    protected static string Hundredths(double value) => (Math.Round(value, 2) + 0.0).ToString("F2");

    // The heap the report keeps, and the most the analysis held live at once.
    //
    // Kept is the difference of two readings taken one after the other once the analysis is over:
    // with the report alive, and with it dropped (the report is analyzed again for the caller
    // afterwards). The heap before the analysis is no baseline: the process holds the dead large
    // arrays of the tests that ran before, which the collector reports as live until a later
    // collection lets go of them, at a time that varies by run and platform. Taken across an
    // analysis, that read as 47 MB negative (ubuntu CI, and here in half of the runs) or, on
    // macOS, as 4 MB too much. Two readings in a row see the same leftovers.
    //
    // A single pair is still one observation of a heap the process shares with the runner: on
    // ubuntu-24.04-arm one pair read 321 bytes per packet for a stream that keeps 69 (a parser
    // that is deterministic, whose peak read the usual 109 in the same run), and consecutive
    // readings here differ by tens of megabytes while the analysis of the first test after a big
    // one is still holding what its frame held (the ASF tests). So the pair is taken twice, in two
    // separate analyses, and kept is the smaller difference: noise inflates one pair, a real
    // regression inflates both. A planted per-packet allocation in the SV8 record path fails the
    // tests with it (docs/development.md, "Memory tests").
    //
    // Peak: while the analysis runs, a sampler thread forces a blocking full collection every few
    // milliseconds and reads the heap that survived it from the collector's record of that
    // collection. Reading the heap's size afterwards also counts what the analysis allocated
    // since, which on a busy machine, where the sampler waits to run again, tripled the peak
    // (macOS on x64, 125 bytes per Opus packet where linux-x64 measures 41). It is taken from the
    // heap before the analysis, so those leftovers can only lower it, never fail a test. The forced
    // collections slow the analysis down, so the files stay small. The sampler may not get to read
    // the heap once the analysis has built its report, which a short analysis on a loaded machine
    // finishes first: UnknownSv8PacketsStayCompact, whose bound is on the growth of the peak from 1
    // to 4 million packets, read 992 bytes for the first (macOS on x64) where the second read 3.5
    // MB, and failed with 1.17 bytes per further packet. The heap with the report alive, after the
    // analysis, is the least the analysis held at its end, so it counts as a reading too (the
    // smaller of the two pairs' readings). MeasureExcess is the variant for tests whose bound is on
    // a growth between two counts and so must not depend on the heap before the analysis.
    protected static (Report Report, long Kept, long Peak) Measure(string name, byte[] data, byte[]? warmUp = null, AnalysisOptions? options = null, Analyzer? analyzer = null)
    {
        (Report report, long kept, long before, long sampled, long atEnd) = Run(name, data, warmUp, options, analyzer);
        return (report, kept, Math.Max(Math.Max(before, sampled), atEnd) - before);
    }

    // What the analysis holds above the heap it starts and ends with (report alive), as the highest of the
    // live heap read at 32 places in the input and by a thread that reads it every 300 ms, all by
    // the compacting collections of Live, so only live objects count and no reading can be too
    // high. The places are fixed fractions of the input, read after the read that reaches them in
    // any pass over it (ProbeStream), so they do not depend on timing; the thread covers the work
    // after the last read and the stretches between places, and its timing only decides whether
    // it catches a transient that exists. docs/development.md, "Memory tests", gives the
    // measurement, what it can and cannot see, and the failures that led to it. The reference is the
    // higher of the heap before the analysis and the heap at its end, so memory that earlier work
    // releases during the analysis lowers the reading instead of raising it. As with kept, two
    // analyses are taken and the smaller excess counts.
    // The report's size, from two analyses with the heap counted after each. The peak while the series are recorded is not asserted here: the
    // sampler sees every 300 ms and the transient lies below the final heap, so it read 0 for any count and could not fail. The recording
    // buffer's 12 bytes per unit at peak are documented in the design.
    protected static (Report Report, long Kept) MeasureKept(string name, byte[] data, byte[]? warmUp = null)
    {
        (Report report, long kept, _, _) = MeasureExcess(name, data, warmUp);
        return (report, kept);
    }

    protected static (Report Report, long Kept, long Excess, string Where) MeasureExcess(string name, byte[] data, byte[]? warmUp = null, AnalysisOptions? options = null)
    {
        var analyzer = Analyzer.CreateDefault();
        analyzer.Analyze("warm-up", new MemoryStream(warmUp ?? data[..Math.Min(data.Length, 4096)]), options);
        Live();
        (long before, long withReport, long highest, string where) = ProbedAnalysis(analyzer, name, data, options);
        long withoutReport = Live();
        (long secondBefore, long secondWithReport, long secondHighest, string secondWhere) = ProbedAnalysis(analyzer, name, data, options);
        long secondWithoutReport = Live();
        Report report = analyzer.Analyze(name, new MemoryStream(data), options);
        GC.KeepAlive(data);
        long kept = Math.Min(withReport - withoutReport, secondWithReport - secondWithoutReport);
        long excess = Math.Max(0, highest - Math.Max(before, withReport));
        long secondExcess = Math.Max(0, secondHighest - Math.Max(secondBefore, secondWithReport));
        return (report, kept, Math.Min(excess, secondExcess),
            $"[{where}, before {before}, at end {withReport} | {secondWhere}, before {secondBefore}, at end {secondWithReport}]");
    }

    // The live heap before the analysis, with its report alive after it, and the highest live heap
    // read during it; the report is dropped on return. A measurement whose probes did not fire is a failure, not a pass.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    protected static (long Before, long WithReport, long Highest, string Where) ProbedAnalysis(Analyzer analyzer, string name, byte[] data, AnalysisOptions? options)
    {
        var stream = new ProbeStream(data);
        long before = Live();
        bool done = false;
        var sampler = new Thread(() =>
        {
            long next = 0;
            while (!Volatile.Read(ref done))
            {
                if (Environment.TickCount64 >= next || stream.Ended)
                {
                    stream.Record(LiveOnce(), stream.Ended ? "thread after the end" : "thread");
                    next = Environment.TickCount64 + 300;
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        });
        sampler.IsBackground = true;
        sampler.Start();
        Report report;
        try
        {
            report = analyzer.Analyze(name, stream, options);
        }
        finally
        {
            Volatile.Write(ref done, true);
            sampler.Join();
        }

        Assert.True(stream.FullPasses >= 1, $"No pass over the input reached all {ProbeStream.Places.Length} places ({stream.Probes} places reached in all).");
        long live = Live();
        GC.KeepAlive(report);
        return (before, live, stream.Highest, stream.Where);
    }

    private sealed class ProbeStream(byte[] data) : MemoryStream(data)
    {
        private readonly byte[] _data = data;

        // Prime numerators of 137: irregular, fixed fractions of the input.
        public static readonly int[] Places =
            [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71, 73, 79, 83, 89, 97, 101, 103, 107, 109, 113, 127, 131];

        private const int Denominator = 137;

        private long _highest;
        private int _passed;
        private int _probes;
        private int _fullPasses;
        private long _endedUntil;

        public long Highest { get { lock (this) { return _highest; } } }

        public int Probes => Volatile.Read(ref _probes);

        // Passes that reached every place without a read back before one.
        public int FullPasses => Volatile.Read(ref _fullPasses);

        // True for half a second after a read has reached the end of the input, when what the
        // analysis does next (checks, the stream info, the findings) is read back to back.
        public bool Ended => Environment.TickCount64 < Volatile.Read(ref _endedUntil);

        // Which reading was the highest, and what the collector recorded for the collection that made it.
        public string Where { get; private set; } = "none";

        public void Record(long live, string source)
        {
            lock (this)
            {
                if (live <= _highest)
                {
                    return;
                }

                _highest = live;
                GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
                string generations = string.Join("/", info.GenerationInfo.ToArray().Select(g => $"{g.SizeBeforeBytes >> 10}>{g.SizeAfterBytes >> 10}"));
                Where = $"{live} by {source} (position {Position}, places {_passed}), generations KB before>after {generations}, pinned {info.PinnedObjectsCount}, finalization pending {info.FinalizationPendingCount}, promoted {info.PromotedBytes}, fragmented {info.FragmentedBytes}";
            }
        }

        // After a read, once for the places it reached whatever the pass over the input; a read
        // back before a place makes it count again.
        private void Probe()
        {
            long position = Position;
            int passed = _passed;
            while (passed > 0 && At(passed - 1) > position)
            {
                passed--;
            }

            int reached = passed;
            while (reached < Places.Length && At(reached) <= position)
            {
                reached++;
            }

            if (position >= Length)
            {
                Volatile.Write(ref _endedUntil, Environment.TickCount64 + 500);
            }

            _passed = reached;
            if (reached > passed)
            {
                Interlocked.Add(ref _probes, reached - passed);
                if (reached == Places.Length)
                {
                    Interlocked.Increment(ref _fullPasses);
                }

                Record(LiveOnce(), $"place {reached}");
            }
        }

        private long At(int place) => Length * Places[place] / Denominator;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            Probe();
            return read;
        }

        // Not base.Read(buffer): a MemoryStream of a derived type reads a span through Stream.Read(Span), which rents an array of the span's size
        // from the shared pool and leaves it there (SharedPoolGuard).
        public override int Read(Span<byte> buffer)
        {
            if (Position >= Length)
            {
                Probe();
                return 0;
            }

            int read = (int)Math.Min(Length - Position, buffer.Length);
            _data.AsSpan((int)Position, read).CopyTo(buffer);
            Position += read;
            Probe();
            return read;
        }

        public override int ReadByte()
        {
            int value = base.ReadByte();
            Probe();
            return value;
        }
    }

    protected static (Report Report, long Kept, long Before, long Sampled, long AtEnd) Run(string name, byte[] data, byte[]? warmUp, AnalysisOptions? options = null, Analyzer? given = null)
    {
        Analyzer analyzer = given ?? Analyzer.CreateDefault();
        analyzer.Analyze("warm-up", new MemoryStream(warmUp ?? data[..Math.Min(data.Length, 4096)]), options);
        long before = Live();
        long withReport = SampledAnalysis(analyzer, name, data, out long sampled, options);
        long withoutReport = Live();
        long secondWithReport = AnalysisWithReport(analyzer, name, data, options);
        long secondWithoutReport = Live();
        Report report = analyzer.Analyze(name, new MemoryStream(data), options);
        GC.KeepAlive(data);
        long kept = Math.Min(withReport - withoutReport, secondWithReport - secondWithoutReport);
        return (report, kept, before, sampled, Math.Min(withReport, secondWithReport));
    }

    // The live heap with the report of an analysis alive; the report is dropped on return.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    protected static long AnalysisWithReport(Analyzer analyzer, string name, byte[] data, AnalysisOptions? options = null)
    {
        Report report = analyzer.Analyze(name, new MemoryStream(data), options);
        long live = Live();
        GC.KeepAlive(report);
        return live;
    }

    // The live heap with the report of an analysis alive, read while a sampler thread forces
    // collections during it; the report is dropped on return.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    protected static long SampledAnalysis(Analyzer analyzer, string name, byte[] data, out long sampled, AnalysisOptions? options = null)
    {
        bool done = false;
        long shared = 0;
        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                Volatile.Write(ref shared, Math.Max(Volatile.Read(ref shared), Collected(compacting: false)));
                Thread.Sleep(SampleInterval);
            }
        });
        sampler.IsBackground = true;
        sampler.Start();
        Report report;
        try
        {
            report = analyzer.Analyze(name, new MemoryStream(data), options);
        }
        finally
        {
            Volatile.Write(ref done, true);
            sampler.Join();
        }

        long live = Live();
        GC.KeepAlive(report);
        sampled = Volatile.Read(ref shared);
        return live;
    }

    // One blocking full collection and the heap that survived it, as it stood when the collection ended, with what the shared array pools
    // had dropped before it added back (PoolAccounting): an array a pool drops after the collection, in the finalizer it triggers, is still
    // in this reading and is counted into the next. The finalizers of an earlier collection are waited for first, and one collection runs
    // at a time (the sampler threads read while the test thread does), so a drop is never counted without being gone from the heap read.
    // What the accounting cannot add back is checked per test by SharedPoolGuard, on the test thread: an assertion here would end the test
    // host when a sampler thread reads.
    protected static long Collected(bool compacting)
    {
        lock (CollectionLock)
        {
            GC.WaitForPendingFinalizers();
            long dropped = PoolAccounting.Dropped;
            if (compacting)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: compacting);
            GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            return info.HeapSizeBytes - info.FragmentedBytes + dropped;
        }
    }

    protected static readonly object CollectionLock = new();

    // One such collection: a reading to compare with a bound of MB, taken while the analysis runs,
    // where the repeats of Live (for what finalizers release) would cost seconds.
    protected static long LiveOnce() => Collected(compacting: true);

    // The live heap after full, compacting collections that also compact the large object heap,
    // where series and record chunks live, so the space their growth left behind does not count.
    // Read from the collector's record of the last collection, as the sampler does, not from
    // GC.GetTotalMemory: that is the collector's running estimate, not a measurement of what
    // survived. With the segments collector (DOTNET_GCName=libclrgc) it reads 94 MB where the
    // record says 49 MB and the gap changes between calls, which showed as 3.5 MB kept after 1
    // million unknown SV8 packets and 9.4 MB after 4 million. Collections repeat until two
    // readings in a row agree to within 64 KB.
    internal static long Live()
    {
        SharedPoolGuard.ReadOn(Environment.CurrentManagedThreadId);
        long previous = long.MaxValue;
        long current = 0;
        for (int i = 0; i < 8 && Math.Abs(previous - current) > 64 * 1024; i++)
        {
            previous = current;
            current = Collected(compacting: true);
        }

        return current;
    }
}
