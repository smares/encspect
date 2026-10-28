using System.Globalization;
using Encspect.Core;

namespace Encspect.Host;

// One line of a stream's measurements in the text output and the front ends: the figure's name (the names the BWF bext chunk uses
// for the loudness values it states, where the figure is the same kind), its value text, and the description and topic that --explain
// and the GUI's hover text show, as a node's have. Constant strings, never built per stream.
public sealed record MeasurementLine(string Name, string Text, string Description, string? Topic);

// How measurements read: the lines under a stream's "Verified:" line, and the units of the integers the model holds.
public static class MeasurementText
{
    public const string MeasuredDescription = "What the host computed from the stream's decoded audio, as the decoder wrote it before any trimming. These figures are measured, not written by the file's creator.";

    public const string SpectralCutoffDescription = "The frequency where the frames' own steps end, found by walking up from the largest step of the average spectrum: never below that step, and equal to it on a sharp edge. It comes with the size of the step, where the transition to it starts and how many of the frames with signal show a step there. It is a measured edge, not the lowpass frequency an encoder states.";

    public const string MeasuredTopic = Topics.Measurements;

    public const string SpectralCutoffTopic = Topics.SpectralCutoff;

    public const string SpectrogramDescription = "The average power over time and frequency of the decoded audio: 512 rows from 0 Hz to Nyquist and up to 1,024 columns of the FFT hops, with 0 dB the level of a full-scale sine centered on a bin.";

    public const string SpectrogramTopic = Topics.Spectrogram;

    public const string WaveformDescription = "The smallest and largest sample of each channel in each column of the spectrogram's time axis, in 16-bit sample codes.";

    public const string SpectrumDescription = "The average level of each of 512 rows from 0 Hz to Nyquist over all frames, in tenths of a dB relative to a full-scale sine centered on a bin, for the channels together (all but the LFE) and for each channel.";

    public const string SamplePeakDescription = "The largest sample magnitude of each channel in decibels relative to full scale, where the most negative integer code counts as full scale; the stream's figure is the highest of the channels. A channel whose samples are all 0 has no peak.";

    public const string RmsDescription = "The root mean square of the samples in decibels relative to full scale, where a full-scale square wave reads 0 dBFS and a full-scale sine 3.01 dB lower. The stream's figure covers all measured channels together.";

    public const string DcOffsetDescription = "The mean of each channel's samples as a percentage of full scale and in decibels relative to full scale; the stream's figure is that of the channel with the largest offset.";

    public const string ClippingDescription = "Runs of at least 3 consecutive samples at the same extreme: the most positive or the most negative integer code, or a float at or beyond full scale. Three is a convention of this analysis, not of a specification, and a run ends at a gap.";

    public const string DigitalSilenceDescription = "The sample frames in which every channel is exactly 0 and that start the measured samples without a break, and those that end them. A stretch ends at a gap in the decoded blocks.";

    public const string ZeroSamplesDescription = "All the sample frames in which every channel is exactly 0, wherever they are: the digital silence, and also the zeros of quiet audio and of a sine's zero crossings. It is a count of samples, not a stretch of silence.";

    public const string LowBitsUnusedDescription = "The low bits that are 0 in every sample of the stream, found from the OR of all samples, and the largest sample magnitude reached. It is a figure of the samples, not of the file's header.";

    public const string HasOnly16BitValuesDescription = "Whether every float sample is a whole multiple of 2^-15 within [-1, 1), the grid of 16-bit integer audio. It is a figure of the samples, not of the file's header.";

    public const string HasOnly24BitValuesDescription = "Whether every float sample is a whole multiple of 2^-23 within [-1, 1), the grid of 24-bit integer audio. It is a figure of the samples, not of the file's header.";

    public const string LoudnessDescription = "The loudness of the decoded audio as ITU-R BS.1770-5 defines it: the signal is K-weighted, its mean square taken over blocks of 400 ms and summed over the channels with their weights, and the blocks are gated. It is computed from the decoded audio; a loudness the file states is another figure.";

