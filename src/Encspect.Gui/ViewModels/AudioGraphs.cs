using System.Globalization;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Gui.ViewModels;

// The panels under the heading "Audio", drawn from StreamInfo.Measurements (docs/design/spectral-analysis.md): the waveform and the
// spectrogram over time, the average spectrum over frequency, and the measured figures as a list. They hold figures and grids, never
// the audio, and say what a point is in text, as the other graphs do.
public abstract class AudioGraph : Graph
{
    private protected AudioGraph(string title, string unit, StreamInfo stream, StreamMeasurements measurements)
        : base(title, unit)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(measurements);

        Stream = stream;
        Measurements = measurements;
        Locator = SampleLocator.For(stream, measurements);
        Names = ChannelNames(measurements);
    }

    public StreamInfo Stream { get; }

    public StreamMeasurements Measurements { get; }

    // Finds the frame of a time; null for a stream without frames (PCM).
    public SampleLocator? Locator { get; }

    public override string Heading => GraphHeadings.Audio;

    public int SampleRate => Measurements.SampleRate;

    // The channels measured, by their speakers where the stream states distinct ones, else "channel 1", "channel 2".
    public IReadOnlyList<string> Names { get; }

    // The highest frequency of the grids and the spectrum in hertz: half the sample rate.
    public int NyquistHertz => SampleRate / 2;

    // The cutoff drawn as a line: the combined spectrum's, where one is found.
    public SpectralCutoff? Cutoff => Measurements.Spectrum?.Cutoff is { IsFound: true, Frequency: > 0 } cutoff ? cutoff : null;

    // The upper edge of a row in hertz, as the cutoff's figures are; rows are 0 Hz to Nyquist in MeasurementLimits.Rows equal parts.
    public int RowEdgeHertz(int row) => (int)((long)(row + 1) * SampleRate / (2 * MeasurementLimits.Rows));

    // The middle of a row in hertz, to the nearest.
    public int RowCenterHertz(int row) => (int)((((2L * row) + 1) * SampleRate + (2 * MeasurementLimits.Rows)) / (4 * MeasurementLimits.Rows));

    // The row whose frequency range holds a frequency, clamped to the grid.
    public int RowOfHertz(double hertz) => Math.Clamp((int)(hertz / Math.Max(1, NyquistHertz) * MeasurementLimits.Rows), 0, MeasurementLimits.Rows - 1);

    // "Spectral cutoff: 17,054 Hz (a step of 60.4 dB, transition from 16,559 Hz, in 644 of 644 frames with signal)", or null.
    public string? CutoffText => Cutoff is { } cutoff ? "Spectral cutoff: " + MeasurementText.Cutoff(cutoff) : null;

    public static string Level(int tenths) => MeasurementText.Tenths(tenths) + " dB";

    public static string Hertz(int hertz) => MeasurementText.Hertz(hertz);

    private static List<string> ChannelNames(StreamMeasurements m)
    {
        int count = m.ChannelsMeasured;
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
}

// An audio graph over time in the columns of the grids: a column covers HopsPerColumn hops of HopSamples samples, from the first hop the
// stream touched (MeasurementLimits and StreamMeasurer: the origin is the first position rounded down to a whole hop).
public abstract class TimeGraph : AudioGraph
{
    private protected TimeGraph(string title, string unit, StreamInfo stream, StreamMeasurements measurements, int columns)
        : base(title, unit, stream, measurements)
    {
        Columns = columns;
        if (measurements.Spectrogram is { } grid && grid.HopSamples > 0)
        {
            SamplesPerColumn = (long)grid.HopsPerColumn * grid.HopSamples;
            Origin = measurements.FirstPosition / grid.HopSamples * grid.HopSamples;
        }
        else
        {
            // A waveform alone: the columns share the measured samples evenly.
            Origin = measurements.FirstPosition;
            SamplesPerColumn = Math.Max(1, (measurements.Samples + measurements.LostSamples + columns - 1) / Math.Max(1, columns));
        }
    }

    public int Columns { get; }

    // The stream position of the first column's start, and the samples per channel a column covers.
    public long Origin { get; }

    public long SamplesPerColumn { get; }

    public override int Count => Columns;

    // The time of the column's start in seconds; the axis runs from the first column's start to the last one's end.
    public double TimeOfColumn(int column) => SampleRate > 0 ? (Origin + (column * SamplesPerColumn)) / (double)SampleRate : 0;

