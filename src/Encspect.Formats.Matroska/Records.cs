using Encspect.Core;

namespace Encspect.Formats.Matroska;

internal enum ItemKind : byte
{
    // Any other element of a Cluster, read as a leaf: Id, Offset, HeaderLength and DataLength, and for a number its Value.
    Element,

    // Index is the block record.
    SimpleBlock,

    // Index is the first child record and Value how many follow.
    BlockGroup,

    // A CRC-32 element: Value is the stored value; the value computed over the Cluster's data after it is in Records.ComputedCrcs.
    Crc,

    // Bytes the Cluster's element cannot be read further from.
    Unparsed,
}

// One element of a Cluster, kept in a record because a long file holds millions of them: the nodes are built from the
// records when they are read (see ClusterNodes).
// Value holds a BlockGroup's child count: a separate Count field would make an Item 48 bytes, not 40.
internal struct Item
{
    public long Offset;
    public long Value;
    public long DataLength;

    // The element's ID as it is in the file, of up to eight octets (EBMLMaxIDLength).
    public ulong Id;
    public int Index;
    public byte HeaderLength;
    public ItemKind Kind;
}

// A descendant of a BlockGroup, in the order of the file; Depth is below the BlockGroup. Block is the block record of a Block, -1 for another element, or
// UnparsedBlock for bytes that start no element.
internal struct Child
{
    public const int UnparsedBlock = -2;

    public long Offset;
    public long Value;
    public long DataLength;
    public ulong Id;
    public int Block;
    public byte HeaderLength;
    public byte Depth;
}

[Flags]
internal enum BlockStatus : byte
{
    None = 0,

    // The block ends before its track number, timestamp and flags do, or before its lacing header does.
    Short = 1,

    // The lace sizes do not add up to the block, or are not valid for the lacing.
    LaceInvalid = 2,

    // The block states a lacing and holds one frame, which RFC 9559 forbids (section 10.3).
    LaceSingle = 4,

    // The data is larger than a block record holds, so no frame is read.
    TooLarge = 8,

    // The file's laced blocks hold more frames than the analysis keeps, so no frame of this one is read.
    FramesLimit = 16,
}

internal enum Lacing : byte
{
    None = 0,
    Xiph = 1,
    FixedSize = 2,
    Ebml = 3,
}

// A SimpleBlock or Block (RFC 9559 sections 10.1 and 10.2), as far as its header goes: the frames are read from the file again
// when a codec parser asks for them. Offset is the start of the element.
internal struct BlockRecord
{
    public long Offset;
    public uint DataLength;

    // From Offset to the first frame.
    public int FramesDelta;

    // The first of the Frames sizes in the lace sizes, when there are several frames.
    public int Lace;
    public uint Track;
    public short Timestamp;
    public ushort Frames;
    public byte HeaderLength;
    public byte TrackLength;
    public byte Flags;
    public BlockStatus Status;
    public bool IsInGroup;

    public readonly Lacing Lacing => (Lacing)((Flags >> 1) & 3);

    // SimpleBlock only (RFC 9559 section 10.2).
    public readonly bool IsKeyframe => (Flags & 0x80) != 0;

    public readonly bool IsInvisible => (Flags & 0x08) != 0;

    public readonly long FramesOffset => Offset + FramesDelta;

    public readonly long End => Offset + HeaderLength + DataLength;
}

internal struct ClusterRecord
{
    public long Offset;
    public long DataLength;
    public long Timestamp;
    public int FirstItem;
    public int ItemCount;
    public int Blocks;
    public byte HeaderLength;
    public bool IsSizeUnknown;
    public bool HasTimestamp;
}

// What the nodes of a Segment's Clusters are built from, held apart from the parsing state so that a report does not keep the input
// alive.
internal sealed class Records
{
    public ChunkedList<ClusterRecord> Clusters { get; } = new();

    public ChunkedList<Item> Items { get; } = new();

    public ChunkedList<Child> Children { get; } = new();

    public ChunkedList<BlockRecord> Blocks { get; } = new();

    // The sizes of all the frames of each block with Xiph or EBML lacing, the last included. Fixed-size lacing stores none.
    public ChunkedList<int> LaceSizes { get; } = new();

    // The Segment's top-level children in file order: a Cluster by its record (Index), another element by its node.
    public ChunkedList<TopLevel> Entries { get; } = new();

    // The size of frame i of a block whose frames were read.
    public long FrameSize(in BlockRecord block, int i) => block.Frames <= 1 ? block.End - block.FramesOffset
        : block.Lacing == Lacing.FixedSize ? (block.End - block.FramesOffset) / block.Frames
        : LaceSizes[block.Lace + i];

    public List<Node> Nodes { get; } = [];

    // The CRC-32 each Cluster's CRC-32 item computes, by item.
    public Dictionary<int, uint> ComputedCrcs { get; } = [];
}

internal struct TopLevel
{
    public int Cluster;
    public int Node;
}
