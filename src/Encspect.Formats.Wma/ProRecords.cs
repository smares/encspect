namespace Encspect.Formats.Wma;

// What stopped the reading of a Windows Media Audio Professional frame.
internal enum ProStatus : byte
{
    Ok,

    // The subframes cannot be laid out: a subframe length outside the stream's, a channel with more than 32
    // subframes or more samples than a frame (wmaprodec.c: decode_tilehdr).
    TileHeader,

    // A channel transform that FFmpeg's decoder does not support (decode_channel_transform).
    ChannelTransform,

    // The reserved bit of a subframe is set ("Reserved bit").
    ReservedBit,

    // An extended header that skips more bits than the frame has ("invalid number of fill bits").
    FillBits,

    // More vector-coded coefficients than the subframe has coefficients ("num_vec_coeffs is too large").
    VectorCount,

    // A scale factor run past the last band ("invalid scale factor coding").
    ScaleFactors,

    // A channel with a subframe more than its tile header lays out ("broken subframe").
    BrokenSubframe,

    // The frame's length prefix is not where its data ends ("would have to skip n bits").
    LengthPrefix,

    // The frame needs more bits than it has.
    Overrun,
}

// A frame: where its fields lie. Bit positions count from the start of the frame's buffer, the
// concatenation of its parts, whose first bits before the frame (up to 7) belong to the one before.
internal struct ProFrameRecord
{
    public int FirstPart;
    public int StartBit;
    public int LengthEnd;
    public int TileEnd;
    public int PostEnd;
    public int HeaderEnd;
    public int SubframesEnd;
    public int EndBit;
    public int Length;
    public int FirstSubframe;
    public ushort SubframeCount;
    public ushort TrimStart;
    public ushort TrimEnd;
    public byte PartCount;
    public byte Status;
    public byte Flags;
    public byte DrcGain;

    // A postprocessing transform follows the tile header; its matrix is sent.
    public const byte HasPostprocess = 1;
    public const byte PostprocessMatrix = 2;
    public const byte HasDrc = 4;
    public const byte HasTrimInfo = 8;

    // The trailer bit says another frame follows in the packet.
    public const byte More = 16;

    public readonly bool Has(byte flag) => (Flags & flag) != 0;
}

// A subframe: the coefficients of 128 to 8,192 samples of the channels that share it.
internal struct ProSubframeRecord
{
    public int StartBit;
    public int FillEnd;
    public int TransformEnd;
    public int FlagsEnd;
    public int VectorEnd;
    public int StepEnd;
    public int EndBit;
    public int FirstChannel;
    public int FirstGroup;
    public int FillBits;
    public short QuantStep;
    public ushort Offset;
    public ushort Length;
    public byte ChannelMask;
    public byte ChannelCount;
    public byte GroupCount;
    public byte Flags;

    // The subframe sends an extended header.
    public const byte HasExtendedHeader = 1;

    // At least one channel sends coefficients.
    public const byte HasCoefficients = 2;

    // The subframe states how many coefficients are vector coded.
    public const byte SendsVectorCounts = 4;

    public readonly bool Has(byte flag) => (Flags & flag) != 0;
}

// One channel's part of a subframe.
internal struct ProChannelRecord
{
    public int ScaleStart;
    public int ScaleEnd;
    public int CoefficientStart;
    public int CoefficientEnd;
    public short QuantStep;
    public ushort VectorCoefficients;
    public ushort NonZero;
    public ushort Escapes;
    public byte Channel;
    public byte ScaleFactorStep;
    public byte Flags;

    // The channel sends coefficients.
    public const byte Transmits = 1;

    // The coefficients use the second run-level codebook.
    public const byte SecondCodebook = 2;

    // The scale factors are sent for this subframe (the first of a channel always, then one flag).
    public const byte ScalesSent = 4;

    // The scale factors are sent as differences from the earlier ones (run-level), not as a first set.
    public const byte ScalesDifferential = 8;

    // The coefficient codes ran into bits that are no code or past the subframe, which FFmpeg's decoder ignores.
    public const byte CoefficientError = 16;

    public readonly bool Has(byte flag) => (Flags & flag) != 0;
}

// A group of channels of a subframe that share a transform.
internal struct ProGroupRecord
{
    public int Start;
    public int End;
    public uint BandMask;
    public byte ChannelMask;
    public byte ChannelCount;
    public byte Transform;
    public byte BandsOn;
}
