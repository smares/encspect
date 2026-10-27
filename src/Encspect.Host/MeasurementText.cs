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

        return lines;
    }

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
            ? "none at the start or at the end"
            : $"{(stream.SilentAtStart == 0 ? "none" : Samples(stream.SilentAtStart, m.SampleRate))} at the start, {(stream.SilentAtEnd == 0 ? "none" : Samples(stream.SilentAtEnd, m.SampleRate))} at the end";
        lines.Add(new MeasurementLine("Digital Silence", silence, DigitalSilenceDescription, DigitalSilenceTopic));

        string zeros = $"{Samples(stream.ZeroSamples, m.SampleRate)}"
            + (channels.Count > 1 && channels.Any(c => c.ZeroSamples != stream.ZeroSamples) ? $" ({string.Join(", ", names.Select((n, i) => $"{n} {Grouped(channels[i].ZeroSamples)}"))} for the channels)" : string.Empty);
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
