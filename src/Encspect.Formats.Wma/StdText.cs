using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// The words of the Windows Media Audio version 1 and 2 reader: what each node is (constants shared by every node of its kind),
// the finding messages and the reasons a frame is invalid.
internal static class StdText
{
    public const string Stream = "The stream as the container and the codec-specific data describe it, with the frames read from its superframes: statistics, superframes and frames.";
    public const string StreamNotAnalyzed = "The stream as the container and the codec-specific data describe it. Its configuration is one that the format's decoders do not open, so its frames are not read.";
    public const string Statistics = "Figures over the stream's superframes, frames and blocks, counted from the syntax read.";
    public const string SuperframeList = "The media objects of the stream, each read as a superframe of the block alignment's size.";
    public const string FrameList = "The frames of the stream in the order they were read, each a run of blocks whose lengths add up to the frame length.";

    public const string CodecData = "The codec-specific data of the stream's WAVEFORMATEX.";
    public const string Unused = "Two bytes of the version 1 codec data that a decoder does not read.";
    public const string SamplesPerBlock = "The samples per block the encoder states (dwSamplesPerBlock, ASF specification section 11.1.1). A decoder does not read it.";
    public const string SuperBlockAlign = "The super block alignment the encoder states (dwSuperBlockAlign, ASF specification section 11.1.1). A decoder does not read it.";
    public const string EncodeOptions = "The encoder's options: how the exponents are coded, whether the bit reservoir and variable block lengths are used, and how many block sizes there are.";
    public const string UsesExponentVlc = "Whether the exponents are coded with the scale factor codebook; when clear they are coded as line spectral pairs.";
    public const string UsesBitReservoir = "Whether superframes have a header and a frame may continue in the next superframe.";
    public const string UsesVariableBlockLength = "Whether a frame may be divided into blocks of several sizes.";
    public const string BlockSizeCountCode = "With variable block lengths, with the bitrate sets how many block sizes there are.";
    public const string OtherOptions = "The remaining bits of the options word, which a decoder does not read.";
    public const string ExtraData = "Bytes after the codec-specific data the format defines.";

    public const string Superframe = "One media object of the stream read as a superframe: with the bit reservoir a header and the bits of several frames, without it one frame and padding.";
    public const string SuperframeIndex = "The superframe's number, 4 bits. A decoder does not read it; in Encspect's test files it counts up from 0.";
    public const string FramesInSuperframe = "How many frames begin in the superframe, 4 bits. The last one counts where it goes on in the next superframe.";
    public const string LastFrameBitOffset = "How many bits after this header finish the frame begun in an earlier superframe. The frames that begin in this superframe follow them.";
    public const string FrameEnd = "The bits after the header that finish the frame begun in an earlier superframe.";
    public const string FrameMiddle = "A superframe in which no frame ends: everything after its first byte is the middle of a frame that began earlier.";
    public const string FrameData = "The bits of a frame that begins in this superframe, from its first bit to its last.";
    public const string BitReservoir = "The bytes from the one the last frame ends in to the end of the superframe: the start of the frame that the next superframe finishes.";
    public const string Padding = "The bytes after the frame to the end of the superframe, which a decoder does not read.";

    public const string Frame = "One frame: a run of blocks whose lengths add up to the frame length, read to the end of its last block.";
    public const string Block = "One block of a frame: a transform of its samples per channel, with its sizes, flags, total gain, noise substitution, exponents and coefficients.";
    public const string PreviousBlockSize = "The size of the block before this one, sent at the first block after the sizes are reset.";
    public const string BlockSize = "The size of this block in samples per channel, sent at the first block after the sizes are reset.";
    public const string NextBlockSize = "The size of the next block in samples per channel.";
    public const string MidSide = "Whether the two channels of the block are coded as the sum and the difference of left and right (mid/side) rather than as left and right.";
    public const string ChannelCoded = "Whether the channel has coded data in this block.";
    public const string TotalGain = "The block's total gain: 1 plus 7-bit values, a value of 127 meaning another follows. It sets the width of the level of an escape-coded coefficient.";
    public const string NoiseSubstitution = "One flag per high band and coded channel: a set flag replaces the band by noise of a stated level, and its coefficients are not coded.";
    public const string NoiseLevels = "The levels of the noise that replaces the flagged bands: the first as 7 bits, each later one as a difference code.";
    public const string ExponentsSent = "Whether the block sends exponents; when clear every coded channel reuses the exponents it last received.";
    public const string ExponentsScaleFactors = "The channel's exponents, the envelope of its spectrum: one scale factor code per band.";
    public const string ExponentsLineSpectralPairs = "The channel's exponents, the envelope of its spectrum, as ten line spectral pair indexes.";
    public const string Coefficients = "The channel's coefficients as run-level codes, up to an end-of-block code or the last coefficient. The value is the count of non-zero coefficients and the position of the last.";

