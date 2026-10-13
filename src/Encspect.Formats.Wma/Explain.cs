namespace Encspect.Formats.Wma;

// What each Windows Media Audio node is. Constants only, shared by every node of their kind, written
// from FFmpeg 6.1.1's decoder (libavcodec/wmadec.c, wma.c, wma.h and wma_common.c) and the Advanced
// Systems Format Specification, revision 01.20.03, section 11.1.1.
internal static partial class Explain
{
    public const string ExtraData = "Bytes after the fields the format defines, which decoders ignore.";

    public const string SamplesPerBlock = "dwSamplesPerBlock of the type-specific data of Windows Media Audio (ASF specification, section 11.1.1). FFmpeg's decoder does not read it.";
    public const string EncodeOptions = "wEncodeOptions of the type-specific data of Windows Media Audio (ASF specification, section 11.1.1): the coding options of the stream. FFmpeg's decoder reads bit 0 (exponent VLC), bit 1 (bit reservoir), bit 2 (variable block length) and bits 3 and 4 (how many block sizes).";
    public const string SuperBlockAlign = "dwSuperBlockAlign of the type-specific data of Windows Media Audio (ASF specification, section 11.1.1). FFmpeg's decoder does not read it.";
    public const string Version1Unused = "The first two bytes of the codec-specific data of version 1, which FFmpeg's decoder does not read; it takes the encode options from the two after them.";
    public const string OptionExponentVlc = "Set if the exponents of each block are coded with the scale factor codebook (as differences from the one before), clear if they are coded as line spectral pairs, as Vorbis' floors are (wmadec.c: use_exp_vlc).";
    public const string OptionBitReservoir = "Set if frames can be longer than a superframe leaves room for, so that a frame goes on in the next superframe, whose header then says how many bits of it there are (wmadec.c: use_bit_reservoir).";
    public const string OptionVariableBlockLength = "Set if a frame can be divided into blocks of different lengths, as AAC divides its frames into short windows (wmadec.c: use_variable_block_len).";
    public const string OptionBlockSizeCount = "With variable block lengths, one less than the number of block sizes below the frame length that the stream uses, before it is raised by 2 for a bitrate of 32 kbps per channel or more (wmadec.c: nb_block_sizes).";
    public const string OptionOthers = "Bits of the encode options that FFmpeg's decoder does not read.";

    public const string Stream = "The Windows Media Audio stream: its superframes, which the container carries as media objects, and the frames in them.";
    public const string Superframes = "The superframes in the order they are stored: the media objects of the stream, each the size of the block alignment. A frame may begin in one and end in the next.";
    public const string Frames = "The frames in decoding order: the unit that is decoded to frame-length samples. A frame that spans superframes lies in the bytes of both.";
    public const string Superframe = "A superframe: a media object of the block alignment's size. With the bit reservoir it holds a header, the end of the frame before, the frames that begin in it and the start of the next (wmadec.c: wma_decode_superframe).";
    public const string SuperframeIndex = "The number of the superframe, counted by the encoder in 4 bits; FFmpeg's decoder skips it.";
    public const string FramesInSuperframe = "How many frames end in this superframe, counting the one that began in the superframe before; the first superframe of a stream, with no frame before it, counts one less (wmadec.c: nb_frames).";
    public const string LastFrameBitOffset = "How many bits of this superframe, after the header, finish the frame that began in the superframe before; the frames of this superframe start after them (wmadec.c: bit_offset).";
    public const string FrameEnd = "The bits that finish the frame that began in the superframe before.";
    public const string FrameMiddle = "The whole superframe after its first byte: the middle of a frame that began earlier and ends later, since no frame ends in this superframe.";
    public const string FrameData = "The bits of a frame that begins and ends in this superframe.";
    public const string BitReservoir = "The bits after the last frame that ends in this superframe: the start of a frame that goes on in the next one.";
    public const string SuperframePadding = "The bits after the frame, which fill the superframe.";

