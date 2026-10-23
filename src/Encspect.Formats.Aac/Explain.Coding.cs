namespace Encspect.Formats.Aac;

// The category series of a stream's coding and the statistics nodes they are anchored to (docs/design/coding-graphs.md).
internal static partial class Explain
{
    public const string WindowOnlyLong = "window_sequence 0, ONLY_LONG_SEQUENCE: one long window for the whole frame (ISO/IEC 14496-3 4.5.2, 4.6.11).";
    public const string WindowLongStart = "window_sequence 1, LONG_START_SEQUENCE: a long window whose second half slopes toward the short windows that follow (ISO/IEC 14496-3 4.5.2, 4.6.11).";
    public const string WindowEightShort = "window_sequence 2, EIGHT_SHORT_SEQUENCE: eight short windows, each with an eighth of the frame's spectral lines (ISO/IEC 14496-3 4.5.2, 4.6.11).";
    public const string WindowLongStop = "window_sequence 3, LONG_STOP_SEQUENCE: a long window whose first half slopes up from the short windows before it (ISO/IEC 14496-3 4.5.2, 4.6.11).";

    public const string StatBlockSizes = "The channel streams grouped by the size of their transform: long, with the long start and long stop windows that lead into and out of short windows, and short (eight short windows). The window sequences, grouped as the Block Sizes of MPEG audio groups its block types (ISO/IEC 14496-3 4.6.11).";
    public const string SizeLong = "The channel streams with a long transform: only long, long start and long stop windows (ISO/IEC 14496-3 4.6.11), with their share.";
    public const string SizeShort = "The channel streams with eight short windows (ISO/IEC 14496-3 4.6.11), with their share.";

    public const string StatStereoCoding = "How each channel pair element codes its two channels, whether or not it has a common window: left/right stereo, mid/side stereo in at least one band, intensity stereo in at least one band, or both (ISO/IEC 14496-3 4.6.8). A pair with ms_mask_present 3, which the specification reserves, is counted apart.";
    public const string CodingLeftRight = "A channel pair with no mid/side band and no intensity stereo band: both channels are coded as they are (ISO/IEC 14496-3 4.6.8).";
    public const string CodingMidSide = "A channel pair with at least one band coded as mid/side and no intensity stereo band: ms_mask_present 1 with an ms_used flag set outside the intensity stereo bands, or 2, which sets every band (ISO/IEC 14496-3 4.6.8.1).";
    public const string CodingIntensity = "A channel pair with at least one band of its second channel in codebook 14 or 15, intensity stereo, and no mid/side band; a pair without a common window can code it too (ISO/IEC 14496-3 4.6.8.2).";
    public const string CodingMidSideAndIntensity = "A channel pair with at least one mid/side band and at least one intensity stereo band; in an intensity stereo band ms_used inverts the sign and does not code mid/side (ISO/IEC 14496-3 4.6.8.1, 4.6.8.2).";
    public const string CodingReserved = "A channel pair with ms_mask_present 3, which the specification reserves (ISO/IEC 14496-3 4.6.8.1); it counts here whatever its bands hold.";

    public const string StatCodedBandwidth = "The upper edge of the highest scalefactor band that a channel stream codes, max_sfb bands from the lowest, in whole hertz at the core's sample rate, the highest over the channel streams of each access unit or frame (ISO/IEC 14496-3 4.5.2, 4.6.11). It is a bound the stream states, not the encoder's lowpass setting: a band below it may be coded as zero, as noise substitution or as intensity stereo, and SBR adds content above the core.";
    public const string BandwidthLowest = "The lowest coded bandwidth of any access unit or frame.";
    public const string BandwidthMostCommon = "The coded bandwidth most access units or frames have; the lowest of them where several are equally common.";
    public const string BandwidthHighest = "The highest coded bandwidth of any access unit or frame.";

    public const string CodedBandwidthSeries = "The coded bandwidth of each access unit or frame: the upper edge of the highest scalefactor band that any of its channel streams codes, max_sfb bands from the lowest, in whole hertz at the core's sample rate (ISO/IEC 14496-3 4.5.2). It is the bound the stream states, read without decoding; bands below it may be coded as zero, as noise substitution or as intensity stereo, SBR adds content above the core, and it is not the encoder's lowpass setting.";
    public const string CodedBandwidthOverTimeSeries = "The highest coded bandwidth of the access units or frames that start in each second of audio. A stream of more than about 36 hours, or one stated at a very low sample rate, is graphed in spans of 2, 4 or more seconds.";

    public const string WindowSequencesSeries = "The window sequence of every channel stream of every access unit or frame, as the Window Sequences statistics count them (ISO/IEC 14496-3 4.5.2, 4.6.11); a raw data block of several elements gives a value for each of its channel streams.";
    public const string BlockSizesSeries = "The channel streams of Window Sequences grouped by the size of their transform: long, with the long start and long stop windows, and short.";
    public const string StereoCodingSeries = "How each channel pair element codes its channels, whether or not it has a common window, as the Stereo Coding statistics count them (ISO/IEC 14496-3 4.6.8); a unit without a channel pair has no value.";
}
