namespace Encspect.Formats.Musepack;

// The background articles the Musepack nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string SubbandCoding = "musepack-subband-coding";
    public const string Sv7Stream = "musepack-sv7-stream";
    public const string OldStreams = "musepack-sv4-to-sv6";
    public const string Sv8Packets = "musepack-sv8-packets";
    public const string NoiseSubstitution = "noise-substitution";
    public const string MidSideStereo = "mid-side-stereo";
    public const string IntensityStereo = "intensity-stereo";
    public const string ReplayGain = "replaygain";
    public const string GaplessPlayback = "gapless-playback";
    public const string EncoderDetection = "encoder-detection";
    public const string BitrateModes = "bitrate-modes";
    public const string Crc = "crc";
    public const string ApeTag = "ape-tag";
}

// What each Musepack node is, in the words the report shows next to it. Constants only: frame and
// packet nodes are built by the hundred thousand, so a description picks one of these and never
// allocates. The sources: the SV8 specification (Musepack's, as mirrored in mutagen-specs,
// mpc/sv8.rst), libmpcdec and libmpcenc of musepack_src r495, mppenc 1.16, libmpcdec 1.2.6 for
// stream versions 4 to 6, and ffmpeg 6.1's mpc and mpc8 demuxers. The Portable Musepack decoder
// (mpc.zip, 2004), the C++ code libmpcdec 1.2.6 was ported from, reads versions 4 to 6 the
// same way, so it is cited as the same reading, not a second one.
internal static class Explain
{
    public const string Sv7Stream = "A Musepack stream of version 7: a header of 25 bytes, then frames of 1,152 samples per channel, each led by its length in bits, in 32-bit little-endian words (libmpcdec r495 streaminfo.c, mpc_demux.c).";
    public const string Sv8Stream = "A Musepack stream of version 8: the magic number MPCK, then packets of a two-letter key, a size and a payload, ending with a stream end packet (SV8 specification).";
    public const string OldStream = "A Musepack stream of version 4, 5 or 6, the versions before SV7: a header of 48 bits (version 4) or 64 bits in 32-bit little-endian words, then frames of 1,152 samples per channel, each block of them led by its length in bits (libmpcdec 1.2.6).";
    public const string Gap = "Bytes after the end of the stream, which no frame or packet of it claims.";
    public const string AfterEmbeddedTag = "Bytes after an ID3v2 tag that lies inside the Musepack stream, which the tag breaks; the analyzer does not read them as part of the stream.";
    public const string AudioData = "The frames of the stream, which the analyzer does not read for this stream or its settings.";