    public const string IntegratedLoudnessDescription = "The loudness of the whole stream (BS.1770-5 equation 5): blocks of 400 ms every 100 ms, K-weighted and channel-weighted, keeping those above -70 LUFS and above a gate 10 LU below the average loudness of those. It is computed from the decoded audio, not written by the file's creator.";

    public const string LoudnessRangeDescription = "The spread of the short-term loudness (3 s windows every 100 ms) between its 10th and its 95th percentile, after gating at -70 LUFS and 20 LU below the average, as EBU Tech 3342 defines it. It is computed from the decoded audio, not written by the file's creator.";

    public const string MaximumMomentaryLoudnessDescription = "The highest momentary loudness: the loudness of 400 ms blocks, rectangular, taken every 100 ms (EBU R 128). It is computed from the decoded audio, not written by the file's creator.";

    public const string MaximumShortTermLoudnessDescription = "The highest short-term loudness: the loudness of 3 s windows taken every 100 ms (EBU R 128). It is computed from the decoded audio, not written by the file's creator.";

    public const string TruePeakDescription = "The highest value of the waveform between the samples, found by oversampling with the filter of ITU-R BS.1770-5 Annex 2 (4 times below 96 kHz, 2 times from 96 kHz, none from 192 kHz, where it is the sample peak), in dBTP relative to full scale. Never below the sample peak; the stream's figure is the highest channel's.";

    public const string ShortTermSeriesDescription = "The short-term loudness (a window of 3 s) in LUFS, one value every 100 ms, merged in pairs as the stream grows so that at most 1,024 points remain; each point is the energy average of the values it covers, at the end of the window of the first of them, and a silent stretch reads -70 LUFS.";

    public const string LoudnessPanelDescription = "The short-term loudness (a window of 3 s) over time, with the figures of the loudness measurement: the integrated loudness, the loudness range, the maxima and the true peak. Measured from the decoded audio, not written by the file's creator.";

    public const string LoudnessTopic = Topics.Loudness;

    public const string LoudnessRangeTopic = Topics.LoudnessRange;

    public const string TruePeakTopic = Topics.TruePeak;

    public const string LevelsTopic = Topics.Levels;

    public const string ClippingTopic = Topics.Clipping;

    public const string DcOffsetTopic = Topics.DcOffset;

    public const string DigitalSilenceTopic = Topics.DigitalSilence;

    public const string BitDepthUsageTopic = Topics.BitDepthUsage;

    // The header line's text after "Measured: ", then the figure lines.
    public static IReadOnlyList<MeasurementLine> Lines(StreamMeasurements m, bool isLossy)
    {
        ArgumentNullException.ThrowIfNull(m);

        var lines = new List<MeasurementLine> { new("Measured", Header(m, isLossy), MeasuredDescription, MeasuredTopic) };
        if (m.Spectrum is { } spectrum && m.Status != MeasurementStatus.Failed)
        {
            lines.Add(new MeasurementLine("Spectral Cutoff", Cutoff(spectrum.Cutoff), SpectralCutoffDescription, SpectralCutoffTopic));
        }

        if (m.Levels is { } levels && m.Status != MeasurementStatus.Failed)
        {
            AddLevels(lines, m, levels);
        }

        // The order of the bext chunk's loudness fields: integrated loudness, loudness range, maximum true peak level, then the maxima of the
        // momentary and the short-term loudness.
        if (m.Loudness is { } loudness && m.Status != MeasurementStatus.Failed)
        {
            AddLoudness(lines, m, loudness, isMaxima: false);
        }

        if (m.TruePeak is { } truePeak && m.Status != MeasurementStatus.Failed)
        {
            AddTruePeak(lines, m, truePeak);
        }

        if (m.Loudness is { NotMeasured: null } maxima && m.Status != MeasurementStatus.Failed)
        {
            AddLoudness(lines, m, maxima, isMaxima: true);
        }

        return lines;
    }

