using Encspect.Core;

namespace Encspect.Formats.Alac;

// What the kind slot of a stored channel says.
internal enum StoredKind
{
    // The walk stored nothing for the channel: the frame has no element for it, or the walk stopped before it.
    None = 0,

    // The residuals of a compressed element are in the channel's plane, and the slots after the kind hold its parameters.
    Compressed = 1,

    // The samples of an uncompressed element are in the channel's plane, as they are.
    Uncompressed = 2,

    // The element states more samples than the frame length of the cookie, which the buffers do not hold (Apple's decoder reads past its
    // buffers there): its sample count is in the samples slot and nothing else is stored.
    TooLong = 3,
}

// How the walk of a frame stores it in the buffers the decode session lends (IDecodeSession.Lend): channel c of the cookie holds its samples from
// c * Stride on, the first FrameLength of them the plane (the residuals of a compressed element, the samples of an uncompressed one), and after them
// MetaSlots integers that describe the element the channel is in. The decoder reads the same layout; the bits of the shifted bytes stay in the packet,
// the slots say where they start.
internal static class StoredLayout
{
    public const int MaxOrder = 31;

    public const int Kind = 0;

    // The samples per channel the element states.
    public const int Samples = 1;

    public const int BytesShifted = 2;

    public const int MixBits = 3;

    // The signed mixing resolution, as the decoder takes it (ALACDecoder.cpp: int8_t).
    public const int MixResolution = 4;

    // 0 for a single channel or LFE element, 1 and 2 for the first and the second channel of a channel pair.
    public const int Role = 5;

    public const int PredictionType = 6;

    public const int PredictorShift = 7;

    public const int Order = 8;

    // The bit in the packet where the shifted bytes of the element begin, in two halves.
    public const int ShiftedAtLow = 9;

    public const int ShiftedAtHigh = 10;

    // Order values, each the signed 16 bits the element stores.
    public const int Coefficients = 11;

    public const int MetaSlots = Coefficients + MaxOrder + 2;

    public static int Capacity(in SampleBuffers lent) => lent.Stride - MetaSlots;

    public static Span<int> Plane(in SampleBuffers lent, int channel) => lent.Narrow.Slice(channel * lent.Stride, Capacity(lent));

    public static Span<int> Meta(in SampleBuffers lent, int channel) => lent.Narrow.Slice((channel * lent.Stride) + Capacity(lent), MetaSlots);
}
