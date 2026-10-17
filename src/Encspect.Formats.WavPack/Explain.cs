namespace Encspect.Formats.WavPack;

// The background articles the WavPack nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string Blocks = "wavpack-blocks";
    public const string SubBlocks = "wavpack-sub-blocks";
    public const string Hybrid = "wavpack-hybrid-mode";
    public const string Decorrelation = "wavpack-decorrelation";
    public const string Dsd = "wavpack-dsd";
    public const string FloatAndLargeIntegers = "wavpack-float-and-large-integers";
    public const string Wrapper = "wavpack-wrapper";
    public const string Channels = "channel-layouts";
    public const string MidSideStereo = "mid-side-stereo";
    public const string Crc = "crc";
    public const string EncoderDetection = "encoder-detection";
}

// What each WavPack node is, in the words the report shows next to it. Constants only: the blocks
// of a long file are built by the hundred thousand, so a description picks one of these and never
// allocates. The sources: the WavPack 4 and 5 binary file/block format document (David Bryant,
// April 2020, doc/WavPack5FileFormat.pdf of wavpack-5.6.0), and libwavpack 5.6.0's source:
// include/wavpack.h, src/wavpack_local.h, open_utils.c, decorr_utils.c, entropy_utils.c,
// write_words.c, pack.c, pack_utils.c, unpack_dsd.c and common_utils.c.
internal static class Explain
{
    public const string Stream = "The blocks of a WavPack file, one after another: each has a 32-byte header and metadata sub-blocks, the last of which is normally the compressed audio (WavPack 4 and 5 binary file/block format, section 1.0).";
    public const string Block = "One block: a header and the sub-blocks that follow it up to the size the header states. Every block decodes by itself to mono or stereo audio; a multichannel file repeats a set of blocks, one for each stereo pair or single channel (format document, sections 1.0 to 3.0).";
    public const string MetadataBlock = "A block that counts no sample: it carries metadata only, such as a wrapper or a checksum of the audio, and no audio (format document, section 2.0, \"block_samples\").";
    public const string AfterEmbeddedTag = "Bytes after an ID3v2 tag that lies between the WavPack blocks, which the tag breaks; the analyzer does not read them as blocks.";
    public const string SkippedData = "Bytes between or around blocks that hold no block header; libwavpack's reader skips such bytes up to the next header (read_next_header in open_utils.c).";
    public const string CutHeader = "The start of a block header that the data ends inside.";
    public const string UndetailedBlocks = "Blocks that count no sample and hold nothing but their header, which the report no longer lists one by one once it has detailed 4,096 blocks. They are read and checked in every case.";
    public const string UndetailedSubBlocks = "The block's sub-blocks, which the report lists only for the first 4,096 blocks. They are read and checked in every block.";
    public const string FurtherSubBlocks = "More sub-blocks, which the report does not list one by one.";
    public const string TrailingBytes = "A byte after the last sub-block that holds none; a sub-block takes at least two bytes, and libwavpack refuses a block that does not end with one.";

    public const string BlockHeader = "The 32-byte header every block starts with, in the main file and in the correction file (format document, section 2.0).";
    public const string Signature = "The characters \"wvpk\" (format document, section 2.0, ckID).";
    public const string BlockSize = "The size of the whole block in bytes. The field stores it less 8, the size of the signature and itself; libwavpack takes an even size from 24 up to under 1 MiB (format document, section 2.0, ckSize; read_next_header).";
    public const string StreamVersion = "The stream version: libwavpack 5.6.0 decodes 0x402 to 0x410. It writes 0x407, which its source calls compatible with all decoders back to 4.0, when asked for a compatible stream, and otherwise 0x410, which adds the mono optimization: a stereo block whose channels are identical is coded as mono (format document, section 2.0; wavpack.h MIN_STREAM_VERS and MAX_STREAM_VERS; wavpack_local.h CUR_STREAM_VERS; pack_utils.c; pack.c).";
    public const string TotalSamples = "The number of samples (frames of all channels) in the whole file, 40 bits in two fields; valid only in the first block, whose index is 0 (format document, section 2.0, total_samples).";
    public const string TotalSamplesUnknown = "The low 32 bits are all ones, which means the length is unknown, as when an encoder cannot seek back to fill it in; libwavpack then finds the length from the last block (format document, section 2.0).";
    public const string BlockIndex = "The index of the block's first sample counted from the start of the file, 40 bits in two fields; 0 in the first block (format document, section 2.0, block_index).";
    public const string BlockSamples = "The number of samples in the block, all channels of the block counting as one (format document, section 2.0, block_samples). Blocks hold up to 131,072.";
    public const string BlockSamplesZero = "0 marks a block with no audio, one that carries metadata only (format document, section 2.0, block_samples).";
    public const string BlockCrc = "A CRC of the block's decoded audio, which only decoding can check. Block checksums, which cover the block's bytes, are the checksum sub-blocks (format document, section 2.0, crc).";
    public const string Flags = "The flags that tell how to decode the block and some general facts about it: sample size and format, hybrid or lossless, mono or stereo and the sample rate (format document, section 2.0, flags).";

