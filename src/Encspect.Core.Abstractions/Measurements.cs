namespace Encspect.Core;

// What the host measured of a stream's decoded audio (docs/design/spectral-analysis.md): plain data like StreamVerification,
// computed by the host and attached to StreamInfo.Measurements for every session of a measured analysis. It holds figures and
// grids, never nodes, so the tree is the same with or without measuring. Every figure is an integer in a fixed unit (whole
// hertz, tenths of a dB, samples), so no writer formats a floating-point number.
public sealed record StreamMeasurements
{
    public required MeasurementStatus Status { get; init; }

    // Why less than was asked was measured, as text for the front ends ("the stream decodes to DSD", "no decoder for <codec>"); null
    // where everything asked was measured.
    public string? Reason { get; init; }

    // Samples per channel measured (not counting gaps), the position of the first one (untrimmed, as PcmBlock.Position), and
    // what the gaps skipped: the positions they covered, and their number. The positions of a lost first or last unit are among them.
    public long Samples { get; init; }

    public long FirstPosition { get; init; }

    public long LostSamples { get; init; }

    // Of LostSamples, the positions before the first measured block and after the last one that the stream's coded units cover: a lost
    // first or last unit (the host gives the measurer the span of the units the parser handed to the decoding). The rest lies between
    // blocks. They are counted only for a stream whose decoding ended with its units, and each counts as a gap.
    public long LeadingLostSamples { get; init; }

    public long TrailingLostSamples { get; init; }

    // The lost samples between the first and the last measured block: the part of the span [FirstPosition, FirstPosition + Samples +
    // InnerLostSamples) that the grids and graphs cover.
    public long InnerLostSamples => LostSamples - LeadingLostSamples - TrailingLostSamples;

    // The positions of units the decoder read and decoded but whose output it held back (DecodeContext.Withhold), apart from lost samples:
    // a gap's positions are withheld where the decoder reported them and lost for the rest. WithheldByReason holds the samples for each
    // WithholdReason; it sums to WithheldSamples. The leading and trailing parts are counted as the lost ones are.
    public long WithheldSamples { get; init; }

    public long LeadingWithheldSamples { get; init; }

    public long TrailingWithheldSamples { get; init; }

    public WithheldCounts WithheldByReason { get; init; }

    public long InnerWithheldSamples => WithheldSamples - LeadingWithheldSamples - TrailingWithheldSamples;

    // The samples the decoder wrote that the stream's own syntax trims away (IDecodeSession.Trim) and that were left out of every figure: the positions
    // before the trimmed range's start and from its end on. Lost and withheld samples are counted at the coded stage, trimmed range or not.
    public long LeadingTrimmedSamples { get; init; }

    public long TrailingTrimmedSamples { get; init; }

    // The samples the stream's syntax trims inside the measured span (IDecodeSession.TrimInner), between the first and the last measured sample;
    // the grids and graphs cover them like inner lost samples.
    public long InnerTrimmedSamples { get; init; }

    public long TrimmedSamples => LeadingTrimmedSamples + InnerTrimmedSamples + TrailingTrimmedSamples;

    // Lost and withheld samples together: the positions of the span the measured blocks cover that hold no measured sample.
    public long SkippedSamples => LostSamples + WithheldSamples;

    public long LeadingSkippedSamples => LeadingLostSamples + LeadingWithheldSamples;

    public long TrailingSkippedSamples => TrailingLostSamples + TrailingWithheldSamples;

    public long InnerSkippedSamples => InnerLostSamples + InnerWithheldSamples;

    public int Gaps { get; init; }

    public int SampleRate { get; init; }

    // The channels measured, in the stream's order; the speakers where the decoder stated them.
    public IReadOnlyList<Speaker>? ChannelLayout { get; init; }

    // The stream's channels, and how many were measured (at most MeasurementLimits.MaxChannels).
    public int Channels { get; init; }

    public int ChannelsMeasured { get; init; }

    // True where a float sample beyond 1,024 times full scale was clamped to that; a peak that reads +60.2 dBFS is the clamp, not the file.
    public bool HasClampedSamples { get; init; }

    // Float samples that were NaN or infinite; they were measured as 0.
    public long NonFiniteSamples { get; init; }

    public SpectrumFigures? Spectrum { get; init; }

    public LevelFigures? Levels { get; init; }

    public LoudnessFigures? Loudness { get; init; }

    public TruePeakFigures? TruePeak { get; init; }

    public SpectrogramGrid? Spectrogram { get; init; }

