using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Encspect.Tests;

// The bytes the shared array pools (ArrayPool<T>.Shared) have dropped since the accounting started (with the first memory test), which the
// memory tests add to every reading of the live heap (MemoryTests.Collected): a dropped array reads as if it were still there, so the
// difference of two readings does not depend on when a pool drops one. A pool keeps the arrays returned to it and drops them at a full
// collection when its clock says so (System.Private.CoreLib's SharedArrayPool<T>.Trim, .NET 10: an array kept for a thread 30 s after the
// first full collection that saw it, 15 s under medium memory pressure, at every full collection under high; one kept for all threads 60 s,
// then one every 15 s, or after 10 s under high pressure), and drops a returned array at once when the per-core stack it would go to is full.
// An array returned and dropped between two readings was in neither and is still added to the later one. Under low pressure that needs
// 30 s between them; under medium pressure 15 s, which a sampled analysis on a slow runner can take between the reading before it and the
// one with its report: the arrays made and dropped inside are the test host's (about 16 KiB each) and the test's (SharedPoolGuard), and they
// only move that peak's baseline, whose bounds are MB.
//
// The events used are ArrayPoolEventSource's Informational ones, which fire rarely:
// - BufferTrimmed(bufferId, bufferSize, poolId): a trim dropped an array.
// - BufferDropped(bufferId, bufferSize, poolId, bucketId, reason): with reason Full (0), Return pushed the thread's previous array into a
//   full per-core stack and dropped it, an array of the same size. The event names the array just returned, which stays pooled, so the
//   drop is added without entering the dedupe below, where it would hide that array's own trim later. OverMaximumSize is an array the
//   pool never held.
// - BufferTrimPoll(milliseconds, pressure): each trim's memory pressure. Under high pressure (2) a pool drops every array a thread keeps at
//   each full collection, so an array returned between two readings can be dropped inside the later one and counted though it was never
//   in the earlier one; SharedPoolGuard fails a test during which that pressure was seen.
// - BufferAllocated(bufferId, bufferSize, poolId, bucketId, reason): where a pool not identified made its first array (its frames' method
//   names, kept once per such pool: about a KB, possibly inside a window, which is accepted; the frames are read without file and line,
//   which would load assemblies), and SharedPoolGuard's check of the arrays the pools make on the test's thread.
// The Verbose events (every Rent and Return) would let the accounting follow every array in and out, which high pressure would not
// disturb, but ManyLogicalStreamsStayBounded alone raises 28.8 million of them, and following them took that test from about 40 s to 8 min.
//
// A drop is counted once per (pool, array) among the last few thousand, so a trim that reports an array twice (SharedArrayPool<T>.Trim
// says it may, when a Return or Rent on another thread races with it) counts it once. Two residuals stay: an array a Rent took just
// before the trim logged it reads as dropped while its renter holds it, and an array a Return pushed back reads as dropped until it goes.
//
// Bytes need the element type, which the events do not carry, and the frames of SharedArrayPool<T> name only the open type: the pools are
// identified by registering the Shared pool of every type of every loaded assembly (about 14,400 pools, 1.1 KB each). That allocates for
// good, so it never runs inside a test: an assembly that loads later is only queued, and the next SharedPoolGuard registers it before
// its test's first reading. A pool still unknown (a generic instantiation such as a tuple, or a type of an assembly loaded during the
// test) cannot be sized: its drops are listed, with where its first array was allocated, and SharedPoolGuard fails the test during
// which one happened. So does a drop from an ID that two element sizes share (2 of 14,416 IDs in one run): which of the two pools is in
// use is known only to a private field of the runtime, so a pool in use whose ID collides fails each test during which it drops an array.
internal sealed class PoolAccounting : EventListener
{
    private const int Ambiguous = -1;
    private const int RecentCount = 4096;
    private const int HighPressure = 2;

    private static readonly Lazy<PoolAccounting> Instance = new(() => new PoolAccounting());

    private readonly ConcurrentDictionary<int, int> _elementSizes = new();
    private readonly ConcurrentDictionary<int, string> _origins = new();
    private readonly ConcurrentQueue<string> _unknownDrops = new();
    private readonly ConcurrentQueue<Assembly> _loaded = new();
    private readonly ConcurrentQueue<string> _registrationFailures = new();

    // The last drops counted, sized ahead so that keeping them allocates nothing between two readings.
    private readonly HashSet<(int Pool, int Array)> _recentDrops = new(RecentCount);
    private readonly (int Pool, int Array)[] _recent = new (int, int)[RecentCount];
    private int _recentNext;

    private long _dropped;
    private int _highPressureTrims;
    private volatile Action<int, long?, int>? _onAllocated;
    private volatile bool _isReady;

    private PoolAccounting()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
        {
            try
            {
                _loaded.Enqueue(e.LoadedAssembly);
            }
            catch (Exception ex)
            {
                // Nothing may escape into the code whose load raised the event; the guard reports it.
                _registrationFailures.Enqueue($"{e.LoadedAssembly.GetName().Name} was not queued: {ex.GetType().Name}: {ex.Message}");
            }
        };
        foreach (Assembly assembly in Encspect(typeof(PoolAccounting).Assembly).Concat(AppDomain.CurrentDomain.GetAssemblies()).Distinct())
        {
            TryRegister(assembly);
        }

