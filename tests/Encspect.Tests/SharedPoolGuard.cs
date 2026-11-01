using System.Collections.Concurrent;

namespace Encspect.Tests;

// The memory tests add what the shared array pools drop back into their readings (PoolAccounting), so a drop does not move them. This
// guard, made for each memory test, first registers the pools of the assemblies loaded since the last test (so that registering never
// allocates inside a test), then fails the test
// - when a pool PoolAccounting cannot size dropped an array during it, with where that pool's first array was allocated;
// - when registering an assembly's pools failed;
// - when a trim ran under high memory pressure during it, where a pool can drop an array inside the reading after the one it was returned
//   before, which the accounting would count without its having been in the earlier reading;
// - when a shared pool made an array of more than 8,216 bytes on the test's thread (its code or the analysis it runs), or one of a pool
//   that cannot be sized;
// - when a reading (MemoryTests.Live) ran on another thread than the one the guard watches, which would leave the check above blind.
//
// The check of made arrays is a detector of new sources, not a bound: it sees a pool miss, an array the pool makes because it has none of
// that size, which on the test's thread is deterministic only for the first rent of a size in the process; a large array the test rents
// that some thread made earlier is not seen. 8,216 bytes is the size of DeflateStream's buffer, which the Matroska and ID3v2 parsers
// inflate through. The usual sources of larger arrays are a LINQ ToArray or ToList over many elements (a collection expression that
// spreads an IEnumerable is one; it returns all its segments together when it finishes), a Read(Span) on a class derived from MemoryStream
// (which reads through a pooled array of the span's size) and System.Text.Json's documents.
//
// What the accounting cannot see stays: a dedicated thread's arrays leave with it when it exits, with no event, and a trim can race with a
// Rent or Return on another thread (PoolAccounting). Each is a rare miss of one array, on any thread, up to the largest array pooled then:
// one of the test host's arrays of about 16 KiB is 6.8 bytes per further slice over the 2,400 of the WAVE test, whose bound is 8.
//
// Arrays made on other threads are not the test's: the test host's and an analysis that a timed-out damaged-file test left running
// (CorruptFiles), on a thread pool thread. The accounting covers their drops while their threads live, and the test project keeps thread
// pool threads alive (System.Threading.ThreadPool.ThreadsToKeepAlive).
internal sealed class SharedPoolGuard
{
    private const long LargestBytes = 8216;

    private static SharedPoolGuard? s_current;

    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<string> _arrays = new();
    private readonly int _unknownDrops;
    private readonly int _highPressureTrims;
    private readonly int _registrationFailures;
    private int _otherThread;

    public SharedPoolGuard()
    {
        PoolAccounting.Start();
        _registrationFailures = PoolAccounting.RegistrationFailureCount;
        PoolAccounting.RegisterLoaded();
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

        if (PoolAccounting.FirstRegistrationFailureSince(_registrationFailures) is { } failure)
        {
            Assert.Fail($"Registering the shared array pools of an assembly failed, so its pools cannot be sized: {failure}.");
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
            Assert.Fail($"{_arrays.Count} arrays of more than {LargestBytes} bytes, or of a pool that cannot be sized, were made by a shared array pool on the test's thread. The first: {first}.");
        }
    }

    private void Allocated(int pool, long? bytes, int length)
    {
        if (Environment.CurrentManagedThreadId != _thread || bytes <= LargestBytes)
        {
            return;
        }

        _arrays.Enqueue($"{(bytes is { } known ? $"{known} bytes" : $"{length} elements of pool {pool}, which cannot be sized")}, made by {PoolAccounting.Callers(6)}");
    }
}
