namespace Encspect.Formats.Opus;

// The background articles the Opus nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string OpusModes = "opus-modes";
    public const string OpusToc = "opus-toc";
    public const string OpusPreSkip = "opus-pre-skip";
    public const string CodedBandwidth = "coded-bandwidth";
    public const string OpusChannelMapping = "opus-channel-mapping";
    public const string GranulePosition = "granule-position";
    public const string BitrateModes = "bitrate-modes";
    public const string EncoderDetection = "encoder-detection";
    public const string OpusCelt = "opus-celt";
    public const string OpusSilk = "opus-silk";
    public const string IntensityStereo = "intensity-stereo";
    public const string BlockSwitching = "block-switching";
    public const string MidSideStereo = "mid-side-stereo";
}

// What each Opus node is. Constants only, shared by every node of their kind: the headers from RFC
// 7845, section 5, and the packets from RFC 6716, sections 3.1 to 3.4 and appendix B.
internal static class Explain
{
    public const string Head = "The identification header, the stream's first packet: channels, pre-skip, the input's sample rate, the output gain and the channel mapping (RFC 7845, section 5.1).";
    public const string HeadShort = "A first packet too short to hold the identification header's fields (RFC 7845, section 5.1).";
    public const string MagicHead = "\"OpusHead\", which identifies the stream (RFC 7845, section 5.1).";
    public const string Version = "The version of the encapsulation, 1 for RFC 7845; decoders accept any whose upper four bits are 0 (section 5.1).";
    public const string Channels = "The number of output channels, at least 1; how they come from the streams is the channel mapping's (section 5.1).";
    public const string PreSkip = "Samples at 48 kHz the decoder drops at the start, the encoder's delay, before the first sample of audio (section 5.1, and section 4.2).";
    public const string InputSampleRate = "The sample rate of the encoder's input, for information only: Opus always decodes at 48 kHz, and a player may resample to this rate (section 5.1).";
    public const string OutputGain = "A gain in dB that the decoder applies to its output, stored in steps of 1/256 dB (section 5.1).";
    public const string MappingFamily = "The channel mapping family: 0 for one stream of mono or stereo, 1 for up to eight channels in the Vorbis order, 2 and 3 for ambisonics (RFC 8486), 255 for channels without a defined layout (RFC 7845, section 5.1.1).";
    public const string StreamCount = "How many Opus streams each packet holds (section 5.1.1).";
    public const string CoupledCount = "How many of the streams are stereo; they come first and decode to two channels each, the others to one (section 5.1.1).";
    public const string ChannelMapping = "For each output channel, the decoded channel it takes: coupled streams give channels 2i and 2i+1, the rest one each, and 255 is silence (section 5.1.1).";
    public const string ChannelMappingEntry = "The decoded channel this output channel takes, and where it plays: a speaker in family 1, an ambisonic channel number (ACN) or the non-diegetic pair in family 2 (RFC 7845, section 5.1.1; RFC 8486, section 3.1).";
    public const string DemixingMatrix = "The demixing matrix family 3 stores in place of the channel mapping: one row per output channel and one column per decoded channel, 16-bit Q15 coefficients in column-major order; the output gain may scale it (RFC 8486, section 3.2).";
    public const string ChannelLayout = "The speaker of each channel, in order, which the channel count gives in mapping families 0 and 1 (RFC 7845, sections 5.1.1.1 and 5.1.1.2).";
    public const string HeadExtraData = "Bytes in the identification header packet after its fields, which decoders ignore.";
    public const string MagicTags = "\"OpusTags\", which starts the comment header, before the Vorbis comment (RFC 7845, section 5.2).";
    public const string TagsExtraData = "Data after the comments: binary data to keep where the first byte's least significant bit is 1, padding otherwise (RFC 7845, section 5.2).";
    public const string HasBinaryData = "The least significant bit of the first byte after the comments: 1 marks binary data that tools should keep, 0 padding they may drop (section 5.2).";
    public const string UnexpectedPacket = "A packet where RFC 7845 puts the comment header, which is not one (section 3).";