    public const string BytesPerSample = "How many bytes each sample takes in the decoded data, 1 to 4, stored less 1 in two bits. With the left shift, it gives the sample's bit depth (format document, section 2.0).";
    public const string IsMono = "Whether the block holds one channel instead of two (format document, section 2.0, bit 2).";
    public const string IsHybrid = "Whether the block is coded in hybrid mode, which is lossy unless a correction file adds what was left out (format document, section 2.0, bit 3).";
    public const string UsesJointStereo = "Whether the two channels are coded as mid and side (their average and difference) instead of left and right (format document, section 2.0, bit 4; the encoder's joint stereo in pack.c).";
    public const string UsesCrossChannelDecorrelation = "Whether the decorrelation passes may predict one channel from the other with no delay (format document, section 2.0, bit 5).";
    public const string UsesHybridNoiseShaping = "Whether the noise the hybrid mode leaves out is shaped in frequency instead of being flat (format document, section 2.0, bit 6).";
    public const string IsFloatingPoint = "Whether the samples are 32-bit floating point numbers (format document, section 2.0, bit 7).";
    public const string HasExtendedIntegers = "Whether the samples are integers of more than 24 bits, or integers that need a shift after decoding, so an Int32 Info sub-block says how they are coded (format document, section 2.0, bit 8).";
    public const string UsesHybridBitrateControl = "Whether the hybrid mode's parameters control the bitrate, which is the only mode WavPack uses; the other would control the noise level (format document, section 2.0, bit 9).";
    public const string UsesHybridBalance = "Whether the hybrid mode's noise is balanced between the two channels (format document, section 2.0, bit 10).";
    public const string IsInitialBlock = "Whether this is the first block of a set of blocks that hold the channels of one stretch of audio; a mono or stereo file has sets of one block, which is both initial and final (format document, section 1.0 and 2.0, bit 11).";
    public const string IsFinalBlock = "Whether this is the last block of its set (format document, section 2.0, bit 12).";
    public const string LeftShift = "How many places the decoded samples are shifted left to the sample size, which restores bits the encoder found to be always 0 (format document, section 2.0, bits 13 to 17).";
    public const string MaxMagnitude = "The number of bits integers in the block need, less 1 (format document, section 2.0, bits 18 to 22).";
    public const string SampleRateIndex = "The sample rate by index: 6000, 8000, 9600, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000 or 192000 Hz (format document, section 2.0, bits 23 to 26; common_utils.c sample_rates).";
    public const string SampleRateCustom = "Index 15 stands for a rate that is not one of the 15 standard ones; a Sample Rate sub-block gives it (format document, section 2.0, bits 23 to 26).";
    public const string ReservedFlag = "Bit 27, reserved; decoders ignore it (format document, section 2.0; wavpack.h IGNORED_FLAGS).";
    public const string HasBlockChecksum = "Whether the block ends with a checksum sub-block, added with version 5.0 (format document, section 2.0, bit 28).";
    public const string UsesIirNoiseShaping = "Whether a filter with feedback shapes the noise when the shaping weight is negative (format document, section 2.0, bit 29; wavpack.h NEW_SHAPING).";
    public const string IsFalseStereo = "Whether the block is stereo but its data is mono: both channels are the same, so the data is coded once (format document, section 2.0, bit 30).";
    public const string IsDsd = "Whether the audio is DSD, 1-bit samples in bytes of 8, instead of PCM, added with version 5.0 (format document, section 2.0, bit 31).";