    public double StartSeconds => TimeOfColumn(0);

    public double EndSeconds => TimeOfColumn(Columns);

    // The time in the middle of a column, as the hover text names it.
    public double MiddleOfColumn(int column) => SampleRate > 0 ? (Origin + ((column + 0.5) * SamplesPerColumn)) / SampleRate : 0;

    public long PositionOfColumn(int column) => Origin + (column * SamplesPerColumn) + (SamplesPerColumn / 2);

    public override long? FrameAt(int index, long? selected) =>
        (uint)index >= (uint)Columns || Locator is not { } locator ? null : locator.FrameOffsetAt(PositionOfColumn(index));

    public override int? IndexOfFrame(long offset)
    {
        if (Locator?.PositionOfFrame(offset) is not { } position || Columns == 0)
        {
            return null;
        }

        return (int)Math.Clamp((position - Origin) / SamplesPerColumn, 0, Columns - 1);
    }
}

// The smallest and largest sample of each channel in each column, in lanes one under the other.
public sealed class WaveformGraph : TimeGraph
{
    // The channels shown on their own; a stream measures at most MeasurementLimits.MaxChannels.
    private const int HoverChannels = 8;

    public WaveformGraph(StreamInfo stream, StreamMeasurements measurements)
        : base("Waveform", "16-bit sample codes", stream, measurements, measurements.Waveform?.Columns ?? throw new ArgumentException("No waveform was measured."))
    {
        Grid = measurements.Waveform!;
    }

    public WaveformGrid Grid { get; }

    public int Channels => Grid.Channels;

    public override string? Description => MeasurementText.WaveformDescription;

    public override string? Topic => MeasurementText.MeasuredTopic;

    public override double Maximum => 32768;

    public short Minimum(int channel, int column) => Grid.Minimum.Span[(channel * Columns) + column];

    public short Peak(int channel, int column) => Grid.Maximum.Span[(channel * Columns) + column];

    // The larger magnitude of the channels in the column.
    public override double ValueAt(int index)
    {
        int peak = 0;
        for (int c = 0; c < Channels; c++)
        {
            peak = Math.Max(peak, Math.Max(Math.Abs((int)Minimum(c, index)), Math.Abs((int)Peak(c, index))));
        }

        return peak;
    }

    // "0:12.4 · front left -12,034 to 15,200 · front right -11,500 to 14,980 (16-bit sample codes)".
    public override string Describe(int index)
    {
        var parts = new List<string> { Format.Timestamp(MiddleOfColumn(index)) };
        for (int c = 0; c < Math.Min(Channels, HoverChannels); c++)
        {
            parts.Add($"{Names[c]} {Minimum(c, index).ToString("N0", CultureInfo.InvariantCulture)} to {Peak(c, index).ToString("N0", CultureInfo.InvariantCulture)}");
        }

        if (Channels > HoverChannels)
        {
            parts.Add($"and {Format.Count(Channels - HoverChannels, "more channel", "more channels")}");
        }

        return string.Join(" · ", parts) + " (16-bit sample codes)";
    }
}

// The average power over time and frequency: 512 rows by the grid's columns, one byte a cell (the level in dB plus 200; 0 is no data).
public sealed class SpectrogramGraph : TimeGraph
{
    public SpectrogramGraph(StreamInfo stream, StreamMeasurements measurements)
        : base("Spectrogram", "dB", stream, measurements, measurements.Spectrogram?.Columns ?? throw new ArgumentException("No spectrogram was measured."))
    {
        Grid = measurements.Spectrogram!;
    }

    public SpectrogramGrid Grid { get; }

    public override string? Description => MeasurementText.SpectrogramDescription;

    public override string? Topic => MeasurementText.SpectrogramTopic;

    public override double Maximum => 20;

    public int Rows => MeasurementLimits.Rows;

    // The cell's byte: the level in dB plus MeasurementLimits.GridOffset, 0 for no data.
    public byte Cell(int column, int row) => Grid.Power.Span[(column * Rows) + row];

    // The level of a cell in whole dB, or null where the column holds no data.
    public int? LevelAt(int column, int row) => Cell(column, row) is var cell and > 0 ? cell - MeasurementLimits.GridOffset : null;