    public const string AudioPackets = "The audio packets, from the third packet on: each holds one Opus packet per stream (RFC 7845, section 3).";
    public const string Packet = "An audio packet: for each stream a TOC byte, the frames it packs and their lengths, and any padding (RFC 6716, section 3).";
    public const string StreamSelfDelimited = "One stream's part of a multistream packet, in the self-delimiting framing that adds the length of its last frame, so the next stream's part can be found (RFC 6716, appendix B).";
    public const string StreamLast = "The last stream's part of a multistream packet, in the ordinary framing, which takes the rest of the packet (RFC 7845, section 5.1.1).";
    public const string TocByte = "The table of contents byte: the configuration, the stereo flag and how the frames are packed (RFC 6716, section 3.1).";
    public const string Config = "The configuration number, 0 to 31: the mode, the audio bandwidth and the frame duration (RFC 6716, section 3.1, table 2).";
    public const string IsStereo = "Whether the frames code stereo; a mono decoder downmixes them (RFC 6716, section 3.1).";
    public const string FrameCountCode = "How the frames are packed: 0 one frame, 1 two of equal size, 2 two of different sizes, 3 any number (RFC 6716, section 3.2).";
    public const string FrameCountByte = "The byte code 3 adds: whether the frames vary in size, whether padding follows, and how many frames there are (RFC 6716, section 3.2.5).";
    public const string CountIsVbr = "Whether each frame has its own length (VBR) or all have the same (CBR) (RFC 6716, section 3.2.5).";
    public const string CountHasPadding = "Whether padding follows the frames, with its length after this byte (RFC 6716, section 3.2.5).";
    public const string CountFrames = "How many frames the packet holds, 1 to 48, at most 120 ms in all (RFC 6716, section 3.2.5).";
    public const string PaddingLength = "The length of the padding in bytes, as the bytes at this position code it: each byte of 255 adds 254 and continues, the last byte adds its value (RFC 6716, section 3.2.5).";
    public const string FrameLengths = "The frame lengths the packet states, one or two bytes each (RFC 6716, section 3.2.1); a self-delimited part adds its last frame's.";
    public const string Frame = "A compressed frame, which the analyzer does not decode (RFC 6716, section 3).";
    public const string EmptyFrame = "A frame of no bytes, which tells the decoder there is no data for it, as in discontinuous transmission (RFC 6716, section 3.2.1).";
    public const string Padding = "Padding after the frames, which decoders ignore; CBR encoders pad to keep every packet the same size (RFC 6716, section 3.2.5).";
    public const string Unparsed = "The rest of a packet whose framing breaks RFC 6716, section 3.4, so its frames cannot be told apart.";