    public const string SubBlock0 = "A sub-block: an ID byte, a size and data padded to an even length (format document, section 3.0).";
    public const string SubBlockId = "The sub-block's ID byte: a function number in the low six bits, bit 5 (0x20) among them marking a sub-block a decoder may skip, whether its data is one byte shorter than its size says (0x40) and whether its size takes three bytes (0x80) (format document, section 3.0).";
    public const string SubBlockFunction = "What the sub-block holds, by the number in the low six bits of the ID (format document, section 3.0; open_utils.c ID_UNIQUE). A number from 0x20 up is that of an optional sub-block, which decoders may skip.";
    public const string IsOptional = "Whether a decoder that does not know the sub-block may skip it (ID 0x20 in section 3.0). libwavpack refuses a block with an unknown sub-block that is not optional.";
    public const string HasOddSize = "Whether the data is one byte shorter than the size says, the last byte being padding (ID 0x40 in section 3.0).";
    public const string IsLarge = "Whether the size takes three bytes instead of one, for a sub-block of more than 510 bytes (ID 0x80 in section 3.0).";
    public const string SubBlockSize = "The size of the data in words of two bytes, in one byte, or in three (little-endian) for a large sub-block (format document, section 3.0).";
    public const string SubBlockData = "The sub-block's data.";
    public const string SubBlockPad = "The byte that pads data of an odd length to an even one.";

    private const string Dummy = "Bytes that pad a block; a decoder ignores them (ID_DUMMY, format document, section 3.0).";
    private const string EncoderInfo = "ID 0x01 is named ID_ENCODER_INFO in wavpack.h, but libwavpack 5.6.0 neither writes nor reads it, and refuses a block that holds it as one with an unknown sub-block that is not optional (open_utils.c process_metadata).";
    private const string DecorrTerms = "The decorrelation passes the block uses, one byte each: the term in the low five bits (less 5) and the delta in the high three. The passes are the encoder's order, and a decoder runs them in reverse (ID_DECORR_TERMS; decorr_utils.c read_decorr_terms).";
    private const string DecorrWeights = "The initial weight of each pass for each channel, rounded to a signed byte; passes after the last non-zero weight are left out (ID_DECORR_WEIGHTS; pack.c write_decorr_weights). A stereo block stores the first channel's weight and then the second's: left and right, or side and mid with joint stereo.";
    private const string DecorrSamples = "The history samples the first passes start from, as signed 16-bit logarithms; the encoder sends them for the first pass only (ID_DECORR_SAMPLES; pack.c write_decorr_samples).";
    private const string EntropyVars = "The medians the entropy coder starts from, three for each channel, as 16-bit logarithms (ID_ENTROPY_VARS; write_words.c write_entropy_vars). A stereo block stores the first channel's and then the second's: left and right, or side and mid with joint stereo (pack.c).";
    private const string HybridProfile = "The hybrid mode's starting state: the slow level of each channel, the bitrate accumulator that sets the target bitrate and, if it changes, its delta (ID_HYBRID_PROFILE; write_words.c write_hybrid_profile). A stereo block stores the first channel's values before the second's: left and right, or side and mid with joint stereo.";
    private const string ShapingWeights = "What a decoder needs to rebuild the lossless audio of a hybrid file from its correction file: the noise shaping weights, or the error and accumulator of each channel (ID_SHAPING_WEIGHTS; decorr_utils.c read_shaping_info). A stereo block stores left and then right, also with joint stereo, since the noise is shaped before the channels are converted to side and mid (pack.c).";
    private const string FloatInfo = "How the 32-bit floating point samples are coded: which parts of them a decoder must restore, and the exponents (ID_FLOAT_INFO; open_utils.c read_float_info).";
    private const string Int32Info = "How integers of more than 24 bits, or integers that need a shift after decoding, are coded: how many bits are sent, and how many are zeros, ones or copies (ID_INT32_INFO; open_utils.c read_int32_info).";
    private const string WvBitstream = "The compressed audio of the main file: the residuals of the decorrelation passes, entropy coded (ID_WV_BITSTREAM; format document, section 3.0).";
    private const string WvcBitstream = "The compressed correction data of a hybrid file, which restores what the main file left out (ID_WVC_BITSTREAM; format document, section 3.0).";
    private const string WvxBitstream = "The extra bits of 32-bit floating point or integers of more than 24 bits, with a CRC of their own, in the main file (lossless) or the correction file (hybrid) (ID_WVX_BITSTREAM; open_utils.c init_wvx_bitstream).";
    private const string ChannelInfo = "The number of channels and the channel mask, which says which speakers they feed, in the order of the bits of the WAVE channel mask (ID_CHANNEL_INFO; open_utils.c read_channel_info).";
    private const string DsdBlock = "The compressed DSD audio: the rate multiplier as a power of two, the coding mode and the coded data (ID_DSD_BLOCK; unpack_dsd.c init_dsd_block).";
    private const string RiffHeader = "The header of the source WAVE file, kept so that decoding can write the file back (ID_RIFF_HEADER; format document, section 3.0).";
    private const string RiffTrailer = "The chunks after the audio in the source WAVE file (ID_RIFF_TRAILER; format document, section 3.0).";
    private const string AltHeader = "The header of a source file that is not a WAVE file, such as AIFF, CAF, DSF or DSDIFF, added with version 5.0 (ID_ALT_HEADER; format document, section 3.0).";
    private const string AltTrailer = "The bytes after the audio in a source file that is not a WAVE file, added with version 5.0 (ID_ALT_TRAILER; format document, section 3.0).";
    private const string ConfigBlock = "Some of the encoder's settings, for information only: the flags of its configuration from bit 8 up and, with extra processing, its level (ID_CONFIG_BLOCK; pack.c write_config_info).";
    private const string Md5Checksum = "The MD5 digest of the raw audio data of the source, 16 bytes, to compare with the decoded audio (ID_MD5_CHECKSUM; format document, section 3.0).";
    private const string SampleRate = "The sample rate, where it is not one of the 15 the flags can name, in three bytes, or four with the top bit reserved (ID_SAMPLE_RATE; open_utils.c read_sample_rate).";
    private const string AltExtension = "The extension of the file to decode to, such as \"wav\" or \"dsf\", added with version 5.0 (ID_ALT_EXTENSION; format document, section 3.0).";
    private const string AltMd5Checksum = "The MD5 digest of the raw audio data of a source that is not a WAVE file, with a byte order or sample format other than WAVE's, added with version 5.0 (ID_ALT_MD5_CHECKSUM; format document, section 3.0).";
    private const string NewConfigBlock = "The source file's format, the flags of how its samples are stored and a Core Audio channel layout, added with version 5.0 (ID_NEW_CONFIG_BLOCK; open_utils.c read_new_config_info).";
    private const string ChannelIdentities = "Which speaker each channel feeds that the channel mask cannot name, one byte each; 0xFF stands for none (ID_CHANNEL_IDENTITIES; open_utils.c read_channel_identities).";
    private const string BlockChecksum = "A checksum of the block's bytes before it: each 16-bit little-endian word is added to three times the sum so far, from -1. Two bytes keep the sum folded onto itself, four keep it whole; added with version 5.0 (ID_BLOCK_CHECKSUM; open_utils.c WavpackVerifySingleBlock).";
    private const string Unknown = "A sub-block that WavPack does not define. A decoder skips it if it is optional and refuses the block otherwise (open_utils.c process_metadata).";

