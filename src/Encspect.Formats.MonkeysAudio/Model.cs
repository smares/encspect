using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio;

[Flags]
internal enum EntryStatus : byte
{
    None = 0,

    // The data ends inside the frame, or at the last whole word of it.
    Cut = 1,

    // The seek table puts the frame outside the data or before the one that precedes it, so its bytes cannot be told.
    Misplaced = 2,

    // The CRC word has its bit 31 set and a word of special frame codes follows (APEDecompressCore.cpp, StartFrame).
    HasSpecialCodes = 4,

    // The frame is too short to hold the fields the format starts it with.
    NoStartFields = 8,

    // The frame has no stored CRC: the version predates it.
    NoCrc = 16,

    // The frame is past the size the analyzer reads into memory to decode.
    TooLarge = 32,
}

// A frame in 32 bytes. Start and End count bytes of the stream the frames make, from the first frame's first byte (see WordOrder).
[StructLayout(LayoutKind.Auto)]
internal readonly struct Entry
{
    public long Start { get; init; }

    public long End { get; init; }

    public uint Blocks { get; init; }

    // The stored CRC, 31 bits, and the special frame codes (APE_SPECIAL_FRAME_*).
    public uint Crc { get; init; }

    public uint Special { get; init; }

    public EntryStatus Status { get; init; }

    public long Length => End - Start;
}

// What nodes are built from once the analysis is over, and nothing else: the state, with the reader and the findings, is not reachable from a node
// builder. Written only while parsing.
internal sealed class Records
{
    public ChunkedList<Entry> Entries { get; } = new();

    // Where the seek table starts in the file and the file offset of the first frame's first byte.
    public long SeekTableAt { get; set; }

    public long DataStart { get; set; }

    // The offset the seek table's offsets count from: the junk before the descriptor.
    public long Junk { get; set; }

    // The file's end for the nodes: frames are clipped to whole words that the data holds.
    public long DataLimit { get; set; }

    public int SeekEntries { get; set; }

    public bool HasFrameFields { get; set; }

    // Whether the frames' coded data is range-coded (3.91 and later) and not Rice-coded.
    public bool HasRangeCoder { get; set; }

    public bool HasSpecialFrames { get; set; }

    // Frames not kept as entries, past the most the report lists.
    public long UnlistedFrames { get; set; }

    public long UnlistedStart { get; set; }

    public long UnlistedEnd { get; set; }
}
