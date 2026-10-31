namespace Encspect.Formats.Wma;

// What each node of a Windows Media Audio Lossless stream is that Professional's nodes are not. Constants, shared by every node of their
// kind; the texts say what the project's description of the format establishes (docs/design/wma-lossless-format.md).
internal static class LosslessExplain
{
    public const string Stream = "The stream as the packets of its container carry it: the frames Encspect reads out of them, the figures over them and the layout of each frame down to its residuals. Nothing is reconstructed.";
    public const string EncryptedStream = ProExplain.EncryptedStream;
    public const string NotAnalyzedStream = ProExplain.NotAnalyzedStream;

    public const string HasLpcFlags = "Set if each subframe that is not raw PCM carries a flag for an LPC filter. This changes where the update speed of the cascaded filters is applied.";
    public const string SampleSize = "The sample size of the audio in bits, 16 or 24 (the low byte of the first two bytes of the codec-specific data).";

    public const string SeekableFrameInPacket = "Whether a frame that starts in this packet begins with a seekable subframe or a raw PCM subframe, as in every test file.";
    public const string Spliced = "A bit that a decoder asks for a sample file for when it is set; it reads 0 in every test file.";

    public const string Trim = "How many samples are cut from the start and the end of the frame. A decoder applies the trim at the end and reads the one at the start without using it.";
    public const string SeekableSubframe = "Whether the subframe carries every filter parameter and resets the state of the filters and of the residual code, so that a decoder can start there.";
    public const string ArithmeticCoding = "A flag for arithmetic coding of the residuals, which a decoder does not support; it reads 0 in every test file.";
    public const string RecursiveFilter = "Whether a recursive filter runs over every channel after the other filters and before the quantization step.";
    public const string MidSide = "Whether the two channels are coded as their mean (rounded down) and their difference, which the decoder undoes.";
    public const string MultichannelFilter = "Whether a filter that predicts each channel from the last samples of all channels runs.";
    public const string RecursiveParameters = "The order and scaling of the recursive filter, followed by its coefficients.";
    public const string MultichannelParameters = "The order and scaling of the multichannel filter, and its initial coefficients where it sends them.";
    public const string CascadedFilters = "The cascaded filters of every channel: how many each channel has, their orders and scalings, and their initial coefficients where they are sent.";
    public const string MeanScaling = "The averaging constant of the residual code: how slowly the running mean of the residuals follows them.";
    public const string QuantizationStep = "The step by which the decoded values are multiplied.";
    public const string RawPcm = "Whether the subframe holds the samples as they are, without filters.";
    public const string CodedChannels = "Whether each channel codes residuals in the subframe; a channel that does not has residuals of 0.";
    public const string LpcFilter = "Whether an LPC filter follows and its order; a decoder reads its parameters and does not use them.";
    public const string ZeroLowBits = "How many low bits of every sample are 0 and left out; the samples are shifted left by this many bits on output.";
    public const string Samples = "The channel's samples of the subframe, uncoded.";
    public const string Residuals = "The channel's residuals of the subframe: for each sample, a quotient in unary and a remainder whose width follows the running mean of the residuals before.";

    public const string StatSeekable = "How many subframes are seekable, and their share of all.";
    public const string StatRawPcm = "How many subframes hold raw samples, and their share of all.";
    public const string StatCodedChannels = "How many channels of the subframes that are not raw PCM code residuals, and their share of all channels of subframes.";
    public const string StatEscapes = "How many residual codes have a quotient of 32 or more, which takes an escape: a length code and extra bits added to the quotient.";
    public const string StatTransients = "How many channels of subframes set the transient bit.";
    public const string StatZeroLowBits = "How many subframes leave out zero low bits.";
    public const string StatMidSide = "How many seekable subframes code the two channels as their mean (rounded down) and their difference.";
    public const string StatRecursive = "How many seekable subframes use a recursive filter.";
    public const string StatMultichannel = "How many seekable subframes use a multichannel filter.";
    public const string StatLpc = "How many subframes carry an LPC filter.";
    public const string StatCascaded = "How many cascaded filters each channel has in seekable subframes.";
    public const string StatSamples = "The samples a decoder outputs for the stream: each frame's samples less its trim at the end. The first frame is output as the others are.";
    public const string StatTrimmedSamples = "The samples the trims at the ends of the frames cut.";
    public const string StatSampleSize = "The sample size the codec-specific data states.";

    public static string Reason(LosslessFault fault) => fault switch
    {
        LosslessFault.TileHeader => "its tile header lays out subframes the stream cannot have",
        LosslessFault.ArithmeticCoding => "a subframe uses arithmetic coding, which is not read",
        LosslessFault.FilterOrder => "a subframe states a cascaded filter of an order above 256",
        LosslessFault.WaitingForSeekable => "a subframe needs the filter parameters of a seekable subframe that has not come",
        LosslessFault.NoBitsLeft => "a subframe ends before its zero low bits flag",
        LosslessFault.ZeroLowBits => "a subframe states more zero low bits than its samples have",
        LosslessFault.TrimTooLarge => "its trim at the end is the whole frame or more",
        LosslessFault.NeedsMoreBits => "it needs more bits than it has",
        LosslessFault.LengthPrefix => "its length prefix is not where its data ends",
        _ => string.Empty,
    };
}
