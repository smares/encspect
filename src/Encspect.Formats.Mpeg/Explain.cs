namespace Encspect.Formats.Mpeg;

// The background articles the MPEG nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string FrameSync = "frame-sync";
    public const string FrameHeader = "frame-header";
    public const string GranulesAndFrames = "granules-and-frames";
    public const string MpegVersions = "mpeg-versions";
    public const string MpegLayers = "mpeg-layers";
    public const string Crc = "crc";
    public const string BitrateModes = "bitrate-modes";
    public const string Padding = "padding";
    public const string FreeFormat = "free-format";
    public const string ChannelModes = "channel-modes";
    public const string JointStereo = "joint-stereo";
    public const string MidSideStereo = "mid-side-stereo";
    public const string IntensityStereo = "intensity-stereo";
    public const string Emphasis = "emphasis";
    public const string FilterBank = "filter-bank";
    public const string Scalefactors = "scalefactors";
    public const string Scfsi = "scfsi";
    public const string ScalefacScaleAndPreflag = "scalefac-scale-and-preflag";
    public const string GlobalGain = "global-gain";
    public const string BlockSwitching = "block-switching";
    public const string HuffmanCoding = "huffman-coding";
    public const string BitReservoir = "bit-reservoir";
    public const string SubbandCoding = "subband-coding";
    public const string AncillaryData = "ancillary-data";
    public const string Multichannel = "mpeg-multichannel";
    public const string XingHeader = "xing-header";
    public const string VbriHeader = "vbri-header";
    public const string LameTag = "lame-tag";
    public const string GaplessPlayback = "gapless-playback";
    public const string ReplayGain = "replaygain";
    public const string Lowpass = "lowpass";
    public const string Ath = "ath";
    public const string NoiseShaping = "noise-shaping";
    public const string EncoderDetection = "encoder-detection";
    public const string Mp3Pro = "mp3pro";
    public const string Mp3Surround = "mp3-surround";
    public const string CodedBandwidth = "coded-bandwidth";
}

// What each MPEG node is, in the words the report shows next to it. Constants only: frame nodes
// are built by the hundred thousand, so a description picks one of these and never allocates.
internal static class Explain
{
    public const string Region = "A stretch of MPEG audio between tags: its frames, the gaps between them, and what the analyzer concludes about them.";
    public const string Frame = "One MPEG audio frame: a 4-byte header, an optional CRC, then the coded audio of 384 samples per channel in Layer I, 1152 in Layers II and III, or 576 in Layer III of MPEG-2 and 2.5.";
    public const string Gap = "Bytes between frames that are not a frame: data before the first frame, where sync was lost, or after the last frame.";

    public const string Header = "The 32-bit frame header: a sync word of set bits, then the fields that say how the frame is coded and how long it is (ISO/IEC 11172-3 2.4.1.3, 2.4.2.3).";
    public const string Version = "Which standard the frame follows: MPEG-1 (ISO/IEC 11172-3) at 32, 44.1 and 48 kHz, MPEG-2 (ISO/IEC 13818-3) at half those rates, or MPEG 2.5, Fraunhofer's extension, at a quarter.";
    public const string Layer = "The coding layer: Layer I is the simplest, Layer II (MP2) codes the bit allocation more finely, and Layer III (MP3) adds a transform, Huffman coding and the bit reservoir.";
    public const string IsProtected = "Whether a 16-bit CRC follows the header so decoders can detect damaged frames; the stored protection_bit is 0 when it does (ISO/IEC 11172-3 2.4.2.3).";
    public const string Bitrate = "The bitrate bitrate_index gives; with the sample rate and the padding bit it sets the frame's length (ISO/IEC 11172-3 2.4.2.3).";
    public const string FreeBitrate = "bitrate_index 0, free format: the stream has a fixed bitrate that the table does not list, so the frame's length comes from the distance to the next frame.";
    public const string SampleRate = "Samples per second in each channel, from sampling_frequency and the version (ISO/IEC 11172-3 2.4.2.3).";
    public const string HasPadding = "Whether the frame carries one extra slot (a byte, or 4 bytes in Layer I), which keeps the average bitrate exact where the frame length is not a whole number of slots, as at 44.1 kHz.";
    public const string IsPrivate = "A bit left to applications; the standard gives it no meaning and decoders ignore it.";
    public const string ChannelStereo = "Two channels, left and right, coded independently in every frame.";
    public const string ChannelJoint = "Two channels that may be coded together where Mode Extension says: mid/side or intensity stereo in Layer III, intensity stereo above a bound in Layers I and II.";
    public const string ChannelDual = "Two independent mono channels, such as two languages, coded separately and not meant as a stereo pair.";
    public const string ChannelMono = "A single channel.";
    public const string ModeExtensionUnused = "Selects the joint stereo tools of a frame. It has no meaning here, since the channel mode is not joint stereo.";
    public const string ModeExtensionLayer3 = "Which joint stereo tools this frame uses: mid/side stereo codes the sum and difference of the channels, intensity stereo codes high bands as one channel with a direction. (ISO/IEC 11172-3 2.4.2.3)";
    public const string ModeExtensionBound = "The subband from which on intensity stereo codes both channels as one signal with a scalefactor for each; the subbands below it are coded separately (ISO/IEC 11172-3 2.4.2.3).";
    public const string IsCopyrighted = "The copyright bit: whether the encoder marked the audio as copyrighted. Nothing enforces it.";
    public const string IsOriginal = "The original bit: set for an original, clear for a copy. Encoders set it as they are configured, so it says little about where the file came from.";
    public const string Emphasis = "Whether the audio was pre-emphasized before encoding and the decoder must de-emphasize it. 'None' is almost universal.";