    public const string StatSuperframes = "How many media objects the stream has, short ones included.";
    public const string StatFrames = "How many frames were read, valid or not.";
    public const string StatInvalidFrames = "How many frames broke a rule of the syntax and ended their superframe.";
    public const string StatFrameLength = "The samples per channel in a frame, from the sampling rate.";
    public const string StatSamples = "The valid frames less the first, times the frame length: what a decoder outputs, which drops the first frame's overlap.";
    public const string StatDuration = "The samples at the sampling rate.";
    public const string StatBlockAlignment = "The size of each superframe, the container's block alignment.";
    public const string StatAverageBitrate = "The bytes of the superframes over the duration.";
    public const string StatSmallestFrame = "The fewest bytes a valid frame takes.";
    public const string StatLargestFrame = "The most bytes a valid frame takes.";
    public const string StatMinimumFrameBitrate = "The bitrate of the smallest valid frame if frames of that size followed one another.";
    public const string StatMaximumFrameBitrate = "The bitrate of the largest valid frame if frames of that size followed one another.";
    public const string StatUsesBitReservoir = "Whether the encode options turn the bit reservoir on.";
    public const string StatFramesPerSuperframe = "The most frames read in one superframe.";
    public const string StatFramesSpanning = "Frames that began in one superframe and ended in a later one.";
    public const string StatLargestReservoir = "The most bytes a superframe left for the next one to finish, superframes inside a frame included.";
    public const string StatAverageReservoir = "The bytes the superframes left for the next one, over all superframes.";
    public const string StatFramesFollowedByFiller = "Frames that began in one superframe and are followed by bits up to the bit offset the next superframe states.";
    public const string StatFillerBits = "The bits between the end of such a frame and the bit offset.";
    public const string StatSuperframesInsideFrame = "Superframes in which no frame ends.";
    public const string StatExponentCoding = "How the exponents are coded, by the encode options.";
    public const string StatUsesVariableBlockLength = "Whether frames may be divided into blocks of several sizes; the setting a decoder works with.";
    public const string StatUsesNoiseSubstitution = "Whether noise substitution is on, derived from the sampling rate and the bitrate.";
    public const string StatBlocks = "How many blocks were read, failed ones included.";
    public const string StatBlockSizes = "How many block sizes occur, with the share of blocks at each.";
    public const string StatMidSideBlocks = "Blocks coded as mid/side, as a share of the blocks whose size is known.";
    public const string StatNoiseSubstitutedBlocks = "Blocks with a coded channel and a band replaced by noise, as a share of all blocks.";
    public const string StatBlocksWithoutCoded = "Blocks in which no channel is coded.";
    public const string StatBlocksReusingExponents = "Blocks with a coded channel that send no exponents.";
    public const string StatSmallestGain = "The smallest total gain over blocks with a coded channel.";
    public const string StatLargestGain = "The largest total gain over blocks with a coded channel.";
    public const string StatNonZero = "Non-zero coefficients over blocks with a coded channel: every run-level code and every escape.";
    public const string StatEscapes = "Coefficients coded as an escape, whatever their level.";

    public const string BitrateSeries = "The bitrate of each frame if frames of its size followed one another, at the frame's first byte; 0 for an invalid frame.";
    public const string BitrateOverTimeSeries = "The bitrate over each second of the stream, from the valid frames that begin in it.";
    public const string BitReservoirSeries = "The bytes each superframe leaves for the next one to finish, at the superframe's first byte.";

    public static string Sizes(long count) => Counts.Of(count, "size", "sizes");

    public static string Reason(FrameFault fault) => fault switch
    {
        FrameFault.BlockSize => "a block size index outside the stream's block sizes",
        FrameFault.FrameOverflow => "its blocks add up to more samples than a frame holds",
        FrameFault.GainOverread => "the total gain runs past the data",
        FrameFault.ExponentRange => "an exponent is outside the range of the table of powers",
        FrameFault.ExponentsMissing => "a block codes a channel but reuses exponents it never received",
        FrameFault.CoefficientOverflow => "coefficients run past the end of the block",
        _ => "the frame needs more bits than it has (FFmpeg accepts a frame read from a superframe's bit offset that ends up to 7 bits past the superframe, and does not check the other frames; this analysis reports any)",
    };

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Hex(long offset) => "0x" + offset.ToString("X", CultureInfo.InvariantCulture);

    public static string SuperframeShort(int count, int align, long offset) =>
        $"{(count == 1 ? "A media object holds" : $"{Number(count)} media objects hold")} fewer bytes than the block alignment of {Number(align)}, which a decoder rejects (wmadec.c); the first is at {Hex(offset)}.";