    // The level of the loudest row of the column, for ValueAt: the column's peak.
    public override double ValueAt(int index)
    {
        int best = 0;
        for (int r = 0; r < Rows; r++)
        {
            best = Math.Max(best, Cell(index, r));
        }

        return best == 0 ? double.NaN : best - MeasurementLimits.GridOffset;
    }

    public override string Describe(int index) => $"{Format.Timestamp(MiddleOfColumn(index))} · {ValueAt(index).ToString("0", CultureInfo.InvariantCulture)} dB at the loudest frequency";

    // "0:12.4 · 4,305 Hz · -63 dB", with the cutoff's figures where the pointer is on its row.
    public string DescribeCell(int column, int row)
    {
        string level = LevelAt(column, row) is { } dB ? $"{dB.ToString("0", CultureInfo.InvariantCulture)} dB" : "no data";
        string text = $"{Format.Timestamp(MiddleOfColumn(column))} · {Hertz(RowCenterHertz(row))} · {level}";
        return IsOnCutoff(row) && CutoffText is { } cutoff ? text + " · " + cutoff : text;
    }

    // Whether a row is within two rows of the cutoff's edge: close enough to the line to read its figures.
    public bool IsOnCutoff(int row) => Cutoff is { } cutoff && Math.Abs(row - RowOfHertz(Math.Max(0, cutoff.Frequency - 1))) <= 2;
}

// The average level of each of the 512 rows over all frames, as one line over frequency (all channels but the LFE) or a line for each channel.
public sealed class SpectrumGraph : AudioGraph
{
    private readonly int _top;
    private readonly int _bottom;

    public SpectrumGraph(StreamInfo stream, StreamMeasurements measurements, GraphForm view)
        : base("Spectrum", "dB", stream, measurements)
    {
        Spectrum = measurements.Spectrum ?? throw new ArgumentException("No spectrum was measured.");
        View = view;
        short? high = null;
        short? low = null;
        foreach (ReadOnlyMemory<short> series in view == GraphForm.PerChannel ? Spectrum.PerChannel : [Spectrum.Combined])
        {
            foreach (short value in series.Span)
            {
                if (value == MeasurementLimits.NoPower)
                {
                    continue;
                }

                high = high is null ? value : Math.Max(high.Value, value);
                low = low is null ? value : Math.Min(low.Value, value);
            }
        }

        // A multiple of 20 dB at both ends, at least 60 dB apart, so an empty or flat spectrum still has an axis.
        _top = high is { } h ? (int)Math.Ceiling(h / 200.0) * 20 : 0;
        _bottom = Math.Min(low is { } l ? (int)Math.Floor(l / 200.0) * 20 : -100, _top - 60);
    }

    public SpectrumFigures Spectrum { get; }

    public GraphForm View { get; }

    public override string? Description => MeasurementText.SpectrumDescription;

    // The article that defines the average spectrum; the spectral cutoff is a figure read from it.
    public override string? Topic => MeasurementText.SpectrogramTopic;

    public override int Count => MeasurementLimits.Rows;

    // The axis, in dB: a multiple of 20 above the loudest row, and below the quietest.
    public int AxisTop => _top;

    public int AxisBottom => _bottom;

    public override double Maximum => _top;

    public int Lines => View == GraphForm.PerChannel ? Spectrum.PerChannel.Count : 1;

    public ReadOnlyMemory<short> Line(int index) => View == GraphForm.PerChannel ? Spectrum.PerChannel[index] : Spectrum.Combined;

    public string LineName(int index) => View == GraphForm.PerChannel ? Names[Math.Min(index, Names.Count - 1)] : "all channels but the LFE";

    // The row's level in dB, or NaN for a row with no power.
    public override double ValueAt(int index) => ValueAt(0, index);

    // The level of a line at a row in dB, or NaN for a row with no power.
    public double ValueAt(int line, int index) => Line(line).Span[index] is var tenths && tenths != MeasurementLimits.NoPower ? tenths / 10.0 : double.NaN;

    public override long? FrameAt(int index, long? selected) => null;

    public override int? IndexOfFrame(long offset) => null;