    public const string Header = "The SV7 stream header: the frame count, the coding settings, ReplayGain, the gapless length of the last frame and the encoder's version, in seven 32-bit little-endian words read from their most significant bit (libmpcdec r495 streaminfo_read_header_sv7, mppenc 1.16 WriteHeader_SV7).";
    public const string Magic = "The magic number: \"MP+\" for stream version 7, \"MPCK\" for version 8 (libmpcdec r495 mpc_demux_header, SV8 specification, File magic number).";
    public const string StreamVersion = "The stream version, the low four bits of the header's fourth byte: 7 for SV7 (libmpcdec r495 mpc_demux_header).";
    public const string PnsFlag = "The high four bits of the header's fourth byte: mppenc 1.16 writes 1 (byte 0x17) when it encodes with perceptual noise substitution, 0 otherwise; libmpcdec r495 reads them as the PNS flag.";
    public const string FrameCount = "The number of frames the header counts, 1,152 samples each; with true gapless, the last of them holds only the samples Last Frame Samples gives (libmpcdec r495 streaminfo_read_header_sv7).";
    public const string IntensityStereoFlag = "The former intensity stereo flag: mppenc 1.16 writes 0 (\"not supported anymore\"), and libmpcdec r495 skips it.";
    public const string MidSideFlag = "Whether the frames may code a subband as mid and side instead of left and right: each such subband then carries a mid/side flag (libmpcdec r495 mpc_decoder_read_bitstream_sv7).";
    public const string MaxBand = "The highest subband, counted from 0, whose resolution every frame codes; the frames code subbands 0 to this one (libmpcdec r495 mpc_decoder_read_bitstream_sv7). libmpcdec refuses 0 and values from 32 on.";
    public const string Profile = "The quality profile the encoder used, by number: 7 Telephone, 8 Thumb, 9 Radio, 10 Standard, 11 Extreme, 12 Insane, 13 BrainDead, 5 and 6 and 14 and 15 qualities 0, 1, 9 and 10; the encoder's quality setting is the number minus 5 (libmpcdec r495 streaminfo.c, mpcdec).";
    public const string ProfileName = "The profile's name, as libmpcdec r495 gives it (versionNames in streaminfo.c).";
    public const string Quality = "The encoder's quality setting that the profile stands for: the profile minus 5, as mpcdec prints it (q=).";
    public const string Link = "Two bits mppenc 1.16 writes as 0 (\"for future use\"), which libmpcdec r495 skips.";
    public const string SampleRateIndex = "The sample rate by index: 0 is 44,100 Hz, 1 48,000 Hz, 2 37,800 Hz, 3 32,000 Hz (libmpcdec r495 streaminfo.c, SV8 specification, Frequency table).";
    public const string MaxLevel = "The largest sample value of the input; mppenc 1.16 writes 0 and leaves it to its ReplayGain tool, and libmpcdec r495 skips it (\"MaxLevel\").";
    public const string Sv7TitleGain = "The track's ReplayGain adjustment in hundredths of a dB, signed, 0 when not computed. libmpcdec r495 reads it as a loudness of 64.82 dB minus the adjustment, which mpcdec prints.";
    public const string Sv7TitlePeak = "The track's largest sample value on a 16-bit scale, 0 when not computed; libmpcdec r495 reads it as 20·log10 of the value in dB.";
    public const string Sv7AlbumGain = "The album's ReplayGain adjustment in hundredths of a dB, signed, 0 when not computed; libmpcdec r495 reads it as a loudness of 64.82 dB minus the adjustment.";
    public const string Sv7AlbumPeak = "The album's largest sample value on a 16-bit scale, 0 when not computed.";
    public const string TrueGapless = "Whether Last Frame Samples gives how many samples the last frame holds; without it, decoders drop 481 samples at the end (libmpcdec r495 streaminfo_read_header_sv7).";
    public const string LastFrameSamples = "How many of the last frame's 1,152 samples belong to the audio, 0 meaning all of them (libmpcdec r495). The same 11 bits follow the last frame in the bitstream.";
    public const string FastSeek = "Whether the encoder marked the stream as fit for fast seeking; mppenc 1.16 sets it (\"we now support fast seeking\").";
    public const string Unused = "Bits the header leaves unused, which mppenc 1.16 writes as 0.";
    public const string EncoderVersion = "The encoder's version in one byte: mppenc 1.16 writes 116 (major × 100 + minor digits). libmpcdec r495 reads a multiple of 10 as a release, other even numbers as a beta, odd ones as an alpha, and 0 as Buschmann 1.7.0 to 1.7.9 or Klemm 0.90 to 1.05.";

    public const string VersionName = "The name libmpcdec r495 gives the encoder's version (mpc_get_encoder_string), which mpcdec prints as the encoder.";

    public const string Sv7Frame = "One frame of 1,152 samples per channel: a 20-bit length, then that many bits of subband resolutions, scalefactors and samples (libmpcdec r495 mpc_demux_decode_inner).";
    public const string ExtraFrame = "A frame after the frames the header counts: with true gapless and more than 671 samples in the last counted frame, libmpcdec r495 decodes one more frame to finish the synthesis filter's output, and mppenc 1.16 writes it.";
    public const string FrameLength = "The frame's length in bits after this 20-bit field (libmpcdec r495 mpc_demux_decode_inner).";
    public const string FrameData = "The frame's coded data: resolutions, mid/side flags, scalefactors and samples of each subband.";
    public const string UsedBands = "How many subbands, from the lowest, carry data in this frame: the highest subband with a resolution other than 0 in either channel, plus 1.";
    public const string MidSideBands = "The subbands this frame codes as mid and side rather than left and right, counted from 0 (the mid/side flag after each coded subband's resolutions).";
    public const string NoiseBands = "How many subband channels this frame fills with noise instead of coded samples (resolution -1).";
    public const string FinalSamples = "The 11 bits after the last frame the header counts: how many of its samples belong to the audio, as in the header; with true gapless, libmpcdec r495 takes these bits rather than the header's (mpc_decoder_decode_frame).";