    public static string SuperframeLong(int count, int align) =>
        $"{(count == 1 ? "A media object holds" : $"{Number(count)} media objects hold")} more bytes than the block alignment of {Number(align)}; a decoder takes {Number(align)} bytes as a superframe and feeds the rest in again (FFmpeg's decode.c), which this analysis does not: it reads the first {Number(align)} only.";

    public static string SuperframeHeader(int count, long offset) =>
        $"{(count == 1 ? "A superframe states" : $"{Number(count)} superframes state")} no frame ending in {(count == 1 ? "it" : "them")}, or fewer than none, with no room for the middle of a frame; a decoder rejects {(count == 1 ? "it" : "them")}. The first is at {Hex(offset)}.";

    public static string BitOffset(int count, long offset) =>
        $"{(count == 1 ? "A superframe gives" : $"{Number(count)} superframes give")} a last frame bit offset past {(count == 1 ? "its" : "their")} end, or {(count == 1 ? "is" : "are")} too short to hold that field; a decoder rejects {(count == 1 ? "it" : "them")}. The first is at {Hex(offset)}.";

    public static string Filler(int count, bool isStuffing, long bits, long offset) =>
        $"{(count == 1 ? "A frame that began in the superframe before is" : $"{Number(count)} frames that began in the superframe before are")} followed by {(isStuffing ? "zero bits" : "bits that are not zero")} up to the bit offset the superframe states, which a decoder skips; the first has {Number(bits)} (in the superframe at {Hex(offset)}).";

    public static string ReservoirLength(int count, bool isTooLarge) =>
        $"{(count == 1 ? "The last frame of a superframe ends" : $"The last frame of {Number(count)} superframes ends")} {(isTooLarge ? "leaving more than 32,768 bytes" : "past its end")}; a decoder rejects {(count == 1 ? "it" : "them")}.";

    public static string FrameInvalid(int count, FrameFault fault, long offset) =>
        $"{(count == 1 ? "A frame is" : $"{Number(count)} frames are")} invalid: {Reason(fault)}; a decoder drops the superframe and its bit reservoir. The first is at {Hex(offset)}.";

    public static string ReservoirAtEnd(long bits) =>
        $"The last superframe ends with {Counts.Of(bits, "bit", "bits")} after its last frame that no frame uses, since no superframe follows to finish a frame begun in them; a decoder drops them.";

    public static string RecordsLimit(long skipped, int limit) =>
        $"The stream has more than {Number(limit)} media objects; the {Number(skipped)} after them are not read, so the statistics and the series cover the first {Number(limit)}.";

    public static string BlocksUndetailed(long superframes, long bytes) =>
        $"The bytes of the first {Number(bytes / (1 << 20))} MiB of superframes are kept to list the blocks of their frames; the {Number(superframes)} superframes after them are read for the statistics and the series, and their frames list no blocks.";

    public static string CodecDataSize(int version, int bytes, bool isTooShort) =>
        $"The codec-specific data of Windows Media Audio {Number(version)} is {Counts.Bytes(bytes)}; {(version == 1 ? "FFmpeg's encoder writes 4 (wmaenc.c), the specification defines no version 1 layout" : "the ASF specification, section 11.1.1, has 10")}, and FFmpeg's decoder reads the options from {(version == 1 ? "4" : "6")} bytes on{(isTooShort ? ", so it takes none here" : string.Empty)}.";

    public const string VariableBlocksDisabled = "The encode options are 0x000D with variable block length. FFmpeg's decoder disables variable block length for them (wmadec.c, its issue 1503), so frames are read with one block size.";

    public static string FormatUnsupported(string reason) => $"The stream is not analyzed: {reason}.";

    public const string PayloadNotAnalyzed = "Windows Media Audio handed over as one payload is not analyzed; only the media objects of a container are.";
    public const string NoFormat = "The container states no WAVEFORMATEX for the stream, so its Windows Media Audio is not analyzed.";

    public static string FFmpegEvidence(int version, long padded) =>
        padded == 1
            ? $"The superframe has bytes after the frame that are all 'N' (0x4E), which ffmpeg's wmav{Number(version)} encoder writes (wmaenc.c), with no bit reservoir and no variable block lengths"
            : $"All {Number(padded)} padded superframes have bytes after the frame that are all 'N' (0x4E), which ffmpeg's wmav{Number(version)} encoder writes (wmaenc.c), with no bit reservoir and no variable block lengths";

    public static string MicrosoftEvidence(long samplesPerBlock) =>
        $"The codec data states {Number(samplesPerBlock)} samples per block (17 frames) with the bit reservoir and variable block lengths, as the encoders of the Windows Media Format SDK and of Media Foundation write them";
}
