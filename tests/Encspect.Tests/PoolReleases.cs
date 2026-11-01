using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;

namespace Encspect.Tests;

// The bytes the shared array pools (ArrayPool<T>.Shared) have released since the test assembly was loaded, which the memory tests add to
// every reading of the live heap (MemoryTests.Live). A pool keeps the arrays returned to it and drops them at a full collection once they
// have gone unused long enough (System.Private.CoreLib's SharedArrayPool<T>.Trim, .NET 10: an array kept for a thread 30 s after the
// first full collection that saw it, 15 s under medium memory pressure, at once under high; one kept for all threads 60 s, then one every
// 15 s). Which collection that is depends on the clock, not on the test, so a release can fall between any two readings, on any thread's
// arrays: the test host returns an array of 16 KiB with each result it sends. Counted back in, a released array reads as if it were still
// there, and the difference of two readings no longer depends on when the pool trims.
//
// The pools report each release (ArrayPoolEventSource's BufferTrimmed: the array's length and the pool's ID, its hash code) while their
// events are on, which this listener keeps them for the whole run. The bytes need the element type, which the events do not carry (and
// the frames of SharedArrayPool<T> name only the open type), so the pools are identified ahead: the Shared pool of every type of the
// Encspect assemblies and of the runtime's and the test host's assemblies loaded with this one. A release from a pool not among them (a
// generic instantiation such as a tuple) is not guessed: Unknown says so and the reading fails, and SharedPoolGuard fails a test that
// returns an array to such a pool on its own thread, where it happens. A thread's arrays also go when the thread exits, with no event, so
// the test project keeps thread pool threads alive (System.Threading.ThreadPool.ThreadsToKeepAlive).
internal sealed class PoolReleases : EventListener
{
    private static readonly PoolReleases Listener = new();

    private readonly ConcurrentDictionary<int, int> _elementSizes = new();
    private long _released;
    private volatile bool _isReady;
    private string? _unknown;

    private PoolReleases()
    {
        Type[] runtime = [typeof(byte), typeof(sbyte), typeof(char), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong),
            typeof(float), typeof(double), typeof(decimal), typeof(bool), typeof(nint), typeof(nuint), typeof(object), typeof(string), typeof(byte[]), typeof(char[]),
            typeof(int[]), typeof(long[]), typeof(double[]), typeof(Type), typeof(DateTime), typeof(TimeSpan), typeof(Guid)];
        // The runtime's assemblies and the test host's (vstest, its serializer, xunit) that are loaded when the test assembly is, and the
        // Encspect assemblies: a pool's element type is a type of the code that rents from it, mostly through LINQ.
        string[] prefixes = ["System.Private.CoreLib", "System.Linq", "System.Collections", "Microsoft.TestPlatform", "Microsoft.VisualStudio.TestPlatform", "testhost", "xunit", "Newtonsoft.Json"];
        IEnumerable<System.Reflection.Assembly> host = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => prefixes.Any(prefix => assembly.GetName().Name!.StartsWith(prefix, StringComparison.Ordinal)));
        IEnumerable<System.Reflection.Assembly> encspect = Directory.EnumerateFiles(AppContext.BaseDirectory, "Encspect*.dll")
            .Select(path => System.Reflection.Assembly.Load(System.Reflection.AssemblyName.GetAssemblyName(path)));
        // Every Encspect*.dll in the test output is a project reference of this assembly, so each loads.
        IEnumerable<Type> own = host.Concat(encspect).Distinct().SelectMany(assembly =>
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                return ex.Types.OfType<Type>().ToArray();
            }
        });
        foreach (Type type in runtime.Concat(own))
        {
            if (type.IsGenericTypeDefinition || type.ContainsGenericParameters || type.IsByRefLike || type.IsPointer || (type.IsAbstract && type.IsSealed) || type == typeof(void))
            {
                continue;
            }

            try
            {
                object pool = typeof(ArrayPool<>).MakeGenericType(type).GetProperty(nameof(ArrayPool<byte>.Shared))!.GetValue(null)!;
                _elementSizes.TryAdd(pool.GetHashCode(), type.IsValueType ? SizeOf(type) : IntPtr.Size);
            }
            catch (Exception ex) when (ex is ArgumentException or TypeLoadException or NotSupportedException or System.Reflection.TargetInvocationException)
            {
                // Not an array element type (a type the runtime rejects as a generic argument).
            }
        }

        // Releases before this point are not counted: no reading is taken before it, and a reading compares only with later ones.
        _isReady = true;
    }

    public static long Released => Volatile.Read(ref Listener._released);

    // The first release from a pool whose element type is not known, or null.
    public static string? Unknown => Volatile.Read(ref Listener._unknown);

    // The bytes an array of the pool takes on the heap (its header and elements, rounded to a pointer), or null for a pool not identified.
    public static long? ArrayBytes(int poolId, int length) =>
        Listener._elementSizes.TryGetValue(poolId, out int size) ? ((3L * IntPtr.Size) + ((long)size * length) + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size : null;

    [ModuleInitializer]
    internal static void Start() => _ = Listener;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "System.Buffers.ArrayPoolEventSource")
        {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!_isReady)
        {
            return;
        }

        switch (eventData.EventName)
        {
            // BufferTrimmed(bufferId, bufferSize, poolId)
            case "BufferTrimmed" when eventData.Payload is [_, int length, int pool, ..]:
                if (ArrayBytes(pool, length) is { } bytes)
                {
                    Interlocked.Add(ref _released, bytes);
                }
                else
                {
                    Interlocked.CompareExchange(ref _unknown, $"the shared array pool {pool}, whose element type is not known, released an array of {length} elements", null);
                }

                break;
        }
    }

    private static int SizeOf(Type type) => (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(type).Invoke(null, null)!;
}