    public static string SubBlock(int function) => function switch
    {
        SubBlockIds.Dummy => Dummy,
        SubBlockIds.EncoderInfo => EncoderInfo,
        SubBlockIds.DecorrTerms => DecorrTerms,
        SubBlockIds.DecorrWeights => DecorrWeights,
        SubBlockIds.DecorrSamples => DecorrSamples,
        SubBlockIds.EntropyVars => EntropyVars,
        SubBlockIds.HybridProfile => HybridProfile,
        SubBlockIds.ShapingWeights => ShapingWeights,
        SubBlockIds.FloatInfo => FloatInfo,
        SubBlockIds.Int32Info => Int32Info,
        SubBlockIds.WvBitstream => WvBitstream,
        SubBlockIds.WvcBitstream => WvcBitstream,
        SubBlockIds.WvxBitstream => WvxBitstream,
        SubBlockIds.ChannelInfo => ChannelInfo,
        SubBlockIds.DsdBlock => DsdBlock,
        SubBlockIds.RiffHeader => RiffHeader,
        SubBlockIds.RiffTrailer => RiffTrailer,
        SubBlockIds.AltHeader => AltHeader,
        SubBlockIds.AltTrailer => AltTrailer,
        SubBlockIds.ConfigBlock => ConfigBlock,
        SubBlockIds.Md5Checksum => Md5Checksum,
        SubBlockIds.SampleRate => SampleRate,
        SubBlockIds.AltExtension => AltExtension,
        SubBlockIds.AltMd5Checksum => AltMd5Checksum,
        SubBlockIds.NewConfigBlock => NewConfigBlock,
        SubBlockIds.ChannelIdentities => ChannelIdentities,
        SubBlockIds.BlockChecksum => BlockChecksum,
        _ => Unknown,
    };