    private static void AddLoudness(List<MeasurementLine> lines, StreamMeasurements m, LoudnessFigures l, bool isMaxima)
    {
        if (l.NotMeasured is { } reason)
        {
            lines.Add(new MeasurementLine("Loudness", $"not measured ({reason})", LoudnessDescription, LoudnessTopic));
            return;
        }

        string block = ShortStretch(m, "400 ms");
        string window = ShortStretch(m, "3 s");
        if (!isMaxima)
        {
            lines.Add(new MeasurementLine("Integrated Loudness", Loudness(l.IntegratedTenths, l.IntegratedAbsence, "LUFS", block), IntegratedLoudnessDescription, LoudnessTopic));
            lines.Add(new MeasurementLine("Loudness Range", Loudness(l.RangeTenths, l.RangeAbsence, "LU", window), LoudnessRangeDescription, LoudnessRangeTopic));
            return;
        }

        lines.Add(new MeasurementLine("Maximum Momentary Loudness", Loudness(l.MaximumMomentaryTenths, l.MomentaryAbsence, "LUFS", block), MaximumMomentaryLoudnessDescription, LoudnessTopic));
        lines.Add(new MeasurementLine("Maximum Short-Term Loudness", Loudness(l.MaximumShortTermTenths, l.ShortTermAbsence, "LUFS", window), MaximumShortTermLoudnessDescription, LoudnessTopic));
    }

    private static void AddTruePeak(List<MeasurementLine> lines, StreamMeasurements m, TruePeakFigures t)
    {
        if (t.NotMeasured is { } reason)
        {
            lines.Add(new MeasurementLine("Maximum True Peak Level", $"not measured ({reason})", TruePeakDescription, TruePeakTopic));
            return;
        }

        List<string> names = ChannelNames(m, t.ChannelTenths.Count);
        string basis = t.Oversampling switch
        {
            4 => string.Empty,
            2 => "; oversampled 2 times",
            _ => "; the sample peak, as the rate is 192,000 Hz or more",
        };
        lines.Add(new MeasurementLine("Maximum True Peak Level", $"{SignedTenths(t.StreamTenths)} dBTP{PerChannel(names, t.ChannelTenths.Select(SignedTenths))}{basis}", TruePeakDescription, TruePeakTopic));
    }

    // Why a stream has no block: it is shorter than the block, or no stretch between gaps is.
    private static string ShortStretch(StreamMeasurements m, string length) =>
        m.Gaps == 0 ? $"the stream is shorter than {length}" : $"no stretch between gaps lasts {length}";

    private static string Loudness(int tenths, LoudnessAbsence absence, string unit, string shortReason) => absence switch
    {
        LoudnessAbsence.TooShort => $"none ({shortReason})",
        LoudnessAbsence.BelowGate => "none (every block is below the absolute gate of -70 LUFS)",
        _ => $"{Tenths(tenths)} {unit}",
    };

    // Tenths of a unit with a sign on a positive value, for the peaks that can pass full scale.
    public static string SignedTenths(int tenths) => tenths > 0 ? "+" + Tenths(tenths) : Tenths(tenths);

