namespace Encspect.Formats.Wma;

// What each node of a Windows Media Audio Professional stream is. Constants, shared by every node of their kind; nothing is built per frame.
// Microsoft has published no specification of the coding, so the texts say what the project's description of the format establishes
// (docs/design/wma-pro-format.md) and leave out what it marks as not determined.
internal static class ProExplain
{
    public const string Stream = "The stream as the packets of its container carry it: the frames Encspect reads out of them, the figures over them and the layout of each frame down to its subframes.";
    public const string EncryptedStream = "The stream as the container and the codec-specific data describe it: its packets are encrypted and their frames are not read.";
    public const string NotAnalyzedStream = "The stream as the container and the codec-specific data describe it: the data states a configuration the reader does not take, so the packets and frames are not read.";

    // Codec-specific data.
    public const string SampleSize = "The sample size of the audio the stream was encoded from, in bits (bytes 0 and 1 of the codec-specific data). It sets the start value of the quantization step.";
    public const string ChannelMask = "Which speakers the channels feed, one bit per speaker in the order of WAVEFORMATEXTENSIBLE (bytes 2 to 5 of the codec-specific data). Where it is not 0, the number of set bits is the channel count.";
    public const string Unknown = "Bytes of the codec-specific data whose meaning is not known; they read 0 in most files.";
    public const string DecodeFlags = "The decode flags (bytes 14 and 15 of the codec-specific data): the frame length adjustment (bits 1 and 2), the number of subframes (bits 3 to 5), the length prefix of frames (bit 6) and dynamic range compression (bit 7).";
    public const string ExtraData = "Bytes after the 18 the format defines, which the reader does not use.";
    public const string UnreadFlag = "A bit of the decode flags whose meaning is not known; it reads 0 in some files and 1 in others.";
    public const string FrameLengthAdjustment = "Raises or lowers the frame length from the one the sampling rate gives: 1 doubles it, 2 halves it, 3 quarters it.";
    public const string SubframeCountCode = "The base-2 logarithm of the most subframes a channel is divided into in one frame.";
    public const string HasLengthPrefix = "Set if every frame starts with its length in bits, which lets the frames of a packet be told apart without reading them.";
    public const string UsesDynamicRange = "Set if every frame carries an 8-bit gain for dynamic range compression.";
    public const string OtherFlags = "Bits of the decode flags whose meaning is not known.";

    // Packets.
    public const string Packets = "The packets of the stream, one for each media object of the container: its first block alignment's bytes, with a header, the end of the frame that began in an earlier packet, frames, and the start of a frame that goes on.";
    public const string Packet = "A packet: a header with a sequence number and the count of bits that finish the frame before, then the bits of frames, one after another.";
    public const string SequenceNumber = "Counts the packets modulo 16, so that a decoder can tell that a packet is missing.";
    public const string UnusedBits = "Two bits of the packet header whose meaning is not known; Encspect does not read them.";
    public const string BitsOfPreviousFrame = "How many bits right after the header finish the frame that began in an earlier packet. Where it covers the whole payload, the packet only continues that frame.";
    public const string FrameEnd = "The bits that finish the frame which began in the packet before.";
    public const string FrameData = "A frame that lies wholly in this packet.";
    public const string FrameStart = "The start of a frame that goes on in the next packet.";
    public const string Padding = "Bits after the last frame of the packet, which belong to no frame.";
    public const string UnfinishedFrame = "Bits after a frame that says another follows, with no frame read from them: the start of a frame that no packet finished.";

    // Frames.
    public const string Frames = "Every frame read from the packets, in order, with its layout. A frame holds the same number of samples of every channel.";
    public const string Frame = "A frame: a block of samples of every channel, coded as one string of bits that may start anywhere in a packet and may end in the next.";
    public const string FrameLength = "The length of the frame in bits, from this field to the trailer bit.";
    public const string TileHeader = "Divides each channel's samples of the frame into subframes, and says which channels share a subframe.";
    public const string ChannelSubframes = "The lengths in samples of the subframes that hold this channel, in order.";
    public const string PostprocessingTransform = "A bit that says whether a postprocessing transform of the channels follows. Encspect reads and does not use it.";
    public const string DynamicRangeGain = "The gain for dynamic range compression the frame states, read and not used by Encspect.";
    public const string Trim = "How many samples a decoder cuts from the start and the end of the frame's output.";
    public const string FramePadding = "A bit between the last subframe and the trailer bit, which is 1 in the files seen.";
    public const string MoreFrames = "Whether another frame starts after this one in the same packet.";

