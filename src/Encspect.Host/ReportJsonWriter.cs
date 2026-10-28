using System.Buffers.Binary;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Encspect.Core;

namespace Encspect.Host;

// Schema version 1: per report schemaVersion, tool, source, root, streams and findings. A node has
// name, kind (unless Detail), value as a JSON string, number or boolean, unit, offset, length,
// bitOffset and bitLength, ranges (for bytes that are not contiguous), and children; a finding has
// severity, code, message, offset, length, path and count. A report whose format is not the
// plain detected one (other formats match, or the analysis was forced) also has detection, with
// the format that ran, its evidence, whether it was forced, the format detection would have chosen
// then, and the alternatives with their evidence (class, offset, frames, verifiedFrames, note). A stream of a coding that
// stores checksums has verification after protection: isVerified (where known), isLossy (where true), decoding,
// decodedSamples and checksums (name, covers, coversAllAudio, stored, checked, failed). A stream with category series
// has categorySeries after series (name, concept, valueLabel, valuesPerUnit, valueNames, categories with their counts
// and isReserved, total, uncodedValues, xSeries or x, valueCounts, runs of [code, length], weights, seconds, or groupOf
// and map for a grouping, which has the x of its source); a series, numeric or category, that belongs to a shared
// concept also has concept, and a numeric series that is a form of a quantity has quantity. A stream of an analysis that
// asked for measurements has measurements after verification: status, samples, firstPosition, lostSamples, gaps, sampleRate,
// channels, channelsMeasured, the spectrum (rows in tenths of a dB, the cutoff in whole hertz and tenths of a dB as
// {value, unit}), the levels (samplePeak, rms, dcOffset and dcOffsetLevel as {value, unit, perChannel}, null for minus infinity;
// clipping, digitalSilence and bitDepth as counts, bare, with perChannel objects), the loudness (integratedLoudness, loudnessRange,
// maximumMomentaryLoudness and maximumShortTermLoudness as {value, unit}, or the unit and an absence where there is no value; the
// channel weights, the gating blocks and the short-term series) and the truePeak (oversampling and maximumTruePeakLevel as
// {value, unit, perChannel}) and, with --measure-grids, the spectrogram and waveform grids as base64.
public static class ReportJsonWriter
{
    // Raised whenever a field changes meaning or type, or one is removed; added fields keep it.
    public const int SchemaVersion = 1;