    public const string Pass = "One decorrelation pass: a term that says which earlier values predict the current one, and a delta that sets how fast its weight adapts.";
    public const string Delta = "How far the pass's weight moves with each sample, 0 to 7 (decorr_utils.c read_decorr_terms).";
    public const string TermPositive = "The pass predicts each sample from the sample this many places before it in the same channel (terms 1 to 8, decorr_utils.c).";
    public const string TermSeventeen = "Term 17: the pass predicts a sample as twice the one before it less the one before that (a linear extrapolation, unpack.c).";
    public const string TermEighteen = "Term 18: the pass predicts a sample as three times the one before it less the one before that, halved (unpack.c).";
    public const string TermNegative = "A negative term predicts each channel from the other one, as stereo data allows: -1 and -2 use the other channel's previous sample for one channel and its current sample for the other, -3 the other channel's previous sample for both (unpack.c; decorr_utils.c read_decorr_terms).";
    public const string TermInvalid = "A term libwavpack refuses: it must be 1 to 8, 17 or 18, or -1 to -3 in stereo (decorr_utils.c read_decorr_terms).";

    public static string TermOf(int term) => term switch
    {
        17 => TermSeventeen,
        18 => TermEighteen,
        < 0 => TermNegative,
        _ => TermPositive,
    };