    private static void AddLevels(List<MeasurementLine> lines, StreamMeasurements m, LevelFigures levels)
    {
        ChannelLevels stream = levels.Stream;
        IReadOnlyList<ChannelLevels> channels = levels.Channels;
        List<string> names = ChannelNames(m, channels.Count);
        bool isFloat = levels.Format != SampleFormat.Integer;

        lines.Add(new MeasurementLine("Sample Peak", $"{Tenths(stream.SamplePeakTenths)} dBFS{PerChannel(names, channels.Select(c => Tenths(c.SamplePeakTenths)))}", SamplePeakDescription, LevelsTopic));
        lines.Add(new MeasurementLine("RMS Level", $"{Tenths(stream.RmsTenths)} dBFS{PerChannel(names, channels.Select(c => Tenths(c.RmsTenths)))}", RmsDescription, LevelsTopic));
        lines.Add(new MeasurementLine("DC Offset", $"{Percent(stream.DcOffsetMicropercent)}% of full scale ({Tenths(stream.DcOffsetTenths)} dBFS{PerChannelInside(names, channels.Select(c => Percent(c.DcOffsetMicropercent) + "%"))})", DcOffsetDescription, DcOffsetTopic));

        string what = isFloat ? "at or beyond full scale" : "at full scale";
        string clipping = stream.ClippingRuns == 0
            ? "none"
            : $"{Plural(stream.ClippingRuns, "run", "runs")} of {MeasurementLimits.MinimumClippingRun} or more samples {what}, {Plural(stream.ClippedSamples, "sample", "samples")}, the longest {Grouped(stream.LongestClippingRun)}{PerChannel(names, channels.Select(c => Plural(c.ClippingRuns, "run", "runs")))}";
        lines.Add(new MeasurementLine("Clipping", clipping, ClippingDescription, ClippingTopic));

        string silence = stream.SilentAtStart == 0 && stream.SilentAtEnd == 0
            ? "none"
            : $"{(stream.SilentAtStart == 0 ? "none" : Samples(stream.SilentAtStart, m.SampleRate))} at the start, {(stream.SilentAtEnd == 0 ? "none" : Samples(stream.SilentAtEnd, m.SampleRate))} at the end";
        lines.Add(new MeasurementLine("Digital Silence", silence, DigitalSilenceDescription, DigitalSilenceTopic));

        string zeros = $"{Plural(stream.ZeroSamples, "sample", "samples")}{PerChannel(names, channels.Select(c => Plural(c.ZeroSamples, "sample", "samples")))}";
        lines.Add(new MeasurementLine("Zero Samples", zeros, ZeroSamplesDescription, DigitalSilenceTopic));

        if (isFloat)
        {
            lines.Add(new MeasurementLine("Has Only 16-Bit Values", YesNo(stream.HasOnly16BitValues, channels.Select(c => c.HasOnly16BitValues), names), HasOnly16BitValuesDescription, BitDepthUsageTopic));
            lines.Add(new MeasurementLine("Has Only 24-Bit Values", YesNo(stream.HasOnly24BitValues, channels.Select(c => c.HasOnly24BitValues), names), HasOnly24BitValuesDescription, BitDepthUsageTopic));
            return;
        }

        string bits = stream.LowBitsUnused is { } unused
            ? $"{unused} of {levels.BitsPerSample}, the highest magnitude {Grouped(stream.HighestMagnitude)}"
            : "not defined (every sample is 0)";
        if (stream.LowBitsUnused is not null && channels.Any(c => c.LowBitsUnused != stream.LowBitsUnused))
        {
            bits += $"; by channel: {string.Join(", ", names.Select((n, i) => $"{n} {channels[i].LowBitsUnused?.ToString(CultureInfo.InvariantCulture) ?? "none"}"))}";
        }

        lines.Add(new MeasurementLine("Low Bits Unused", bits, LowBitsUnusedDescription, BitDepthUsageTopic));
    }

    // The channels' names: their speakers where the stream states distinct ones, otherwise "channel 1", "channel 2".
    private static List<string> ChannelNames(StreamMeasurements m, int count)
    {
        if (m.ChannelLayout is { } layout && layout.Count == count)
        {
            List<string> speakers = [.. layout.Select(ChannelLayouts.Name)];
            if (speakers.Distinct().Count() == count)
            {
                return speakers;
            }
        }

        return [.. Enumerable.Range(1, count).Select(i => "channel " + i.ToString(CultureInfo.InvariantCulture))];
    }

    private static string PerChannel(List<string> names, IEnumerable<string> values)
    {
        string[] parts = [.. values];
        return parts.Length < 2 ? string.Empty : $" ({string.Join(", ", names.Select((n, i) => $"{n} {parts[i]}"))})";
    }

    private static string PerChannelInside(List<string> names, IEnumerable<string> values)
    {
        string[] parts = [.. values];
        return parts.Length < 2 ? string.Empty : $"; {string.Join(", ", names.Select((n, i) => $"{n} {parts[i]}"))}";
    }

    private static string YesNo(bool stream, IEnumerable<bool> channels, List<string> names)
    {
        bool[] parts = [.. channels];
        string text = stream ? "yes" : "no";
        return parts.Length < 2 || parts.All(p => p == stream) ? text : $"{text} ({string.Join(", ", names.Select((n, i) => $"{n} {(parts[i] ? "yes" : "no")}"))})";
    }

