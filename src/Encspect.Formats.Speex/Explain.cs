namespace Encspect.Formats.Speex;

// The background articles the Speex nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string SpeexModes = "speex-modes";
    public const string GranulePosition = "granule-position";
    public const string BitrateModes = "bitrate-modes";
    public const string EncoderDetection = "encoder-detection";
}

// What each Speex node is. Constants only, shared by every node of their kind: the header from the
// Speex manual, section 7.3 (table 7.1) and speex_header.h, the frames from sections 5.5, 9.3 and
// 10.4 and the bits per submode from speex_mode_query in libspeex 1.2.1.
internal static class Explain
{
    public const string Header = "The Speex header, the stream's first packet: 80 bytes that give the mode, sample rate, channels and frames per packet (Speex manual, section 7.3, table 7.1).";
    public const string HeaderShort = "A first packet too short to hold the 80-byte Speex header (Speex manual, section 7.3).";
    public const string SpeexString = "\"Speex\" and three spaces, which identify the stream (speex_string).";
    public const string SpeexVersion = "The version of Speex that encoded the stream, as text of up to 20 bytes (speex_version).";
    public const string VersionId = "The version of the header format, 1 in Speex 1.0 to 1.2 (speex_version_id).";
    public const string HeaderSize = "The header's size in bytes, 80 (header_size).";
    public const string SampleRate = "The sample rate of the audio in Hz; narrowband, wideband and ultra-wideband are built for 8, 16 and 32 kHz (rate).";
    public const string Mode = "The mode: 0 narrowband, 1 wideband, 2 ultra-wideband, which sets the frame size and the layers of each frame (mode).";
    public const string ModeInvalid = "A mode other than 0 (narrowband), 1 (wideband) and 2 (ultra-wideband), which decoders reject (mode).";
    public const string ModeBitstreamVersion = "The version of the mode's bit-stream, 4 in Speex 1.2, which decoders compare with their own (mode_bitstream_version).";
    public const string Channels = "The number of channels, 1 or 2: a stereo stream is coded as mono with intensity stereo parameters sent in-band (nb_channels).";
    public const string Bitrate = "The bitrate the encoder gave in bits per second, or -1 where it gave none (bitrate).";
    public const string FrameSize = "The samples per frame: 160 in narrowband, 320 in wideband and 640 in ultra-wideband, 20 ms in each (frame_size).";
    public const string IsVbr = "Whether the encoder ran in VBR mode, choosing each frame's submodes by the speech; speexenc and ffmpeg leave it unset in ABR (vbr).";
    public const string FramesPerPacket = "How many frames each audio packet holds; speexenc packs 1 to 10 (frames_per_packet).";
    public const string ExtraHeaders = "How many further header packets follow the comment before the audio (extra_headers).";
    public const string Reserved = "A field reserved for future use, which speex_header.h says must be 0.";
    public const string HeaderExtraData = "Bytes in the header packet after the 80-byte header, which decoders ignore.";
    public const string ExtraHeader = "A further header packet the Speex header announces, which decoders skip (extra_headers).";
    public const string CommentTrailingData = "Bytes in the comment packet after the comment, which readers ignore.";

