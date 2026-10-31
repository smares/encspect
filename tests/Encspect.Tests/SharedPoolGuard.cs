using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace Encspect.Tests;

// The memory tests read the live heap of the whole process, so between two readings nothing may change it but the analysis they measure.
// The shared array pools (ArrayPool<T>.Shared) keep the arrays returned to them and release one about 30 s after its last use (measured
// on linux-x64), at whichever full collection comes first then: inside a later memory test, where the release lowers one reading by the
// array's size. Releases of arrays of different sizes come seconds apart, so two can land
// in the two passes of one measurement, and the smaller of the passes then carries the smaller release. That failed
// DecodingAndMeasuringALongWaveFileHoldsNothingPerSlice in run 41200838167 on two platforms with one array of 128 KiB in both passes
// (131,096 bytes with its header: the report kept that much more on macos-15-intel, and the analysis held 54.62 bytes more per further
// slice on ubuntu-26.04), released about 30 s after MeasuringManyChainedStreamsStaysWithinTheBound had last read through ProbeStream.
//
// So the memory tests return no array of 8,192 elements or more to those pools: a test that does, through its own code or the analysis
// it runs, fails here with where it returned it. The usual sources are a LINQ ToArray over many elements (a collection expression that
// spreads an IEnumerable is one), a Read(Span) on a class derived from MemoryStream (which reads through a pooled array of the span's
// size) and System.Text.Json's documents. Smaller arrays move a reading by less than the bounds' margins.
internal sealed class SharedPoolGuard : EventListener
{
    private const int Smallest = 8192;

    private readonly ConcurrentQueue<string> _returns = new();

    public void Check()
    {
        if (!_returns.IsEmpty)
        {
            Assert.Fail($"{_returns.Count} arrays of {Smallest} elements or more were returned to a shared array pool during the test, which releases each about 30 s after its last use, inside a later memory test's readings. The first: {_returns.First()}");
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "System.Buffers.ArrayPoolEventSource")
        {
            EnableEvents(eventSource, EventLevel.Verbose);
        }
    }

    // BufferReturned(bufferId, bufferSize, poolId): the pool keeps the array, a new one or one it lent before, until it releases it. Only
    // returns from Encspect's code count: the test host returns arrays of its own (its messages to the runner, which stay below the bound
    // while the tests pass), and those are not the test's to remove.
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName == "BufferReturned" && eventData.Payload is [_, int size, ..] && size >= Smallest)
        {
            string[] frames = [.. Environment.StackTrace.Split('\n').Select(line => line.Trim())];
            // The frames of the guard, the event's dispatch and the pool come first; some are inlined, so the caller is the frame after the last of them.
            int caller = Array.FindLastIndex(frames, line => line.Contains("ArrayPool", StringComparison.Ordinal) || line.Contains(nameof(SharedPoolGuard), StringComparison.Ordinal)
                || line.Contains("System.Diagnostics.Tracing.", StringComparison.Ordinal) || line.Contains("System.Environment.", StringComparison.Ordinal)) + 1;
            if (frames.Skip(caller).Any(line => line.StartsWith("at Encspect.", StringComparison.Ordinal)))
            {
                _returns.Enqueue($"{size} elements on thread {Environment.CurrentManagedThreadId}, {string.Join(" < ", frames.Skip(caller).Take(6))}");
            }
        }
    }
}
