namespace Encspect.Formats.Aac;

// The category series of a stream's coding and the statistics nodes they are anchored to (docs/design/coding-graphs.md).
internal static partial class Explain
{
    public const string WindowOnlyLong = "window_sequence 0, ONLY_LONG_SEQUENCE: one long window for the whole frame (ISO/IEC 14496-3 4.5.2, 4.6.11).";
    public const string WindowLongStart = "window_sequence 1, LONG_START_SEQUENCE: a long window whose second half slopes toward the short windows that follow (ISO/IEC 14496-3 4.5.2, 4.6.11).";
    public const string WindowEightShort = "window_sequence 2, EIGHT_SHORT_SEQUENCE: eight short windows, each with an eighth of the frame's spectral lines (ISO/IEC 14496-3 4.5.2, 4.6.11).";
    public const string WindowLongStop = "window_sequence 3, LONG_STOP_SEQUENCE: a long window whose first half slopes up from the short windows before it (ISO/IEC 14496-3 4.5.2, 4.6.11).";

    public const string StatBlockSizes = "The channel streams grouped by the size of their transform: long, with the long start and long stop windows that lead into and out of short windows, and short (eight short windows). The window sequences, grouped as the Block Sizes of MPEG audio groups its block types (ISO/IEC 14496-3 4.6.11).";
    public const string SizeLong = "Channel streams with a long transform: only long, long start and long stop windows (ISO/IEC 14496-3 4.6.11).";
    public const string SizeShort = "Channel streams with eight short windows (ISO/IEC 14496-3 4.6.11).";

    public const string StatStereoCoding = "How each channel pair element codes its two channels, whether or not it has a common window: left/right stereo, mid/side stereo in at least one band, intensity stereo in at least one band, or both (ISO/IEC 14496-3 4.6.8). A pair with ms_mask_present 3, which the specification reserves, is counted apart.";
    public const string CodingLeftRight = "A channel pair with no mid/side band and no intensity stereo band: both channels are coded as they are (ISO/IEC 14496-3 4.6.8).";
    public const string CodingMidSide = "A channel pair with at least one band coded as mid/side and no intensity stereo band: ms_mask_present 1 with an ms_used flag set, or 2, which sets every band, on a band that neither channel codes with noise substitution or intensity stereo, (ISO/IEC 14496-3 4.6.8.1); FFmpeg 6.1.1 applies no mid/side on a noise substitution band of either channel, which is the rule counted here, where faad2 and fdk-aac skip fewer bands.";
    public const string CodingIntensity = "A channel pair with at least one band of its second channel in codebook 14 or 15, intensity stereo, and no mid/side band; a pair without a common window can code it too (ISO/IEC 14496-3 4.6.8.2).";
    public const string CodingMidSideAndIntensity = "A channel pair with at least one mid/side band and at least one intensity stereo band; in an intensity stereo band ms_used inverts the sign, and FFmpeg applies no mid/side in a noise substitution band either, so neither codes mid/side (ISO/IEC 14496-3 4.6.8.1, 4.6.8.2).";
    public const string CodingReserved = "A channel pair with ms_mask_present 3, which the specification reserves (ISO/IEC 14496-3 4.6.8.1); it counts here whatever its bands hold.";

    public const string StatCodedBandwidth = "The upper edge of the highest scalefactor band that a channel stream codes, max_sfb bands from the lowest, in whole hertz at the core's sample rate, the highest over the channel streams of each access unit, ADTS frame or ADIF block (ISO/IEC 14496-3 4.5.2, 4.6.11). It is a bound the stream states, not the encoder's lowpass setting: a band below it may be coded as zero, as noise substitution or as intensity stereo, and SBR adds content above the core.";
    public const string StatIntensityBound = "The lowest frequency from which the pairs of an access unit, ADTS frame or ADIF block code intensity stereo, in whole hertz at the core's sample rate: the lower edge of the lowest scalefactor band of any pair's second channel that uses codebook 14 or 15 (ISO/IEC 14496-3 4.6.8.2). The figure is what the stream states, not a measurement of the sound.";
    public const string BoundLowest = "The lowest intensity stereo bound of any access unit, frame or block.";
    public const string BoundHighest = "The highest intensity stereo bound of any access unit, frame or block.";
    public const string StatLastCoefficient = "The upper edge of the highest non-zero quantized spectral coefficient that a channel stream codes, in whole hertz at the core's sample rate, the highest over the channel streams of each access unit, ADTS frame or ADIF block (ISO/IEC 14496-3 4.6.3). It is read from the Huffman-coded spectral data, so it lies at or below the coded bandwidth, which max_sfb states; pulse data, noise substitution and intensity stereo are not counted, and SBR adds content above the core.";
    public const string LastCoefficientLowest = "The lowest last non-zero coefficient of any access unit, frame or block that has one.";
    public const string LastCoefficientMostCommon = "The last non-zero coefficient most access units, frames or blocks have; the lowest of them where several are equally common.";
    public const string LastCoefficientHighest = "The highest last non-zero coefficient of any access unit, frame or block.";
    public const string StatNonZero = "The quantized spectral coefficients of all channel streams that are not zero, counted in the Huffman-coded spectral data (ISO/IEC 14496-3 4.6.3); pulse data is not counted.";
    public const string StatEscapes = "The non-zero coefficients coded with an escape sequence: codebook 11 codes the value 16 as an escape_prefix and escape_word that give a value of 16 or more (ISO/IEC 14496-3 4.6.3.3). Shown with its share of the non-zero coefficients.";
    public const string LastCoefficientSeries = "The upper edge of the highest non-zero quantized spectral coefficient that any channel stream of each access unit, ADTS frame or ADIF block codes, in whole hertz at the core's sample rate (ISO/IEC 14496-3 4.6.3). It is read from the Huffman-coded spectral data and lies at or below the coded bandwidth; pulse data is not counted, and a unit whose coefficients are all zero has none.";
    public const string LastCoefficientOverTimeSeries = "The highest last non-zero coefficient of the access units, frames or blocks that start in each second of audio. A stream of more than about 36 hours, or one stated at a very low sample rate, is graphed in spans of 2, 4 or more seconds.";
    public const string EscapesSeries = "The share of the non-zero quantized coefficients of each access unit, ADTS frame or ADIF block that are coded with an escape sequence (codebook 11, ISO/IEC 14496-3 4.6.3.3), in percent; a unit without a non-zero coefficient has none.";
    public const string BandwidthLowest = "The lowest coded bandwidth of any access unit, frame or block.";
    public const string BandwidthMostCommon = "The coded bandwidth most access units, frames or blocks have; the lowest of them where several are equally common.";
    public const string BandwidthHighest = "The highest coded bandwidth of any access unit, frame or block.";

