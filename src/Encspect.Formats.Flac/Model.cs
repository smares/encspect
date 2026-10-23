using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.Flac;

// The ids of the metadata block types (RFC 9639, section 8.1).
internal static class BlockTypes
{
    public const int StreamInfo = 0;
    public const int Padding = 1;
    public const int Application = 2;
    public const int SeekTable = 3;
    public const int VorbisComment = 4;
    public const int CueSheet = 5;
    public const int Picture = 6;
    public const int Forbidden = 127;

    public static string Name(int type) => type switch
    {
        StreamInfo => "STREAMINFO",
        Padding => "PADDING",
        Application => "APPLICATION",
        SeekTable => "SEEKTABLE",
        VorbisComment => "VORBIS_COMMENT",
        CueSheet => "CUESHEET",
        Picture => "PICTURE",
        Forbidden => "Forbidden",
        _ => "Reserved",
    };
}

internal enum EntryKind : byte
{
    Frame,
    Skipped,
}

[Flags]
internal enum EntryStatus : ushort
{
    None = 0,

    // The CRC-16 differs from the one computed over the frame.
    Crc16Fails = 1,

    // The data ends inside the frame.
    Cut = 2,

    // A subframe cannot be read; the frame's extent is where the next frame was found.
    Unreadable = 4,

    // The frame's end is where the next frame header was found, not where its subframes end.
    ExtentFound = 8,

    // Frames before this one are missing or out of order.
    FollowsGap = 16,

    // The padding bits before the CRC-16 are not all 0.
    PaddingSet = 32,

    // The frame came as one packet of Ogg pages, so its bytes may lie in several places (Records.Ranges).
    HasRanges = 64,

    // The CRC-8 of the header differs, but the rest of the frame reads and its CRC-16 holds.
    Crc8Fails = 128,
}

// A frame, or a stretch of bytes between frames that holds none, in 56 bytes. Offsets are where the frame starts in the file.
[StructLayout(LayoutKind.Auto)]
internal readonly struct Entry
{
    public EntryKind Kind { get; init; }

    public EntryStatus Status { get; init; }

    public long Offset { get; init; }

    public long Length { get; init; }

    // The frame number or the first sample number, as the header codes it.
    public long Number { get; init; }

    public int BlockSize { get; init; }

    // The sample rate the header states; 0 where it leaves it to STREAMINFO.
    public int StatedRate { get; init; }

    public byte BlockSizeCode { get; init; }

    public byte RateCode { get; init; }

    public byte ChannelCode { get; init; }

    public byte SizeCode { get; init; }

    public byte NumberLength { get; init; }

    public byte Crc8 { get; init; }

    public byte ComputedCrc8 { get; init; }

    public bool IsVariable { get; init; }

    public ushort Crc16 { get; init; }

    public ushort ComputedCrc16 { get; init; }

    // The index of the frame's FrameDetail, or -1 for a frame past the ones the report details.
    public int Detail { get; init; }

    public long End => Offset + Length;

    public int Channels => ChannelCode < 8 ? ChannelCode + 1 : 2;
}

// A subframe of a detailed frame: what its nodes are built from, with the bytes of its coefficients and partitions in the store.
[StructLayout(LayoutKind.Auto)]
internal readonly struct SubframeRecord
{
    public SubframeType Type { get; init; }

    public int StartBit { get; init; }

    public int EndBit { get; init; }

    public int ResidualStartBit { get; init; }

    public int Order { get; init; }

    public int WastedBits { get; init; }

    public int SampleBits { get; init; }

    public int Precision { get; init; }

    public int Shift { get; init; }

    public int ResidualMethod { get; init; }

    public int PartitionOrder { get; init; }

    public int EscapedPartitions { get; init; }

    public int MinParameter { get; init; }

    public int MaxParameter { get; init; }

    public SubframeFault Fault { get; init; }

    // Bytes of the store: Order coefficients of 2 bytes, then the partitions listed, each a start bit (4 bytes), a parameter and an escape flag.
    public uint StoredAt { get; init; }

    public int Partitions { get; init; }

    public long Constant { get; init; }
}

internal sealed class FrameDetail(SubframeRecord[] subframes)
{
    public SubframeRecord[] Subframes { get; } = subframes;
}

// What nodes are built from once the analysis is over, and nothing else: the state, with the reader and the findings, is not reachable
// from a node builder, so a build can neither consume nor keep it. Written only while parsing.
internal sealed class Records
{
    public ChunkedList<Entry> Entries { get; } = new();

    public List<FrameDetail> Details { get; } = [];

    public ByteStore Store { get; } = new();

    // File ranges of the frames that came as packets spanning Ogg pages, by entry.
    public Dictionary<int, ByteRange[]> Ranges { get; } = [];

    // What the frames leave to STREAMINFO.
    public int StreamRate { get; set; }

    public int StreamBits { get; set; }

    // The sample rate of the frame, which its header states or STREAMINFO does.
    public int RateOf(in Entry e) => e.RateCode switch
    {
        0 => StreamRate,
        1 => 88200,
        2 => 176400,
        3 => 192000,
        4 => 8000,
        5 => 16000,
        6 => 22050,
        7 => 24000,
        8 => 32000,
        9 => 44100,
        10 => 48000,
        11 => 96000,
        _ => e.StatedRate,
    };

    public int BitsOf(in Entry e) => e.SizeCode switch { 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32, _ => StreamBits };
}
