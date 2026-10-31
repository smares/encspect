// Follows Apple's ALAC reference implementation (macosforge/alac, ALACAudioTypes.h and ALACDecoder.cpp), Apache License 2.0:
// Copyright (c) 2011 Apple Inc. See THIRD-PARTY-NOTICES.md.
using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.Alac;

// The element tags of a frame (ALACAudioTypes.h and ALACDecoder.cpp: ID_SCE to ID_END): the 3 bits that begin every element.
internal static class ElementTags
{
    public const int SingleChannel = 0;
    public const int ChannelPair = 1;
    public const int Coupling = 2;
    public const int Lfe = 3;
    public const int DataStream = 4;
    public const int ProgramConfig = 5;
    public const int Fill = 6;
    public const int End = 7;

    public static string Name(int tag) => tag switch
    {
        SingleChannel => "Single Channel Element",
        ChannelPair => "Channel Pair Element",
        Coupling => "Coupling Channel Element",
        Lfe => "LFE Element",
        DataStream => "Data Stream Element",
        ProgramConfig => "Program Config Element",
        Fill => "Fill Element",
        _ => "End",
    };

    // The elements that carry audio, which the channels of the cookie are counted against.
    public static bool IsAudio(int tag) => tag is SingleChannel or ChannelPair or Lfe;

    public static int Channels(int tag) => tag == ChannelPair ? 2 : tag is SingleChannel or Lfe ? 1 : 0;
}

[Flags]
internal enum ElementFlags : byte
{
    None = 0,

    // The element states its own sample count (the "partial frame" flag).
    Partial = 1,

    // The element holds its samples as they are (the "escape" flag).
    Uncompressed = 2,

    // The data stream element's byte-align flag.
    ByteAligned = 4,
}

// What the walk of one element found, in the bit positions of the frame; the nodes of the element follow from it because the syntax fixes
// where every field lies (ALACDecoder.cpp, Decode). Tag, instance, flags and shift come from the element header; Samples is the sample count of an
// audio element, and the number of data bytes of a data stream or fill element.
[StructLayout(LayoutKind.Auto)]
internal struct ElementRecord
{
    public byte Tag;
    public byte Instance;
    public ElementFlags Flags;
    public byte BytesShifted;
    public byte MixBits;
    public sbyte MixResolution;

    // Whether the 12 bits after the instance tag, which are reserved, were all zero.
    public bool AreReservedBitsZero;

    // Whether the walk read the element's header through its sample count, so the element counts for the frame's samples and channels.
    public bool IsHeaderRead;

    // The channels of a compressed element whose residuals the walk read to their end.
    public byte ChannelsDone;

    public long Start;
    public long End;
    public uint Samples;

    // The channels of the element, in the order the bitstream has them: one for a single channel or LFE element, two for a pair.
    public ChannelRecord First;
    public ChannelRecord Second;

    public readonly bool IsPartial => (Flags & ElementFlags.Partial) != 0;

    public readonly bool IsUncompressed => (Flags & ElementFlags.Uncompressed) != 0;
}

// The prediction parameters and the residual data of one channel of a compressed element.
[StructLayout(LayoutKind.Auto)]
internal struct ChannelRecord
{
    public byte PredictionType;
    public byte PredictorShift;
    public byte WeightFactor;
    public byte Order;

    // The coefficients in the ByteStore: Order values of 2 bytes, big-endian.
    public uint CoefficientsAt;

    // The bit where the channel's residuals end; they begin at the end of the shifted bits or of the other channel's residuals.
    public long ResidualEnd;

    // The runs of zeros the residuals code, and the values coded with the long prefix (ag_dec.c, dyn_get_32bit: nine ones, then the raw value).
    public uint ZeroRuns;
    public uint LongCodes;
}

[Flags]
internal enum FrameFlags : ushort
{
    None = 0,

    // An element states its own sample count.
    Partial = 1,

    // An element holds its samples uncompressed.
    Uncompressed = 2,

    // The walk could not read the frame to its end: the packet ends, or the syntax breaks.
    Unreadable = 4,

    // The walk read the frame and its end tag.
    HasEnd = 8,

    // The frame was not walked because the cookie does not allow it.
    NotWalked = 16,

    // The frame came as a packet whose bytes lie in several places (Records.Ranges).
    HasRanges = 32,

    // Bytes follow the end of the frame as the walk found it.
    HasTrailingData = 64,

    // The walk ran out of packet.
    IsTruncated = 128,
}

// A frame, one packet of the container, in 40 bytes. Offset is where the packet starts in the file.
[StructLayout(LayoutKind.Auto)]
internal readonly struct FrameRecord
{
    public long Offset { get; init; }

    public int Length { get; init; }

    // The samples of each channel the frame codes, from its first audio element; 0 where the walk read no audio element.
    public uint Samples { get; init; }

    public FrameFlags Flags { get; init; }

    // The elements the walk read, END included.
    public int Elements { get; init; }

    // The bytes the walk used, as far as the end tag and its padding; the rest of the packet is trailing data.
    public int Walked { get; init; }

    // The index of the frame's detail, or -1 for a frame past the ones the report details.
    public int Detail { get; init; }

    public bool IsWalked => (Flags & FrameFlags.NotWalked) == 0;
}

// The elements of a detailed frame. A frame keeps at most Records.MaxDetailedElements of them; the bits from RestStart to RestEnd hold the others.
internal sealed class FrameDetail(ElementRecord[] elements, int total, long restStart, long restEnd)
{
    public ElementRecord[] Elements { get; } = elements;

    // All the elements the walk read, END included.
    public int Total { get; } = total;

    public long RestStart { get; } = restStart;

    public long RestEnd { get; } = restEnd;
}

// What nodes are built from once the analysis is over, and nothing else: the state, with the packets and the findings, is not reachable from a
// node builder, so a build can neither consume nor keep the input. Written only while parsing.
internal sealed class Records
{
    // The most frames the records keep: a crafted container can hold millions of tiny packets. The frames past it are walked and counted, but have no
    // record, no node and no place in the graphs.
    public const int MaxEntries = 1 << 21;

    // The frames whose elements the records keep, as FLAC keeps the subframes of its first 4,096 frames; past them a frame is its record alone.
    public const int MaxDetailedFrames = 4096;

    public const int MaxDetailedElements = 64;

    // Of those, the fill and data stream elements a frame lists; the frame's other elements after the first of the rest are not listed either.
    public const int MaxDetailedFillElements = 8;

    public ChunkedList<FrameRecord> Frames { get; } = new();

    public List<FrameDetail> Details { get; } = [];

    public ByteStore Store { get; } = new();

    // File ranges of the packets whose bytes are not contiguous.
    public Dictionary<int, IReadOnlyList<ByteRange>> Ranges { get; } = [];

    public AlacCookie? Cookie { get; set; }
}

// The background articles the ALAC nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string Alac = "alac";
    public const string AdaptiveGolomb = "alac-adaptive-golomb";
    public const string Prediction = "alac-prediction";
    public const string MidSide = "mid-side-stereo";
    public const string ChannelLayouts = "channel-layouts";
    public const string WastedBits = "wasted-bits";
    public const string Verification = "verification";
    public const string BitrateModes = "bitrate-modes";
    public const string Mp4SampleTables = "mp4-sample-tables";
}
