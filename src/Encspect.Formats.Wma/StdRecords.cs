using Encspect.Core;

namespace Encspect.Formats.Wma;

// One run of a frame's bits: the index of the superframe holding it, the first bit in that superframe and the number of bits.
internal readonly record struct FramePart(int Superframe, int StartBit, int BitCount);

internal enum SuperframeState : byte
{
    // The header was valid and the frames of the superframe were read (none, one or several, a failed one among them).
    Read,

    // A media object shorter than the block alignment: not read.
    Short,

    // A frame count that leaves no frame to end in it, and no room for the middle of one: not read.
    InvalidHeader,

    // No frame ends in it: all of it after its first byte is the middle of a frame.
    Middle,

    // A bit offset past the superframe's end.
    BitOffsetRejected,
}

internal enum FrameFiller : byte
{
    None,
    Stuffing,
    Filler,
}

// What is kept of a media object once it is read; the nodes are built from it and the bytes in the store when they are asked for.
internal struct SuperframeRecord
{
    public long Offset;

    // Where the store holds the file ranges of a media object that is not contiguous, or uint.MaxValue.
    public uint RangesRef;

    public int RangeCount;

    // The bytes of the media object that count: the block alignment, or fewer for a short object.
    public int Length;

    // Where the store holds the bytes, or uint.MaxValue for an object that was not kept.
    public uint DataRef;

    public SuperframeState State;

    public int Index;

    public int Count;

    // The bit offset the header states, or -1 where it was not read.
    public int BitOffset;

    public int FirstFrame;

    public int Frames;

    // Bit where the last frame read in the superframe ends, or -1 where none was read; without the bit reservoir the padding follows.
    public int EndBit;

    // Bytes of the reservoir the next superframe starts from.
    public int Reservoir;

    // What the series shows for the superframe.
    public int Kept;
}

internal struct FrameRecord
{
    // The superframe the frame was read in, which is where it ends.
    public int Superframe;

    // The superframe where its first bit lies, and that bit; both are those of Superframe for a frame read in one superframe.
    public int StartSuperframe;

    public int StartBit;

    public int Bits;

    public int State;

    public int Blocks;

    public FrameFault Fault;

    public FrameFiller Filler;

    public int FillerBits;

    // The bit of the frame's buffer it starts at: the bit in the superframe, or for a spanning frame the start bit in its first byte.
    public readonly int BufferStart => IsSpanning ? StartBit & 7 : StartBit;

    public readonly bool IsSpanning => StartSuperframe != Superframe;

    public readonly bool IsValid => Fault == FrameFault.None;
}