    private static string Grouped(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Plural(long count, string singular, string plural) => $"{Grouped(count)} {(count == 1 ? singular : plural)}";

    // A count of samples and, where the rate is known and the time is at least a tenth of a second, the time.
    private static string Samples(long samples, int rate) =>
        rate > 0 && samples >= (rate + 9) / 10 ? $"{Plural(samples, "sample", "samples")} ({Seconds(samples, rate)} s)" : Plural(samples, "sample", "samples");

    // Millionths of a percent as a percentage with up to six decimals, placing the decimal point and never formatting a double.
    public static string Percent(long micropercent) =>
        (micropercent / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);

    private static string Header(StreamMeasurements m, bool isLossy)
    {
        if (m.Status == MeasurementStatus.NotPossible)
        {
            return m.Reason is { } reason ? $"not measured ({reason})" : "not measured";
        }

        if (m.Status == MeasurementStatus.Failed)
        {
            return "failed";
        }

        if (m.Samples == 0 && m.Status == MeasurementStatus.Stopped)
        {
            return "nothing before the measuring stopped";
        }

        string duration = m.SampleRate > 0 ? $"{Seconds(m.Samples, m.SampleRate)} s" : Counts.Of(m.Samples, "sample", "samples");
        string channels = m.Channels == m.ChannelsMeasured
            ? Counts.Of(m.Channels, "channel", "channels")
            : $"{Counts.Of(m.Channels, "channel", "channels")} ({m.ChannelsMeasured} measured)";
        string text = $"{duration}{(isLossy ? " (lossy stream)" : string.Empty)} of {channels}";
        if (m.Gaps > 0)
        {
            text += $"; {Counts.Of(m.Gaps, "gap", "gaps")} skipped {Counts.Of(m.LostSamples, "sample", "samples")}";
        }

        return m.Status switch
        {
            MeasurementStatus.Stopped => text + "; measuring stopped before the end of the stream",
            MeasurementStatus.FormatChanged => text + "; measuring stopped where the format of the decoded blocks changed",
            _ => text,
        };
    }

    public static string Cutoff(SpectralCutoff cutoff)
    {
        if (!cutoff.IsSearched)
        {
            return "not searched (the sample rate is below 8,000 Hz)";
        }

        if (cutoff.IsFound)
        {
            return $"{Hertz(cutoff.Frequency)} (a step of {Tenths(cutoff.StepTenthsOfDb)} dB, transition from {Hertz(cutoff.TransitionStart)}, in {cutoff.FramesShowing.ToString("N0", CultureInfo.InvariantCulture)} of {cutoff.FramesWithSignal.ToString("N0", CultureInfo.InvariantCulture)} frames with signal)";
        }

        return cutoff.FramesWithSignal == 0 && cutoff.StepFrequency == 0
            ? "none found (no frame with signal)"
            : $"none found (largest step {Tenths(cutoff.StepTenthsOfDb)} dB, at {Hertz(cutoff.StepFrequency)})";
    }

    public static string Hertz(int hertz) => hertz.ToString("N0", CultureInfo.InvariantCulture) + " Hz";

    // Tenths of a unit as text with one decimal, placing the decimal point and never formatting a double; a value of -0.0 is 0.0.
    public static string Tenths(int tenths)
    {
        if (tenths == int.MinValue)
        {
            return "-inf";
        }

        long magnitude = Math.Abs((long)tenths);
        string sign = tenths < 0 && magnitude != 0 ? "-" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{magnitude / 10:N0}.{magnitude % 10}");
    }

    // Samples at a rate as seconds with one decimal, by integer arithmetic.
    public static string Seconds(long samples, int rate)
    {
        UInt128 tenths = (UInt128)(ulong)samples * 10 / (ulong)rate;
        return string.Create(CultureInfo.InvariantCulture, $"{(ulong)(tenths / 10):N0}.{(ulong)(tenths % 10)}");
    }
}