    public const string PassWeights = "The initial weight or weights of one pass, in the range of 12 bits that libwavpack restores them to.";
    public const string Weight = "A decorrelation weight, restored from the byte as libwavpack does (entropy_utils.c restore_weight); 1024 stands for 1.";
    public const string PassSamples = "The history samples of one pass, as 16-bit values, which the decoder starts its predictions from.";
    public const string Medians = "The three medians the entropy coder adapts from, as the base-2 logarithms of the values, in 1/256 steps (write_words.c).";
    public const string Median = "A median of the residuals' magnitudes, stored as a 16-bit logarithm of its value (entropy_utils.c read_entropy_vars).";
    public const string SlowLevel = "The slow-moving level of the residuals, a logarithm, from which hybrid mode sets how coarse its quantization is (entropy_utils.c read_hybrid_profile).";
    public const string BitrateAccumulator = "The accumulator that holds the target bitrate: its high 16 bits are stored. The target is the stored value plus 568, in 1/256 bits per sample, for a mono block or a stereo block with balanced noise (write_words.c word_set_bitrate).";
    public const string BitrateAccumulatorFalseStereo = "The accumulator that holds the target bitrate of a block whose two channels are the same. Its high 16 bits are stored; the target is the stored value plus 568 and plus 512, halved, in 1/256 bits per sample, since the data is coded once (write_words.c word_set_bitrate).";
    public const string BitrateDelta = "How much the accumulator changes with each sample, a signed logarithm; the encoder writes none (write_words.c).";
    public const string TargetBitrateAtMost = "The bitrate the hybrid encoder aimed at, as a limit: it stores a bitrate accumulator of 0 for any target below 568/256 bits per sample (about 2.22), so a smaller target cannot be told from that one (write_words.c word_set_bitrate).";
    public const string TargetBitrateAtMostFalseStereo = "The bitrate the hybrid encoder aimed at, as a limit: for a block whose two channels are the same it stores a bitrate accumulator of 0 for any target below 540/256 bits per sample (about 2.11), so a smaller target cannot be told from that one (write_words.c word_set_bitrate).";
    public const string TargetBitrate = "The bitrate the hybrid encoder aimed at, in bits per sample and channel, worked out from the bitrate accumulator as write_words.c sets it from the encoder's setting.";
    public const string ShapingWeight = "A noise shaping weight, restored from the byte as libwavpack restores decorrelation weights.";
    public const string ShapingWeightUnused = "The second weight of a two-byte sub-block, which libwavpack reads for a second channel; a block of one channel has none (decorr_utils.c read_shaping_info).";
    public const string ShapingError = "The error of the hybrid quantization at the start of the block, as a signed logarithm (decorr_utils.c read_shaping_info).";
    public const string ShapingAccumulator = "The accumulator of the noise shaping weight at the start of the block, as a signed logarithm (decorr_utils.c read_shaping_info).";
    public const string ShapingDelta = "How the noise shaping weight changes with each sample, as a signed logarithm (decorr_utils.c read_shaping_info).";
    public const string FloatFlags = "What the floating point samples need restored: bits shifted into them, zeros that are not all real zeros, negative zeros and exceptions such as infinities (wavpack_local.h, flags for float_flags).";
    public const string FloatShiftOnes = "Whether bits shifted into the numbers are all ones.";
    public const string FloatShiftSame = "Whether bits shifted into the numbers are the same in all of them.";
    public const string FloatShiftSent = "Whether bits shifted into the numbers are sent literally.";
    public const string FloatZerosSent = "Whether the zeros are not all real zeros, so their bits are sent.";
    public const string FloatNegativeZeros = "Whether the audio holds negative zeros.";
    public const string FloatExceptions = "Whether the audio holds exceptions: infinities, not-a-number values and the like.";
    public const string FloatShift = "How many bits are shifted into the numbers' mantissas (open_utils.c read_float_info).";
    public const string FloatMaxExponent = "The largest exponent among the numbers.";
    public const string FloatNormExponent = "The exponent the numbers are normalized to when a reader asks for values between -1 and 1.";
    public const string Int32SentBits = "How many bits of each sample the extra bitstream sends literally; with none, the sample's low bits are lost, which makes a block lossy without that bitstream (open_utils.c read_int32_info).";
    public const string Int32Zeros = "How many low bits are always zero, restored by a shift.";
    public const string Int32Ones = "How many low bits are always one.";
    public const string Int32Dups = "How many low bits repeat the bit above them.";
    public const string WvxCrc = "A CRC of the extra bits, which only decoding can check (open_utils.c init_wvx_bitstream).";
    public const string Bitstream = "The coded bits, which the analyzer does not decode.";
    public const string ChannelCount = "How many channels the file holds, over all its blocks (open_utils.c read_channel_info). The newer form, in six or seven bytes, takes twelve bits for it.";
    public const string StreamCount = "How many blocks, stereo or mono, make up one set that holds all channels (open_utils.c read_channel_info).";
    public const string ChannelMask = "The speakers the channels feed, one bit each, as in the WAVE channel mask; the channels follow the order of the bits (format document, section 1.0).";
    public const string ChannelLayoutField = "The speakers the mask names, in the order of the file's channels.";
    public const string DsdMultiplier = "The factor of two by which the stored sample rate is multiplied for DSD; the audio's bit rate is that times 8 (unpack_dsd.c init_dsd_block; common_utils.c WavpackGetNativeSampleRate).";
    public const string DsdMode = "How the DSD bytes are coded: stored as they are (0), fast (1) or high (3) (unpack_dsd.c init_dsd_block).";
    public const string DsdHistoryBits = "How many previous bits the fast mode's probability model looks at, up to 5 (unpack_dsd.c init_dsd_block_fast).";
    public const string DsdData = "The coded DSD bytes, which the analyzer does not decode.";
    public const string DummyBytes = "Bytes that fill the sub-block.";
    public const string Md5Digest = "The digest as 32 hexadecimal digits. The analyzer does not decode the audio, so it does not check it.";
    public const string SampleRateField = "The sample rate in Hz (open_utils.c read_sample_rate).";
    public const string Extension = "The extension of the file to decode to.";
    public const string FileFormat = "The format of the source file: 0 WAVE, 1 Wave64, 2 CAF, 3 DSDIFF, 4 DSF, 5 AIFF (wavpack.h WP_FORMAT_*).";
    public const string QualityFlags = "How the source file stores its samples, where it differs from WAVE (wavpack.h QMODE_*).";
    public const string QmodeBigEndian = "Whether the source's samples are big-endian.";
    public const string QmodeSignedBytes = "Whether the source's 8-bit samples are signed.";
    public const string QmodeUnsignedWords = "Whether the source's samples of more than 8 bits are unsigned.";
    public const string QmodeReordered = "Whether the source's channels were not in the Microsoft order and were reordered.";
    public const string QmodeDsdLsb = "Whether the source's DSD bytes hold the first bit in the least significant bit, as most DSF files do.";
    public const string QmodeDsdMsb = "Whether the source's DSD bytes hold the first bit in the most significant bit, as DSDIFF files do.";
    public const string QmodeDsdBlocks = "Whether the source's DSD data is blocked by channel, as in DSF files.";
    public const string LayoutTag = "The Core Audio channel layout of the source, the byte that stands in the third place of its tag (open_utils.c read_new_config_info).";
    public const string LayoutChannels = "How many channels the layout names.";
    public const string Reordering = "The order of the source's channels, where it was not the Microsoft order, as indexes into the channels.";
    public const string Identity = "The speaker this channel feeds, by its position in the WAVE channel mask, or unassigned (open_utils.c read_channel_identities).";
    public const string FurtherItems = "More entries, which the report does not list one by one.";
    public const string StoredChecksum = "The checksum as the sub-block stores it.";
    public const string ComputedChecksum = "The checksum worked out over the block's bytes before this sub-block, in the size the sub-block stores (open_utils.c WavpackVerifySingleBlock).";
    public const string ChecksumIsValid = "Whether the stored checksum equals the one worked out.";