    public const string Crc = "The CRC-16 the encoder stored over the last 16 bits of the header and the side information or bit allocation after it, so decoders can detect damage (ISO/IEC 11172-3 2.4.3.1).";
    public const string ComputedCrc = "The CRC the analyzer computed over the same bits. Where it differs from the stored one, those bits changed after encoding.";

    public const string SideInfo = "Layer III side information: where the frame's main data starts and, for each granule and channel, how its spectrum is coded (ISO/IEC 11172-3 2.4.1.7, 2.4.2.7).";
    public const string HeaderFrameSideInfo = "The zero side information of the VBR header frame: it declares no main data, so a decoder that does not know the header plays the frame as silence.";
    public const string MainDataBegin = "How many bytes before this frame's header its main data starts, in earlier frames' unused space, the bit reservoir. 0 means the frame does not borrow.";
    public const string ReservoirFill = "The free bytes in the bit reservoir when this frame starts: what earlier frames left unused and main_data_begin can reach. Computed, not stored, so it points at main_data_begin, which it bounds.";
    public const string PrivateBits = "Bits left to the encoder's own use; the standard gives them no meaning.";
    public const string Scfsi = "Scalefactor selection information, four bits per channel: for each of four groups of scalefactor bands, whether granule 1 reuses granule 0's scalefactors (ISO/IEC 11172-3 2.4.2.7).";
    public const string ScfsiChannel = "This channel's four scfsi bits, for scalefactor bands 0 to 5, 6 to 10, 11 to 15 and 16 to 20; a 1 means granule 1 sends no scalefactors there and reuses granule 0's.";
    public const string Granule = "One granule: 576 samples per channel, the unit Layer III codes. An MPEG-1 frame holds two, an MPEG-2 or 2.5 frame one.";
    public const string GranuleChannel = "How this channel's 576 frequency lines are coded in the granule.";
    public const string Part23Length = "part2_3_length: the bits of main data this granule and channel use, for its scalefactors (part 2) and its Huffman-coded spectrum (part 3).";
    public const string BigValues = "big_values: how many pairs of frequency lines, from the lowest, are coded in the big-values region, where values may exceed 1. At most 288 pairs fit the 576 lines.";
    public const string GlobalGain = "global_gain: the quantizer step size of the granule and channel. One step changes the decoded level by 1.5 dB, before the scalefactors adjust it band by band.";
    public const string ScalefacCompress = "scalefac_compress: how many bits the scalefactors take, as slen1 and slen2 in MPEG-1, and as four lengths with a partition of the bands in MPEG-2 and 2.5 (ISO/IEC 13818-3 2.4.3.2).";
    public const string BlockNormal = "The window and transform of the granule. Normal: one long transform over 576 lines, used for steady sound.";
    public const string BlockStart = "The window and transform of the granule. Start: a long transform whose window leads into short blocks in the next granule.";
    public const string BlockShort = "The window and transform of the granule. Short: three transforms of 192 lines each, which keep a sudden attack from spreading ahead of it as pre-echo.";
    public const string BlockStop = "The window and transform of the granule. Stop: a long transform whose window leads back from short blocks.";
    public const string BlockReserved = "The window and transform of the granule. Block type 0 with window switching, which ISO/IEC 11172-3 reserves.";
    public const string IsMixedBlock = "mixed_block_flag: whether a short block keeps the long transform for the two lowest of the 32 subbands, where long transforms resolve frequency better.";
    public const string TableSelect = "table_select: the Huffman table of each region of the big-values region, two regions with window switching and three without; table 0 codes a region of zeros.";
    public const string SubblockGain = "subblock_gain: for each of the three short windows, a further attenuation of 12 dB per step below the global gain. Decoders apply it where the block is short.";
    public const string Region0Count = "region0_count: how many scalefactor bands, minus one, the first region of the big-values region spans; each region has its own Huffman table.";
    public const string Region1Count = "region1_count: how many scalefactor bands, minus one, the second region of the big-values region spans; the third region takes the rest.";
    public const string HasPreflag = "preflag: whether the fixed amounts of the standard's preemphasis table are added to the scalefactors of bands 11 to 20, which then need fewer bits.";
    public const string HasCoarseScalefactors = "scalefac_scale: whether the scalefactors step by 6 dB instead of 3 dB, which reaches further with the same bits at a coarser resolution.";
    public const string Count1Table = "count1table_select: the Huffman table, A or B, for the count1 region, the quadruples of lines above the big values whose values are -1, 0 or 1.";