    public const string Sv4Header = "The header of stream versions 4 to 6, 48 bits in version 4 and 64 in 5 and 6, in 32-bit little-endian words read from their most significant bit (libmpcdec 1.2.6 streaminfo_read_header_sv6).";
    public const string Sv4Bitrate = "A constant bitrate in kbps, 0 for variable; libmpcdec 1.2.6 refuses streams with a constant bitrate.";
    public const string Sv4IntensityStereo = "Whether the stream uses intensity stereo; libmpcdec 1.2.6 refuses streams that do.";
    public const string Sv4MidSide = "Whether the frames may code subbands as mid and side (libmpcdec 1.2.6).";
    public const string Sv4StreamVersion = "The stream version, 4, 5 or 6, in 10 bits (libmpcdec 1.2.6).";
    public const string Sv4MaxBand = "The highest subband the frames code, counted from 0 (libmpcdec 1.2.6).";
    public const string Sv4BlockSize = "How many frames each length leads; libmpcdec 1.2.6 decodes only 1. The SV4 encoder writes what its -frm switch asks, and both the SV4 and the SV5 encoder wrote 16 when asked for intensity stereo at a constant bitrate.";
    public const string Sv4FrameCount = "The number of frames: 16 bits in version 4, 32 in 5 and 6. In versions 4 and 5, libmpcdec 1.2.6 and mppdec decode all but the last of them; in version 6 they decode them all.";
    public const string OldFrame = "One frame of 1,152 samples per channel: a 20-bit length, then that many bits of subband resolutions, mid/side flags, scalefactors and samples (libmpcdec 1.2.6 mpc_decoder_read_bitstream_sv6).";
    public const string OldLastFrame = "The last frame the header counts in version 4 or 5, which decoders (libmpcdec 1.2.6, mppdec) do not decode. The SV4 and SV5 encoders write whole 32-bit words only, so it can run up to 31 bits past the end of the data.";
    public const string Sv6LastFrame = "The last frame the header counts in version 6, which libmpcdec 1.2.6 decodes like the others, followed by 11 bits that say how many of its samples belong to the audio (mpc_decoder.c mpc_decoder_decode_internal).";
    public const string Sv6LastFrameCut = "The last frame the header counts in version 6, which libmpcdec 1.2.6 decodes like the others; the data ends before the frame or the 11 bits that should follow it do.";
    public const string Sv6FinalSamples = "The 11 bits after the last frame of version 6: how many of its 1,152 samples belong to the audio, 0 meaning all of them. libmpcdec 1.2.6 outputs them and then the synthesis filter's 481 samples of decay (\"reconstruct exact filelength\").";
    public const string Sv6PaddingWord = "A 32-bit word of zeros after a stream that ends on a word boundary. mppenc 1.16, the SV7 encoder that descends from the earlier ones, always writes the word it is filling at the end, even an empty one (encode_sv7.c FinishBitstream); decoders take nothing from it.";
    public const string OldStatFrames = "The frames decoders decode: those the header counts but the last (libmpcdec 1.2.6, mppdec).";
    public const string Sv6StatFrames = "The frames decoders decode: in version 6 all the header counts, the last one too (libmpcdec 1.2.6, mppdec).";
    public const string OldStatSamples = "The samples per channel the frames before the last give when libmpcdec 1.2.6 decodes them whole, less the synthesis filter's 481. A damaged frame stops the decoder, which then outputs fewer.";
    public const string Sv6StatSamples = "The samples per channel the counted frames give when libmpcdec 1.2.6 decodes them whole: 1,152 for each frame before the last and the samples the 11 bits after the last frame give; the 481 of the synthesis filter's delay dropped at the start are decoded at the end. A damaged frame stops the decoder, which then outputs fewer.";

    public const string UndetailedPackets = "Packets the analyzer no longer details, once it has detailed 4,096, that neither carry audio nor describe the stream, such as packets of unknown keys, kept as one node with their count; decoders skip packets they do not know (SV8 specification, Packet formatting).";
    public const string Sv8Magic = "The magic number \"MPCK\" (SV8 specification, File magic number). The seek table counts its offsets from here.";
    public const string Key = "The packet's key, two letters from A to Z, which says what the packet holds (SV8 specification, Packet formatting).";
    public const string Size = "The packet's length in bytes, key and this field included, in 1 to 9 bytes of 7 bits each, the high bit set on every byte but the last (SV8 specification, Packet formatting).";
    public const string Payload = "The packet's data after its key and size.";
    public const string Padding = "Bytes after the packet's last field, which the SV8 specification requires to be zero (\"All unused bits in a packet MUST be null\").";
    public const string UnknownPacket = "A packet whose key the SV8 specification does not reserve. libmpcdec r495 and ffmpeg skip packets they do not know.";

