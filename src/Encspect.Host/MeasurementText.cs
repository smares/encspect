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

    public const string SpectralCutoffDescription = "Where the average spectrum of the decoded audio ends: the highest frequency more than 6 dB above the level above the largest step, with the size of that step, where the transition to it starts and how many of the frames with signal show a step there. It is a measured edge, not the lowpass frequency an encoder states.";

    public const string MeasuredTopic = "measurements";

    public const string SpectralCutoffTopic = "spectral-cutoff";

    public const string SpectrogramDescription = "The average power over time and frequency of the decoded audio: 512 rows from 0 Hz to Nyquist and up to 1,024 columns of the FFT hops, with 0 dB the level of a full-scale sine centered on a bin.";

    public const string SpectrogramTopic = "spectrogram";

    public const string WaveformDescription = "The smallest and largest sample of each channel in each column of the spectrogram's time axis, in 16-bit sample codes.";

    public const string SpectrumDescription = "The average level of each of 512 rows from 0 Hz to Nyquist over all frames, in tenths of a dB relative to a full-scale sine centered on a bin, for the channels together (all but the LFE) and for each channel.";

    // The header line's text after "Measured: ", then the figure lines.
    public static IReadOnlyList<MeasurementLine> Lines(StreamMeasurements m, bool isLossy)
    {
        ArgumentNullException.ThrowIfNull(m);

        var lines = new List<MeasurementLine> { new("Measured", Header(m, isLossy), MeasuredDescription, MeasuredTopic) };
        if (m.Spectrum is { } spectrum && m.Status != MeasurementStatus.Failed)
        {
            lines.Add(new MeasurementLine("Spectral Cutoff", Cutoff(spectrum.Cutoff), SpectralCutoffDescription, SpectralCutoffTopic));
        }

        return lines;
    }

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