    // "4,305 Hz · -63.2 dB", for each channel in the per-channel view, with the cutoff where the row is on it.
    public override string Describe(int index)
    {
        var levels = new List<string>();
        for (int i = 0; i < Lines; i++)
        {
            short tenths = Line(i).Span[index];
            string level = tenths == MeasurementLimits.NoPower ? "no power" : Level(tenths);
            levels.Add(View == GraphForm.PerChannel ? $"{LineName(i)} {level}" : level);
        }

        string text = $"{Hertz(RowCenterHertz(index))} · {string.Join(", ", levels)}";
        return IsOnCutoff(index) && CutoffText is { } cutoff ? text + " · " + cutoff : text;
    }

    public bool IsOnCutoff(int row) => Cutoff is { } cutoff && Math.Abs(row - RowOfHertz(Math.Max(0, cutoff.Frequency - 1))) <= 2;
}

// The short-term loudness over time as one line (the loudness series of the measurements), with the integrated loudness as a reference
// line and the loudness figures in the hover text and the automation text. The series is spread evenly over the width, as the spectrum's
// rows are; a stretch lost to a gap is not marked.
public sealed class LoudnessGraph : AudioGraph
{
    // The figures the header and the automation text name, in the order the command line prints them.
    private static readonly MeasurementFigure[] FigureKeys = [MeasurementFigure.IntegratedLoudness, MeasurementFigure.LoudnessRange, MeasurementFigure.MaximumTruePeakLevel, MeasurementFigure.MaximumMomentaryLoudness, MeasurementFigure.MaximumShortTermLoudness];

    private const double MaximumAxisLufs = 1000;

    public LoudnessGraph(StreamInfo stream, StreamMeasurements measurements)
        : base("Loudness", "LUFS", stream, measurements)
    {
        Loudness = measurements.Loudness ?? throw new ArgumentException("No loudness was measured.");
        Series = Loudness.ShortTerm ?? throw new ArgumentException("No short-term loudness was measured.");
        Figures = [.. MeasurementText.Lines(measurements, stream.Verification?.IsLossy == true).Where(l => FigureKeys.Contains(l.Figure))];
        // The measurer's values lie between -70 and a few LUFS; the clamp and the NaN check keep the axis whole for any series, since the ticks
        // are counted in integers.
        double high = double.NegativeInfinity;
        double low = double.PositiveInfinity;
        foreach (double value in Series.Values.Span)
        {
            if (!double.IsNaN(value))
            {
                high = Math.Max(high, Math.Clamp(value, -MaximumAxisLufs, MaximumAxisLufs));
                low = Math.Min(low, Math.Clamp(value, -MaximumAxisLufs, MaximumAxisLufs));
            }
        }

        if (IntegratedTenths is { } integrated)
        {
            high = Math.Max(high, Math.Clamp(integrated / 10.0, -MaximumAxisLufs, MaximumAxisLufs));
            low = Math.Min(low, Math.Clamp(integrated / 10.0, -MaximumAxisLufs, MaximumAxisLufs));
        }

        if (high < low)
        {
            high = low = -23;
        }

        // Multiples of 5 LU at both ends, at least 20 LU apart, so a steady signal still has an axis.
        AxisTop = (int)Math.Ceiling(high / 5.0) * 5;
        AxisBottom = Math.Max(-70, Math.Min((int)Math.Floor(low / 5.0) * 5, AxisTop - 20));
        AxisTop = Math.Max(AxisTop, AxisBottom + 20);
    }

    public LoudnessFigures Loudness { get; }

    public Series Series { get; }

    // The loudness lines, as the command line words them.
    public IReadOnlyList<MeasurementLine> Figures { get; }

    public int AxisTop { get; }

    public int AxisBottom { get; }

    public override string? Description => MeasurementText.LoudnessPanelDescription;

    public override string? Topic => MeasurementText.LoudnessTopic;

    public override int Count => Series.Values.Length;

    public override double Maximum => AxisTop;

    // The integrated loudness in tenths of a LUFS, where there is one (not for silence or a stream without a gated block).
    public int? IntegratedTenths => Loudness.IntegratedAbsence == LoudnessAbsence.None && Loudness.IntegratedTenths != MeasurementLimits.NoLevel ? Loudness.IntegratedTenths : null;

    // The stream's position of the first measured sample in seconds: the series counts from there.
    public double OffsetSeconds => SampleRate > 0 ? Measurements.FirstPosition / (double)SampleRate : 0;

    // The time of a point: the end of the window of the first value it covers.
    public double TimeAt(int index) => OffsetSeconds + Series.X.Span[index];