    public WaveformGrid? Waveform { get; init; }
}

public enum MeasurementStatus
{
    Complete,

    // Decoding ended before the stream did (the front end's stop, the decoders' budget, the measurer's own budget, a decoder that
    // failed, positions that went back); the figures cover what was measured.
    Stopped,

    // The stream was measured to its end, but frames were not decoded or were delivered in part (a frame that failed may have written
    // blocks first); the figures cover what the decoder delivered, which Reason counts against the samples decoded.
    Incomplete,

    // Nothing, or less than asked, can be measured: no decoder, DSD, a rate of 0 Hz, a configuration the decoder refused, the
    // memory bound of the measurements.
    NotPossible,

    // A block's channels or format differed from the declared output (no decoder does this today).
    FormatChanged,

    // A measurement's bug (measure.failed); the figures are dropped.
    Failed,
}

public static class MeasurementLimits
{
    // The most channels measured on their own; the combined figures cover those.
    public const int MaxChannels = 32;

    // The rows of the average spectrum and of the spectrogram: linear, 0 Hz to Nyquist.
    public const int Rows = 512;

    // The most columns of the grids.
    public const int MaxColumns = 1024;

    // The spectrogram's cell value of a level of 0 dB is this offset plus 0; a cell of 0 means no data.
    public const int GridOffset = 200;

    // The sentinel of a spectrum row that holds no power at all.
    public const short NoPower = short.MinValue;

    // The sentinel of a level in tenths of a dB that is minus infinity: a channel whose samples are all 0 has no peak, no RMS level
    // and a DC offset of exactly 0 has no level.
    public const int NoLevel = int.MinValue;

    // The shortest run of consecutive samples at an extreme that counts as clipping. A convention of the design, not a specification's.
    public const int MinimumClippingRun = 3;

    // The fewest bits in use at which the extremes of the bits in use replace those of the declared width: with fewer, the low bits that are 0 come from the
    // signal more often than from padding, and the positive extreme would be far from full scale.
    public const int MinimumBitsInUse = 8;

    // The lowest sample rate loudness is measured at: the K-weighting derived for other rates drifts from the 48 kHz response by 0.5 dB at
    // 8,000 Hz and is unstable below 3,364 Hz.
    public const int MinimumLoudnessRate = 8000;

    // The most points of the short-term loudness series; adjacent points are merged as the stream grows.
    public const int MaxSeriesPoints = 1024;
}

// The levels of a stream's decoded audio (the levels topic): sample peak, RMS, DC offset, clipping runs, digital silence and bit-depth
// usage, per measured channel and for the stream. Integer samples are measured exactly, floats in double. Levels are tenths of a dB
// relative to full scale (NoLevel for minus infinity), counts are samples, and the DC offset is in millionths of a percent of full
// scale, so no writer formats a floating-point number.
public sealed record LevelFigures
{
    // Integer or float samples, and for integers the bits of a sample (full scale is 2^(BitsPerSample - 1)); a float's full scale is 1.0.
    public required SampleFormat Format { get; init; }

    public required int BitsPerSample { get; init; }

    // The stream's figures: the highest peak, the RMS level over all measured channels together, the DC offset of the channel with the
    // largest one, the clipping runs added up (the longest the longest), the silence of the sample frames in which every measured channel
    // is 0, and the bit usage of all channels together.
    public required ChannelLevels Stream { get; init; }

    // The measured channels, in the stream's order.
    public required IReadOnlyList<ChannelLevels> Channels { get; init; }

    // Integers only (null for floats, and where no span was measured): the low bits unused over spans of 32 samples.
    public LowBitsSpanFigures? Spans { get; init; }

    // The bits whose extremes the clipping runs are counted at (the clipping topic): for integers the declared width, or the bits in use (the width less the
    // stream's Low Bits Unused) from MinimumBitsInUse bits on; for floats 16 or 24 where every sample is on that grid, null otherwise (full scale, at or beyond).
    public int? ClippingBitsInUse { get; init; }

    // The shortest stretch of sample frames that counts as digital silence, in samples (10 ms at the stream's rate, rounded up), or 0 where the stream states
    // no sample rate and the stretch figures are not measured.
    public int MinimumSilentStretch { get; init; }
}

