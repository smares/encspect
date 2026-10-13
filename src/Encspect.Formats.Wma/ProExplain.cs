namespace Encspect.Formats.Wma;

// What each Windows Media Audio Professional node is. Constants only, shared by every node of their kind,
// written from FFmpeg 6.1.1's decoder (libavcodec/wmaprodec.c and wma_common.c).
internal static class ProExplain
{
    public const string ExtraData = "Bytes after the fields the format defines, which decoders ignore.";

    public const string BitsPerSample = "The sample size of the audio the stream was encoded from, in bits (the first two bytes of the codec-specific data). FFmpeg's decoder scales its output by it and starts the quantization step from it (wmaprodec.c: bits_per_sample).";
    public const string ChannelMask = "Which speakers the channels feed, as a WAVE channel mask (bytes 2 to 5 of the codec-specific data). FFmpeg's decoder takes the channel count from it, and the position of the low-frequency channel (wmaprodec.c).";
    public const string Unknown = "Bytes of the codec-specific data that FFmpeg's decoder does not read.";
    public const string DecodeFlags = "The decode flags (bytes 14 and 15 of the codec-specific data): the coding tools the stream uses. FFmpeg's decoder reads the frame length adjustment (bits 1 and 2), the number of subframes (bits 3 to 5), the length prefix of frames (bit 6) and dynamic range compression (bit 7).";
    public const string FlagFrameLength = "Raises (2) or lowers (4, 6) the frame length from the one the sample rate gives: 2 doubles it, 4 halves it, 6 quarters it (wma_common.c: ff_wma_get_frame_len_bits).";
    public const string FlagSubframes = "The base-2 logarithm of the most subframes a frame is divided into (wmaprodec.c: max_num_subframes).";
    public const string FlagLengthPrefix = "Set if every frame starts with its length in bits, which lets a decoder find the frames of a packet without reading them (wmaprodec.c: len_prefix).";
    public const string FlagDynamicRange = "Set if every frame carries a gain for dynamic range compression (wmaprodec.c: dynamic_range_compression).";
    public const string FlagOthers = "Bits of the decode flags that FFmpeg's decoder does not read.";

    public const string Stream = "The Windows Media Audio Professional stream: its packets, which the container carries as media objects, and the frames in them.";
    public const string Packets = "The packets in the order they are stored: the media objects of the stream, each the size of the block alignment. A frame may begin in one and end in the next.";
    public const string Frames = "The frames in decoding order: the unit that is decoded to a fixed number of samples per channel. A frame that spans packets lies in the bytes of both.";
    public const string Packet = "A packet: a media object of the block alignment's size. It holds a header, the end of the frame before, whole frames and the start of one that goes on (wmaprodec.c: decode_packet).";
    public const string PacketSequence = "The number of the packet, counted from 0 to 15 and around. A decoder that finds a gap takes packets to be lost and drops the frame that crosses into the packet.";
    public const string PacketUnused = "Two bits that FFmpeg's decoder skips.";
    public const string PacketPreviousBits = "How many bits after the header finish the frame that began in the packet before.";
    public const string PacketFrameEnd = "The last bits of the frame that began in the packet before.";
    public const string PacketFrameStart = "The first bits of a frame that goes on in the next packet.";
    public const string PacketFrameData = "The bits of a frame that lies in this packet.";
    public const string PacketPadding = "Bits at the end of the packet after the last frame, whose trailer bit says that no frame follows; a decoder keeps them in case they begin a frame and drops them when the next packet finishes none.";
    public const string PacketUnfinished = "Bits at the end of the packet that begin a frame which the stream does not finish in a later packet.";

    public const string Frame = "A frame: the data of a fixed number of samples of every channel, divided into subframes (wmaprodec.c: decode_frame).";
    public const string FrameLength = "The length of the frame in bits, counting this field, which a decoder checks against where it finds the frame to end (wmaprodec.c: len_prefix).";
    public const string TileHeader = "How the frame is divided into subframes: whether every channel uses the same division, which channels have a subframe at each position, and the subframes' lengths (wmaprodec.c: decode_tilehdr).";
    public const string ChannelLayout = "The lengths in samples of the subframes of one channel, in order.";
    public const string Postprocess = "A flag that a transform of the output channels follows, with a matrix of 4 bits for each pair of channels (wmaprodec.c: decode_frame). FFmpeg's decoder skips it.";
    public const string DynamicRange = "The gain of dynamic range compression for the frame (wmaprodec.c: drc_gain). FFmpeg's decoder reads it and does not apply it.";
    public const string Trim = "Samples to cut from the start and from the end of the decoded audio, where the frame says so (wmaprodec.c: trim_start and trim_end).";
    public const string FramePadding = "Bits between the last subframe and the trailer bit, which the length prefix accounts for (wmaprodec.c: decode_frame).";
    public const string MoreFrames = "The trailer bit of a frame: set if another frame follows it in the packet.";