    // Subframes.
    public const string Subframe = "A stretch of the frame's samples in one or more channels: the channels whose next subframes start at the same sample and have the same length are coded together.";
    public const string ExtendedHeader = "A flag, and where it is set a count of fill bits that follow and are skipped. What they carry is not known.";
    public const string ReservedBit = "A bit that reads 0 in every file seen; a set bit makes the frame invalid.";
    public const string ChannelTransform = "How the channels of the subframe are grouped for a channel transform such as mid/side coding, and where in the spectrum each group's transform applies.";
    public const string ChannelGroup = "A group of channels that share a transform, and the scale factor bands the transform applies in.";
    public const string CoefficientsSent = "Whether each channel of the subframe sends spectral coefficients; a channel that does not is silent in the subframe.";
    public const string VectorCoefficients = "How many of each channel's coefficients are coded as vectors of four values: all of them, or the count the subframe states.";
    public const string QuantizationStep = "The quantization step of the subframe, before the per-channel modifiers: a start value from the sample size, a signed 6-bit change and, for a change at either end of its range, escape groups.";
    public const string ScaleFactors = "How the channel's scale factors, which set the step of each band of the spectrum, are coded in this subframe: as a new set, as differences from the last set, or not at all.";
    public const string Coefficients = "The channel's spectral coefficients: a choice of codebook, then vectors and run-level codes.";

    // Statistics.
    public const string Statistics = "Figures over the frames Encspect read: counts, sizes, bitrates and how often each coding tool is used.";
    public const string StatPackets = "How many media objects the stream has.";
    public const string StatFrames = "How many frames were read, including invalid ones.";
    public const string StatInvalidFrames = "How many frames failed one of the rules of the syntax and were dropped.";
    public const string StatFrameLength = "How many samples of each channel a frame holds.";
    public const string StatSamples = "The samples a decoder outputs for the stream: the frames read, less the first, which only fills the overlap of the transform, and less the samples each frame's trims cut.";
    public const string StatTrimmedSamples = "The samples the trims of the frames cut at their starts and ends.";
    public const string StatDuration = "The samples divided by the sampling rate.";
    public const string StatBlockAlignment = "The size of each packet, the container's block alignment.";
    public const string StatAverageBitrate = "The bytes of the full packets over the stream's duration, as a bitrate.";
    public const string StatSmallestFrame = "The fewest bytes a frame read without error takes.";
    public const string StatLargestFrame = "The most bytes a frame read without error takes.";
    public const string StatMinimumFrameBitrate = "The bitrate of the smallest frame, were every frame that size.";
    public const string StatMaximumFrameBitrate = "The bitrate of the largest frame, were every frame that size.";
    public const string StatFramesSpanningPackets = "How many frames begin in one packet and end in the next.";
    public const string StatMostFramesInPacket = "The most frames read while one packet is read.";
    public const string StatHasLengthPrefix = "Whether every frame starts with its length.";
    public const string StatMostSubframes = "The most subframes a channel may be divided into in one frame.";
    public const string StatSubframes = "How many subframes were read, counting a subframe shared by channels once.";
    public const string StatSubframeSizes = "How many different subframe lengths occur; each size lists how many subframes have it.";
    public const string StatSubframeSize = "How many subframes have this length, and their share of all.";
    public const string StatChannelsPerSubframe = "The average number of channels a subframe is shared by.";
    public const string StatChannelGroups = "How many channel groups use a transform, and their share of all groups.";
    public const string StatBandsTransformed = "In how many scale factor bands of the groups with a transform the transform applies, and their share.";
    public const string StatPostprocessingFrames = "How many frames set the postprocessing bit.";
    public const string StatDynamicRangeFrames = "How many frames carry a dynamic range gain.";
    public const string StatTrimmedFrames = "How many frames state a trim at the start or the end.";
    public const string StatExtendedHeaders = "How many subframes have an extended header.";
    public const string StatVectorCounts = "How many subframes state how many coefficients are coded as vectors.";
    public const string StatCodedChannels = "How many channels of subframes send coefficients, and their share of all channels of subframes.";
    public const string StatScaleFactorsSent = "Of the channels of subframes that send coefficients, those whose scale factors are sent in the subframe.";
    public const string StatScaleFactorDifferences = "Of the scale factors sent, those coded as differences from the last set.";
    public const string StatSecondCodebook = "Of the channels of subframes that send coefficients, those that use the second coefficient codebook.";
    public const string StatSmallestStep = "The smallest quantization step of a subframe that sends coefficients.";
    public const string StatLargestStep = "The largest quantization step of a subframe that sends coefficients.";
    public const string StatNonZero = "How many coefficients are not 0.";
    public const string StatEscapes = "How many coefficients are coded with an escape for a large value.";
    public const string StatDamaged = "How many channels of subframes have a coefficient code that breaks the syntax.";
    public const string StatLowFrequencyChannel = "The channel the channel mask names as low-frequency, counted from 1.";

    // Series.
    public const string BitrateSeries = "The bitrate of each frame, as the frame's size over the time its samples last.";
    public const string BitrateOverTimeSeries = "The bitrate of the frames that start in each second of audio.";

    public static string Reason(ProFault fault) => fault switch
    {
        ProFault.TileHeader => "its tile header lays out subframes the stream cannot have",
        ProFault.FillBits => "an extended header skips more bits than the frame has",
        ProFault.ReservedBit => "a subframe sets a reserved bit",
        ProFault.ChannelTransform => "a channel transform that FFmpeg's decoder does not support",
        ProFault.VectorCount => "a subframe states more vector-coded coefficients than it has coefficients",
        ProFault.ScaleFactorRun => "a scale factor run goes past the last band",
        ProFault.NeedsMoreBits => "it needs more bits than it has",
        ProFault.LengthPrefix => "its length prefix is not where its data ends",
        _ => string.Empty,
    };
}