    public const string ConfigFlags = "The encoder's configuration flags from bit 8 up (wavpack.h CONFIG_*). They are for information; decoding does not read them.";
    public const string ConfigFast = "Whether the encoder ran in fast mode (-f).";
    public const string ConfigHigh = "Whether the encoder ran in high quality mode (-h), which very high quality (-hh) sets too.";
    public const string ConfigVeryHigh = "Whether the encoder ran in very high quality mode (-hh).";
    public const string ConfigKbps = "Whether the hybrid bitrate was given in kilobits a second instead of bits per sample.";
    public const string ConfigAutoShaping = "Whether the hybrid noise shaping was chosen automatically.";
    public const string ConfigShapeOverride = "Whether the noise shaping was set by the user.";
    public const string ConfigJointOverride = "Whether the joint stereo mode was set by the user (-j).";
    public const string ConfigDynamicShaping = "Whether the hybrid mode used dynamic noise shaping.";
    public const string ConfigExe = "Whether the encoder made a self-extracting executable.";
    public const string ConfigCreateWvc = "Whether the encoder wrote a correction file next to this one.";
    public const string ConfigOptimizeWvc = "Whether the encoder maximized the hybrid compression of the lossless result (-cc).";
    public const string ConfigCompatibleWrite = "Whether the encoder wrote an older stream version, 0x407, for decoders before 4.3, which the legacy WavpackSetConfiguration always asks for (wavpack.h CONFIG_COMPATIBLE_WRITE; pack_utils.c).";
    public const string ConfigCalcNoise = "Whether the encoder calculated the noise in hybrid mode (-n).";
    public const string ConfigExtra = "Whether the encoder used extra processing (-x), whose level follows.";
    public const string ConfigSkipWvx = "Whether the encoder left out the extra bitstream of floating point and large integer data.";
    public const string ConfigMd5 = "Whether the encoder stored an MD5 digest (-m).";
    public const string ConfigMerge = "Whether the encoder merged blocks of equal redundancy (--merge-blocks).";
    public const string ConfigPair = "Whether the encoder coded channels without a speaker in stereo pairs.";
    public const string ConfigMono = "Whether the encoder optimized streams that are mono posing as stereo.";
    public const string ConfigOther = "Configuration flags that have no meaning WavPack 5.6.0 defines, or an older one.";
    public const string ConfigExtraLevel = "The level of extra processing, 1 to 6 (-x1 to -x6): higher levels try more filters and take longer to encode (pack_utils.c).";
    public const string ConfigFurther = "Bytes after the fields libwavpack 5.6.0 reads from the config block.";

    public const string RiffChunk = "The RIFF chunk that wraps a WAVE file: the signature, the size and the form type.";
    public const string ChunkId = "The chunk's four-character ID.";
    public const string ChunkSize = "The chunk's size in bytes, less its ID and this field.";
    public const string FormType = "The form of the file: \"WAVE\" for a WAVE file.";
    public const string FormatChunk = "The \"fmt \" chunk of the WAVE file, which describes the audio (Microsoft WAVEFORMATEX).";
    public const string DataChunk = "The \"data\" chunk's header; the audio that follows it is what the blocks hold.";
    public const string DataChunkSize = "The size of the audio the WAVE file held, in bytes, which libwavpack uses to write the file back.";
    public const string RiffOtherChunk = "A chunk of the WAVE file other than the format and the audio.";
    public const string WaveFormatTag = "The coding of the samples: PCM, IEEE float or the extensible form.";
    public const string WaveChannels = "The number of channels.";
    public const string WaveSampleRate = "The sample rate in Hz.";
    public const string WaveByteRate = "The bytes a second.";
    public const string WaveBlockAlign = "The bytes of one sample of all channels.";
    public const string WaveBits = "The bits of each sample's container.";
    public const string WaveValidBits = "The bits of each sample that hold audio.";
    public const string WaveChannelMask = "The speakers the channels feed, one bit each.";
    public const string WaveSubFormat = "The coding of the samples in the extensible form.";
    public const string AlternateSignature = "The first four bytes of the source file, which say what format it is: \"FORM\" for AIFF, \"caff\" for CAF, \"DSD \" for DSF, \"FRM8\" for DSDIFF.";
    public const string AlternateFurther = "The rest of the header or trailer, which the analyzer does not read.";

