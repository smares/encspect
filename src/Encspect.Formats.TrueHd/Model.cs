using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.TrueHd;

internal enum EntryKind : byte
{
    Unit,
    Skipped,
}

[Flags]
internal enum UnitStatus : ushort
{
    None = 0,

    // The unit carries a major sync.
    HasSync = 1,

    // The major sync's CRC differs from the one computed.
    SyncCrcFails = 2,

    // A field of the major sync is out of range.
    SyncInvalid = 4,

    // The unit header's and directory's parity does not come out right.
    ParityFails = 8,

    // The directory breaks a rule.
    DirectoryInvalid = 16,

    // The data ends inside the unit.
    Cut = 32,

    // A substream the analyzer reads could not be read.
    SubstreamFault = 64,

    // A parity byte or CRC-8 of a substream differs.
    CheckFails = 128,

    // A restart header's CRC-8 differs.
    RestartCrcFails = 256,

    // The unit comes before the first major sync, or after damage that left the stream waiting for the next one.
    Unsynchronized = 512,

    // Units before this one are missing.
    FollowsGap = 1024,

    // An end of stream marker ends a substream of the unit.
    HasEndOfStream = 2048,

    // The unit holds fewer samples than its sampling rate says.
    Short = 4096,

    // The unit uses what the analyzer does not read (a negative output shift, MLP channel groups at different rates), which is not damage.
    Unsupported = 8192,
}

// An access unit, or a stretch of bytes between units that holds none, in 16 bytes. Header holds the unit's first four bytes, or the length of a
// stretch.
[StructLayout(LayoutKind.Auto)]
internal readonly struct Entry
{
    public EntryKind Kind { get; init; }

    public UnitStatus Status { get; init; }

    public long Offset { get; init; }

    public uint Header { get; init; }

    // The size of the major sync in bytes, 0 for a unit without one.
    public byte SyncLength { get; init; }

    public int Length => Kind == EntryKind.Skipped ? (int)Header : ((int)(Header >> 16 & 0xFFF) * 2);
}

// What the nodes of a unit with detail are built from: its bytes (all of them, or the parts the nodes need), the substream states before it,
// and the verdicts of its checks.
internal sealed class UnitDetail
{
    // Where the unit's bytes are in the store, and how many are kept.
    public uint Store { get; init; }

    public int Kept { get; init; }

    // The unit's bytes are all kept, so blocks can be read again; otherwise the header, the major sync, the directory and the start and end of each substream.
    public bool IsFull { get; init; }

    // The substream count and the stream type the unit was read with.
    public int KnownCount { get; init; }

    public bool KnownIsMlp { get; init; }

    // The major sync that was in force and the states before the unit, for the substreams the analyzer reads (full detail only).
    public SubstreamState[]? States { get; init; }

    // For each substream: whether its parity byte and CRC-8 hold, and whether its restart header's CRC-8 holds (bit 0 parity, bit 1 CRC-8, bit 2
    // restart header, bit 3 checks present).
    public byte[] Verdicts { get; init; } = [];

    // Whether the unit's substreams could be read.
    public bool IsReadable { get; init; }

    // The major sync in force when the unit was read: its own, or the last usable one.
    public MajorSync? InForce { get; init; }
}

// What nodes are built from once the analysis is over, and nothing else: written only while parsing, read by builders on several threads.
internal sealed class Records
{
    public ChunkedList<Entry> Entries { get; } = new();

    public ByteStore Store { get; } = new();

    // The units with detail by index: the first units, up to a budget of stored bytes, then the units that carry a major sync, up to another.
    public Dictionary<int, UnitDetail> Details { get; } = [];

    // The lengths of the units the data cuts short.
    public Dictionary<int, int> CutLengths { get; } = [];

    public int StreamRate { get; set; }

    public bool IsMlp { get; set; }

    // The unit being handed to the decoder, by the index of its record, or -1 where it has none (past the limit of records): the decoder points its
    // findings at the nodes of that unit.
    public int DecodingIndex { get; set; } = -1;

    // The node that stands for what has no node of its own, the units past the limit of records: findings about them point to it. Set when the
    // analysis ends.
    public Node? Fallback { get; set; }

    public UnitDetail? DetailOf(int index) => Details.GetValueOrDefault(index);
}
