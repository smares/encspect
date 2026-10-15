using Encspect.Core;

namespace Encspect.Formats.Wma;

// What stopped the reading of a Windows Media Audio Voice superframe (wmavoice.c: synth_superframe).
internal enum VoiceStatus : byte
{
    Ok,

    // The first bit is clear: the superframe holds music, Windows Media Audio Professional data that FFmpeg's decoder does not
    // support ("WMAPro-in-WMAVoice").
    Music,

    // The superframe states more samples than the 480 it holds.
    SampleCount,

    // A frame's type code is one that the frame type table gives no type.
    FrameType,

    // The superframe needs more bits than the packets give it.
    Overrun,
}

// A frame of 160 samples: where its bits lie, counted from the start of the superframe's own bits. 20 bytes.
internal struct VoiceFrameRecord
{
    public int StartBit;
    public int EndBit;

    // The frame's pitch in samples: the pitch of its last sample for a pitch per frame, of its last block for a pitch per
    // block, and 0 for a frame with no adaptive codebook.
    public ushort Pitch;

    // The silence gain index (8 bits) of a silence frame, or the index of the pitch-adaptive window's first pulse.
    public ushort Parameter;
    public byte Type;
    public byte TypeBits;
    public byte Flags;

    // The pitch-adaptive window's index was read in 8 bits, not 6.
    public const byte WindowExtended = 1;

    // The first block of a pitch-adaptive window frame has pulses at its pitch lag, which sets the width of its second pulse field.
    public const byte WindowPulsesInFirstBlock = 2;

    public readonly bool Has(byte flag) => (Flags & flag) != 0;
}

// A superframe: three frames, the unit that is decoded to 480 samples or fewer. It lies in the bits of one packet, or its start is
// the end of one packet and the rest the start of the next. 48 bytes.
internal struct VoiceSuperframeRecord
{
    public int FirstPart;

    // The bits the superframe is read from (a packet's tail and the spillover bits that follow the next packet's header), and
    // how many the reading used.
    public int AvailableBits;
    public int Bits;
    public int FirstFrame;

    // The bits of the header: the speech bit, the sample count and the LSPs of a residual superframe.
    public int HeaderBits;

    // Where the frames end, and so where the optional additional data begins.
    public int FramesEnd;
    public int Packet;
    public ushort Samples;

    // The bits after the additional data's flag.
    public ushort ExtraBits;
    public byte PartCount;
    public byte FrameCount;
    public byte Status;
    public byte Flags;

    // The superframe states how many samples it holds.
    public const byte HasSampleCount = 1;

    // The LSPs of all three frames are sent once, at the start (the packet header's flag).
    public const byte ResidualLsps = 2;

    // The additional data after the frames is present.
    public const byte HasAdditionalData = 4;

    // It began in the packet before.
    public const byte StartsEarlier = 8;

    // It is the one that began in the last packet and is read at the end of the stream, with no bits after the packet's.
    public const byte AtEnd = 16;

    public readonly bool Has(byte flag) => (Flags & flag) != 0;
}

// A packet: one block of the media object, the unit the superframes are carried in. 56 bytes.
internal struct VoicePacketRecord
{
    public long Offset;
    public int Length;
    public int RangeIndex;

    // The superframes read while this packet was the current one: the one begun in the packet before (if there is one) and then
    // those that lie in this packet.
    public int FirstSuperframe;
    public int SuperframeCount;
    public int HeaderEnd;

    // The spillover bits as the header states them, and as many as the packet holds.
    public int StatedSpillover;
    public int Spillover;

    // The count of superframes the header states, which includes the one that goes on in the next packet.
    public int StatedSuperframes;

    // Where the bits that begin a superframe the packet does not finish start, and how many there are.
    public int TailStart;
    public int TailBits;
    public byte Sequence;
    public byte Flags;

    // The block is shorter than the block alignment: not read.
    public const byte Short = 1;

    // The header runs past the packet.
    public const byte BadHeader = 2;

    // The packet's header sets the residual LSPs flag.
    public const byte ResidualLsps = 4;

    // The first superframe of the packet is the one that began in the packet before.
    public const byte CompletesPrevious = 8;

    // A superframe failed, so the rest of the packet is not read.
    public const byte Failed = 16;

    // The packet states spillover bits and no superframe begins before them.
    public const byte SpilloverDropped = 32;

    // Its sequence number does not follow the one before.
    public const byte SequenceBreak = 64;

    public readonly bool Has(byte flag) => (Flags & flag) != 0;
}

// Figures counted while the packets are read.
internal sealed class VoiceTotals
{
    public long Packets { get; set; }

    public long ShortPackets { get; set; }

    public long BadHeaders { get; set; }

    public long SequenceBreaks { get; set; }

    public long UsedBytes { get; set; }

    // Superframes read to their end, and those that failed.
    public long Superframes { get; set; }

    public long FailedSuperframes { get; set; }

    public long Frames { get; set; }

    public long[] FramesByType { get; } = new long[VoiceParameters.FrameTypes];

    public long Samples { get; set; }

    public long SuperframeBits { get; set; }

    public long MinSuperframeBits { get; set; } = long.MaxValue;

    public long MaxSuperframeBits { get; set; }

    public long SuperframesWithSampleCount { get; set; }

    public long ResidualSuperframes { get; set; }

    public long AdditionalDataSuperframes { get; set; }

    public long AdditionalDataBits { get; set; }

    // Superframes that begin in one packet and end in the next.
    public long StraddlingSuperframes { get; set; }

    // Bits of a superframe's span that its reading did not use.
    public long IgnoredBits { get; set; }

    public long SkippedSpilloverBits { get; set; }

    public long TailBitsAtEnd { get; set; }

    public long UnfinishedAtEnd { get; set; }

    public int MaxSuperframesInPacket { get; set; }

    public long MusicSuperframes { get; set; }

    public long OverlongCaches { get; set; }
}

// The records a stream's nodes are built from, and nothing else: the stream's parameters and the lists the walk fills. The
// stream that wrote them also keeps the reading state, the bits of the superframe in progress and the findings, which a node
// builder must never reach: it runs again after a collection and on several threads, and what it captures stays alive as long
// as the report. Written only while the stream is walked.
internal sealed class VoiceRecords(VoiceParameters parameters)
{
    public VoiceParameters Parameters => parameters;

    public ChunkedList<VoicePacketRecord> Packets { get; } = new();

    public ChunkedList<VoiceSuperframeRecord> Superframes { get; } = new();

    public ChunkedList<VoiceFrameRecord> Frames { get; } = new();

    public ChunkedList<FramePart> Parts { get; } = new();

    public Dictionary<int, ByteRange[]> Ranges { get; } = [];

    // The superframe each packet's tail begins, by packet: the one that is read when the tail is finished.
    public Dictionary<int, int> TailOwners { get; } = [];
}
