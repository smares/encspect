using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace Encspect.Tests;

// The memory tests count back in what the shared array pools release (PoolReleases), so a release does not move their readings. What
// this guard keeps out is the arrays that make those releases large or unaccountable: a test whose own code, or the analysis it runs,
// returns to a shared pool an array of 16 KiB or more, or an array to a pool whose element type PoolReleases could not identify, fails
// with where it returned it. The usual sources are a LINQ ToArray over many elements (a collection expression that spreads an IEnumerable
// is one; it returns all its segments together when it finishes, the largest about half of them), a Read(Span) on a class derived from
// MemoryStream (which reads through a pooled array of the span's size) and System.Text.Json's documents. The 8 KiB buffers of
// DeflateStream, which the Matroska and ID3v2 parsers inflate through, stay below the bound.
//
// Only returns on the thread that runs the test count. The test host returns arrays on threads of its own (an array of 16 KiB with each
// result it sends), and an analysis that a timed-out damaged-file test left running (CorruptFiles) returns them on a thread pool thread;
// neither is the test's, and both are counted back in by PoolReleases when the pool releases them.
internal sealed class SharedPoolGuard : EventListener
{
    private const long SmallestBytes = 16 * 1024;

    private readonly int _thread = Environment.CurrentManagedThreadId;

    private readonly ConcurrentQueue<string> _returns = new();

    public void Check()
    {
        if (!_returns.IsEmpty)
        {
            Assert.Fail($"{_returns.Count} arrays were returned to a shared array pool during the test that its readings should not have to count back in (16 KiB or more, or of a pool whose element type is not known). The first: {_returns.First()}");
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "System.Buffers.ArrayPoolEventSource")
        {
            EnableEvents(eventSource, EventLevel.Verbose);
        }
    }

    // BufferReturned(bufferId, bufferSize, poolId): the pool keeps the array, a new one or one it lent before, until it releases it.
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (Environment.CurrentManagedThreadId != _thread || eventData.EventName != "BufferReturned" || eventData.Payload is not [_, int length, int pool, ..])
        {
            return;
        }

        long? bytes = PoolReleases.ArrayBytes(pool, length);
        if (bytes < SmallestBytes)
        {
            return;
        }

        // The frames of the guard, the event's dispatch and the pool come first; some are inlined, so the caller is the frame after the last of them.
        string[] frames = [.. Environment.StackTrace.Split('\n').Select(line => line.Trim())];
        int caller = Array.FindLastIndex(frames, line => line.Contains("ArrayPool", StringComparison.Ordinal) || line.Contains(nameof(SharedPoolGuard), StringComparison.Ordinal)
            || line.Contains("System.Diagnostics.Tracing.", StringComparison.Ordinal) || line.Contains("System.Environment.", StringComparison.Ordinal)) + 1;
        _returns.Enqueue($"{(bytes is { } known ? $"{known} bytes" : $"{length} elements of an unknown type")}, {string.Join(" < ", frames.Skip(caller).Take(6))}");
    }
}