    public const string CodedBandwidthSeries = "The coded bandwidth of each access unit, ADTS frame or ADIF block: the upper edge of the highest scalefactor band that any of its channel streams codes, max_sfb bands from the lowest, in whole hertz at the core's sample rate (ISO/IEC 14496-3 4.5.2). It is the bound the stream states, read without decoding; bands below it may be coded as zero, as noise substitution or as intensity stereo, SBR adds content above the core, and it is not the encoder's lowpass setting.";
    public const string IntensityStereoBoundSeries = "The lowest frequency from which each access unit, ADTS frame or ADIF block codes intensity stereo, in whole hertz at the core's sample rate: the lower edge of the lowest scalefactor band of any pair's second channel that uses codebook 14 or 15 (ISO/IEC 14496-3 4.6.8.2); a unit without such a band has none.";
    public const string CodedBandwidthOverTimeSeries = "The highest coded bandwidth of the access units, frames or blocks that start in each second of audio. A stream of more than about 36 hours, or one stated at a very low sample rate, is graphed in spans of 2, 4 or more seconds.";

    public const string ShapeSine = "Channel streams whose window_shape selects the sine window (ISO/IEC 14496-3, 4.5.2).";
    public const string ShapeKbd = "Channel streams whose window_shape selects the Kaiser-Bessel derived window (ISO/IEC 14496-3, 4.5.2).";
    public const string FlagWithTns = "Channel streams that carry temporal noise shaping data (tns_data_present, ISO/IEC 14496-3, 4.6.9).";
    public const string FlagWithoutTns = "Channel streams without temporal noise shaping data.";
    public const string FlagWithPulses = "Channel streams that carry pulse data (pulse_data_present, ISO/IEC 14496-3, 4.6.3).";
    public const string FlagWithoutPulses = "Channel streams without pulse data.";
    public const string FlagWithNoise = "Channel streams that code at least one band with perceptual noise substitution (codebook 13, ISO/IEC 14496-3, 4.6.2).";
    public const string FlagWithoutNoise = "Channel streams without a band coded with perceptual noise substitution.";
    public const string FlagWithSbr = "Raw data blocks that hold SBR data in a fill element (an extension_payload, ISO/IEC 14496-3, 4.4.2.7 and 4.5.2).";
    public const string FlagWithoutSbr = "Raw data blocks without SBR data, and those that could not be read.";
    public const string FlagWithPs = "Raw data blocks whose SBR data carries the parametric stereo extension (ISO/IEC 14496-3, 4.4.2.7 and 4.5.2).";
    public const string FlagWithoutPs = "Raw data blocks without the parametric stereo extension, and those that could not be read.";
    public const string WindowShapesSeries = "The window shape of every channel stream of every unit, sine or Kaiser-Bessel derived (ISO/IEC 14496-3, 4.5.2), as the tally of Window Shapes counts them.";
    public const string TnsSeries = "Whether each channel stream of every unit carries temporal noise shaping data, as the tally of TNS counts them.";
    public const string PulseSeries = "Whether each channel stream of every unit carries pulse data, as the tally of Pulse Data counts them.";
    public const string NoiseSeries = "Whether each channel stream of every unit codes a band with perceptual noise substitution, as the tally of Noise Substitution counts them.";
    public const string SbrSeries = "Whether each raw data block holds SBR data, as the tally of SBR counts them; a block that could not be read has none.";
    public const string PsSeries = "Whether each raw data block's SBR data carries the parametric stereo extension, as the tally of Parametric Stereo counts them; a block that could not be read has none.";
    public const string MidSideBandsSeries = "The share of the scalefactor bands of each unit's channel pairs with a common window that are coded as mid/side, leaving out intensity stereo bands, in percent; a unit without such a pair has none.";
    public const string WindowSequencesSeries = "The window sequence of every channel stream of every access unit, frame or block, as the Block Usage statistics count them (ISO/IEC 14496-3 4.5.2, 4.6.11); a raw data block of several elements gives a value for each of its channel streams.";
    public const string BlockSizesSeries = "The channel streams of Block Usage grouped by the size of their transform: long, with the long start and long stop windows, and short.";
    public const string StereoCodingSeries = "How each channel pair element codes its channels, whether or not it has a common window, as the Stereo Coding statistics count them (ISO/IEC 14496-3 4.6.8); a unit without a channel pair has no value.";
}
