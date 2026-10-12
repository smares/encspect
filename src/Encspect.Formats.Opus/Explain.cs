namespace Encspect.Formats.Opus;

// The background articles the Opus nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string OpusModes = "opus-modes";
    public const string OpusToc = "opus-toc";
    public const string OpusPreSkip = "opus-pre-skip";
    public const string OpusChannelMapping = "opus-channel-mapping";
    public const string GranulePosition = "granule-position";
    public const string BitrateModes = "bitrate-modes";
    public const string EncoderDetection = "encoder-detection";
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
    public const string PaddingLength = "The padding's length: each byte of 255 adds 254 and continues, the last byte adds its value (RFC 6716, section 3.2.5).";
    public const string FrameLengths = "The frame lengths the packet states, one or two bytes each (RFC 6716, section 3.2.1); a self-delimited part adds its last frame's.";
    public const string Frame = "A compressed frame, which the analyzer does not decode (RFC 6716, section 3).";
    public const string EmptyFrame = "A frame of no bytes, which tells the decoder there is no data for it, as in discontinuous transmission (RFC 6716, section 3.2.1).";
    public const string Padding = "Padding after the frames, which decoders ignore; CBR encoders pad to keep every packet the same size (RFC 6716, section 3.2.5).";
    public const string Unparsed = "The rest of a packet whose framing breaks RFC 6716, section 3.4, so its frames cannot be told apart.";

    public const string Statistics = "Figures over the stream's audio packets, counted from their TOC bytes and framing and from the granule positions.";
    public const string Packets = "How many audio packets the stream holds.";
    public const string Frames = "How many frames the audio packets hold, over all streams.";
    public const string DecodedSamples = "The samples at 48 kHz the packets code, each the frames of its TOC times their duration, before the pre-skip and end trimming are dropped.";
    public const string StatPreSkip = "The samples at the start the decoder drops, from the identification header (RFC 7845, section 4.2).";
    public const string StartOffset = "The first audio page's granule position less the samples up to it: 0 for a stream that starts at its beginning, above 0 for one that starts later (RFC 7845, section 4.5).";
    public const string EndTrimming = "Samples the packets code beyond the last granule position, which a decoder drops from the end (RFC 7845, section 4.4).";
    public const string Samples = "The samples at 48 kHz a decoder outputs: the decoded samples less the pre-skip and the end trimming.";
    public const string Duration = "The samples over 48 kHz, in seconds.";
    public const string StatIsVbr = "Whether the audio packets differ in size. In CBR encoding every packet has the same size; in VBR and constrained VBR they differ.";
    public const string AverageBitrate = "The bits of the audio packets over the samples they code.";
    public const string MinimumBitrate = "The lowest bitrate of an audio packet: its bits over the samples it codes.";
    public const string MaximumBitrate = "The highest bitrate of an audio packet: its bits over the samples it codes.";
    public const string PacketSizes = "The smallest, largest and average size of the audio packets.";
    public const string PacketSizesConstant = "Every audio packet has the same size, as CBR encoding writes them.";
    public const string Modes = "How many frames use each mode: SILK for speech, CELT for music, hybrid for both in one frame (RFC 6716, section 2).";
    public const string ModeShare = "Frames of this mode, and their share of all frames.";
    public const string Bandwidths = "How many frames code each audio bandwidth (RFC 6716, section 2, table 1).";
    public const string BandwidthShare = "Frames of this bandwidth, and their share of all frames.";
    public const string FrameSizes = "How many frames have each duration (RFC 6716, section 3.1).";
    public const string FrameSizeShare = "Frames of this duration, and their share of all frames.";
    public const string StereoCoding = "How many packets, per stream in a multistream file, set the TOC's stereo flag.";
    public const string StereoShare = "Packets whose TOC sets the stereo flag, and their share.";
    public const string MonoShare = "Packets whose TOC leaves the stereo flag clear, and their share.";
    public const string FramePacking = "How many packets use each frame count code (RFC 6716, section 3.2).";
    public const string CodeShare = "Packets of this code, and their share.";
    public const string EmptyFrames = "Frames of no bytes, which carry no data, as in discontinuous transmission (RFC 6716, section 3.2.1).";
    public const string PaddingTotal = "The padding bytes of all audio packets, which decoders ignore (RFC 6716, section 3.2.5).";

    public const string Encoder = "Which program or library encoded the stream, as the analyzer concludes from the evidence below.";
    public const string Confidence = "How sure the naming is: high when opusenc's ENCODER field and the libopus vendor string agree, medium for a name in one field other software may write.";
    public const string Evidence = "What points to the encoder, at the bytes it was found in.";
}

internal static class SeriesText
{
    public const string Bitrate = "The bitrate of each audio packet: its bits over the samples it codes, 0 for a packet that codes none.";
    public const string BitrateOverTime = "The bitrate in each second of audio: the bits of the packets that start in it over their duration.";
}