    public const string MainData = "This frame's main data: the scalefactors and Huffman-coded spectrum of its granules. Where main_data_begin is above 0, part of it lies in earlier frames.";
    public const string ReservoirBytes = "Bytes here that hold main data of a later frame, which reaches back into this frame through the bit reservoir.";
    public const string Layer3Ancillary = "Bytes that no frame's main data uses. Encoders may fill them with their own data, such as their name or an extension like mp3PRO; decoders skip them.";
    public const string Layer12Ancillary = "Bytes after the samples that the audio does not use, including the padding slot of a padded frame, which ISO/IEC 11172-3 counts as ancillary data and the statistics leave out. Encoders may fill them with their own data; decoders skip them.";
    public const string FrameData = "The frame's bytes after its header and CRC, which the analyzer does not break down further here.";
    public const string HeaderFrameUnused = "Bytes of the VBR header frame after the header, which hold no audio.";

    public const string Layer1Allocation = "Bit allocation: how many bits the samples of each subband take, per channel, with one value for both channels above the intensity stereo bound; 0 means the subband is not coded (ISO/IEC 11172-3 2.4.2.5).";
    public const string Layer2Allocation = "Bit allocation: for each subband and channel an index into the allocation table the value names, which the bitrate and sample rate select (ISO/IEC 11172-3 2.4.2.6, Annex B).";
    public const string Subband = "One of the 32 subbands of the polyphase filterbank, each an equal slice of the frequency range; subband 0 is the lowest.";
    public const string Layer1ChannelAllocation = "This channel's allocation in the subband: the bits per sample, or None when the subband is not coded. ISO/IEC 11172-3 forbids the value 15.";
    public const string Layer2ChannelAllocation = "This channel's allocation in the subband, as the number of quantization levels its samples use, or None when the subband is not coded.";
    public const string SharedAllocation = "Above the intensity stereo bound one allocation serves both channels, which share their samples and keep their own scalefactors.";
    public const string Layer1Scalefactors = "A 6-bit scalefactor for each coded subband and channel, the factor its samples are scaled by, in steps of 2 dB (ISO/IEC 11172-3 Table B.1).";
    public const string Layer1Scalefactor = "This channel's scalefactor index in the subband; each step up scales the samples down by 2 dB. Table B.1 leaves 63 undefined.";
    public const string Layer2Scfsi = "Scalefactor selection information for each coded subband and channel: how many scalefactors it sends for the frame's three parts of 12 samples (ISO/IEC 11172-3 2.4.2.6).";
    public const string Layer2ScfsiChannel = "0: three scalefactors, one for each part; 1: two, the first for parts 1 and 2; 2: one for all three; 3: two, the second for parts 2 and 3.";
    public const string Layer2Scalefactors = "The 6-bit scalefactors of each coded subband and channel, one to three as scfsi says, in steps of 2 dB (ISO/IEC 11172-3 Table B.1).";
    public const string Layer2Scalefactor = "This channel's scalefactor indexes in the subband, for the frame's three parts as scfsi groups them; each step up scales the samples down by 2 dB.";
    public const string Layer1Samples = "The quantized subband samples: 12 for each coded subband and channel, one set for both channels above the intensity stereo bound.";
    public const string Layer2Samples = "The quantized subband samples: 36 for each coded subband and channel in three parts of 12, one set for both channels above the intensity stereo bound.";