    public const string Statistics = "Figures computed over the blocks of the stream.";
    public const string StatBlocks = "The blocks in the file, those with and those without audio.";
    public const string StatAudioBlocks = "The blocks that count samples.";
    public const string StatMetadataBlocks = "The blocks that count no sample and carry metadata only.";
    public const string StatSets = "The sets of blocks that hold the channels of one stretch of audio, a mono or stereo block alone or several together for more channels.";
    public const string StatSamples = "The samples of each channel the file holds: the samples of the first block of each set, added up.";
    public const string StatSamplesDsd = "The 1-bit samples of each channel the file holds: eight for each sample byte the blocks count.";
    public const string StatDuration = "The duration of the samples at the sample rate.";
    public const string StatRate = "The sample rate of the audio, from the first audio block.";
    public const string StatRateDsd = "The DSD bit rate of each channel: the stored sample rate times the rate multiplier times 8.";
    public const string StatChannels = "The number of channels, from the channel info or, without one, from the first set of blocks.";
    public const string StatLayout = "The speakers the channels feed, from the channel mask; without a channel info sub-block a mono file feeds the front center and a stereo file the front left and right (open_utils.c).";
    public const string StatBits = "The bits of each sample: the bytes the flags give times 8, less the left shift; 32 for floating point.";
    public const string StatBitsDsd = "DSD samples are 1 bit each.";
    public const string StatIsLossless = "Whether decoding gives back the source exactly: the file is not hybrid and holds every bit its coding needs.";
    public const string StatIsHybrid = "Whether the audio is coded in hybrid mode, in which the main file is lossy.";
    public const string StatIsCorrectionFileCreated = "Whether a correction file was created with the stream: this file is that correction file, or the config block says the encoder wrote one next to it. The analyzer does not look for the file.";
    public const string StatIsCorrectionFile = "Whether this file is a correction file: its audio blocks hold correction data and no audio bitstream.";
    public const string StatIsFloat = "Whether the samples are 32-bit floating point numbers.";
    public const string StatIsDsd = "Whether the audio is DSD.";
    public const string StatMode = "The speed mode the encoder's config block records: fast, normal, high or very high.";
    public const string StatExtraLevel = "The level of extra processing the config block records, or none.";
    public const string StatTargetBits = "The bitrate the hybrid encoder aimed at in the first hybrid block, in bits per sample and channel.";
    public const string StatTargetRate = "The target bitrate over all channels: the target bits per sample times the sample rate times the channels.";
    public const string StatPasses = "How many decorrelation passes the blocks use.";
    public const string StatMidSide = "The stereo blocks coded as mid and side instead of left and right.";
    public const string StatChecksums = "The blocks whose checksum sub-block equals the checksum worked out over the block's bytes.";
    public const string StatChecksumsFailed = "The blocks whose checksum differs from the one worked out, or that say they have one and hold none.";
    public const string StatMd5 = "The MD5 digest of the source audio that the first MD5 sub-block stores. The analyzer does not decode the audio, so it does not check it.";
    public const string StatSourceFormat = "The format of the file the audio was encoded from, from the new config block (an empty one stands for WAVE).";
    public const string StatSourceExtension = "The extension of the file to decode to, from the extension sub-block.";
    public const string AverageBitrate = "The bits of the audio blocks, less the wrapper sub-blocks (the source file's header and trailer) they hold, over the time their samples last.";
    public const string MinimumBitrate = "The bitrate of the set with the lowest bitrate.";
    public const string MaximumBitrate = "The bitrate of the set with the highest bitrate.";
    public const string Smallest = "The smallest set of blocks.";
    public const string Largest = "The largest set of blocks.";
    public const string BitrateMode = "Whether the sets all have the same bitrate: variable where they differ, constant where they do not, and unknown for a single set, since one set shows no variation. Lossless blocks are as long as their data needs; hybrid blocks aim at one bitrate.";
    public const string Distribution = "How many sets of blocks fall in each bitrate range of 32 kbps.";
    public const string DistributionBin = "The sets whose bitrate falls in this range, and their share.";
    public const string BitrateSeries = "The bitrate of each set of blocks: its bits over the time its samples last.";
    public const string BitrateOverTimeSeries = "The bitrate of each second of audio: the bits of the sets that start in that second over the time their samples last.";

    public const string Encoder = "The encoder that wrote the stream, as far as the blocks tell it, with the evidence and the confidence in it.";
    public const string ConfidenceMedium = "How sure the analyzer is: medium, since a new config block and block checksums point to libwavpack 5.0 or later but name no release.";
    public const string ConfidenceLow = "How sure the analyzer is: low, since a WavPack 4 stream names no encoder, and encoders other than libwavpack write such streams.";
    public const string Evidence = "Something in the file that points to the encoder.";
}