// The low bits that are 0 in every sample of a span of 32 consecutive samples of one channel (the low-bits-per-span topic), counted over the
// stream's spans, all channels together. Spans lie on the grid of 32 stream positions; one that is cut short by a gap, by withheld output or by
// the end of the stream, or that starts late, has fewer samples. All figures are integers: counts of spans, changes, times and samples.
public sealed record LowBitsSpanFigures
{
    // The spans that were measured, silent ones (every sample 0, which have no count) apart.
    public long Spans { get; init; }

    public long SilentSpans { get; init; }

    // Spans whose largest sample needs fewer than ThinMargin bits above the zeroed ones: counted in the distribution and apart, and not in the changes.
    public long ThinSpans { get; init; }

    // Spans of fewer than 32 samples: counted in the distribution, not in the changes.
    public long ShortSpans { get; init; }

    // The number of spans (silent ones excluded) for each count of low bits unused, from 0 up to the highest count that occurs.
    public required IReadOnlyList<long> Distribution { get; init; }

    // The spans whose count is above the stream's Low Bits Unused.
    public long SpansAboveLowBitsUnused { get; init; }

    // The spans, neither silent, short nor thin, whose count differs from the same channel's last such span.
    public long Changes { get; init; }

    // The span positions (moments) at which some channel's count changes, however many channels change there.
    public long ChangeMoments { get; init; }

    // The grid of the changes in samples (a power of two from MinimumGrid to MaximumGrid), or 0 where none, with the moments within one span of the phase with the most moments.
    public int Grid { get; init; }

    public long MomentsOnGrid { get; init; }

    // For a stream of several channels: the times (span positions at which no channel's span is silent) and those at which all channels have the same count.
    public long Times { get; init; }

    public long TimesWithEqualCounts { get; init; }

    public const int SpanLength = 32;

    // A span is thin when its magnitude is fewer than this many bits above its count.
    public const int ThinMargin = 4;

    public const int MinimumMomentsForGrid = 20;

    public const int MinimumGrid = 256;

    public const int MaximumGrid = 65536;
}

// The levels of one channel, or of the stream.
public sealed record ChannelLevels
{
    // The largest sample magnitude in tenths of a dB relative to full scale; the most negative integer code counts as full scale.
    public int SamplePeakTenths { get; init; } = MeasurementLimits.NoLevel;

    // The root mean square in tenths of a dB relative to full scale, where a full-scale square wave reads 0.
    public int RmsTenths { get; init; } = MeasurementLimits.NoLevel;

    // The mean of the samples, signed, in millionths of a percent of full scale, and the magnitude of it in tenths of a dB.
    public long DcOffsetMicropercent { get; init; }

    public int DcOffsetTenths { get; init; } = MeasurementLimits.NoLevel;

    // Runs of at least MinimumClippingRun consecutive samples at the same extreme: how many, the samples in them and the longest.
    public long ClippingRuns { get; init; }

    public long ClippedSamples { get; init; }

    public long LongestClippingRun { get; init; }

    // Sample frames that are exactly 0 in every channel (for a channel, samples that are 0). Digital silence is a stretch of them that lasts at least
    // MinimumSilentStretch samples: the stretches (how many, the samples in them and the longest, those at the start and the end included), and the one that
    // starts the measured samples and the one that ends them (0 where shorter than the minimum). The zero samples are all of them, wherever they are, which
    // include a sine's zero crossings and the zeros of quiet audio. When every sample is 0 and the stream is long enough the first two are equal to the
    // zero samples. A stretch ends at a gap, and each part must reach the minimum on its own. All are 0 where the minimum is not measured.
    public long SilentStretches { get; init; }

    public long SilentSamples { get; init; }

    public long LongestSilentStretch { get; init; }

    public long SilentAtStart { get; init; }

    public long SilentAtEnd { get; init; }

    public long ZeroSamples { get; init; }

    // Integers: the low bits that are 0 in every sample (null where every sample is 0), and the largest sample magnitude as a code.
    public int? LowBitsUnused { get; init; }

    public long HighestMagnitude { get; init; }

    // Floats: whether every sample is a whole multiple of 2^-15 (2^-23) in [-1, 1).
    public bool HasOnly16BitValues { get; init; }

    public bool HasOnly24BitValues { get; init; }
}

public sealed record SpectrumFigures
{
    // The points of each FFT frame; a frame lasts 85 to 170 ms at the common rates.
    public required int FftSize { get; init; }

    // The frames averaged.
    public int Frames { get; init; }

    // The bins that make up one row: FftSize / 1,024.
    public int BinsPerRow => FftSize / (MeasurementLimits.Rows * 2);