    public const string Multichannel = "The MPEG-2 multichannel extension in this MPEG-1 Layer II frame's ancillary data: the channels that, with the stereo base, make up the configuration in the value (ISO/IEC 13818-3).";
    public const string McHeader = "The multichannel header: which channels the extension adds and how the programme was mixed into the stereo base.";
    public const string McHasExtension = "ext_bit_stream_present: whether the multichannel data continues in an extension bitstream, a separate stream for what the base frames cannot hold.";
    public const string McAncillaryBytes = "n_ad_bytes: how many bytes at the end of the base frame remain MPEG-1 ancillary data instead of multichannel data.";
    public const string McCenter = "center: whether a center channel is coded. Phantom: its high subbands are left out and carried in the left and right channels.";
    public const string McSurround = "surround: which surround channels are coded: none, one, two, or a second stereo programme instead of surround.";
    public const string McHasLfe = "lfe: whether a low-frequency effects channel is coded.";
    public const string McAudioMix = "audio_mix: which kind of listening room, large or small, the programme was mixed for, as ISO/IEC 13818-3 defines the bit.";
    public const string McDematrix = "dematrix_procedure: which matrix mixed the added channels into the stereo base, so the decoder can undo it.";
    public const string McMultilingualChannels = "no_of_multi_lingual_ch: how many additional commentary or language channels the frames carry.";
    public const string McMultilingualRate = "multi_lingual_fs: whether the multilingual channels use the main sample rate or half of it.";
    public const string McMultilingualLayer = "multi_lingual_layer: the layer the multilingual channels are coded in.";
    public const string McCopyrightBit = "copyright_identification_bit: one bit of a copyright identifier that the frames carry a bit at a time.";
    public const string McCopyrightStart = "copyright_identification_start: whether the copyright identifier starts with this frame's bit.";
    public const string McCrc = "The multichannel CRC-16 over the multichannel header and the side information after it, which tells a real extension from random ancillary data.";
    public const string McDataContinued = "This frame's part of the multichannel data. Its side information continues in the extension bitstream, a separate file, so it is not read here.";
    public const string McStatus = "The composite status information: how the added channels share data with the base channels in this frame (ISO/IEC 13818-3).";
    public const string McHasDynamicCrosstalk = "dyn_cross_on: whether dynamic crosstalk is on, so some subband groups of a channel reuse the samples of another channel instead of carrying their own.";
    public const string McHasPrediction = "mc_prediction_on: whether the added channels are predicted from the base channels, so only the difference is coded.";
    public const string McChannelAllocation = "tc_allocation: which channels the transmission channels after the base pair carry, for all 12 subband groups at once or for each.";
    public const string McFromRight = "dyn_cross_LR: whether reused subbands come from the right base channel instead of the left.";
    public const string McDynamicModes = "dyn_cross_mode of each subband group: which channels share their samples with which.";
    public const string McPrediction = "mc_prediction of each subband group: whether prediction is used in it.";
    public const string McLfeAllocation = "The bit allocation of the LFE channel, which has one subband, or None.";
    public const string McAllocation = "The bit allocation of the subbands the added channels transmit; others are copied or left out as the status information says.";
    public const string McChannelLevels = "This transmission channel's allocation in the subband, as quantization levels, or None.";
    public const string McScfsi = "Scalefactor selection information of the added channels' coded subbands, read as in Layer II.";
    public const string McScfsiChannel = "How many scalefactors the subband sends for the frame's three parts: 0 three, 1 and 3 two, 2 one.";
    public const string McData = "The rest of the multichannel data: the added channels' scalefactors and samples, and any multilingual channels.";

    public const string Xing = "The Xing header: frame and byte counts and a seek table for the stream after it. LAME writes 'Info' instead of 'Xing' for a CBR stream.";
    public const string XingFlags = "Which optional fields follow, one bit each.";
    public const string HasFrameCount = "Whether the header holds the stream's frame count.";
    public const string HasByteCount = "Whether the header holds the stream's byte count.";
    public const string HasToc = "Whether the header holds a table of contents of 100 entries for seeking.";
    public const string HasQuality = "Whether the header holds a quality indicator.";
    public const string FrameCount = "The stream's frames by the encoder's count, from which players take the duration without reading every frame. LAME leaves out the header's own frame, Xing's encoder counts it.";
    public const string ByteCount = "The stream's bytes by the encoder's count.";
    public const string Toc = "The table of contents for seeking: entry i gives where i percent of the playing time is reached, in 256ths of the byte count.";
    public const string TocEntry = "Where this percentage of the playing time is reached, in 256ths of the byte count.";
    public const string Quality = "A quality indicator the encoder chose; what its number means depends on the encoder.";
    public const string GogoQuality = "Gogo writes \"GOGO\" here instead of a quality indicator.";
    public const string EncoderString = "Text after the Xing header that does not start a LAME tag, such as the version string of LAME before 3.90.";