    public const string AudioPackets = "The audio packets, each holding the frames the header gives per packet, from the third packet on (Speex manual, section 7.3).";
    public const string Packet = "An audio packet: its frames of 20 ms each, then bits that fill the last byte (Speex manual, sections 5.5 and 7.3).";
    public const string Frame = "A frame of 20 ms: a narrowband layer, followed in wideband and ultra-wideband by a layer for each higher band (Speex manual, section 10.4).";
    public const string InBandData = "Bits before the frame's narrowband layer: in-band requests and parameters such as stereo (code 14), application data (code 13), or a wideband layer a narrowband decoder skips (Speex manual, section 5.5).";
    public const string NarrowbandLayer = "The narrowband layer, which codes the band up to 4 kHz with CELP; its submode sets its bits (Speex manual, section 9.3).";
    public const string WidebandLayer = "The wideband layer, which codes the band from 4 to 8 kHz; its submode sets its bits (Speex manual, section 10.4).";
    public const string UltraWidebandLayer = "The ultra-wideband layer, which codes the band from 8 to 16 kHz as the wideband layer codes 4 to 8 kHz (libspeex, speex_uwb_mode).";
    public const string IsNarrowbandBit = "0: the narrowband layer starts here (the wideband bit, Speex manual, table 9.1).";
    public const string IsWidebandBit = "1: a layer for a higher band starts here (the wideband bit, Speex manual, table 10.1).";
    public const string NarrowbandSubmode = "The narrowband submode, 4 bits: 0 is the 5-bit frame of discontinuous transmission, 1 to 8 set bit allocations of 43 to 492 bits per frame (Speex manual, table 9.1).";
    public const string WidebandSubmode = "The wideband layer's submode, 3 bits: 0 to 4, which take 4 to 352 bits per layer (Speex manual, table 10.1).";
    public const string UltraWidebandSubmode = "The ultra-wideband layer's submode, 3 bits: 0 or 1, which take 4 or 36 bits per layer (libspeex 1.2.1, speex_uwb_mode).";
    public const string NarrowbandCoded = "The layer's coded parameters: line spectral pairs, pitch, gains and codebook indices, in the bits the submode fixes (Speex manual, table 9.1).";
    public const string HighBandCoded = "The layer's coded parameters: line spectral pairs, gains and codebook indices, without pitch prediction (Speex manual, section 10).";
    public const string Padding = "Bits after the last frame, which fill the last byte; since Speex 1.0.2 they start with the terminator code 15 (Speex manual, section 5.5).";
    public const string IgnoredData = "Bits after the frames the header gives per packet, which decoders do not read.";
    public const string Unparsed = "Bits the analyzer could not read as frames, because a submode is undefined or a frame runs past the packet's end.";

    public const string Statistics = "Figures over the stream's audio packets, counted from their frames and the granule positions.";
    public const string Packets = "How many audio packets the stream holds.";
    public const string Frames = "How many frames the audio packets hold.";
    public const string CodedSamples = "The samples the frames code: the number of frames times the frame size.";
    public const string EncoderDelay = "Samples the first audio page's packets code beyond its granule position, which speexenc and ffmpeg set to the encoder's lookahead.";
    public const string EndTrimming = "Samples the packets code beyond the last granule position, which a decoder drops from the end.";
    public const string Samples = "The samples the granule positions give: the stream's length once the delay and the end trimming are dropped.";
    public const string Duration = "The samples over the sample rate, in seconds.";
    public const string StatIsVbr = "Whether the audio packets differ in size, so the bitrate changes from packet to packet.";
    public const string AverageBitrate = "The bits of the audio packets over the samples they code.";
    public const string MinimumBitrate = "The bitrate of the smallest audio packet.";
    public const string MaximumBitrate = "The bitrate of the largest audio packet.";
    public const string PacketSizes = "How many audio packets have each size, with the bitrate that size gives.";
    public const string PacketSize = "Audio packets of this size, their share of all packets, and the bitrate one of them gives.";
    public const string NarrowbandSubmodes = "How many frames use each narrowband submode, with the bits per frame it takes (Speex manual, table 9.1).";
    public const string WidebandSubmodes = "How many frames use each wideband submode, with the bits per layer it takes (Speex manual, table 10.1).";
    public const string UltraWidebandSubmodes = "How many frames use each ultra-wideband submode, with the bits per layer it takes (libspeex 1.2.1, speex_uwb_mode).";
    public const string SubmodeShare = "Frames of this submode, and their share of all frames.";
    public const string LayerAbsent = "Frames without a layer for this band, and their share of all frames.";
    public const string InBandRequests = "How many in-band requests, parameters and application data the frames carry (codes 14 and 13, Speex manual, section 5.5).";
    public const string StereoParameters = "How many of them are intensity stereo parameters (in-band code 9), which turn the mono frames into stereo.";

    public const string Encoder = "Which program encoded the stream, as the analyzer concludes from the evidence below.";
    public const string Confidence = "How sure the naming is: high when speexenc's vendor string and the header's version agree, medium for an encoder's name alone, low when only the library's version points to it.";
    public const string Evidence = "What points to the encoder, at the bytes it was found in.";
}

internal static class SeriesText
{
    public const string Bitrate = "The bitrate of each audio packet: its bits over the samples its frames code.";
    public const string BitrateOverTime = "The bitrate in each second of audio: the bits of the packets that start in it over their duration.";
}