    public static string ToolVersion { get; } =
        typeof(ReportJsonWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    // Utf8JsonWriter buffers everything until it is flushed, which for a report with a node per
    // frame header field means the whole document in memory. Flushing once this much is pending
    // keeps memory flat however large the report gets.
    private const int FlushThreshold = 1 << 16;

    // Text from files stays readable: non-ASCII characters are written as UTF-8, not as \u escapes.
    // Quotes, backslashes and control characters are still escaped; the output is never embedded in HTML.
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    // A node takes two levels (its object and its children array) below the report array, the
    // report and the root; the writer stops at Node.MaxDepth, so this limit is never reached.
    private const int MaxJsonDepth = (2 * Node.MaxDepth) + 8;

    // Reports are written as they are enumerated, so a lazy sequence is analyzed and written one
    // file at a time.
    public static void Write(IEnumerable<Report> reports, Stream output) => Write(reports, output, ReportFilter.All);

    public static void Write(IEnumerable<Report> reports, Stream output, ReportFilter filter) => Write(reports, output, filter, explain: false);

    // explain: write each node's description and topic, and each series' description.
    public static void Write(IEnumerable<Report> reports, Stream output, ReportFilter filter, bool explain)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(filter);

        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true, Encoder = Encoder, MaxDepth = MaxJsonDepth });

        writer.WriteStartArray();
        foreach (Report report in reports)
        {
            WriteReport(writer, report, filter, explain);
            writer.Flush();
        }

        writer.WriteEndArray();
    }

    // Newline-delimited JSON: one compact report per line, so every finished line can be used on
    // its own, even when the run stops early, and outputs can be concatenated.
    public static void WriteLines(IEnumerable<Report> reports, Stream output) => WriteLines(reports, output, ReportFilter.All);

    public static void WriteLines(IEnumerable<Report> reports, Stream output, ReportFilter filter) => WriteLines(reports, output, filter, explain: false);

    public static void WriteLines(IEnumerable<Report> reports, Stream output, ReportFilter filter, bool explain)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(filter);

        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = Encoder, MaxDepth = MaxJsonDepth });

        foreach (Report report in reports)
        {
            WriteReport(writer, report, filter, explain);
            writer.Flush();
            output.WriteByte((byte)'\n');

            // A writer accepts only one top-level value until it is reset.
            writer.Reset(output);
        }
    }

    private static void WriteReport(Utf8JsonWriter writer, Report report, ReportFilter filter, bool explain)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteStartObject("tool");
        writer.WriteString("name", "encspect");
        writer.WriteString("version", ToolVersion);
        writer.WriteEndObject();
        writer.WriteString("source", report.Source);

        writer.WritePropertyName("root");
        WriteNode(writer, report.Root, filter, explain, 0);

        writer.WriteStartArray("streams");
        foreach (StreamInfo stream in report.Streams)
        {
            WriteStream(writer, stream, report.Root, filter, explain);
        }

        writer.WriteEndArray();

        if (report.Detection is { } detection)
        {
            WriteDetection(writer, detection);
        }

        writer.WriteStartArray("findings");
        foreach (Finding finding in report.Findings)
        {
            writer.WriteStartObject();
            writer.WriteString("severity", finding.Severity.ToString());
            writer.WriteString("code", finding.Code);
            writer.WriteString("message", finding.Message);
            if (finding.Node is not null)
            {
                writer.WriteNumber("offset", finding.Node.Offset);
                writer.WriteNumber("length", finding.Node.Length);
            }

            if (finding.Path is { } path && FilteredPath(report.Root, filter, path) is { } shownPath)
            {
                WritePath(writer, "path", shownPath);
            }

            writer.WriteNumber("count", finding.Count);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDetection(Utf8JsonWriter writer, FormatDetection detection)
    {
        writer.WriteStartObject("detection");
        writer.WriteString("format", detection.Format);
        WriteEvidence(writer, "evidence", detection.Evidence);
        writer.WriteBoolean("isForced", detection.IsForced);
        if (detection.IsForced)
        {
            writer.WriteString("detected", detection.Detected);
        }

        writer.WriteStartArray("alternatives");
        foreach (FormatCandidate candidate in detection.Alternatives)
        {
            writer.WriteStartObject();
            writer.WriteString("format", candidate.Format);
            WriteEvidence(writer, "evidence", candidate.Evidence);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteEvidence(Utf8JsonWriter writer, string name, SniffResult evidence)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("class", evidence.Class);
        writer.WriteNumber("offset", evidence.Offset);
        writer.WriteNumber("frames", evidence.Frames);
        writer.WriteNumber("verifiedFrames", evidence.VerifiedFrames);
        if (evidence.Note is { } note)
        {
            writer.WriteString("note", note);
        }

        writer.WriteEndObject();
    }

    // The paths the host finds index the whole tree; the JSON holds only the nodes the filter keeps, so
    // each index is counted among the kept siblings. A path whose target or one of whose ancestors is
    // hidden names no node of the JSON and becomes null.
    private static IReadOnlyList<int>? FilteredPath(Node root, ReportFilter filter, IReadOnlyList<int> path)
    {
        if (filter.Hidden == ReportSections.None)
        {
            return path;
        }

        var shown = new List<int>(path.Count);
        Node node = root;
        foreach (int index in path)
        {
            IReadOnlyList<Node> children = node.Children;
            if (index < 0 || index >= children.Count || !filter.Includes(children[index]))
            {
                return null;
            }

            int before = 0;
            for (int i = 0; i < index; i++)
            {
                if (filter.Includes(children[i]))
                {
                    before++;
                }
            }

            shown.Add(before);
            node = children[index];
        }

        return shown;
    }

    private static void WriteStream(Utf8JsonWriter writer, StreamInfo stream, Node root, ReportFilter filter, bool explain)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("nodes");
        foreach (IReadOnlyList<int>? fullPath in stream.NodePaths)
        {
            IReadOnlyList<int>? path = fullPath is null ? null : FilteredPath(root, filter, fullPath);
            if (path is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                WritePath(writer, null, path);
            }
        }

        writer.WriteEndArray();
        writer.WriteString("codec", stream.Codec);
        if (stream.Kind == StreamKind.Audio)
        {
            writer.WriteNumber("sampleRate", stream.SampleRate);
            writer.WriteNumber("channels", stream.Channels);
        }
        else
        {
            writer.WriteString("kind", stream.Kind.ToString());
        }

        if (stream.ChannelMode is not null)
        {
            writer.WriteString("channelMode", stream.ChannelMode);
        }

        if (stream.ChannelLayout is { } layout)
        {
            writer.WriteStartArray("channelLayout");
            foreach (Speaker speaker in layout)
            {
                writer.WriteStringValue(ChannelLayouts.Name(speaker));
            }

            writer.WriteEndArray();
            if (stream.ChannelLayoutName is { } name)
            {
                writer.WriteString("channelLayoutName", name);
            }
        }

        if (stream.MultilingualChannels > 0)
        {
            writer.WriteNumber("multilingualChannels", stream.MultilingualChannels);
        }

        if (stream.BitsPerSample is { } bitsPerSample)
        {
            writer.WriteNumber("bitsPerSample", bitsPerSample);
        }

        if (stream.Kind == StreamKind.Audio)
        {
            writer.WriteNumber("samples", stream.Samples);
        }

        writer.WriteNumber("duration", stream.Duration);
        if (stream.AverageBitrate is { } bitrate)
        {
            writer.WriteNumber("averageBitrate", bitrate);
        }

        if (stream.Kind == StreamKind.Audio)
        {
            writer.WriteBoolean("isVbr", stream.IsVbr);
        }

        if (stream.PlaybackGroup is { } group)
        {
            writer.WriteNumber("playbackGroup", group);
        }

        if (stream.HasObjectAudio)
        {
            writer.WriteBoolean("hasObjectAudio", true);
        }

        if (stream.JocObjectCount is { } objects)
        {
            writer.WriteNumber("jocObjectCount", objects);
        }

        if (stream.Protection is { } protection)
        {
            writer.WriteBoolean("isProtected", true);
            writer.WriteStartObject("protection");
            writer.WriteString("scheme", protection.Scheme);
            writer.WriteBoolean("isEncrypted", protection.IsEncrypted);
            writer.WriteStartArray("systems");
            foreach (string system in protection.Systems)
            {
                writer.WriteStringValue(system);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (stream.Verification is { HasChecksums: true } verification)
        {
            WriteVerification(writer, verification);
        }

        if (stream.Measurements is { } measurements && filter.ShowsMeasurements)
        {
            WriteMeasurements(writer, measurements, filter.WritesGrids, explain);
        }

        if (stream.Encoder is not null)
        {
            writer.WriteString("encoder", stream.Encoder);
        }

        if (stream.EncoderConfidence is { } confidence)
        {
            writer.WriteString("encoderConfidence", confidence.ToString());
        }

        if (stream.Writer is not null)
        {
            writer.WriteString("writer", stream.Writer);
        }

        if (stream.WriterConfidence is { } writerConfidence)
        {
            writer.WriteString("writerConfidence", writerConfidence.ToString());
        }

        writer.WriteStartArray("series");
        foreach (Series series in stream.Series)
        {
            writer.WriteStartObject();
            writer.WriteString("name", series.Name);
            writer.WriteString("unit", series.Unit);
            writer.WriteString("axis", series.Axis == SeriesAxis.FrameOffset ? "frameOffset" : "seconds");
            if (series.Concept != GraphConcept.None)
            {
                writer.WriteString("concept", ConceptName(series.Concept));
            }

            if (series.Quantity is { } quantity)
            {
                writer.WriteString("quantity", quantity);
            }

            if (series.DeclaredRange is { } range)
            {
                WriteDeclaredRange(writer, range);
            }

            if (explain && series.Description is { } description)
            {
                writer.WriteString("description", description);
            }

            WriteNumbers(writer, "x", series.X.Span);
            WriteNumbers(writer, "values", series.Values.Span);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (stream.CategorySeries.Count > 0)
        {
            writer.WriteStartArray("categorySeries");
            foreach (CategorySeries series in stream.CategorySeries)
            {
                WriteCategorySeries(writer, series, explain);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static string ConceptName(GraphConcept concept) => concept switch
    {
        GraphConcept.StereoCoding => "stereoCoding",
        GraphConcept.BlockSwitching => "blockSwitching",
        GraphConcept.CodedBandwidth => "codedBandwidth",
        GraphConcept.BitDistribution => "bitDistribution",
        _ => throw new ArgumentOutOfRangeException(nameof(concept)),
    };

    private static void WriteCategorySeries(Utf8JsonWriter writer, CategorySeries series, bool explain)
    {
        writer.WriteStartObject();
        writer.WriteString("name", series.Name);
        if (series.Concept != GraphConcept.None)
        {
            writer.WriteString("concept", ConceptName(series.Concept));
        }

        writer.WriteString("valueLabel", series.ValueLabel);
        if (series.ValuesPerUnit > 1)
        {
            writer.WriteNumber("valuesPerUnit", series.ValuesPerUnit);
        }

        if (series.ValuesPerUnit > 1 && series.ValueNames.Count > 0)
        {
            writer.WriteStartArray("valueNames");
            foreach (string name in series.ValueNames)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
        }

        if (explain)
        {
            writer.WriteString("description", series.Description);
            if (series.Topic is { } topic)
            {
                writer.WriteString("topic", topic);
            }
        }

        writer.WriteStartArray("categories");
        for (int i = 0; i < series.Categories.Count; i++)
        {
            Category category = series.Categories[i];
            writer.WriteStartObject();
            writer.WriteString("name", category.Name);
            writer.WriteNumber("count", series.Counts[i]);
            if (category.IsReserved)
            {
                writer.WriteBoolean("isReserved", true);
            }

            if (explain)
            {
                writer.WriteString("description", category.Description);
                if (category.Topic is { } categoryTopic)
                {
                    writer.WriteString("topic", categoryTopic);
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("total", series.Total);
        if (series.UncodedValues > 0)
        {
            writer.WriteNumber("uncodedValues", series.UncodedValues);
        }

        if (series.XSeries is { } xSeries)
        {
            writer.WriteString("xSeries", xSeries.Name);
        }
        else if (series.Source is null)
        {
            WriteNumbers(writer, "x", series.X.Span);
        }

        if (series.Source is { } source)
        {
            writer.WriteString("groupOf", source.Name);
            writer.WriteStartArray("map");
            foreach (byte code in series.Map)
            {
                if (code == CategorySeries.None)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteNumberValue(code);
                }
            }

            writer.WriteEndArray();
        }
        else
        {
            WriteRuns(writer, series);
        }

        if (series.Time is { } time)
        {
            WriteIntegers(writer, "seconds", time.FirstUnits.Span);
        }

        writer.WriteEndObject();
    }

    // The coded values value by value within each unit and unit by unit as runs of [code, length], null for a value
    // without a category; a variable layout's number of values per unit as runs of [count, units]; weights as runs of
    // [weight, length] where any is not 1.
    private static void WriteRuns(Utf8JsonWriter writer, CategorySeries series)
    {
        Span<byte> codes = stackalloc byte[CategorySeries.MaxValuesPerUnit];
        Span<byte> weights = stackalloc byte[CategorySeries.MaxValuesPerUnit];

        if (series.ValuesPerUnit == 0)
        {
            writer.WriteStartArray("valueCounts");
            var counts = new Runs(writer);
            CategorySeries.UnitReader reader = series.Reader();
            int n;
            while ((n = reader.Next(codes)) >= 0)
            {
                counts.Add(n);
            }

            counts.End();
            writer.WriteEndArray();
        }

        writer.WriteStartArray("runs");
        var runs = new Runs(writer, CategorySeries.None);
        CategorySeries.UnitReader values = series.Reader();
        int count;
        while ((count = values.Next(codes)) >= 0)
        {
            for (int i = 0; i < count; i++)
            {
                runs.Add(codes[i]);
            }
        }

        runs.End();
        writer.WriteEndArray();

        if (series.HasWeights)
        {
            writer.WriteStartArray("weights");
            var weightRuns = new Runs(writer);
            CategorySeries.UnitReader reader = series.Reader();
            while ((count = reader.Next(codes, weights)) >= 0)
            {
                for (int i = 0; i < count; i++)
                {
                    weightRuns.Add(weights[i]);
                }
            }

            weightRuns.End();
            writer.WriteEndArray();
        }
    }

    // Writes a sequence of integers as [value, length] pairs; nullValue, the code of a value without a category, is
    // written as null.
    private struct Runs(Utf8JsonWriter writer, int nullValue = -1)
    {
        private int _value = -1;
        private long _length;

        public void Add(int value)
        {
            if (value == _value)
            {
                _length++;
                return;
            }

            End();
            _value = value;
            _length = 1;
        }

        public void End()
        {
            if (_length == 0)
            {
                return;
            }

            writer.WriteStartArray();
            if (_value == nullValue)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteNumberValue(_value);
            }

            writer.WriteNumberValue(_length);
            writer.WriteEndArray();
            _length = 0;
            if (writer.BytesPending > FlushThreshold)
            {
                writer.Flush();
            }
        }
    }

    private static void WriteMeasurements(Utf8JsonWriter writer, StreamMeasurements m, bool includeGrids, bool explain)
    {
        writer.WriteStartObject("measurements");
        writer.WriteString("status", m.Status switch
        {
            MeasurementStatus.Complete => "complete",
            MeasurementStatus.Stopped => "stopped",
            MeasurementStatus.NotPossible => "notPossible",
            MeasurementStatus.FormatChanged => "formatChanged",
            _ => "failed",
        });
        if (m.Reason is not null)
        {
            writer.WriteString("reason", m.Reason);
        }

        if (explain)
        {
            writer.WriteString("description", MeasurementText.MeasuredDescription);
            writer.WriteString("topic", MeasurementText.MeasuredTopic);
        }

        writer.WriteNumber("samples", m.Samples);
        writer.WriteNumber("firstPosition", m.FirstPosition);
        writer.WriteNumber("lostSamples", m.LostSamples);
        writer.WriteNumber("gaps", m.Gaps);
        writer.WriteNumber("sampleRate", m.SampleRate);
        writer.WriteNumber("channels", m.Channels);
        writer.WriteNumber("channelsMeasured", m.ChannelsMeasured);
        if (m.ChannelLayout is { } layout)
        {
            writer.WriteStartArray("channelLayout");
            foreach (Speaker speaker in layout)
            {
                writer.WriteStringValue(ChannelLayouts.Name(speaker));
            }

            writer.WriteEndArray();
        }

        if (m.HasClampedSamples)
        {
            writer.WriteBoolean("hasClampedSamples", true);
        }

        if (m.NonFiniteSamples > 0)
        {
            writer.WriteNumber("nonFiniteSamples", m.NonFiniteSamples);
        }

        if (m.Status != MeasurementStatus.Failed && m.Spectrum is { } spectrum)
        {
            writer.WriteStartObject("spectrum");
            writer.WriteNumber("fftSize", spectrum.FftSize);
            writer.WriteNumber("frames", spectrum.Frames);
            writer.WriteNumber("rowCount", MeasurementLimits.Rows);
            writer.WriteNumber("binsPerRow", spectrum.BinsPerRow);
            if (explain)
            {
                writer.WriteString("description", MeasurementText.SpectrumDescription);
            }

            writer.WriteStartObject("rows");
            writer.WriteString("unit", "dB");
            writer.WriteNumber("valuesPerUnit", 10);
            WriteRow(writer, "combined", spectrum.Combined.Span);
            writer.WriteStartArray("perChannel");
            foreach (ReadOnlyMemory<short> channel in spectrum.PerChannel)
            {
                WriteRowValues(writer, channel.Span);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            WriteCutoff(writer, "cutoff", spectrum.Cutoff, explain);
            writer.WriteStartArray("channelCutoffs");
            foreach (SpectralCutoff channel in spectrum.ChannelCutoffs)
            {
                writer.WriteStartObject();
                WriteCutoffFields(writer, channel);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (m.Status != MeasurementStatus.Failed && m.Levels is { } levels)
        {
            WriteLevels(writer, levels, explain);
        }

        if (m.Status != MeasurementStatus.Failed && m.Loudness is { } loudness)
        {
            WriteLoudness(writer, loudness, explain);
        }

        if (m.Status != MeasurementStatus.Failed && m.TruePeak is { } truePeak)
        {
            WriteTruePeak(writer, truePeak, explain);
        }

        if (includeGrids && m.Status != MeasurementStatus.Failed)
        {
            if (m.Spectrogram is { } grid)
            {
                writer.WriteStartObject("spectrogram");
                writer.WriteNumber("columns", grid.Columns);
                writer.WriteNumber("rows", MeasurementLimits.Rows);
                writer.WriteNumber("hopsPerColumn", grid.HopsPerColumn);
                writer.WriteNumber("hopSamples", grid.HopSamples);
                writer.WriteString("unit", "dB");
                writer.WriteNumber("valueOffset", MeasurementLimits.GridOffset);
                writer.WriteNumber("noData", 0);
                writer.WriteBase64String("power", grid.Power.Span);
                writer.WriteEndObject();
            }

            if (m.Waveform is { } wave)
            {
                writer.WriteStartObject("waveform");
                writer.WriteNumber("columns", wave.Columns);
                writer.WriteNumber("channels", wave.Channels);
                writer.WriteString("unit", "16-bit sample codes");
                writer.WriteBase64String("minimum", LittleEndian(wave.Minimum.Span));
                writer.WriteBase64String("maximum", LittleEndian(wave.Maximum.Span));
                writer.WriteEndObject();
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteDescribed(Utf8JsonWriter writer, bool explain, string description, string topic)
    {
        if (explain)
        {
            writer.WriteString("description", description);
            writer.WriteString("topic", topic);
        }
    }

    // A loudness figure: { value, unit } with a null value for minus infinity, or the unit and why there is no value.
    private static void WriteLoudnessFigure(Utf8JsonWriter writer, string name, int tenths, LoudnessAbsence absence, string unit, bool explain, string description, string topic, Action<Utf8JsonWriter>? more = null)
    {
        writer.WriteStartObject(name);
        WriteDescribed(writer, explain, description, topic);
        if (absence == LoudnessAbsence.None)
        {
            writer.WritePropertyName("value");
            WriteLevel(writer, tenths);
        }
        else
        {
            writer.WriteString("absence", absence == LoudnessAbsence.TooShort ? "tooShort" : "belowAbsoluteGate");
        }

        writer.WriteString("unit", unit);
        if (absence == LoudnessAbsence.None)
        {
            more?.Invoke(writer);
        }

        writer.WriteEndObject();
    }

    private static void WriteLoudness(Utf8JsonWriter writer, LoudnessFigures l, bool explain)
    {
        writer.WriteStartObject("loudness");
        WriteDescribed(writer, explain, MeasurementText.LoudnessDescription, MeasurementText.LoudnessTopic);
        if (l.NotMeasured is { } reason)
        {
            writer.WriteString("notMeasured", reason);
            writer.WriteEndObject();
            return;
        }

        writer.WriteStartArray("channelWeights");
        foreach (int weight in l.ChannelWeights)
        {
            writer.WriteNumberValue(weight / 100m);
        }

        writer.WriteEndArray();
        WriteLoudnessFigure(writer, "integratedLoudness", l.IntegratedTenths, l.IntegratedAbsence, "LUFS", explain, MeasurementText.IntegratedLoudnessDescription, MeasurementText.LoudnessTopic);
        WriteLoudnessFigure(writer, "loudnessRange", l.RangeTenths, l.RangeAbsence, "LU", explain, MeasurementText.LoudnessRangeDescription, MeasurementText.LoudnessRangeTopic, w =>
        {
            w.WriteStartObject("low");
            w.WritePropertyName("value");
            WriteLevel(w, l.RangeLowTenths);
            w.WriteString("unit", "LUFS");
            w.WriteEndObject();
            w.WriteStartObject("high");
            w.WritePropertyName("value");
            WriteLevel(w, l.RangeHighTenths);
            w.WriteString("unit", "LUFS");
            w.WriteEndObject();
        });
        WriteLoudnessFigure(writer, "maximumMomentaryLoudness", l.MaximumMomentaryTenths, l.MomentaryAbsence, "LUFS", explain, MeasurementText.MaximumMomentaryLoudnessDescription, MeasurementText.LoudnessTopic);
        WriteLoudnessFigure(writer, "maximumShortTermLoudness", l.MaximumShortTermTenths, l.ShortTermAbsence, "LUFS", explain, MeasurementText.MaximumShortTermLoudnessDescription, MeasurementText.LoudnessTopic);
        writer.WriteStartObject("gatingBlocks");
        writer.WriteNumber("total", l.Blocks);
        writer.WriteNumber("aboveAbsoluteGate", l.BlocksAboveAbsoluteGate);
        writer.WriteNumber("aboveRelativeGate", l.BlocksAboveRelativeGate);
        writer.WriteEndObject();
        if (l.ShortTerm is { } series)
        {
            writer.WriteStartObject("shortTerm");
            writer.WriteString("unit", series.Unit);
            writer.WriteNumber("stepsPerPoint", l.StepsPerPoint);
            if (explain)
            {
                writer.WriteString("description", series.Description);
            }

            WriteNumbers(writer, "x", series.X.Span);
            WriteNumbers(writer, "values", series.Values.Span);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteTruePeak(Utf8JsonWriter writer, TruePeakFigures t, bool explain)
    {
        writer.WriteStartObject("truePeak");
        WriteDescribed(writer, explain, MeasurementText.TruePeakDescription, MeasurementText.TruePeakTopic);
        if (t.NotMeasured is { } reason)
        {
            writer.WriteString("notMeasured", reason);
            writer.WriteEndObject();
            return;
        }

        writer.WriteNumber("oversampling", t.Oversampling);
        writer.WriteStartObject("maximumTruePeakLevel");
        writer.WritePropertyName("value");
        WriteLevel(writer, t.StreamTenths);
        writer.WriteString("unit", "dBTP");
        if (t.ChannelTenths.Count > 1)
        {
            writer.WriteStartArray("perChannel");
            foreach (int tenths in t.ChannelTenths)
            {
                WriteLevel(writer, tenths);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    // A level in tenths of a dB as a decimal placed and not formatted from a double; minus infinity is null.
    private static void WriteLevel(Utf8JsonWriter writer, int tenths)
    {
        if (tenths == MeasurementLimits.NoLevel)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(tenths / 10m);
        }
    }

    // One figure: { value, unit } and, for a stream of several channels, the same figure by channel in that unit.
    private static void WriteFigure(Utf8JsonWriter writer, string name, string unit, Action<ChannelLevels> write, LevelFigures levels, string? description, string? topic)
    {
        writer.WriteStartObject(name);
        if (description is not null)
        {
            writer.WriteString("description", description);
            writer.WriteString("topic", topic);
        }

        writer.WritePropertyName("value");
        write(levels.Stream);
        writer.WriteString("unit", unit);
        if (levels.Channels.Count > 1)
        {
            writer.WriteStartArray("perChannel");
            foreach (ChannelLevels channel in levels.Channels)
            {
                write(channel);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteLevels(Utf8JsonWriter writer, LevelFigures levels, bool explain)
    {
        writer.WriteStartObject("levels");
        writer.WriteString("format", levels.Format switch { SampleFormat.Integer => "integer", SampleFormat.Float32 => "float32", _ => "float64" });
        if (levels.Format == SampleFormat.Integer)
        {
            writer.WriteNumber("bitsPerSample", levels.BitsPerSample);
        }

        if (explain)
        {
            writer.WriteString("topic", MeasurementText.LevelsTopic);
        }

        WriteFigure(writer, "samplePeak", "dBFS", c => WriteLevel(writer, c.SamplePeakTenths), levels, explain ? MeasurementText.SamplePeakDescription : null, MeasurementText.LevelsTopic);
        WriteFigure(writer, "rms", "dBFS", c => WriteLevel(writer, c.RmsTenths), levels, explain ? MeasurementText.RmsDescription : null, MeasurementText.LevelsTopic);
        WriteFigure(writer, "dcOffset", "%", c => writer.WriteNumberValue(c.DcOffsetMicropercent / 1_000_000m), levels, explain ? MeasurementText.DcOffsetDescription : null, MeasurementText.DcOffsetTopic);
        WriteFigure(writer, "dcOffsetLevel", "dBFS", c => WriteLevel(writer, c.DcOffsetTenths), levels, explain ? MeasurementText.DcOffsetDescription : null, MeasurementText.DcOffsetTopic);
        WriteGroup(writer, "clipping", levels, explain ? MeasurementText.ClippingDescription : null, MeasurementText.ClippingTopic, (w, c) =>
        {
            w.WriteNumber("runs", c.ClippingRuns);
            w.WriteNumber("samples", c.ClippedSamples);
            w.WriteNumber("longestRun", c.LongestClippingRun);
        }, w => w.WriteNumber("minimumRunLength", MeasurementLimits.MinimumClippingRun));
        WriteGroup(writer, "digitalSilence", levels, explain ? MeasurementText.DigitalSilenceDescription : null, MeasurementText.DigitalSilenceTopic, (w, c) =>
        {
            w.WriteNumber("atStart", c.SilentAtStart);
            w.WriteNumber("atEnd", c.SilentAtEnd);
        }, null);
        WriteFigure(writer, "zeroSamples", "samples", c => writer.WriteNumberValue(c.ZeroSamples), levels, explain ? MeasurementText.ZeroSamplesDescription : null, MeasurementText.DigitalSilenceTopic);
        WriteGroup(writer, "bitDepth", levels, explain ? (levels.Format == SampleFormat.Integer ? MeasurementText.LowBitsUnusedDescription : MeasurementText.HasOnly16BitValuesDescription + " " + MeasurementText.HasOnly24BitValuesDescription) : null, MeasurementText.BitDepthUsageTopic, (w, c) =>
        {
            if (levels.Format == SampleFormat.Integer)
            {
                if (c.LowBitsUnused is { } unused)
                {
                    w.WriteNumber("lowBitsUnused", unused);
                }
                else
                {
                    w.WriteNull("lowBitsUnused");
                }

                w.WriteNumber("highestMagnitude", c.HighestMagnitude);
            }
            else
            {
                w.WriteBoolean("hasOnly16BitValues", c.HasOnly16BitValues);
                w.WriteBoolean("hasOnly24BitValues", c.HasOnly24BitValues);
            }
        }, null);
        writer.WriteEndObject();
    }

    // A figure of several fields: those of the stream, and for a stream of several channels the same fields by channel.
    private static void WriteGroup(Utf8JsonWriter writer, string name, LevelFigures levels, string? description, string topic, Action<Utf8JsonWriter, ChannelLevels> fields, Action<Utf8JsonWriter>? constants)
    {
        writer.WriteStartObject(name);
        if (description is not null)
        {
            writer.WriteString("description", description);
            writer.WriteString("topic", topic);
        }

        constants?.Invoke(writer);
        fields(writer, levels.Stream);
        if (levels.Channels.Count > 1)
        {
            writer.WriteStartArray("perChannel");
            foreach (ChannelLevels channel in levels.Channels)
            {
                writer.WriteStartObject();
                fields(writer, channel);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static byte[] LittleEndian(ReadOnlySpan<short> values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        }

        return bytes;
    }

    private static void WriteRow(Utf8JsonWriter writer, string name, ReadOnlySpan<short> values)
    {
        writer.WriteStartArray(name);
        WriteRowItems(writer, values);
        writer.WriteEndArray();
    }

    private static void WriteRowValues(Utf8JsonWriter writer, ReadOnlySpan<short> values)
    {
        writer.WriteStartArray();
        WriteRowItems(writer, values);
        writer.WriteEndArray();
    }

    // A row with no power at all is null.
    private static void WriteRowItems(Utf8JsonWriter writer, ReadOnlySpan<short> values)
    {
        foreach (short value in values)
        {
            if (value == MeasurementLimits.NoPower)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteNumberValue(value);
            }
        }
    }

    private static void WriteCutoff(Utf8JsonWriter writer, string name, SpectralCutoff cutoff, bool explain)
    {
        writer.WriteStartObject(name);
        if (explain)
        {
            writer.WriteString("description", MeasurementText.SpectralCutoffDescription);
            writer.WriteString("topic", MeasurementText.SpectralCutoffTopic);
        }

        WriteCutoffFields(writer, cutoff);
        writer.WriteEndObject();
    }

    // Figures with units as {value, unit}; tenths of a dB as a decimal, placed and not formatted from a double.
    private static void WriteCutoffFields(Utf8JsonWriter writer, SpectralCutoff cutoff)
    {
        if (!cutoff.IsSearched)
        {
            writer.WriteBoolean("isSearched", false);
            return;
        }

        writer.WriteBoolean("isFound", cutoff.IsFound);
        if (cutoff.IsFound)
        {
            WriteQuantity(writer, "frequency", cutoff.Frequency, "Hz");
            WriteQuantity(writer, "transitionStart", cutoff.TransitionStart, "Hz");
        }

        WriteQuantity(writer, "stepFrequency", cutoff.StepFrequency, "Hz");
        writer.WriteStartObject("step");
        writer.WriteNumber("value", cutoff.StepTenthsOfDb / 10m);
        writer.WriteString("unit", "dB");
        writer.WriteEndObject();
        writer.WriteNumber("framesShowing", cutoff.FramesShowing);
        writer.WriteNumber("framesWithSignal", cutoff.FramesWithSignal);
    }

    private static void WriteDeclaredRange(Utf8JsonWriter writer, ValueRange range)
    {
        writer.WriteStartObject("declaredRange");
        if (range.HasSteps)
        {
            WriteNumbers(writer, "steps", range.Steps.Span);
        }
        else
        {
            writer.WriteNumber("minimum", range.Minimum);
            writer.WriteNumber("maximum", range.Maximum);
        }

        writer.WriteEndObject();
    }

    private static void WriteQuantity(Utf8JsonWriter writer, string name, int value, string unit)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("value", value);
        writer.WriteString("unit", unit);
        writer.WriteEndObject();
    }

    private static void WriteVerification(Utf8JsonWriter writer, StreamVerification verification)
    {
        writer.WriteStartObject("verification");
        if (verification.IsVerified is { } isVerified)
        {
            writer.WriteBoolean("isVerified", isVerified);
        }

        if (verification.IsLossy)
        {
            writer.WriteBoolean("isLossy", true);
        }

        writer.WriteString("decoding", verification.Decoding switch
        {
            DecodeStatus.NotRequested => "notRequested",
            DecodeStatus.NoDecoder => "noDecoder",
            DecodeStatus.NotPossible => "notPossible",
            DecodeStatus.Decoded => "decoded",
            DecodeStatus.Stopped => "stopped",
            _ => "failed",
        });
        writer.WriteNumber("decodedSamples", verification.DecodedSamples);
        writer.WriteStartArray("checksums");
        foreach (ChecksumTally tally in verification.Checksums)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tally.Name);
            writer.WriteString("covers", tally.Covers switch { ChecksumCoverage.Header => "header", ChecksumCoverage.CodedData => "codedData", _ => "decodedAudio" });
            writer.WriteBoolean("coversAllAudio", tally.CoversAllAudio);
            writer.WriteNumber("stored", tally.Stored);
            writer.WriteNumber("checked", tally.Checked);
            writer.WriteNumber("failed", tally.Failed);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteNumbers(Utf8JsonWriter writer, string name, ReadOnlySpan<double> values)
    {
        writer.WriteStartArray(name);
        foreach (double value in values)
        {
            writer.WriteNumberValue(value);
            if (writer.BytesPending > FlushThreshold)
            {
                writer.Flush();
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteIntegers(Utf8JsonWriter writer, string name, ReadOnlySpan<int> values)
    {
        writer.WriteStartArray(name);
        foreach (int value in values)
        {
            writer.WriteNumberValue(value);
            if (writer.BytesPending > FlushThreshold)
            {
                writer.Flush();
            }
        }

        writer.WriteEndArray();
    }

    private static void WritePath(Utf8JsonWriter writer, string? name, IReadOnlyList<int> path)
    {
        if (name is null)
        {
            writer.WriteStartArray();
        }
        else
        {
            writer.WriteStartArray(name);
        }

        foreach (int index in path)
        {
            writer.WriteNumberValue(index);
        }

        writer.WriteEndArray();
    }

    private static void WriteValue(Utf8JsonWriter writer, NodeValue value)
    {
        switch (value.Kind)
        {
            case NodeValueKind.Integer:
                writer.WriteNumber("value", value.AsInteger()!.Value);
                break;
            case NodeValueKind.Hex:
                writer.WriteNumber("value", value.AsUnsigned()!.Value);
                break;
            case NodeValueKind.Decimal:
                writer.WriteNumber("value", value.AsDouble()!.Value);
                break;
            case NodeValueKind.Boolean:
                writer.WriteBoolean("value", value.AsBoolean()!.Value);
                break;
            default:
                writer.WriteString("value", value.AsText());
                break;
        }

        if (value.Unit is not null)
        {
            writer.WriteString("unit", value.Unit);
        }
    }

    // depth: levels below the root. Children nested deeper than Node.MaxDepth are left out, and
    // hasOmittedChildren says so.
    private static void WriteNode(Utf8JsonWriter writer, Node node, ReportFilter filter, bool explain, int depth)
    {
        writer.WriteStartObject();
        writer.WriteString("name", node.Name);
        if (node.Kind != NodeKind.Detail)
        {
            writer.WriteString("kind", node.Kind.ToString());
        }

        if (node.Value is { } value)
        {
            WriteValue(writer, value);
        }

        if (explain && node.Description is { } description)
        {
            writer.WriteString("description", description);
        }

        if (explain && node.Topic is { } topic)
        {
            writer.WriteString("topic", topic);
        }

        writer.WriteNumber("offset", node.Offset);
        writer.WriteNumber("length", node.Length);
        if (node.BitOffset is { } bitOffset)
        {
            writer.WriteNumber("bitOffset", bitOffset);
        }

        if (node.BitLength is { } bitLength)
        {
            writer.WriteNumber("bitLength", bitLength);
            if (node.BitOrder == BitOrder.LeastSignificantFirst)
            {
                writer.WriteString("bitOrder", "leastSignificantFirst");
            }
        }

        if (node.Ranges is { } ranges)
        {
            writer.WriteStartArray("ranges");
            foreach (ByteRange range in ranges)
            {
                writer.WriteStartObject();
                writer.WriteNumber("offset", range.Offset);
                writer.WriteNumber("length", range.Length);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (depth >= Node.MaxDepth)
        {
            if (node.HasChildren && node.Children.Any(filter.Includes))
            {
                writer.WriteBoolean("hasOmittedChildren", true);
            }
        }
        else
        {
            IReadOnlyList<Node> children = filter.Hidden == ReportSections.None ? node.Children : node.Children.Where(filter.Includes).ToList();
            if (children.Count > 0)
            {
                writer.WriteStartArray("children");
                foreach (Node child in children)
                {
                    WriteNode(writer, child, filter, explain, depth + 1);
                }

                writer.WriteEndArray();
            }
        }

        writer.WriteEndObject();
        if (writer.BytesPending > FlushThreshold)
        {
            writer.Flush();
        }
    }
}