    public double StartSeconds => TimeAt(0);

    // The end of the last point: the values it covers last StepsPerPoint steps of 0.1 s.
    public double EndSeconds => TimeAt(Count - 1) + (Loudness.StepsPerPoint * 0.1);

    // Where a point sits on the time axis, from 0 at the start to 1 at the end: the middle of the stretch its values cover, so a gap in
    // the stream shows as a gap in the line.
    public double FractionAt(int index) => (TimeAt(index) + (Loudness.StepsPerPoint * 0.05) - StartSeconds) / (EndSeconds - StartSeconds);

    // The point nearest to a fraction of the time axis.
    public int IndexAtFraction(double fraction)
    {
        int low = 0;
        int high = Count - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (FractionAt(middle) < fraction)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low > 0 && fraction - FractionAt(low - 1) <= FractionAt(low) - fraction ? low - 1 : low;
    }

    public override double ValueAt(int index) => Series.Values.Span[index];

    // "0:12.4 · short-term loudness -23.1 LUFS", with the number of values a merged point averages.
    public override string Describe(int index)
    {
        string text = $"{Format.Timestamp(TimeAt(index))} · short-term loudness {ValueAt(index).ToString("0.0", CultureInfo.InvariantCulture)} LUFS";
        return Loudness.StepsPerPoint > 1 ? text + $" (average of {Format.Count(Loudness.StepsPerPoint, "value", "values")})" : text;
    }

    public override long? FrameAt(int index, long? selected) => null;

    public override int? IndexOfFrame(long offset) => null;

    // "Integrated Loudness: -23.0 LUFS · Loudness Range: 5.2 LU", the figures a header can fit.
    public string Header(bool isShort) =>
        string.Join(" · ", Figures.Where(f => isShort ? f.Figure is MeasurementFigure.IntegratedLoudness or MeasurementFigure.LoudnessRange : true).Select(f => $"{f.Name}: {f.Text}"));
}

// The measured figures as lines of text ("Spectral Cutoff: …", "Sample Peak: …"), each with its description; drawn by the window's list.
public sealed class FiguresGraph : AudioGraph
{
    public FiguresGraph(StreamInfo stream, StreamMeasurements measurements)
        : base("Figures", string.Empty, stream, measurements)
    {
        Lines = MeasurementText.Lines(measurements, stream.Verification?.IsLossy == true);
    }

    public IReadOnlyList<MeasurementLine> Lines { get; }

    public override string? Description => "The figures measured from the decoded audio: the spectral cutoff, the levels, the loudness and the true peak, each worded as the command line prints it.";

    public override string? Topic => MeasurementText.MeasuredTopic;

    // The list is no chart: it has no points.
    public override int Count => 0;

    public override double Maximum => 0;

    public override double ValueAt(int index) => 0;

    public override string Describe(int index) => string.Empty;

    public override long? FrameAt(int index, long? selected) => null;

    public override int? IndexOfFrame(long offset) => null;
}

// What the Audio heading says of a stream whose audio is not (fully) measured, and whether the command to decode is offered under it.
public sealed record AudioCaption(string Text, bool OffersDecode);

public static class AudioPanels
{
    public const string WaveformTitle = "Waveform";
    public const string SpectrogramTitle = "Spectrogram";
    public const string SpectrumTitle = "Spectrum";
    public const string LoudnessTitle = "Loudness";
    public const string FiguresTitle = "Figures";

    // "Decode the audio to see its waveform and spectrogram."
    public const string DecodeText = "Decode the audio to see its waveform and spectrogram.";