    public const string LameTag = "The LAME tag (Mp3 Info Tag revision 1): the encoder's version and settings, gapless delay and padding, ReplayGain and two CRCs (LAME tag specification, Gabriel Bouvigne).";
    public const string LameEncoder = "The encoder's short version string, nine bytes.";
    public const string Revision = "The revision of the tag's layout, the high four bits of this byte.";
    public const string VbrMethod = "The bitrate mode the encoder used: CBR, ABR or one of LAME's VBR methods.";
    public const string Lowpass = "The frequency above which the encoder filtered out the audio before encoding, in steps of 100 Hz.";
    public const string Peak = "The largest sample value found while encoding, as a fraction of full scale, where 1.0 is the largest a sample can be without clipping.";
    public const string RadioGain = "The track gain: by how much a player changes the level to play this track at the ReplayGain reference loudness.";
    public const string AudiophileGain = "The album gain: by how much a player changes the level to play the album at the reference loudness, keeping the differences between its tracks.";
    public const string GainName = "Which gain the field holds: radio (track) or audiophile (album).";
    public const string GainOriginator = "Who set the gain: the artist, the user, or the encoder from its own measurement.";
    public const string GainReference = "The loudness the gain was computed against: LAME before 3.95 used 83 dB, later versions 89 dB.";
    public const string EncodingFlags = "Four flags of the encoding: two noise shaping options and the place of the file in a gapless sequence.";
    public const string UsesNsPsytune = "Whether LAME's --nspsytune psychoacoustic tuning was on.";
    public const string UsesNsSafeJoint = "Whether LAME's --nssafejoint option was on.";
    public const string IsNoGapContinued = "Whether the next file of a gapless (--nogap) sequence continues this one's audio.";
    public const string IsNoGapContinuation = "Whether this file continues the audio of the previous file of a gapless (--nogap) sequence.";
    public const string AthType = "Which curve of the absolute threshold of hearing LAME's psychoacoustic model used (its --athtype).";
    public const string LameBitrate = "The bitrate the encoder was set to in CBR; 255 means 255 kbps or more.";
    public const string LameAverageBitrate = "The average bitrate the encoder aimed at in ABR; 255 means 255 kbps or more.";
    public const string LameMinimumBitrate = "The lowest bitrate the encoder was allowed in VBR; 255 means 255 kbps or more.";
    public const string EncoderDelay = "Samples the encoder added before the audio, which a gapless player drops.";
    public const string EncoderPadding = "Samples the encoder added after the audio to fill the last frame, which a gapless player drops.";
    public const string NoiseShaping = "The noise shaping mode of LAME's quantization, 0 to 3.";
    public const string StereoMode = "The stereo mode the encoder was set to, which the frame headers do not show, such as forced mid/side or auto.";
    public const string HasUnwiseSettings = "The LAME tag's 'unwise settings used' bit.";
    public const string SourceSampleRate = "The sample rate of the audio the encoder was given, before any resampling.";
    public const string Mp3Gain = "A level change applied to every frame's global_gain after encoding, in steps of 1.5 dB, as MP3Gain records it.";
    public const string Surround = "The surround encoding of the source: none, Dolby Pro Logic, Pro Logic II or Ambisonic.";
    public const string Preset = "The LAME preset used, such as V2 or a bitrate; None when no preset was named.";
    public const string SettingsMatch = "LAME 3.90 to 3.92 leave out the preset, so this names the preset whose settings this tag holds.";
    public const string MusicLength = "Bytes from the start of this frame to the end of the audio, which the music CRC covers.";
    public const string MusicCrc = "A CRC-16 of the audio frames after this one, up to the music length. Where it no longer matches, the audio changed after encoding.";
    public const string TagCrc = "A CRC-16 of the frame's bytes before this field, which shows whether the tag itself is intact.";
    public const string ComputedTagCrc = "The CRC the analyzer computed over the same bytes.";