    public const string Frame = "A frame: blocks that together hold frame-length samples of every channel, each block a transform of the audio (wmadec.c: wma_decode_frame).";
    public const string Block = "A block of a frame: the coded spectrum of a stretch of the audio, with its gains, exponents and coefficients (wmadec.c: wma_decode_block).";
    public const string PreviousBlockSize = "The size of the block before, sent in the first block of a superframe's frames so that decoding can start there; a power of two, as an index from the frame length (wmadec.c).";
    public const string BlockSize = "The size of this block in samples, sent in the first block of a superframe's frames (wmadec.c).";
    public const string NextBlockSize = "The size of the next block, which sets how this block's window ends (wmadec.c).";
    public const string MidSideStereo = "Set if the two channels of the block are coded as their sum and difference (mid and side) instead of left and right (wmadec.c: ms_stereo).";
    public const string ChannelCoded = "Set if the block holds coded data for this channel; a block of a silent channel holds none (wmadec.c: channel_coded).";
    public const string TotalGain = "The gain of the block: 1 plus 7-bit values, each 127 meaning another follows (wmadec.c). It also sets the bits of an escape-coded level.";
    public const string NoiseSubstitution = "One bit per high band of each coded channel: set if the band is coded as noise at a level instead of as coefficients (wmadec.c: high_band_coded).";
    public const string NoiseLevels = "The level of each noise-substituted band: 7 bits for the first, then a difference coded with the high-band gain codebook (wmadec.c: high_band_values).";
    public const string ExponentsSent = "In a block shorter than the frame: set if the block sends exponents, clear if it reuses those of an earlier block (wmadec.c).";
    public const string ExponentsVlc = "The channel's exponents, one per band: version 1 starts with a 5-bit value, and each band is a difference from the one before, coded with the scale factor codebook (wmadec.c: decode_exp_vlc).";
    public const string ExponentsLsp = "The channel's exponents as ten line spectral pair indexes of 3 or 4 bits, from which a smooth curve of exponents is built (wmadec.c: decode_exp_lsp).";
    public const string Coefficients = "The channel's quantized spectrum: run-level codes with a sign bit, ended by an end-of-block code or the last coefficient; a code of 0 escapes to a level of the gain's bit width and a run of frame-length bits (wma.c: ff_wma_run_level_decode).";

    public const string Statistics = "Figures over the stream's superframes, frames and blocks, counted from the syntax the decoder reads.";
    public const string StatSuperframes = "How many superframes (media objects) the stream holds.";
    public const string StatFrames = "How many frames were read.";
    public const string StatInvalidFrames = "How many frames a decoder would reject.";
    public const string StatFrameLength = "The samples of each channel that a frame holds, from the sample rate (wma_common.c: ff_wma_get_frame_len_bits).";
    public const string StatSamples = "The samples of each channel a decoder outputs: the frames that were read less the first, which only fills the overlap of the transform.";
    public const string StatDuration = "The samples over the sample rate, in seconds.";
    public const string StatBlockAlignment = "The size of a superframe in bytes, from the WAVEFORMATEX.";
    public const string StatAverageBitrate = "The superframes' bytes over the duration of the frames.";
    public const string StatSmallestFrame = "The smallest frame, in bytes.";
    public const string StatLargestFrame = "The largest frame, in bytes.";
    public const string StatMinimumFrameBitrate = "The bitrate of the smallest frame over the time it covers.";
    public const string StatMaximumFrameBitrate = "The bitrate of the largest frame over the time it covers.";
    public const string StatUsesBitReservoir = "Whether the encode options turn the bit reservoir on.";
    public const string StatMaxFramesPerSuperframe = "The most frames that end in one superframe.";
    public const string StatStraddlingFrames = "How many frames begin in one superframe and end in a later one.";
    public const string StatLargestReservoir = "The most bytes that a superframe leaves of a frame for the next.";
    public const string StatAverageReservoir = "The bytes a superframe leaves of a frame for the next, on average.";
    public const string StatFillerFrames = "Frames that began in one superframe and end before the bit offset the next superframe states, with other bits up to it, which a decoder skips.";
    public const string StatFillerBits = "The bits between the end of such frames and the bit offset, all of them.";
    public const string StatContinuations = "How many superframes lie entirely inside a frame.";
    public const string StatExponentCoding = "How the exponents are coded: with the scale factor codebook or as line spectral pairs.";
    public const string StatVariableBlocks = "Whether frames can be divided into blocks of different lengths.";
    public const string StatNoiseCoding = "Whether the stream's rate and bitrate make the encoder substitute noise in the high bands (wma.c: use_noise_coding).";
    public const string StatBlocks = "How many blocks the frames hold.";
    public const string StatBlockSizes = "How many blocks there are of each size.";
    public const string StatBlockSizeShare = "Blocks of this size, and their share of all blocks.";
    public const string StatMidSideBlocks = "Blocks of two channels coded as mid and side, and their share of the stereo blocks.";
    public const string StatNoiseBlocks = "Blocks with at least one high band coded as noise, and their share of all blocks.";
    public const string StatUncodedBlocks = "Blocks that hold no coded channel.";
    public const string StatReusedExponents = "Blocks that reuse the exponents of an earlier block.";
    public const string StatMinimumGain = "The smallest total gain of a block.";
    public const string StatMaximumGain = "The largest total gain of a block.";
    public const string StatNonZero = "The non-zero coefficients coded, counting escape-coded ones.";
    public const string StatEscapes = "The coefficients coded with an escape.";

    public const string Encoder = "Which program encoded the stream, as the analyzer concludes from the evidence below.";
    public const string Confidence = "How sure the naming is: high when the file carries the encoder's own trait, medium for a pattern of the encoder's, low when only a claim in the metadata names it.";
    public const string Evidence = "What points to the encoder, at the bytes it was found in.";
}

internal static class SeriesText
{
    public const string Bitrate = "The bitrate of each frame: its bits over the time its samples last.";
    public const string BitrateOverTime = "The bitrate in each second of audio: the bits of the frames that start in it over their duration.";
    public const string BitReservoir = "The bytes of a frame that each superframe leaves for the next, at the superframe's offset.";
}