    public const string StreamHeader = "The stream header packet (SH): what a decoder needs to decode the stream, protected by a CRC. It is mandatory and comes before the first audio packet (SV8 specification, Stream Header Packet).";
    public const string HeaderCrc = "The CRC-32 of the rest of the packet's payload, the CRC of PNG (polynomial 0x04C11DB7, reflected); 0 is invalid (SV8 specification, Stream Header Packet). libmpcdec r495 refuses a stream whose header fails it.";
    public const string CrcCheck = "Whether the stored CRC equals the one computed over the bytes it covers.";
    public const string Sv8StreamVersion = "The bitstream version, 8 (SV8 specification, Stream Header Packet).";
    public const string SampleCount = "The number of samples per channel in the stream, beginning silence included; 0 means unknown (SV8 specification, Stream Header Packet).";
    public const string BeginningSilence = "The samples at the start that decoders drop, on top of the synthesis filter's 481 (SV8 specification; libmpcdec r495 skips both).";
    public const string MaxUsedBands = "How many of the 32 subbands the frames may use, 1 to 32, stored minus 1 (SV8 specification, Stream Header Packet).";
    public const string ChannelCount = "The number of channels, 1 to 16, stored minus 1 (SV8 specification). libmpcdec r495 decodes at most 2.";
    public const string Sv8MidSide = "Whether the frames may code subbands as mid and side rather than left and right (SV8 specification, \"MS used\").";
    public const string AudioBlockFrames = "The frames in each audio packet, as a power of 4: 4 to the value, 1 to 16,384 (SV8 specification, \"Audio block frames\"). The last packet may hold fewer.";

    public const string ReplayGainPacket = "The ReplayGain packet (RG): the loudness and peak of the track and album, stored in dB in Q8.8 format, 0 meaning not computed. It is mandatory and comes before the first audio packet (SV8 specification, Replaygain Packet).";
    public const string ReplayGainVersion = "The version of the ReplayGain values, 1; libmpcdec r495 ignores packets of other versions.";
    public const string TitleGain = "The track's loudness in dB (Q8.8), \"and not the gain that the player must apply\" (SV8 specification); mpcdec prints 64.82 dB minus it as the gain. 0 means not computed.";
    public const string TitlePeak = "The track's peak in dB above one step of a 16-bit sample (Q8.8), 20·log10 of the largest sample value on a 16-bit scale (SV8 specification, mpcgain). 0 means not computed.";
    public const string AlbumGain = "The album's loudness in dB (Q8.8), as Title Gain is the track's; 0 means not computed (SV8 specification).";
    public const string AlbumPeak = "The album's peak in dB above one step of a 16-bit sample (Q8.8); 0 means not computed (SV8 specification).";
    public const string PlayerGain = "The gain mpcdec derives from the stored loudness: 64.82 dB minus it (mpcdec r495 print_info).";

    public const string EncoderInfo = "The encoder info packet (EI): the profile, whether noise substitution was on, and the encoder's version (SV8 specification, Encoder Info Packet).";
    public const string Sv8Profile = "The quality profile, in 7 bits with 3 of them after the point (\"4.3 format\", 0 to 15.875): 10 is Standard, the encoder's quality setting is the profile minus 5 (SV8 specification, libmpcdec r495, mpcenc r495 writes quality plus 5).";
    public const string Pns = "Whether the encoder used perceptual noise substitution, which fills subbands with noise instead of coding them (SV8 specification, \"PNS tool\").";
    public const string Sv8EncoderVersion = "The encoder's version as major, minor and build (SV8 specification, Encoder Info Packet): mpcenc of musepack_src r495 writes 1.30.1.";
    public const string VersionMajor = "The encoder's major version (SV8 specification, Encoder Info Packet).";
    public const string VersionMinor = "The encoder's minor version: even for a stable release, odd for an unstable one (SV8 specification).";
    public const string VersionBuild = "The encoder's build number (SV8 specification). mpc2sv8 writes 0, with the SV7 encoder's version as major and minor.";