    public const string Vbri = "Fraunhofer's VBRI header: byte and frame counts and a seek table, 36 bytes into the frame.";
    public const string VbriVersion = "The header's version; only 1 is known.";
    public const string VbriDelay = "The delay field Fraunhofer's encoder stores.";
    public const string VbriQuality = "The quality setting Fraunhofer's encoder stores.";
    public const string VbriTocEntries = "How many entries the seek table has.";
    public const string VbriTocScale = "The factor each seek table entry is multiplied by to give bytes.";
    public const string VbriEntrySize = "Bytes per seek table entry, 1 to 4.";
    public const string VbriFramesPerEntry = "How many frames each seek table entry spans.";
    public const string VbriToc = "The seek table: the bytes each stretch of frames takes, in order.";
    public const string VbriTocEntry = "The bytes one stretch of frames takes, already multiplied by the scale.";

    public const string Statistics = "Figures the analyzer computed over the region's complete audio frames, without the VBR header frame and a truncated last frame.";
    public const string StatFrames = "How many complete audio frames the region has.";
    public const string StatSamples = "Samples per channel in those frames.";
    public const string StatDuration = "The playing time of those frames, before any gapless trimming.";
    public const string IsVbr = "Whether the frames use more than one bitrate. A VBR header alone does not make a file VBR.";
    public const string AverageBitrate = "The bits of those frames divided by their playing time.";
    public const string MinimumBitrate = "The lowest bitrate a frame header gives.";
    public const string MaximumBitrate = "The highest bitrate a frame header gives.";
    public const string Distribution = "How many frames use each bitrate.";
    public const string DistributionEntry = "The frames at this bitrate, with their share of all frames.";
    public const string Unpadded = "The frames at this bitrate without a padding slot, by length.";
    public const string Padded = "The frames at this bitrate with a padding slot, by length.";
    public const string MaximumFill = "The largest bit reservoir fill any frame starts with.";
    public const string HeaderCounts = "The counts the VBR header claims, next to those the analyzer counted in the frames it describes.";
    public const string ClaimedFrames = "The frame count the header gives.";
    public const string CountedFrames = "The frames the analyzer counted after the header.";
    public const string ClaimedBytes = "The byte count the header gives.";
    public const string CountedBytes = "The bytes the analyzer counted, the header's frame included.";
    public const string Gapless = "What the LAME tag's delay and padding give for gapless playback of the frames it describes.";
    public const string GaplessSamples = "Samples per channel left once the delay and padding are dropped, which a gapless player outputs.";
    public const string GaplessDuration = "The playing time of those samples.";
    public const string ChannelModes = "How many frames use each channel mode.";
    public const string ChannelModeCount = "The frames in this channel mode, with their share.";
    public const string JointStereoModes = "Of the joint stereo frames, how many use each Layer III stereo coding.";
    public const string JointStereoMode = "The joint stereo frames coded this way, with their share.";
    public const string IntensityBounds = "Of the joint stereo frames, how many start intensity stereo at each subband (Layers I and II).";
    public const string IntensityBound = "The joint stereo frames with this intensity stereo bound, with their share.";
    public const string BlockUsage = "Granules by block type, counted for each channel as mp3guessenc and EncSpot count them.";
    public const string BlockKind = "The granules of this block type, with their share.";
    public const string BlockKindMixed = "The granules of short blocks with mixed_block_flag set, with their share.";

    public const string StereoCoding = "How the complete frames code their channels, counted over all of them whatever their channel mode: left/right stereo, the joint stereo tools (mid/side stereo and intensity stereo in Layer III, intensity stereo above a bound in Layers I and II), dual channel and mono (ISO/IEC 11172-3 2.4.2.3). EncSpot counts left/right stereo, dual channel and mono together as its mode 0. The coding is what the header selects: a Layer II frame whose intensity stereo bound lies at or above its highest coded subband has no band in intensity stereo and still counts as intensity stereo.";
    public const string CodingLeftRight = "The channels are coded on their own: a stereo frame, or a joint stereo frame whose mode_extension selects no joint stereo tool (ISO/IEC 11172-3 2.4.2.3).";
    public const string CodingMidSide = "A Layer III joint stereo frame that codes the sum and the difference of the channels: mode_extension bit 1 set and bit 0 clear (ISO/IEC 11172-3 2.4.2.3).";
    public const string CodingIntensity = "A joint stereo frame that codes high frequencies once for both channels: Layer III with mode_extension bit 0 set and bit 1 clear, Layers I and II above the subband mode_extension gives (ISO/IEC 11172-3 2.4.2.3).";
    public const string CodingMidSideAndIntensity = "A Layer III joint stereo frame with both mode_extension bits set: mid/side stereo in the bands below the intensity stereo region, intensity stereo above it (ISO/IEC 11172-3 2.4.2.3, 2.4.3.4).";
    public const string CodingDual = "Two independent channels, such as two languages, coded separately; no joint stereo tool applies (ISO/IEC 11172-3 2.4.2.3).";
    public const string CodingMono = "A single channel (ISO/IEC 11172-3 2.4.2.3).";