    public const string Subframe = "A subframe: the coefficients of 128 to 4,096 samples of the channels that share its position and length (wmaprodec.c: decode_subframe).";
    public const string ExtendedHeader = "A flag, and when set a count of fill bits that a decoder skips (wmaprodec.c: decode_subframe).";
    public const string ReservedBit = "A bit of unknown use, which FFmpeg's decoder rejects when it is set (wmaprodec.c: decode_subframe).";
    public const string ChannelTransform = "How the channels of the subframe are grouped and which groups are transformed, as mid/side coding does for a pair (wmaprodec.c: decode_channel_transform).";
    public const string ChannelGroup = "Channels of the subframe that share a transform: which they are, whether the transform is on, and for which scale factor bands.";
    public const string CoefficientsSent = "One flag for each channel of the subframe: whether it sends coefficients or is silent for the subframe.";
    public const string VectorCoefficients = "Whether the number of coefficients that are vector coded is sent, and for each channel how many. The rest of the subframe's coefficients are run-level coded (wmaprodec.c: transmit_num_vec_coeffs).";
    public const string QuantizationStep = "The quantization step of the subframe: a 6-bit difference from a start value that follows from the sample size, extended in groups of 5 bits at the extremes; each channel can add a modifier. A decoder scales a band's coefficients by 10 to the power of the step, less the band's distance from the largest scale factor times the scale factor step, over 20 (wmaprodec.c: decode_subframe).";
    public const string ScaleFactors = "The scale factors of the channel's bands: a new set as differences between neighbors, or differences from the scale factors before (run-level coded), or the ones before unchanged (wmaprodec.c: decode_scale_factors).";
    public const string Coefficients = "The quantized coefficients of the channel: groups of four, two or one values by vector codes with signs, then run-level codes for the rest (wmaprodec.c: decode_coeffs).";

    public const string HeaderOnlyStream = "The stream as the container and the codec-specific data describe it: its packets are counted and their frames are not read.";
    public const string StatPacketBytes = "The bytes of all the packets.";
    public const string StatSmallestPacket = "The fewest bytes a packet takes.";
    public const string StatLargestPacket = "The most bytes a packet takes.";
    public const string StatNominalBitrate = "The average bytes per second the container states for the stream (the WAVEFORMATEX's nAvgBytesPerSec), as a bitrate. The packets cover more than the duration where the stream starts with a preroll, so the packets' bytes over the duration would overstate it.";
    public const string StatContainerDuration = "The length of the stream as the container states it, the play duration less the preroll.";

    public const string Statistics = "Figures over the stream's frames, counted while reading them.";
    public const string StatPackets = "How many packets the stream has.";
    public const string StatFrames = "How many frames the stream has, counting those that failed.";
    public const string StatInvalidFrames = "Frames that could not be read to their end.";
    public const string StatFrameLength = "The samples each channel gets from a frame.";
    public const string StatSamples = "The samples per channel a decoder outputs: the samples of the frames that were read less those the frames cut from the start and the end; the first frame cuts all of its own, which only fill the overlap of the transform.";
    public const string StatTrimmedSamples = "The samples per channel that the frames cut from the start and the end of the audio.";
    public const string StatDuration = "The time those samples last.";
    public const string StatBlockAlignment = "The size of each packet, the container's block alignment.";
    public const string StatAverageBitrate = "The packets' bytes over the duration.";
    public const string StatSmallestFrame = "The fewest bytes a frame takes.";
    public const string StatLargestFrame = "The most bytes a frame takes.";
    public const string StatMinimumFrameBitrate = "The bitrate of the smallest frame.";
    public const string StatMaximumFrameBitrate = "The bitrate of the largest frame.";
    public const string StatStraddlingFrames = "Frames that begin in one packet and end in the next.";
    public const string StatMaxFramesPerPacket = "The most frames that end in one packet.";
    public const string StatSubframes = "How many subframes the frames hold.";
    public const string StatSubframeSizes = "How many subframes there are of each length in samples.";
    public const string StatSubframeShare = "The share of the subframes of one length.";
    public const string StatMaxSubframes = "The most subframes a frame can be divided into, from the decode flags.";
    public const string StatChannelsPerSubframe = "The average number of channels that share a subframe.";
    public const string StatGroups = "Channel groups of subframes, and how many of them use a transform.";
    public const string StatTransformBands = "The scale factor bands of the transformed groups that have the transform turned on.";
    public const string StatPostprocess = "Frames that send a postprocessing transform of the output channels.";
    public const string StatDynamicRange = "Frames that carry a dynamic range compression gain.";
    public const string StatTrimmed = "Frames that cut samples from the start or the end of the audio.";
    public const string StatExtendedHeaders = "Subframes that send an extended header.";
    public const string StatVectorCounts = "Subframes that state how many coefficients are vector coded.";
    public const string StatScalesSent = "Channel subframes that send scale factors.";
    public const string StatScalesDifferential = "Channel subframes that send their scale factors as differences from the ones before, run-level coded, of those that send scale factors.";
    public const string StatSecondCodebook = "Channel subframes whose run-level codes use the second codebook.";
    public const string StatQuantStep = "The smallest and largest quantization step of the subframes.";
    public const string StatNonZero = "Coefficients that are not zero.";
    public const string StatEscapes = "Run-level coefficients coded with an escape.";
    public const string StatCoefficientErrors = "Channel subframes whose coefficient codes ran into bits that are no code, which FFmpeg's decoder ignores.";
    public const string StatCodedChannels = "Channel subframes that send coefficients, of all the channel subframes.";
    public const string StatLengthPrefix = "Whether every frame starts with its length.";
    public const string StatLfe = "The channel that carries low frequencies only, from the channel mask.";
}

internal static class ProSeriesText
{
    public const string Bitrate = "The bitrate of each frame: its bits over the time its samples last.";
    public const string BitrateOverTime = "The bitrate in each second of audio: the bits of the frames that start in it over their duration.";
}