    // The panels a stream's measurements have, in the order of the list. The Audio heading of a stream with none still has its caption.
    public static IReadOnlyList<GraphChoice> EntriesFor(StreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var entries = new List<GraphChoice>();
        if (stream.Measurements is not { } m || m.Status == MeasurementStatus.Failed)
        {
            return entries;
        }

        if (m.Waveform is { Columns: > 0 })
        {
            Graph graph = new WaveformGraph(stream, m);
            entries.Add(new GraphChoice(WaveformTitle, GraphHeadings.Audio, graph.Description, [new GraphFormChoice(GraphForm.Audio, WaveformTitle, graph.Description!, graph)]));
        }

        if (m.Spectrogram is { Columns: > 0 })
        {
            Graph graph = new SpectrogramGraph(stream, m);
            entries.Add(new GraphChoice(SpectrogramTitle, GraphHeadings.Audio, graph.Description, [new GraphFormChoice(GraphForm.Audio, SpectrogramTitle, graph.Description!, graph)]));
        }

        if (m.Spectrum is not null)
        {
            var combined = new SpectrumGraph(stream, m, GraphForm.Combined);
            var forms = new List<GraphFormChoice> { new(GraphForm.Combined, "Combined", "The average spectrum of all channels together, but the LFE", combined) };
            if (m.Spectrum.PerChannel.Count > 1)
            {
                forms.Add(new GraphFormChoice(GraphForm.PerChannel, "Per Channel", "The average spectrum of each channel", new SpectrumGraph(stream, m, GraphForm.PerChannel)));
            }

            entries.Add(new GraphChoice(SpectrumTitle, GraphHeadings.Audio, combined.Description, forms));
        }

        if (m.Loudness is { NotMeasured: null, ShortTerm: not null })
        {
            Graph graph = new LoudnessGraph(stream, m);
            entries.Add(new GraphChoice(LoudnessTitle, GraphHeadings.Audio, graph.Description, [new GraphFormChoice(GraphForm.Audio, LoudnessTitle, graph.Description!, graph)]));
        }

        if (m.Spectrum is not null || m.Levels is not null || m.Loudness is not null || m.TruePeak is not null)
        {
            Graph graph = new FiguresGraph(stream, m);
            entries.Add(new GraphChoice(FiguresTitle, GraphHeadings.Audio, graph.Description, [new GraphFormChoice(GraphForm.Audio, FiguresTitle, graph.Description!, graph)]));
        }

        return entries;
    }

    // The line under the heading "Audio" for a stream, or null where the panels say it all (an audio stream measured completely).
    public static AudioCaption? CaptionFor(StreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.Kind != StreamKind.Audio)
        {
            return null;
        }

        StreamMeasurements? m = stream.Measurements;
        if (m is null)
        {
            return stream.Verification?.Decoding switch
            {
                DecodeStatus.NotRequested => new AudioCaption(DecodeText, true),

                // A coding without a decoder, or a stream its container does not hand to the codec's parser: the text claims neither.
                null or DecodeStatus.NoDecoder => new AudioCaption(NotDecodedHere(stream.Codec), false),
                DecodeStatus.NotPossible => new AudioCaption(Cannot("the stream cannot be decoded"), false),
                DecodeStatus.Failed => new AudioCaption("Decoding failed, so the audio has no figures.", false),
                DecodeStatus.Stopped => new AudioCaption("Decoding stopped before any audio was measured.", false),
                _ => new AudioCaption("The audio was decoded, but not measured.", false),
            };
        }

        return m.Status switch
        {
            MeasurementStatus.Complete => null,
            MeasurementStatus.NotPossible => new AudioCaption(Cannot(m.Reason ?? "the stream cannot be measured"), false),
            MeasurementStatus.Failed => new AudioCaption("The measuring failed, so the audio has no figures.", false),
            MeasurementStatus.FormatChanged => new AudioCaption("Measuring stopped where the format of the decoded blocks changed; the figures cover what came before.", false),
            _ => new AudioCaption(StoppedText(m), false),
        };
    }

    public static string NotDecodedHere(string codec) =>
        codec.Length > 0 ? $"The audio of this {codec} stream is not decoded here." : "The audio of this stream is not decoded here.";

    // The reason as a clause after the colon: a decoder's refusal starts with a capital, which an acronym ("DSD", "TTA") keeps.
    private static string Cannot(string reason)
    {
        string clause = reason.TrimEnd('.');
        if (clause.Length > 1 && char.IsUpper(clause[0]) && char.IsLower(clause[1]))
        {
            clause = char.ToLowerInvariant(clause[0]) + clause[1..];
        }

        return $"The audio cannot be decoded: {clause}.";
    }

    private static string StoppedText(StreamMeasurements m) => m.Samples == 0
        ? "Decoding stopped before any audio was measured."
        : m.SampleRate > 0
            ? $"Decoding stopped after {MeasurementText.Seconds(m.Samples, m.SampleRate)} s of audio; the figures cover what was measured."
            : "Decoding stopped before the end of the stream; the figures cover what was measured.";
}