    public const string JointLeftRight = "The joint stereo frames whose mode_extension selects no tool, coded as left and right, with their share.";
    public const string JointMidSide = "The joint stereo frames coded as mid and side (mode_extension bit 1 only), with their share.";
    public const string JointIntensity = "The joint stereo frames with intensity stereo (mode_extension bit 0 only), with their share.";
    public const string JointMidSideAndIntensity = "The joint stereo frames with both tools (both mode_extension bits), with their share.";

    public const string BlockSizes = "Granules by the size of their transform, counted for each channel and grouped as EncSpot counts them: long (with start and stop blocks), short and mixed, and the reserved block type apart, which EncSpot counts as long.";
    public const string SizeLong = "Granules with a long transform: normal blocks and the start and stop blocks, whose long transforms use the windows that lead into and out of short blocks (ISO/IEC 11172-3 2.4.2.7), with their share.";
    public const string SizeShort = "The granules of short blocks without mixed_block_flag: three short transforms in every subband (ISO/IEC 11172-3 2.4.2.7), with their share.";
    public const string SizeMixed = "The granules of short blocks with mixed_block_flag set: long transforms in the two lowest subbands, short ones above (ISO/IEC 11172-3 2.4.2.7), with their share.";
    public const string SizeReserved = "The granules with window_switching_flag set and block_type 0, which ISO/IEC 11172-3 2.4.2.7 reserves, with their share.";

    public const string CodedBandwidth = "The upper edge of the highest subband with a bit allocation other than 0 in either channel, read from each Layer I or II frame without decoding (ISO/IEC 11172-3 2.4.2.5, 2.4.2.6). It is a bound the coded data states, not the encoder's lowpass setting and not a measurement of decoded audio.";
    public const string BandwidthLowest = "The lowest coded bandwidth of any frame.";
    public const string BandwidthMostCommon = "The coded bandwidth most frames have; the lowest of them where several are equally common.";
    public const string BandwidthHighest = "The highest coded bandwidth of any frame.";
    public const string ChannelBitShare = "How the audio bits of stereo, joint stereo and dual channel frames divide between the two channels, as the first channel's share. Layer III counts part2_3_length of every granule and channel; Layers I and II count each channel's allocation, scalefactor selection information (scfsi), scalefactors and samples, leaving out the allocation and samples above the intensity stereo bound that both channels share. The first channel is left in left/right stereo, dual channel and intensity stereo frames and mid in mid/side frames (in a frame with both tools, only below the intensity stereo region), so a stream that switches between them mixes the two.";
    public const string ShareLowest = "The smallest first channel share of any frame.";
    public const string ShareOverall = "The first channel's bits over both channels' bits in all frames.";
    public const string ShareHighest = "The largest first channel share of any frame.";
    public const string GainRange = "The smallest and largest global_gain of each channel.";
    public const string GainChannel = "This channel's range of global_gain.";
    public const string GainMinimum = "The smallest global_gain of the channel.";
    public const string GainMaximum = "The largest global_gain of the channel.";
    public const string AncillaryTotal = "Bytes of ancillary data in the frames, as mp3guessenc counts them.";
    public const string AncillaryShare = "Their share of the frames' bytes.";
    public const string AncillaryRate = "Ancillary bits per second of audio.";
    public const string AncillarySmallest = "The least ancillary data of a frame that has any.";
    public const string AncillaryLargest = "The most ancillary data of any frame.";
    public const string UsesPadding = "Whether any frame carries a padding slot.";
    public const string UsesScfsi = "Whether any frame reuses granule 0's scalefactors through scfsi.";
    public const string UsesScaling = "Whether any granule sets scalefac_scale.";
    public const string ScalingShare = "The share of granules, counted per channel, with scalefac_scale set (EncSpot's 'Scalefac').";
    public const string AverageMainDataBegin = "The mean main_data_begin, how far back frames start their main data (EncSpot's 'Av. Reservoir', which truncates it to whole bytes).";
    public const string MaximumMainDataBegin = "The largest main_data_begin of any frame (EncSpot's 'Max. Reservoir', mp3guessenc's 'Max reservoir').";

