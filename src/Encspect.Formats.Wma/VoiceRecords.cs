using Encspect.Core;

namespace Encspect.Formats.Wma;

// A list of records in blocks, as ChunkedList is, with writable access: the walk fills in a packet's tail once the packet that finishes it is read.
// Written by the one thread that parses; read by any number of threads once the stream is closed.
internal sealed class VoiceList<T>
    where T : struct
{
    private const int BlockBits = 12;
    private const int BlockSize = 1 << BlockBits;

    private readonly List<T[]> _blocks = [];

    public int Count { get; private set; }

    public ref T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            return ref _blocks[index >> BlockBits][index & (BlockSize - 1)];
        }
    }

    public int Add(in T item)
    {
        int block = Count >> BlockBits;
        int slot = Count & (BlockSize - 1);
        if (block == _blocks.Count)
        {
            _blocks.Add(new T[block == 0 ? 16 : BlockSize]);
        }
        else if (slot == _blocks[block].Length)
        {
            T[] grown = new T[Math.Min(BlockSize, slot * 2)];
            Array.Copy(_blocks[block], grown, slot);
            _blocks[block] = grown;
        }

        _blocks[block][slot] = item;
        return Count++;
    }
}

internal enum VoicePacketKind : byte
{
    // Read from its header.
    Header,

    // Read with no header: the rest of a packet.
    Headerless,

    // The header ran past the packet.
    InvalidHeader,

    // Dropped with the rest of its media object, or passed over by a reading that went into it.
    NotRead,
}

// What the spillover after a header was.
internal enum VoiceLead : byte
{
    None,

    // It finished the cached superframe, and the bits were copied.
    Finished,

    // It finished the cached superframe, but the bits were not in the cache.
    FinishedNotCopied,

    // It had no start to finish and was skipped.
    Skipped,
}

internal enum VoiceTail : byte
{
    None,

    // The tail was cached, to be finished by a later packet.
    Cached,

    // The cache refused the tail.
    Refused,
}

internal enum VoiceOutcome : byte
{
    Read,
    Music,
    SampleCount,
    FrameType,
    Overrun,
}

// A packet as the walk read it: where its bytes are, its header and how its bits were used, enough to rebuild its nodes.
internal struct VoicePacket
{
    public int RangeStart;
    public int RangeCount;
    public int Length;
    public VoicePacketKind Kind;
    public VoiceLead Lead;
    public VoiceTail Tail;
    public byte Sequence;
    public byte ValueCount;
    public bool HasResidualLsps;

    // The sum of the header's superframe counts.
    public int Count;

    // The spillover the header states and the bits that the lead part of the packet covers: the spillover, and after a failed cached superframe
    // the bits skipped with the late resync.
    public int Spillover;

    // The bits after the header that the lead part covers: the spillover bits taken, which the walk limits by the media object.
    public int LeadBits;

    // The header's bits, and where the packet's own reading began (the lead's end, or where a headerless reading began).
    public int HeaderBits;
    public int BodyStart;

    // The superframe the lead finished (an index, or -1), the first superframe read in the packet and how many were looked for in it.
    public int Finished;
    public int FirstOwn;
    public int OwnCount;

    // How many of them were read.
    public int OwnRead;

    // The tail: where it begins in the packet, its length, and the superframe that began in it (an index, or -1 until it is read).
    public int TailStart;
    public int TailLength;
    public int TailSuperframe;

    // The superframes that began in the packet, for the value and the statistics.
    public int Begun;

    // Where reading went on after the lead, which differs from BodyStart + the lead's bits by the late resync.
    public int ResyncBits;
}

internal struct VoiceFrame
{
    public byte Type;

    // 1: the window index was extended; 2: the first block of a pitch-adaptive window frame has pulses at the pitch lag.
    public byte Flags;

    // The bits the frame took, its own LSPs and its code included.
    public ushort Bits;

    // The pitch c of a frame with a pitch per frame.
    public ushort Pitch;

    // The silence gain, or the window index.
    public ushort Parameter;
}

// A superframe as the walk read it, enough to rebuild its nodes. Bit positions inside it are relative to its first bit.
internal struct VoiceSuperframe
{
    public VoiceOutcome Outcome;
    public bool IsCached;
    public bool HasSampleCount;
    public bool HasResidualLsps;
    public bool IsLast;
    public byte Frames;
    public byte AdditionalGroups;

    public ushort Samples;
    public ushort SampleCount;

    // The bits the reading used (or needed, for one that failed), and the bits it was read from.
    public int Needed;
    public int Available;

    // Where it begins: the packet and the bit in it; for a cached one the tail's packet and bit.
    public int Packet;
    public int Start;
    public int TailBits;

    // A cached one finished by spillover: the packet, the bit in it, how many bits were copied and where in the cache.
    public int SpillPacket;
    public int SpillStart;
    public ushort SpillBits;
    public ushort SpillAt;

    // The file offset of the first byte, for the series.
    public long Offset;

    public VoiceFrame Frame0;
    public VoiceFrame Frame1;
    public VoiceFrame Frame2;

    public readonly VoiceFrame this[int index] => index switch { 0 => Frame0, 1 => Frame1, _ => Frame2 };
}

// What the walk counted, for the statistics.
internal sealed class VoiceTotals
{
    public long Packets;
    public long PacketBytes;
    public long Superframes;
    public long Invalid;
    public long Samples;
    public long SuperframeBits;
    public long SmallestBits = long.MaxValue;
    public long LargestBits;
    public long SpanningPackets;
    public long MostInPacket;
    public long WithSampleCount;
    public long WithResidualLsps;
    public long WithAdditionalData;
    public long IgnoredBits;
    public long BitsAfterLast;
    public long SkippedSpilloverBits;
    public long[] FrameTypes = new long[VoiceParameters.FrameTypes];
    public long[] FixedCodebooks = new long[4];
    public long[] AdaptiveCodebooks = new long[3];
    public long[] BlockCounts = new long[4];
}
