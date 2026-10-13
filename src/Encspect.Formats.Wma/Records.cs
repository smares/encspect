using Encspect.Core;

namespace Encspect.Formats.Wma;

// A superframe: one media object of the container, the unit the container's packets carry. 48 bytes.
internal struct SuperframeRecord
{
    public long Offset;

    // The bytes the decoder uses (the block alignment), and the media object's own length.
    public int Length;
    public int ObjectLength;

    // The index in the stream's side table of file ranges, or -1 for a media object in one range.
    public int RangeIndex;
    public int FirstFrame;
    public int ReservoirIn;
    public int ReservoirOut;
    public int FrameEnd;
    public ushort FrameCount;
    public ushort BitOffset;
    public byte Index;
    public byte RawFrames;
    public byte Flags;

    // The superframe starts with a frame that began in the one before.
    public const byte Straddles = 1;

    // Nothing but the middle of a frame (the frame count is 0).
    public const byte Continues = 2;

    // The superframe is not read: too short, or its header is invalid.
    public const byte Skipped = 4;

    // The superframe has a header (the bit reservoir is in use).
    public const byte HasHeader = 8;

    // A frame in it failed.
    public const byte Failed = 16;

    public readonly bool Has(byte flag) => (Flags & flag) != 0;
}

// A frame: its blocks and where its bits lie. Bit positions count from the start of the frame's
// buffer, the concatenation of its parts. 24 bytes.
internal struct FrameRecord
{
    public int Superframe;
    public int FirstPart;
    public int FirstBlock;
    public int StartBit;
    public int EndBit;
    public ushort BlockCount;
    public byte PartCount;
    public byte Status;
}

// Bits of one superframe that a frame is made of.
internal readonly record struct FramePart(int Superframe, int StartBit, int BitCount);