    // The average level of each of the 512 rows (0 Hz to Nyquist) in tenths of a dB relative to the power of one bin for a
    // full-scale sine centered on it (a row is the mean of its BinsPerRow bins and the window spreads the sine over the bin and its two neighbors in the ratio 1 : 0.25 : 0.25, so that sine reads 10 log10(f / BinsPerRow) dB in its row, f from 1.25 to 1.5, and 0 dB where a row is one bin), combined (all channels but the LFE) and per channel. A row with no power at all holds MeasurementLimits.NoPower.
    public required ReadOnlyMemory<short> Combined { get; init; }

    public required IReadOnlyList<ReadOnlyMemory<short>> PerChannel { get; init; }

    public required SpectralCutoff Cutoff { get; init; }

    public IReadOnlyList<SpectralCutoff> ChannelCutoffs { get; init; } = [];

    // Where the combined spectrum meets a flat floor that reaches the top of the search, and the same for each channel (see the
    // spectral-bandwidth topic).
    public SpectralBandwidth Bandwidth { get; init; } = new();

    public IReadOnlyList<SpectralBandwidth> ChannelBandwidths { get; init; } = [];

    // The frequency at which the most frames show a ceiling of their own, on the combined spectrum (see the spectral-cutoff topic).
    public PartialCutoff PartialCutoff { get; init; } = new();
}

// Why no spectral bandwidth is found.
public enum SpectralBandwidthReason
{
    None,

    // The floor's run reaches down to the start of the search or leaves no band under it: the spectrum is flat from the top down.
    ReachesTop,

    // The band under the floor's run is less than 20 dB above the floor: a slope, not a floor.
    Slope,
}

// The spectral bandwidth of one spectrum: the highest frequency at which the average spectrum stands at least 20 dB above a flat floor
// that reaches the top of the search and is at least 1 kHz wide. Frequencies are whole hertz at the lower edge of a row, the level in
// tenths of a dB.
public sealed record SpectralBandwidth
{
    // False for a rate under 8,000 Hz, where nothing is searched.
    public bool IsSearched { get; init; } = true;

    public bool IsFound { get; init; }

    // The lower edge of the floor's run; 0 where none is found.
    public int Frequency { get; init; }

    // The floor's mean level: the band that ends at the top of the search (row 506); 0 where none is found.
    public int FloorTenthsOfDb { get; init; }

    public SpectralBandwidthReason Reason { get; init; }
}

// The partial cutoff: the row at which the most frames with signal show a ceiling of their own, which finds an edge that only part
// of the stream has. Where none is found the figures are those of the row that came closest.
public sealed record PartialCutoff
{
    // False for a rate under 8,000 Hz, where nothing is searched.
    public bool IsSearched { get; init; } = true;

    public bool IsFound { get; init; }

    // Whole hertz at the upper edge of the row; 0 where no frame showed a ceiling.
    public int Frequency { get; init; }

    public int FramesShowing { get; init; }

    public int FramesWithSignal { get; init; }
}

// The spectral cutoff of one spectrum (see the spectral-cutoff topic). All frequencies are whole hertz at the upper edge of a row,
// the levels tenths of a dB.
public sealed record SpectralCutoff
{
    // False for a rate under 8,000 Hz, where no cutoff is searched.
    public bool IsSearched { get; init; } = true;

    public bool IsFound { get; init; }

    // The edge, from the frames' own steps: walking up from the step, the last row at which half of the frames with signal show one, less the band's width
    // plus one row, and never below the step; 0 where none is found.
    public int Frequency { get; init; }

    // Where the transition to the edge starts; 0 where none is found.
    public int TransitionStart { get; init; }

    // Where the largest step lies (found or not), and its size.
    public int StepFrequency { get; init; }

    public int StepTenthsOfDb { get; init; }

    // The frames with signal that show an own step of 15 dB at the step's row, and the frames with signal.
    public int FramesShowing { get; init; }

    public int FramesWithSignal { get; init; }
}

// The average power over time and frequency: 512 rows (0 Hz to Nyquist, each the mean power of its bins) by Columns columns of
// 2^k FFT hops. Power holds one byte a cell, column by column (the cell of column c and row r is at c * Rows + r): the level in
// whole dB plus MeasurementLimits.GridOffset, clamped to 1 to 220; 0 is no data.
public sealed record SpectrogramGrid
{
    public required int Columns { get; init; }

    public required int HopsPerColumn { get; init; }

    public required int HopSamples { get; init; }

    public required ReadOnlyMemory<byte> Power { get; init; }
}

