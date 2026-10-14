namespace Encspect.Formats.Aac;

// The SBR layout of FAAC 2.x's HE-AAC encoder (libfaac/sbr_bitstream.c and sbr.c, SbrInit and
// SbrUpdate, in 2.0, 2.1 and 2.2), which names the encoder with medium confidence where no tool atom
// does. FAAC's writer is fixed in ways other encoders' are not:
// - every sbr_header sends bs_header_extra_1 and not bs_header_extra_2, with bs_noise_bands 0, a
//   bs_start_freq of 15 and a bs_xover_band of 0; 2.0 and 2.1 add bs_freq_scale 0 with a
//   bs_stop_freq of 10, 2.2 bs_freq_scale 1 to 3 with bs_alter_scale 0, bs_amp_res 1 and a
//   bs_stop_freq of 10 to 13 (SBR_STOP_FREQ_MIN and MAX) by the sample rate;
// - 2.2 repeats the header every SBR_HEADER_PERIOD (30) access units, so two headers of one element are a
//   multiple of 30 access units apart. 2.0 and 2.1 count the same way, but repeat it in the last access
//   units of some files, which are flush frames: the decision to send the header is made in SbrEncode,
//   which flush frames skip (2.0 never calls doHEAACFrame without samples, 2.1's SbrContextProcessFrame
//   returns before SbrEncode), so a stale "send" stays while frameCount, counted on every write, goes
//   on. The spacing of their headers is not held to the period;
// - the sbr_data is plain: no extra data bits, no coupling, only FIXFIX and VARFIX frame classes,
//   every envelope and noise floor coded across frequency (write_sbr_dtdf writes zeros), no
//   sinusoids and no extended data;
// - 2.0 and 2.1 write one SBR payload after the whole frame, typed as a channel pair element for any
//   channel count above one (channels.c, BuildFrame), so with more than two channels the payload has
//   no channel element to belong to and the stream is not named; 2.2 writes one per element.
// The layout rests on the writers and on sweeps of the three releases over sample rates, mono and stereo
// and bitrates (check_faac_sbr.py). It is held against other encoders' HE-AAC: fdkaac's fixtures and
// sweeps never match it, and neither do the MPEG-4 audio SBR conformance streams and Coding Technologies'
// aacPlus decoder check streams of FFmpeg's FATE suite (check_sbr_samples.py), a cross-encoder basis like the
// one exhale's rule has in the xHE-AAC samples; Nero's, Apple's and Dolby's encoders were
// not at hand (see the encoder detection article). Only the layout of a stream that holds SBR data and
// no unread SBR data counts.
internal static class FaacSbr
{
    // SBR_HEADER_PERIOD.
    public const int HeaderPeriod = 30;

    // Access units with SBR data that the layout needs before it names an encoder: 20, as for any
    // evidence that is a behavior (the plan's guidance). The header's constants alone would not justify
    // fewer: they are a handful of fields, and the plain data (no coupling, no time-coded envelopes)
    // is what makes the layout specific, which needs frames to show.
    private const int MinimumPayloads = 20;

    public static bool HeaderFits(SbrHeader header) =>
        header is { HasHeaderExtra1: true, HasHeaderExtra2: false, NoiseBands: 0, StartFrequency: 15, XoverBand: 0 }
        && header switch
        {
            { FrequencyScale: 0, StopFrequency: 10 } => true,
            { FrequencyScale: >= 1 and <= 3, AlterScale: 0, AmpRes: 1, StopFrequency: >= 10 and <= 13 } => true,
            _ => false,
        };

    // Whether a header that came gap access units after the one before keeps FAAC's period.
    public static bool OnPeriod(SbrHeader header, int gap) => header.FrequencyScale == 0 || gap % HeaderPeriod == 0;

    public static bool IsLayout(CodingTally tally) =>
        tally is { SbrHeaders: > 0, SbrUnread: 0 }
        && tally.SbrPayloads >= MinimumPayloads
        && tally.SbrPlainHeaders == tally.SbrHeaders
        && tally.SbrPlainPayloads == tally.SbrPayloads
        && tally.SbrHeadersOnPeriod;
}