    public const string Encoder = "Which program encoded these frames, as the analyzer concludes from the evidence below.";
    public const string Confidence = "How sure the naming is: high when the encoder wrote its name into a header whose checks hold, medium for weaker but specific evidence, low when only habits or text other software may have written point to it.";
    public const string Evidence = "What points to the encoder, at the bytes it was found in.";
    public const string Enhancement = "An extension carried in the ancillary data of every frame, found by its signature.";
    public const string EnhancementBytes = "The ancillary bytes that carry the extension.";
    public const string EnhancementRate = "The data rate of those bytes.";
    public const string Ofl = "Fraunhofer's Original File Length block: the encoder's delay and the length of the audio it was given, in samples.";
    public const string OflDelay = "Samples before the audio in a decoder's output, the decoder's own delay included.";
    public const string OflLength = "The length of the audio the encoder was given, in samples.";
    public const string OflRate = "The sample rate these counts use; for mp3PRO twice the core's, the rate its decoder outputs.";
    public const string OflDuration = "The original length as playing time.";
}

// What each series of a stream shows, for the graphs.
internal static class SeriesText
{
    public const string Bitrate = "The bitrate of each frame, as its header gives it; a free-format frame's from its length.";
    public const string BitrateOverTime = "The bitrate of each second of audio: the bits of the frames that start in it over their playing time.";
    public const string ReservoirFill = "The free bytes in the bit reservoir as each frame starts, which the frame may borrow for its main data.";
    public const string ReservoirFillOverTime = "The mean bit reservoir fill of the frames that start in each second of audio.";
    public const string CodedBandwidth = "The coded bandwidth of each Layer I or II frame: the upper edge of the highest subband with a bit allocation other than 0 in either channel, in whole hertz (ISO/IEC 11172-3 2.4.2.5, 2.4.2.6). It is the bound the frame's own allocation states, read without decoding; a subband below it may still be quiet, and it is not the encoder's lowpass setting.";
    public const string CodedBandwidthOverTime = "The highest coded bandwidth of the frames that start in each second of audio.";
    public const string ChannelBitShare = "The first channel's share of the audio bits of each stereo, joint stereo or dual channel frame, in percent; the second channel has the rest. Layer III counts part2_3_length of every granule and channel, the scalefactors and the Huffman-coded values (ISO/IEC 11172-3 2.4.2.7). Layers I and II count each channel's allocation, scalefactor selection information (scfsi), scalefactors and samples; above the intensity stereo bound both channels share the allocation and the samples, and those bits count toward neither channel nor the total. The first channel is left in left/right stereo, dual channel and intensity stereo frames and mid in mid/side frames (in a frame with both tools, only below the intensity stereo region).";
    public const string ChannelBitShareOverTime = "The first channel's share of the audio bits of the frames that start in each second of audio, bits over bits, in percent; seconds without a stereo frame have no point. A stream that switches between left/right and mid/side frames mixes the two in one share.";

    public const string ChannelModesSeries = "The channel mode of each complete audio frame (ISO/IEC 11172-3 2.4.2.3).";
    public const string StereoCodingSeries = "How each complete audio frame codes its channels, whatever its channel mode: left/right stereo, mid/side stereo, intensity stereo, both tools, dual channel or mono (ISO/IEC 11172-3 2.4.2.3). A joint stereo frame of Layer I or II counts as intensity stereo, which is all its mode_extension selects, even where a Layer II bound lies at or above the highest coded subband.";
    public const string JointStereoModesSeries = "The Layer III stereo coding of each joint stereo frame, which its mode_extension selects (ISO/IEC 11172-3 2.4.2.3); frames of the other channel modes have no value.";
    public const string IntensityStereoBoundsSeries = "The subband from which each joint stereo frame of Layer I or II codes intensity stereo, which its mode_extension selects (ISO/IEC 11172-3 2.4.2.3); frames of the other channel modes have no value.";
    public const string BlockUsageSeries = "The block type of each granule and channel of every complete Layer III audio frame, counted per channel as the Block Usage statistics count them (ISO/IEC 11172-3 2.4.2.7); the second channel of a mono frame has no value.";
    public const string BlockSizesSeries = "The granules of Block Usage grouped by the size of their transform, as EncSpot counts them: long with start and stop blocks, short, mixed, and the reserved block type apart.";
}