    public const string SeekTableOffset = "The seek table offset packet (SO): where the seek table packet starts, counted from the start of this packet. It comes before the first audio packet (SV8 specification, Seek Table Offset Packet).";
    public const string Offset = "The distance in bytes from the start of this packet to the seek table packet (SV8 specification).";
    public const string SeekTable = "The seek table packet (ST): the offsets of every 2^Seek Distance-th audio packet, so a player can jump to a time without reading the packets before it (SV8 specification, Seek Table Packet).";
    public const string SeekCount = "The number of entries in the table (SV8 specification).";
    public const string SeekDistance = "Every how many audio packets the table holds an entry, as a power of 2 (SV8 specification, \"Seek Distance\").";
    public const string SeekEntries = "The entries: the first two as sizes, the others as Golomb codes (M = 4096) of the change in the step from the entry before, each an offset from the magic number (SV8 specification, Seek Table Packet).";
    public const string SeekEntry = "An entry of the table: the offset of an audio packet from the magic number.";
    public const string AudioPacket = "An audio packet (AP): the number of frames the stream header gives, or fewer in the last packet; the first is a key frame, where decoding can start (SV8 specification, Audio Packet).";
    public const string AudioFrames = "The packet's frames, back to back without a length of their own, which the analyzer does not read: where one ends is known only by decoding it.";
    public const string Chapter = "A chapter-tag packet (CT): where a chapter starts, its loudness and peak, and an APEv2 tag of its own without the preamble (SV8 specification, Chapter-Tag Packet, [beta]).";
    public const string ChapterSample = "Where the chapter starts, in samples from the start of the stream (SV8 specification).";
    public const string ChapterGain = "The chapter's loudness in dB (Q8.8), 0 when not computed (SV8 specification).";
    public const string ChapterPeak = "The chapter's peak in dB above one step of a 16-bit sample (Q8.8), 0 when not computed (SV8 specification).";
    public const string ChapterTag = "The chapter's APEv2 tag without its \"APETAGEX\" preamble: a header of version, size, item count and flags, then the items (SV8 specification, Chapter-Tag Packet).";
    public const string ChapterTagField = "A field of the chapter tag's header (APEv2: version, size, item count, flags, reserved).";
    public const string ChapterItem = "An item of the chapter's tag: its value's size and flags, a key ending in a zero byte, and the value (APEv2).";
    public const string StreamEnd = "The stream end packet (SE): mandatory, the last packet of the stream, 3 bytes long; tags may follow it (SV8 specification, Stream End Packet).";

    public const string Encoder = "The encoder that wrote the stream, from its encoder version field, with the evidence and the confidence in it.";
    public const string Confidence = "How sure the analyzer is of the encoder: high where the encoder's own version field names a release that encodes confirmed, medium where it follows libmpcdec's reading of the field, low for a tag's claim alone.";
    public const string Evidence = "Something in the file that points to the encoder, and where it is.";

    public const string Statistics = "Figures computed over the stream's frames or audio packets.";
    public const string StatFrames = "The frames of 1,152 samples per channel that the stream codes.";
    public const string StatPackets = "The audio packets of the stream.";
    public const string CodedSamples = "The samples per channel the frames code: 1,152 per frame.";
    public const string DecoderDelay = "The samples a decoder drops at the start: the synthesis filter's delay of 481 samples (libmpcdec r495 MPC_DECODER_SYNTH_DELAY), plus the beginning silence of SV8.";
    public const string StatSamples = "The samples per channel decoders output: the coded samples less the decoder delay and the end of the last frame that is not audio (libmpcdec r495).";
    public const string StatDuration = "The duration of the output samples at the sample rate.";
    public const string AverageBitrate = "The bits of the frames or audio packets over the time their coded samples last.";
    public const string MinimumBitrate = "The bitrate of the smallest frame or packet over the time it codes.";
    public const string MaximumBitrate = "The bitrate of the largest frame or packet over the time it codes.";
    public const string Smallest = "The smallest frame or audio packet.";
    public const string Largest = "The largest frame or audio packet.";
    public const string BitrateMode = "Each frame is as long as its data, so the bitrate follows the audio (variable); only the headers of SV4 to SV6 have a bitrate field, which is set for a constant bitrate.";
    public const string Distribution = "How many frames or packets fall in each bitrate range of 32 kbps.";
    public const string DistributionBin = "The frames or packets whose bitrate falls in this range, and their share.";
    public const string MidSideUse = "The coded subbands of all frames, by how they are coded: mid and side, or left and right; subbands left out or filled with noise in both channels are not counted.";
    public const string MidSideCount = "Coded subbands with the mid/side flag set, and their share of the coded subbands.";
    public const string LeftRightCount = "Coded subbands without the mid/side flag, and their share of the coded subbands.";
    public const string FramesWithMidSide = "Frames that code at least one subband as mid and side.";
    public const string NoiseCount = "Subband channels filled with noise (resolution -1) across all frames.";

    public const string BitrateSeries = "The bitrate of each frame or audio packet: its bits over the time its samples last.";
    public const string BitrateOverTimeSeries = "The bitrate of each second of audio: the bits of the frames or packets that start in that second over the time their samples last.";
}
