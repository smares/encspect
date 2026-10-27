namespace Encspect.Core;

// What the host measured of a stream's decoded audio (docs/design/spectral-analysis.md): plain data like StreamVerification,
// computed by the host and attached to StreamInfo.Measurements for every session of a measured analysis. It holds figures and
// grids, never nodes, so the tree is the same with or without measuring. Every figure is an integer in a fixed unit (whole
// hertz, tenths of a dB, samples), so no writer formats a floating-point number.
public sealed record StreamMeasurements
{
    public required MeasurementStatus Status { get; init; }

    // Why less than was asked was measured, as text for the front ends ("the stream decodes to DSD", "no decoder for TTA"); null
    // where everything asked was measured.
    public string? Reason { get; init; }

    // Samples per channel measured (not counting gaps), the position of the first one (untrimmed, as PcmBlock.Position), and
    // what the gaps skipped: the positions they covered, and their number.
    public long Samples { get; init; }

    public long FirstPosition { get; init; }

    public long LostSamples { get; init; }

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

    public SpectrogramGrid? Spectrogram { get; init; }

    public WaveformGrid? Waveform { get; init; }
}

public enum MeasurementStatus
{
    Complete,

    // Decoding ended before the stream did (the front end's stop, the decoders' budget, the measurer's own budget, a decoder that
    // failed, positions that went back); the figures cover what was measured.
    Stopped,

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

    // Sample frames that are exactly 0 in every channel (for a channel, samples that are 0): in the stretch that starts the measured samples
    // and in the one that ends them (the digital silence), and in all, wherever they are (the zero samples, which include a sine's zero crossings
    // and the zeros of quiet audio). When every sample is 0 the three are equal. A stretch ends at a gap.
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

    // The average level of each of the 512 rows (0 Hz to Nyquist) in tenths of a dB relative to a full-scale sine centered on a
    // bin, combined (all channels but the LFE) and per channel. A row with no power at all holds MeasurementLimits.NoPower.
    public required ReadOnlyMemory<short> Combined { get; init; }

    public required IReadOnlyList<ReadOnlyMemory<short>> PerChannel { get; init; }

    public required SpectralCutoff Cutoff { get; init; }

    public IReadOnlyList<SpectralCutoff> ChannelCutoffs { get; init; } = [];
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
