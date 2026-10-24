namespace Encspect.Formats.Ac3;

// The category series of a stream's audio blocks and the statistics node they are anchored to (docs/design/coding-graphs.md).
internal static partial class Explain
{
    public const string CategoryBlockSwitched = "A block in which at least one full bandwidth channel has blksw set: its transform of 512 points is split into two of 256 points (A/52 5.4.3.1, 7.9).";
    public const string CategoryBlockLong = "A block in which no channel has blksw set, or an E-AC-3 block of a frame whose blkswe is 0, which codes no block switch flags: every channel has one transform of 512 points (A/52 5.4.3.1; Annex E 2.2.4, whose audblk syntax sets blksw to 0 where blkswe is 0).";
    public const string CategoryCoupled = "A block with cplinu set: the high frequencies of the coupled channels are coded once, as a coupling channel with a coordinate for each channel and band (A/52 5.4.3.8, 7.4).";
    public const string CategoryNotCoupled = "A block with cplinu 0, or without the field, as in a program of fewer than two full bandwidth channels: every channel codes its own spectrum (A/52 5.4.3.8).";
    public const string CategoryRematrixed = "A block of a 2/0 program in which at least one rematrixing band carries the sum and the difference of left and right instead of left and right: mid/side (sum/difference) coding of the band (A/52 5.4.3.19, 5.4.3.20, 7.5).";
    public const string CategoryNotRematrixed = "A block in which no rematrixing band carries the sum and the difference (A/52 5.4.3.20), including a block of another audio coding mode than 2/0, which has no rematrixing, in a program whose independent frame is 2/0.";
    public const string CategorySpectralExtension = "A block with spxinu set: the high frequencies above the spectral extension begin frequency are rebuilt by the decoder from lower bands and noise, with the coordinates the block codes (A/52 Annex E 2.3.3.2).";
    public const string CategoryNoSpectralExtension = "A block with spxinu 0: every channel codes its spectrum up to its own bandwidth (A/52 Annex E 2.3.3.2).";

    private const string BlocksOfProgramFrame = "of each program frame, an AC-3 syncframe or an E-AC-3 independent frame with the dependent frames that follow it (A/52 Annex E 2.3.1.2), in the order the blocks are read";

    public const string BlockSwitchingSeries = "Whether each audio block read " + BlocksOfProgramFrame + ", has block switching in any channel, as the Blocks With Block Switching statistics count them (A/52 5.4.3.1). A block the analyzer does not read has no value.";
    public const string CouplingSeries = "Whether each audio block read " + BlocksOfProgramFrame + ", has coupling in use, as the Blocks With Coupling statistics count them (A/52 5.4.3.8). A block the analyzer does not read has no value.";
    public const string RematrixingSeries = "Whether each audio block read " + BlocksOfProgramFrame + ", has a rematrixing band coded as sum and difference, mid/side (sum/difference) coding of the band, as the Blocks With Rematrixing statistics count them (A/52 5.4.3.20). A block the analyzer does not read has no value.";
    public const string SpectralExtensionSeries = "Whether each audio block read " + BlocksOfProgramFrame + ", has spectral extension in use, as the Blocks With Spectral Extension statistics count them (A/52 Annex E 2.3.3.2). A block the analyzer does not read has no value.";

    public const string StatCodedBandwidth = "The upper edge of the highest frequency bin that the audio blocks of a program frame code, in whole hertz at the stream's sample rate: the end bin of each full bandwidth channel (37 + 3 × (chbwcod + 12)), the end of the coupling channel where coupling is in use (37 + 12 × (cplendf + 3)), or where spectral extension begins (25 + 12 × its begin sub-band), whichever is highest in any block read (A/52 5.4.3.24, 7.1.3; Annex E 3.3.3). The LFE channel is left out. It is a bound the stream states, not the encoder's lowpass setting, and spectral extension and coupling add content above or within it.";
    public const string BandwidthLowest = "The lowest coded bandwidth of any program frame.";
    public const string BandwidthMostCommon = "The coded bandwidth most program frames have; the lowest of them where several are equally common.";
    public const string BandwidthHighest = "The highest coded bandwidth of any program frame.";

    public const string CodedBandwidthSeries = "The coded bandwidth of each program frame: the upper edge of the highest bin that any full bandwidth channel or the coupling channel of its audio blocks codes, in whole hertz, bin k lying at k × sample rate / 512 (A/52 5.4.3.24, 7.1.3). A coupled channel's own range ends where coupling begins, and a channel in spectral extension ends where spectral extension begins (Annex E 3.3.3). It is the bound the stream states, read without decoding; the LFE channel is left out, and it is not the encoder's lowpass setting.";
    public const string CodedBandwidthOverTimeSeries = "The highest coded bandwidth of the program frames that start in each second of audio.";
}