    public const string Statistics = "Figures over the stream's audio packets, counted from their TOC bytes and framing and from the granule positions.";
    public const string Packets = "How many audio packets the stream holds, not counting a packet the container cuts.";
    public const string Frames = "How many frames the audio packets hold, over all streams.";
    public const string CodedSamples = "The samples at 48 kHz the packets code, each the frames of its TOC times their duration, before the pre-skip and end trimming are dropped.";
    public const string StatPreSkip = "The samples at the start the decoder drops, from the identification header (RFC 7845, section 4.2).";
    public const string StartOffset = "The first audio page's granule position less the samples up to it: 0 for a stream that starts at its beginning, above 0 for one that starts later (RFC 7845, section 4.5).";
    public const string EndTrimmingContainer = "The samples a decoder drops from the end, as the container states them: the DiscardPadding of the track's last block (RFC 9559, section 5.1.3.5.7), at 48 kHz, rounded to the nearest sample, halves up, and no more than the packets hold after the pre-skip.";
    public const string EndTrimming = "Samples the packets code beyond the last granule position, which a decoder drops from the end (RFC 7845, section 4.4).";
    public const string Samples = "The samples at 48 kHz a decoder outputs: the decoded samples less the pre-skip and the end trimming.";
    public const string Duration = "The samples over 48 kHz, in seconds.";
    public const string AverageBitrate = "The bits of the audio packets over the samples they code.";
    public const string MinimumBitrate = "The lowest bitrate of an audio packet: its bits over the samples it codes.";
    public const string MaximumBitrate = "The highest bitrate of an audio packet: its bits over the samples it codes.";
    public const string PacketSizes = "The smallest, largest and average size of the audio packets.";
    public const string PacketSizesConstant = "Every audio packet has the same size, as CBR encoding writes them.";
    public const string Modes = "How many frames use each mode: SILK for speech, CELT for music, hybrid for both in one frame (RFC 6716, section 2).";
    public const string ModeShare = "Frames of this mode, and their share of all frames.";
    public const string StatCodedBandwidth = "The audio bandwidth the TOC bytes of the frames name, in whole hertz: 4,000, 6,000, 8,000, 12,000 or 20,000 for narrowband to fullband (RFC 6716, section 2, table 1). It is the bandwidth the stream's configuration states, not a measurement of the sound.";
    public const string BandwidthLowest = "The lowest coded bandwidth of any frame.";
    public const string BandwidthMostCommon = "The coded bandwidth most frames have; the lowest of them where several are equally common.";
    public const string BandwidthHighest = "The highest coded bandwidth of any frame.";
    public const string Bandwidths = "How many frames code each audio bandwidth (RFC 6716, section 2, table 1).";
    public const string BandwidthShare = "Frames of this bandwidth, and their share of all frames.";
    public const string FrameSizes = "How many frames have each duration (RFC 6716, section 3.1).";
    public const string FrameSizeShare = "Frames of this duration, and their share of all frames.";
    public const string StereoCoding = "How many packets, per stream in a multistream file, set the TOC's stereo flag; the entries below split the packets into stereo and mono. It is the channel count a packet codes, not the left/right or mid/side coding of other formats (RFC 6716, section 3.1).";
    public const string StereoShare = "Packets whose TOC sets the stereo flag, and their share.";
    public const string MonoShare = "Packets whose TOC leaves the stereo flag clear, and their share.";
    public const string FramePacking = "How many packets use each frame count code (RFC 6716, section 3.2).";
    public const string CodeShare = "Packets of this code, and their share.";
    public const string EmptyFrames = "Frames of no bytes, which carry no data, as in discontinuous transmission (RFC 6716, section 3.2.1).";
    public const string CeltBlocks = "How many CELT frames code their spectrum as one long block and how many as several short blocks, which an encoder chooses for a transient (the frame's transient flag, RFC 6716, section 4.3.1). Frames of the SILK layer have no such choice.";
    public const string UsageLong = "CELT frames whose transient flag is clear: one transform for the whole frame (RFC 6716, section 4.3.1).";
    public const string UsageShort = "CELT frames whose transient flag is set: the frame is coded as 2, 4 or 8 short transforms, which follow a sudden onset more closely in time (RFC 6716, section 4.3.1).";
    public const string BlockShare = "CELT frames of this kind, and their share of all CELT frames.";
    public const string CeltStereo = "How the stereo CELT frames code their bands: with an angle that rotates between mid and side (the default), as two independent channels (dual stereo, which a frame states for the bands below its intensity bound) and as intensity stereo (the bands from the intensity bound up share one channel with a gain for each). A frame can use several (RFC 6716, section 4.3.4).";
    public const string StereoAngle = "Stereo CELT frames with at least one band coded as mid/side with an angle (RFC 6716, section 4.3.4).";
    public const string StereoDual = "Stereo CELT frames with dual stereo set and at least one band below the intensity bound, so that the bands code left and right on their own (RFC 6716, section 4.3.4).";
    public const string StereoIntensity = "Stereo CELT frames with at least one band coded as intensity stereo, from the intensity bound up (RFC 6716, section 4.3.4).";
    public const string StereoShareOfFrames = "Stereo CELT frames that use this coding, and their share of all stereo CELT frames; a frame counts once for each coding it uses.";
    public const string IntensityBound = "The lowest frequency from which the stereo CELT frames code intensity stereo, in whole hertz: the lower edge of the first intensity band (RFC 6716, section 4.3.3, table 55). The figure is the frame's coded intensity band, not a measurement of the sound.";
    public const string BoundLowest = "The lowest intensity stereo bound of any frame.";
    public const string BoundHighest = "The highest intensity stereo bound of any frame.";
    public const string CodedFrames = "How many frames carry range-coded data: SILK, CELT or hybrid frames the analyzer read to their end (RFC 6716, section 4); the frames of no bytes are not counted.";
    public const string RedundantFrames = "Frames that end with a redundant CELT frame, which smooths the change between SILK and CELT (RFC 6716, section 4.5.1).";
    public const string PaddingTotal = "The padding bytes of all audio packets, which decoders ignore (RFC 6716, section 3.2.5).";

    public const string Encoder = "Which program or library encoded the stream, as the analyzer concludes from the evidence below.";
    public const string Confidence = "How sure the naming is: medium, as it rests on the libopus vendor string or on ffmpeg's ENCODER ID, which other software may write too.";
    public const string Evidence = "What points to the encoder, at the bytes it was found in.";
    public const string CutCommentData = "The bytes of the comment header that the file holds. The container cuts the header (its finding says where), so it is not read as a comment.";
}

internal static class SeriesText
{
    public const string Bitrate = "The bitrate of each audio packet: its bits over the samples it codes, 0 for a packet that codes none.";
    public const string CodedBandwidth = "The coded bandwidth of each audio packet: the audio bandwidth its TOC byte names, in whole hertz, the highest of the stream parts of a packet with several; a packet that codes no samples has none (RFC 6716, section 2, table 1).";
    public const string CodedBandwidthOverTime = "The highest coded bandwidth of the packets that start in each second of audio.";
    public const string BlockUsage = "How each CELT frame of a packet is coded: one long block or several short ones (the frame's transient flag, RFC 6716, section 4.3.1); a packet with frames of both has both.";
    public const string StereoCoding = "How each stereo CELT frame of a packet codes its bands: with an angle between mid and side, as left and right (dual stereo) or as intensity stereo (RFC 6716, section 4.3.4); a frame that uses several has each.";
    public const string IntensityStereoBound = "The lowest intensity stereo bound of each audio packet's stereo CELT frames, in whole hertz: the lower edge of the first band coded as intensity stereo (RFC 6716, section 4.3.3, table 55); a packet without such a frame has none.";
    public const string BitrateOverTime = "The bitrate in each second of audio: the bits of the packets that start in it over their duration.";
}