// The smallest and largest sample of each channel in each column of the spectrogram's time axis, in 16-bit sample codes, channel by
// channel (the value of channel c and column i is at c * Columns + i). A column without data holds 0 and 0.
public sealed record WaveformGrid
{
    public required int Columns { get; init; }

    public required int Channels { get; init; }

    public required ReadOnlyMemory<short> Minimum { get; init; }

    public required ReadOnlyMemory<short> Maximum { get; init; }
}

// Why a loudness figure has no value although the stream was measured.
public enum LoudnessAbsence
{
    // The figure has a value (or is minus infinity, a level of digital silence).
    None,

    // Not one complete block: the stream is shorter than 400 ms (momentary loudness, integrated loudness) or than 3 s (short-term
    // loudness, loudness range), or every stretch between gaps is.
    TooShort,

    // Blocks exist, but none is above the absolute gate of -70 LUFS.
    BelowGate,
}

// The loudness of a stream (ITU-R BS.1770-5 and EBU Tech 3341/3342; the loudness and loudness-range topics), measured from the decoded
// audio. Levels are tenths of a LUFS or of a LU (MeasurementLimits.NoLevel for minus infinity), rounded to the nearest with halves up.
public sealed record LoudnessFigures
{
    // Why nothing was measured (the rate is below 8,000 Hz or not stated, the channel positions are not stated); null where loudness was measured.
    public string? NotMeasured { get; init; }

    // The weight of each measured channel in hundredths (100 and 141; 0 for an LFE, which BS.1770 leaves out).
    public IReadOnlyList<int> ChannelWeights { get; init; } = [];

    // Gated loudness over the whole stream (BS.1770-5 equation 5) and why it is missing.
    public int IntegratedTenths { get; init; } = MeasurementLimits.NoLevel;

    public LoudnessAbsence IntegratedAbsence { get; init; }

    // The spread of the short-term loudness between its 10th and 95th percentile (Tech 3342), and the two percentiles in LUFS.
    public int RangeTenths { get; init; }

    public int RangeLowTenths { get; init; } = MeasurementLimits.NoLevel;

    public int RangeHighTenths { get; init; } = MeasurementLimits.NoLevel;

    public LoudnessAbsence RangeAbsence { get; init; }

    // The highest momentary (400 ms) and short-term (3 s) loudness (the first every 20 ms, the second every 100 ms), and why one is missing.
    public int MaximumMomentaryTenths { get; init; } = MeasurementLimits.NoLevel;

    public LoudnessAbsence MomentaryAbsence { get; init; }

    public int MaximumShortTermTenths { get; init; } = MeasurementLimits.NoLevel;

    public LoudnessAbsence ShortTermAbsence { get; init; }

    // The gating blocks (400 ms every 100 ms), those above the absolute gate and those above both gates.
    public long Blocks { get; init; }

    public long BlocksAboveAbsoluteGate { get; init; }

    public long BlocksAboveRelativeGate { get; init; }

    // Whether the integrated loudness and the loudness range gate each block and each short-term value on its own (BS.1770-5 equation 7,
    // Tech 3342). False for a stream with more than 131,072 of either above the absolute gate (about 3.6 hours), where the gates work on a
    // histogram of 0.01 LU bins and the integrated loudness can be off by up to -10 log10(1 - p) LU, p being the share of the passing blocks
    // in the gate's bin.
    public bool IsGatedExactly { get; init; } = true;

    // The short-term loudness over the stream in LUFS at most MaxSeriesPoints points (x: seconds since the first measured sample at the
    // end of the 3 s window of the first value a point covers; adjacent values are averaged in energy as the stream grows); null where none.
    public Series? ShortTerm { get; init; }

    // How many 100 ms steps each point of the series covers (a power of 2).
    public int StepsPerPoint { get; init; } = 1;
}

// The true peak of a stream (ITU-R BS.1770-5 Annex 2): the highest value of the oversampled waveform, in tenths of a dB relative to
// full scale (dBTP), per measured channel and for the stream.
public sealed record TruePeakFigures
{
    // Why nothing was measured (the sample rate is not stated); null where the true peak was measured.
    public string? NotMeasured { get; init; }

    // The oversampling: 4 below 96 kHz, 2 from 96 kHz up to 192 kHz, and 1 (the sample peak) from 192 kHz.
    public int Oversampling { get; init; } = 4;

    public int StreamTenths { get; init; } = MeasurementLimits.NoLevel;

    public IReadOnlyList<int> ChannelTenths { get; init; } = [];
}