        _isReady = true;
    }

    // Registers the assemblies loaded since the last call; the guard calls it before its test's first reading.
    public static void RegisterLoaded()
    {
        PoolAccounting accounting = Instance.Value;
        while (accounting._loaded.TryDequeue(out Assembly? assembly))
        {
            accounting.TryRegister(assembly);
        }
    }

    public static int RegistrationFailureCount => Instance.Value._registrationFailures.Count;

    public static string? FirstRegistrationFailureSince(int count) => Instance.Value._registrationFailures.Skip(count).FirstOrDefault();

    public static long Dropped => Volatile.Read(ref Instance.Value._dropped);

    public static int HighPressureTrims => Volatile.Read(ref Instance.Value._highPressureTrims);

    public static int UnknownDropCount => Instance.Value._unknownDrops.Count;

    // Called on the allocating thread for each array a pool makes: its pool, its bytes (null for a pool not sized) and its length.
    public static Action<int, long?, int>? OnAllocated
    {
        set => Instance.Value._onAllocated = value;
    }

    public static string? FirstUnknownDropSince(int count) => Instance.Value._unknownDrops.Skip(count).FirstOrDefault();

    public static void Start() => _ = Instance.Value;

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

        switch (eventData.EventName, eventData.Payload)
        {
            case ("BufferTrimmed", [int array, int length, int pool, ..]):
                Drop(pool, array, length);
                break;
            case ("BufferDropped", [_, int length, int pool, _, var reason, ..]) when Convert.ToInt32(reason, System.Globalization.CultureInfo.InvariantCulture) == 0:
                Drop(pool, null, length);
                break;
            case ("BufferTrimPoll", [_, var pressure, ..]) when Convert.ToInt32(pressure, System.Globalization.CultureInfo.InvariantCulture) == HighPressure:
                Interlocked.Increment(ref _highPressureTrims);
                break;
            case ("BufferAllocated", [_, int length, int pool, ..]):
                long? bytes = Bytes(pool, length);
                if (bytes is null && !_origins.ContainsKey(pool))
                {
                    _origins.TryAdd(pool, "by " + Callers(5));
                }

                _onAllocated?.Invoke(pool, bytes, length);
                break;
        }
    }

    private void Drop(int pool, int? array, int length)
    {
        if (Bytes(pool, length) is not { } bytes)
        {
            string kind = _elementSizes.TryGetValue(pool, out int size) && size == Ambiguous ? "two element sizes share its ID" : "its element type is not known";
            _unknownDrops.Enqueue($"pool {pool} ({kind}) dropped an array of {length} elements; its first array was allocated {(_origins.TryGetValue(pool, out string? origin) ? origin : "before the accounting started")}");
            return;
        }

        if (array is { } id)
        {
            lock (_recentDrops)
            {
                if (!_recentDrops.Add((pool, id)))
                {
                    return;
                }

                _recentDrops.Remove(_recent[_recentNext]);
                _recent[_recentNext] = (pool, id);
                _recentNext = (_recentNext + 1) % RecentCount;
            }
        }

        Interlocked.Add(ref _dropped, bytes);
    }

    // The bytes an array of the pool takes on the heap (its header and elements, rounded to a pointer), or null for a pool not sized.
    private long? Bytes(int poolId, int length) =>
        _elementSizes.TryGetValue(poolId, out int size) && size != Ambiguous ? ((3L * IntPtr.Size) + ((long)size * length) + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size : null;

    // A failure is recorded for the guard to report, so that one assembly cannot fail the accounting (and with it every memory test).
    private void TryRegister(Assembly assembly)
    {
        try
        {
            Register(assembly);
        }
        catch (Exception ex)
        {
            _registrationFailures.Enqueue($"{assembly.GetName().Name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Register(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.OfType<Type>()];
        }

        foreach (Type type in types)
        {
            if (type.IsGenericTypeDefinition || type.ContainsGenericParameters || type.IsByRefLike || type.IsPointer || (type.IsAbstract && type.IsSealed) || type == typeof(void))
            {
                continue;
            }

            try
            {
                object pool = typeof(ArrayPool<>).MakeGenericType(type).GetProperty(nameof(ArrayPool<byte>.Shared))!.GetValue(null)!;
                int size = type.IsValueType ? (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(type).Invoke(null, null)! : IntPtr.Size;
                _elementSizes.AddOrUpdate(pool.GetHashCode(), size, (_, known) => known == size ? known : Ambiguous);
            }
            catch (Exception ex) when (ex is ArgumentException or TypeLoadException or NotSupportedException or TargetInvocationException)
            {
                // Not an array element type (a type the runtime rejects as a generic argument).
            }
        }
    }

    // The methods that called into the pool, innermost first, without file and line.
    internal static string Callers(int count)
    {
        IEnumerable<string> names = new System.Diagnostics.StackTrace(false).GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method is not null)
            .Select(method => $"{method!.DeclaringType?.FullName}.{method.Name}");
        string[] all = [.. names];
        int caller = Array.FindLastIndex(all, name => name.Contains("ArrayPool", StringComparison.Ordinal) || name.Contains("System.Diagnostics.Tracing.", StringComparison.Ordinal)
            || name.StartsWith("Encspect.Tests.PoolAccounting", StringComparison.Ordinal) || name.StartsWith("Encspect.Tests.SharedPoolGuard", StringComparison.Ordinal)) + 1;
        return string.Join(" < ", all.Skip(caller).Take(count));
    }

    // The Encspect assemblies this one references, directly or through another: a stale DLL left in the output is not among them.
    private IEnumerable<Assembly> Encspect(Assembly root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { root.GetName().Name! };
        var pending = new Queue<Assembly>([root]);
        while (pending.TryDequeue(out Assembly? assembly))
        {
            yield return assembly;
            foreach (AssemblyName name in assembly.GetReferencedAssemblies())
            {
                if (name.Name!.StartsWith("Encspect", StringComparison.Ordinal) && seen.Add(name.Name))
                {
                    try
                    {
                        pending.Enqueue(Assembly.Load(name));
                    }
                    catch (Exception ex)
                    {
                        _registrationFailures.Enqueue($"{name.Name} did not load: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
    }
}
