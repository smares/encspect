using System.Collections.Concurrent;

namespace Encspect.Tests;

// The memory tests add what the shared array pools drop back into their readings (PoolAccounting), so a drop does not move them. This
// guard, made for each memory test, fails the test
// - when a pool PoolAccounting cannot size dropped an array during it, with where that pool's first array was allocated;
// - when a trim ran under high memory pressure during it, where a pool can drop an array inside the reading after the one it was returned
//   before, which the accounting would count without its having been in the earlier reading;
// - when the test's own thread (its code or the analysis it runs) had a shared pool make an array of more than 8,216 bytes, or one of a
//   pool that cannot be sized;
// - when a reading (MemoryTests.Live) ran on another thread than the one the guard watches, which would leave the check above blind.
//
// The bound on the test's arrays is for what the accounting cannot see: a dedicated thread's arrays leave with it when it exits, with no
// event, and the races of a trim with a Rent or Return on another thread (PoolAccounting). Keeping the test's own arrays at 8,216 bytes
// or less, the size of DeflateStream's buffer (the Matroska and ID3v2 parsers inflate through it), bounds such a miss on the test's side
// to 8,216 bytes: 3.4 bytes per further slice over the 2,400 of the WAVE test, whose bound is 8. The usual sources of larger arrays are a
// LINQ ToArray or ToList over many elements (a collection expression that spreads an IEnumerable is one; it returns all its segments
// together when it finishes), a Read(Span) on a class derived from MemoryStream (which reads through a pooled array of the span's size)
// and System.Text.Json's documents.
//
// Arrays made on other threads are not the test's: the test host's (an array of about 16 KiB with each result it sends) and an analysis
// that a timed-out damaged-file test left running (CorruptFiles), on a thread pool thread. The accounting covers their drops while their
// threads live, and the test project keeps thread pool threads alive (System.Threading.ThreadPool.ThreadsToKeepAlive).
internal sealed class SharedPoolGuard
{
    private const long LargestBytes = 8216;

    private static SharedPoolGuard? s_current;

    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<string> _arrays = new();
    private readonly int _unknownDrops;
    private readonly int _highPressureTrims;
    private int _otherThread;

    public SharedPoolGuard()
    {
        PoolAccounting.Start();
        _unknownDrops = PoolAccounting.UnknownDropCount;
        _highPressureTrims = PoolAccounting.HighPressureTrims;
        PoolAccounting.OnAllocated = Allocated;
        Volatile.Write(ref s_current, this);
    }

    // Called by every reading on the test's side (MemoryTests.Live) with its thread.
    public static void ReadOn(int thread)
    {
        if (Volatile.Read(ref s_current) is { } guard && thread != guard._thread)
        {
            Interlocked.CompareExchange(ref guard._otherThread, thread, 0);
        }
    }

    public void Check()
    {
        PoolAccounting.OnAllocated = null;
        Interlocked.CompareExchange(ref s_current, null, this);
        if (PoolAccounting.FirstUnknownDropSince(_unknownDrops) is { } drop)
        {
            Assert.Fail($"{PoolAccounting.UnknownDropCount - _unknownDrops} drops during the test came from shared array pools the readings cannot size. The first: {drop}.");
        }

        if (PoolAccounting.HighPressureTrims != _highPressureTrims)
        {
            Assert.Fail($"The shared array pools trimmed under high memory pressure {PoolAccounting.HighPressureTrims - _highPressureTrims} times during the test, where they drop arrays at every full collection and the readings cannot add the drops back without counting arrays that were never in them.");
        }

        if (Volatile.Read(ref _otherThread) is not 0 and int other)
        {
            Assert.Fail($"A reading ran on thread {other}, not on thread {_thread}, which the guard watches for pooled arrays.");
        }

        if (_arrays.TryPeek(out string? first))
        {
            Assert.Fail($"{_arrays.Count} arrays of more than {LargestBytes} bytes, or of a pool that cannot be sized, were made by a shared array pool on the test's thread. The first: {first}");
        }
    }

    private void Allocated(int pool, long? bytes, int length)
    {
        if (Environment.CurrentManagedThreadId != _thread || bytes <= LargestBytes)
        {
            return;
        }

        // The frames of the guard, the event's dispatch and the pool come first; some are inlined, so the caller is the frame after the last of them.
        string[] frames = [.. Environment.StackTrace.Split('\n').Select(line => line.Trim())];
        int caller = Array.FindLastIndex(frames, line => line.Contains("ArrayPool", StringComparison.Ordinal) || line.Contains("Encspect.Tests.PoolAccounting", StringComparison.Ordinal)
            || line.Contains(nameof(SharedPoolGuard), StringComparison.Ordinal) || line.Contains("System.Diagnostics.Tracing.", StringComparison.Ordinal) || line.Contains("System.Environment.", StringComparison.Ordinal)) + 1;
        _arrays.Enqueue($"{(bytes is { } known ? $"{known} bytes" : $"{length} elements of pool {pool}, which cannot be sized")}, {string.Join(" < ", frames.Skip(caller).Take(6))}");
    }
}
