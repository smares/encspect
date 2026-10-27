using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Measurement;

// Every measured fixture of tests/fixtures/measurement (the synthetic ones from make_measurement_fixtures.py and the lossy-derived ones from
// make_external_measurement_fixtures.py) against its manifest, whose figures measurement_model.py works out from the file with its own FFT, and
// against what the signal was built to have; measuring changes nothing else in the report; and a small golden of what measuring adds
// (<name>.measure.json, the grids replaced by their SHA-256, rewritten with ENCSPECT_UPDATE_GOLDEN=1).
public class MeasurementFixtureTests
{
    private static readonly string Directory_ = Path.Combine(Fixtures.FixtureDirectory, "measurement");

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(Directory_, "*.flac").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(path));
        }

        return data;
    }

    internal static Report Analyze(string name, MeasureOptions? measure, long? maxWork = null)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Directory_, name + ".flac"));
        return Analyzer.CreateDefault().Analyze(name + ".flac", stream, new AnalysisOptions { Decoding = new DecodeOptions { Measure = measure, MaxWork = maxWork } });
    }

    private static JsonElement Manifest(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory_, name + ".expected.json"))).RootElement;

    [Theory]
    [MemberData(nameof(Names))]
    public void TheMeasurementsAgreeWithTheModelOfTheFile(string name)
    {
        JsonElement manifest = Manifest(name);
        JsonElement model = manifest.GetProperty("model");
        StreamMeasurements m = Analyze(name, new MeasureOptions()).Streams.Single().Measurements!;

        Assert.Equal(MeasurementStatus.Complete, m.Status);
        Assert.Equal(model.GetProperty("samples").GetInt64(), m.Samples);
        Assert.Equal(model.GetProperty("first_position").GetInt64(), m.FirstPosition);
        Assert.Equal(model.GetProperty("lost_samples").GetInt64(), m.LostSamples);
        Assert.Equal(model.GetProperty("gaps").GetInt32(), m.Gaps);
        Assert.Equal(model.GetProperty("sample_rate").GetInt32(), m.SampleRate);
        Assert.Equal(model.GetProperty("channels_measured").GetInt32(), m.ChannelsMeasured);
        Assert.Equal(model.GetProperty("fft_size").GetInt32(), m.Spectrum!.FftSize);
        Assert.Equal(model.GetProperty("frames").GetInt32(), m.Spectrum.Frames);

        // The rows agree to a tenth of a dB where they are above the rounding noise of the arithmetic, which a coherent signal has far below.
        AssertRows(model.GetProperty("combined"), m.Spectrum.Combined.Span, name);
        int channels = 0;
        foreach (JsonElement row in model.GetProperty("per_channel").EnumerateArray())
        {
            AssertRows(row, m.Spectrum.PerChannel[channels++].Span, name);
        }

        bool reliable = !(manifest.TryGetProperty("construction", out JsonElement built) && built.TryGetProperty("unspecified", out JsonElement unspecified) && unspecified.GetBoolean());
        if (reliable)
        {
            AssertCutoff(model.GetProperty("cutoff"), m.Spectrum.Cutoff, m.SampleRate, m.Spectrum.FftSize);
            int channel = 0;
            foreach (JsonElement cutoff in model.GetProperty("channel_cutoffs").EnumerateArray().Take(m.ChannelsMeasured))
            {
                AssertCutoff(cutoff, m.Spectrum.ChannelCutoffs[channel++], m.SampleRate, m.Spectrum.FftSize);
            }
        }

        // The levels: every count and the DC offset exact, the tenths of a dB within one tenth (the model's logarithm is the C library's).
        JsonElement levels = model.GetProperty("levels");
        Assert.Equal(levels.GetProperty("bits").GetInt32(), m.Levels!.BitsPerSample);
        Assert.Equal(SampleFormat.Integer, m.Levels.Format);
        AssertLevels(levels.GetProperty("stream"), m.Levels.Stream, $"{name} stream");
        int channelIndex = 0;
        foreach (JsonElement channel in levels.GetProperty("channels").EnumerateArray())
        {
            AssertLevels(channel, m.Levels.Channels[channelIndex], $"{name} channel {channelIndex}");
            channelIndex++;
        }

        Assert.Equal(m.ChannelsMeasured, m.Levels.Channels.Count);

        // The grids' time axis and the waveform's extremes, which are exact.
        Assert.Equal(model.GetProperty("spectrogram_columns").GetInt32(), m.Spectrogram!.Columns);
        Assert.Equal(model.GetProperty("hops_per_column").GetInt32(), m.Spectrogram.HopsPerColumn);
        Assert.Equal(m.Spectrogram.Columns * 512, m.Spectrogram.Power.Length);
        int index = 0;
        foreach (JsonElement wave in model.GetProperty("waveform").EnumerateArray())
        {
            short[] minimum = [.. wave.GetProperty("minimum").EnumerateArray().Select(v => (short)v.GetInt32())];
            short[] maximum = [.. wave.GetProperty("maximum").EnumerateArray().Select(v => (short)v.GetInt32())];
            Assert.True(minimum.AsSpan().SequenceEqual(m.Waveform!.Minimum.Span.Slice(index * m.Waveform.Columns, m.Waveform.Columns)), $"{name}: waveform minimum of channel {index}");
            Assert.True(maximum.AsSpan().SequenceEqual(m.Waveform.Maximum.Span.Slice(index * m.Waveform.Columns, m.Waveform.Columns)), $"{name}: waveform maximum of channel {index}");
            index++;
        }
    }

    private static void AssertLevels(JsonElement expected, ChannelLevels actual, string where)
    {
        void Tenths(string key, int value)
        {
            JsonElement e = expected.GetProperty(key);
            if (e.ValueKind == JsonValueKind.Null)
            {
                Assert.True(value == MeasurementLimits.NoLevel, $"{where}: {key} reads {value}, the model has none");
            }
            else
            {
                Assert.True(value != MeasurementLimits.NoLevel && Math.Abs(value - e.GetInt32()) <= 1, $"{where}: {key} reads {value}, the model {e.GetInt32()}");
            }
        }

        void Exact(string key, long value) => Assert.True(expected.GetProperty(key).GetInt64() == value, $"{where}: {key} reads {value}, the model {expected.GetProperty(key).GetInt64()}");

        Tenths("sample_peak_tenths", actual.SamplePeakTenths);
        Tenths("rms_tenths", actual.RmsTenths);
        Tenths("dc_tenths", actual.DcOffsetTenths);
        Exact("dc_micropercent", actual.DcOffsetMicropercent);
        Exact("clipping_runs", actual.ClippingRuns);
        Exact("clipped_samples", actual.ClippedSamples);
        Exact("longest_clipping_run", actual.LongestClippingRun);
        Exact("silent_at_start", actual.SilentAtStart);
        Exact("silent_at_end", actual.SilentAtEnd);
        Exact("silent_samples", actual.SilentSamples);
        Exact("highest_magnitude", actual.HighestMagnitude);
        JsonElement bits = expected.GetProperty("low_bits_unused");
        Assert.Equal(bits.ValueKind == JsonValueKind.Null ? null : bits.GetInt32(), actual.LowBitsUnused);
    }

    // What the levels fixtures were built to have (construction.levels, exact where the construction fixes a figure), whatever the model says.
    [Theory]
    [MemberData(nameof(Names))]
    public void TheLevelsAreWhatTheSignalWasBuiltToHave(string name)
    {
        if (!Manifest(name).TryGetProperty("construction", out JsonElement construction) || !construction.TryGetProperty("levels", out JsonElement built))
        {
            return;
        }

        LevelFigures levels = Analyze(name, new MeasureOptions()).Streams.Single().Measurements!.Levels!;
        AssertBuilt(built.GetProperty("stream"), levels.Stream, name + " stream");
        if (built.TryGetProperty("channels", out JsonElement channels))
        {
            int index = 0;
            foreach (JsonElement channel in channels.EnumerateArray())
            {
                AssertBuilt(channel, levels.Channels[index], $"{name} channel {index++}");
            }
        }
    }

    private static void AssertBuilt(JsonElement built, ChannelLevels actual, string where)
    {
        foreach (JsonProperty property in built.EnumerateObject())
        {
            object? got = property.Name switch
            {
                "sample_peak_tenths" => (object?)(actual.SamplePeakTenths == MeasurementLimits.NoLevel ? null : actual.SamplePeakTenths),
                "rms_tenths" => actual.RmsTenths == MeasurementLimits.NoLevel ? null : actual.RmsTenths,
                "dc_micropercent" => actual.DcOffsetMicropercent,
                "clipping_runs" => actual.ClippingRuns,
                "clipped_samples" => actual.ClippedSamples,
                "longest_clipping_run" => actual.LongestClippingRun,
                "silent_at_start" => actual.SilentAtStart,
                "silent_at_end" => actual.SilentAtEnd,
                "silent_samples" => actual.SilentSamples,
                "low_bits_unused" => actual.LowBitsUnused,
                "highest_magnitude" => actual.HighestMagnitude,
                _ => throw new InvalidOperationException(property.Name),
            };
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                Assert.True(got is null, $"{where}: {property.Name} reads {got}, built to have none");
            }
            else
            {
                Assert.True(got is not null && Convert.ToInt64(got, System.Globalization.CultureInfo.InvariantCulture) == property.Value.GetInt64(), $"{where}: {property.Name} reads {got}, built to have {property.Value.GetInt64()}");
            }
        }
    }

    private static void AssertRows(JsonElement expected, ReadOnlySpan<short> actual, string name)
    {
        int row = 0;
        foreach (JsonElement value in expected.EnumerateArray())
        {
            short got = actual[row];
            if (value.ValueKind != JsonValueKind.Null && got != MeasurementLimits.NoPower && value.GetInt32() > -1700 && got > -1700)
            {
                Assert.True(Math.Abs(value.GetInt32() - got) <= 1, $"{name}: row {row} reads {got}, the model {value.GetInt32()}");
            }

            row++;
        }
    }

    private static void AssertCutoff(JsonElement model, SpectralCutoff actual, int rate, int size)
    {
        if (!model.GetProperty("is_searched").GetBoolean())
        {
            Assert.False(actual.IsSearched);
            return;
        }

        int rowHertz = (int)Math.Ceiling((double)rate * (size / 1024) / size);
        Assert.Equal(model.GetProperty("is_found").GetBoolean(), actual.IsFound);
        Assert.InRange(actual.StepTenthsOfDb, model.GetProperty("step_tenths").GetInt32() - 2, model.GetProperty("step_tenths").GetInt32() + 2);
        Assert.InRange(actual.StepFrequency, model.GetProperty("step_frequency").GetInt32() - rowHertz, model.GetProperty("step_frequency").GetInt32() + rowHertz);
        Assert.InRange(actual.FramesWithSignal, model.GetProperty("frames_with_signal").GetInt32() - 1, model.GetProperty("frames_with_signal").GetInt32() + 1);
        Assert.InRange(actual.FramesShowing, model.GetProperty("frames_showing").GetInt32() - 1, model.GetProperty("frames_showing").GetInt32() + 1);
        if (actual.IsFound)
        {
            Assert.InRange(actual.Frequency, model.GetProperty("frequency").GetInt32() - rowHertz, model.GetProperty("frequency").GetInt32() + rowHertz);
            Assert.InRange(actual.TransitionStart, model.GetProperty("transition_start").GetInt32() - rowHertz, model.GetProperty("transition_start").GetInt32() + rowHertz);
        }

    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheCutoffIsWhereTheSignalWasBuiltToHaveIt(string name)
    {
        if (!Manifest(name).TryGetProperty("construction", out JsonElement construction))
        {
            return;
        }

        StreamMeasurements m = Analyze(name, new MeasureOptions()).Streams.Single().Measurements!;
        SpectralCutoff cutoff = m.Spectrum!.Cutoff;
        int rowHertz = (int)Math.Ceiling((double)m.SampleRate * (m.Spectrum.FftSize / 1024) / m.Spectrum.FftSize);
        bool Has(string key) => construction.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.True;

        if (Has("hidden_by_dither") || Has("no_cutoff"))
        {
            Assert.False(cutoff.IsFound, name);
        }
        else if (construction.TryGetProperty("pass_hz", out JsonElement pass))
        {
            // A sloped lowpass: the cutoff lies between where the pass band ends and where the stop band starts.
            Assert.True(cutoff.IsFound, name);
            Assert.InRange(cutoff.Frequency, pass.GetInt32() - rowHertz, construction.GetProperty("stop_hz").GetInt32() + rowHertz);
        }
        else if (construction.TryGetProperty("cutoff_hz", out JsonElement hertz) && hertz.ValueKind == JsonValueKind.Number)
        {
            Assert.True(cutoff.IsFound, name);
            int tolerance = construction.TryGetProperty("tolerance_hz", out JsonElement wide) ? wide.GetInt32() : 2 * rowHertz;
            Assert.InRange(cutoff.Frequency, hertz.GetInt32() - rowHertz, hertz.GetInt32() + tolerance);
            Assert.InRange(cutoff.TransitionStart, 1, cutoff.Frequency);
        }

        if (cutoff.IsFound)
        {
            Assert.True(cutoff.Frequency >= cutoff.StepFrequency, $"{name}: the cutoff is never below the step");
        }

        Assert.Equal(m.SampleRate >= 8000, cutoff.IsSearched);
    }

    public static TheoryData<string> LossyNames()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(Directory_, "lossy-*.flac").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(path));
        }

        return data;
    }

    // In Encspect's test files: where the encoder applies a lowpass the spectral cutoff is found, and for LAME it lies inside the transition
    // band that lame --verbose prints, within two rows of the analyzer's spectrum; LAME -V 0 and oggenc -q 8 leave none. The analyzer does not
    // name an encoder from a cutoff.
    [Theory]
    [MemberData(nameof(LossyNames))]
    public void ALossyDerivedFileGivesTheCutoffItsEncoderLeaves(string name)
    {
        JsonElement manifest = Manifest(name);
        StreamMeasurements m = Analyze(name, new MeasureOptions()).Streams.Single().Measurements!;
        SpectralCutoff cutoff = m.Spectrum!.Cutoff;
        int rowHertz = (int)Math.Ceiling((double)m.SampleRate * (m.Spectrum.FftSize / 1024) / m.Spectrum.FftSize);

        Assert.Equal(manifest.GetProperty("expect_cutoff").GetString() == "found", cutoff.IsFound);
        if (cutoff.IsFound)
        {
            Assert.True(cutoff.StepTenthsOfDb >= 330, $"{name}: a step of {cutoff.StepTenthsOfDb} tenths");
            Assert.Equal(cutoff.FramesWithSignal, cutoff.FramesShowing);
            Assert.True(cutoff.FramesWithSignal >= 20);
        }

        // The band is checked at the rates of the encoder's usual range; at 22,050 Hz the band reaches Nyquist and the cutoff lies 500 Hz below it.
        if (m.SampleRate >= 44100 && manifest.GetProperty("stated").ValueKind == JsonValueKind.Object && manifest.GetProperty("stated").GetProperty("transition_band_hz") is { ValueKind: JsonValueKind.Array } band)
        {
            Assert.InRange(cutoff.Frequency, band[0].GetInt32() - rowHertz, band[1].GetInt32() + rowHertz);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void MeasuringChangesNothingElseInTheReport(string name)
    {
        Report plain = Analyze(name, null);
        Report measured = Analyze(name, new MeasureOptions());
        Report starved = Analyze(name, new MeasureOptions { MaxWork = 1000 });

        Assert.Null(plain.Streams.Single().Measurements);
        Assert.Equal(Json(plain), Json(measured));
        foreach (Report other in new[] { measured, starved })
        {
            Assert.True(plain.Streams.Single().Verification!.Checksums.SequenceEqual(other.Streams.Single().Verification!.Checksums));
            Assert.Equal(plain.Streams.Single().Verification!.IsVerified, other.Streams.Single().Verification!.IsVerified);
        }

        // The starved run stopped its measuring and said so, and only so.
        Assert.Contains(starved.Findings, f => f.Code == "measure.limit");
        Assert.Equal(plain.Findings.Select(f => f.Code), starved.Findings.Where(f => !f.Code.StartsWith("measure.", StringComparison.Ordinal)).Select(f => f.Code));
    }

    // The report as JSON without its measurements.
    private static string Json(Report report)
    {
        using var stream = new MemoryStream();
        ReportJsonWriter.Write([report], stream, new ReportFilter(ReportSections.Measurements | ReportSections.Frames | ReportSections.Statistics));
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheMeasureGoldenMatches(string name)
    {
        string actual = Golden(Analyze(name, new MeasureOptions()), name);
        string path = Path.Combine(Directory_, name + ".measure.json");
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    // The fixtures whose goldens keep their rows and grids whole; every other golden holds the SHA-256 of them (the rows are held to the model's within
    // 0.1 dB by TheMeasurementsAgreeWithTheModelOfTheFile, so a golden needs only to notice a change).
    private static readonly string[] WholeGoldens = ["synthetic-sine-bin-center", "synthetic-lines-16000-44100", "synthetic-surround-5-1-wideband-lfe", "lossy-lame-b64"];

    // What measuring adds: the measurements as the JSON writer gives them, with the rows and grids replaced by their SHA-256 (but for four
    // fixtures), and the measure findings.
    internal static string Golden(Report report, string name)
    {
        using var stream = new MemoryStream();
        ReportJsonWriter.Write([report], stream, new ReportFilter(ReportSections.Frames | ReportSections.Tags | ReportSections.Statistics | ReportSections.SideInfo) { WritesGrids = true });
        JsonNode measurements = JsonNode.Parse(stream.ToArray())![0]!["streams"]![0]!["measurements"]!.DeepClone();
        if (!WholeGoldens.Contains(name))
        {
            if (measurements["spectrum"]?["rows"] is JsonObject rows)
            {
                rows["combined"] = HashRow(rows["combined"]!.AsArray());
                rows["perChannel"] = new JsonArray([.. rows["perChannel"]!.AsArray().Select(r => (JsonNode)HashRow(r!.AsArray()))]);
            }

            if (measurements["spectrogram"] is JsonObject grid)
            {
                grid["power"] = Hash(grid["power"]!.GetValue<string>());
            }

            if (measurements["waveform"] is JsonObject wave)
            {
                wave["minimum"] = Hash(wave["minimum"]!.GetValue<string>());
                wave["maximum"] = Hash(wave["maximum"]!.GetValue<string>());
            }
        }

        var result = new JsonObject
        {
            ["measurements"] = measurements,
            ["findings"] = new JsonArray([.. report.Findings.Where(f => f.Code.StartsWith("measure.", StringComparison.Ordinal)).Select(f => (JsonNode)new JsonObject { ["code"] = f.Code, ["message"] = f.Message })]),
        };
        return Write(result, 0) + "\n";
    }

    // Indented by two spaces, with every array of plain values on one line.
    private static string Write(JsonNode? node, int depth)
    {
        string pad = new(' ', (depth + 1) * 2);
        switch (node)
        {
            case JsonObject obj:
                return obj.Count == 0 ? "{}" : "{\n" + string.Join(",\n", obj.Select(p => $"{pad}{JsonSerializer.Serialize(p.Key)}: {Write(p.Value, depth + 1)}")) + "\n" + new string(' ', depth * 2) + "}";
            case JsonArray array when array.All(e => e is not JsonObject and not JsonArray):
                return "[" + string.Join(", ", array.Select(e => e is null ? "null" : e.ToJsonString())) + "]";
            case JsonArray array:
                return "[\n" + string.Join(",\n", array.Select(e => pad + Write(e, depth + 1))) + "\n" + new string(' ', depth * 2) + "]";
            default:
                return node is null ? "null" : node.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
    }

    // A row of tenths as 16-bit little-endian values, null as the value of no power.
    private static string HashRow(JsonArray row)
    {
        byte[] bytes = new byte[row.Count * 2];
        for (int i = 0; i < row.Count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), row[i] is null ? MeasurementLimits.NoPower : row[i]!.GetValue<short>());
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string Hash(string base64) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(base64)));

    [Fact]
    public void ABurstShowsInTheColumnsOfTheLongStream()
    {
        StreamMeasurements m = Analyze("synthetic-long-bursts-48-seconds", new MeasureOptions()).Streams.Single().Measurements!;

        Assert.Equal(2, m.Spectrogram!.HopsPerColumn);
        Assert.InRange(m.Spectrogram.Columns, 512, 1024);
        foreach (int second in new[] { 10, 25, 40 })
        {
            int column = second * 44100 / (m.Spectrogram.HopSamples * m.Spectrogram.HopsPerColumn);
            int loudest = Enumerable.Range(column - 2, 5).MaxBy(c => m.Spectrogram.Power.Span[(c * 512) + 100]);
            Assert.True(m.Spectrogram.Power.Span[(loudest * 512) + 100] > m.Spectrogram.Power.Span[(Math.Max(column - 12, 0) * 512) + 100] + 20);
        }
    }

    [Fact]
    public void ASineKeepsItsLevelAtEveryBitDepth()
    {
        foreach ((string name, int slack) in new[] { ("sine-bin-center", 1), ("sine-24-bits", 1), ("sine-32-bits", 1), ("sine-8-bits", 6) })
        {
            StreamMeasurements m = Analyze("synthetic-" + name, new MeasureOptions()).Streams.Single().Measurements!;

            // A sine of amplitude 0.5 on bin 400 and a Hann window: bin 400 reads -6.02 dB and its neighbors -12.04 dB, in row 100 with three empty bins and row 99.
            Assert.InRange(m.Spectrum!.Combined.Span[100], -111 - slack, -111 + slack);
            Assert.InRange(m.Spectrum.Combined.Span[99], -181 - slack, -181 + slack);
        }
    }

    [Fact]
    public void TheSweepMovesUpTheRowsOverTime()
    {
        StreamMeasurements m = Analyze("synthetic-sweep", new MeasureOptions()).Streams.Single().Measurements!;
        SpectrogramGrid grid = m.Spectrogram!;

        int previous = -1;
        for (int column = 2; column < grid.Columns - 2; column += 4)
        {
            int loudest = Enumerable.Range(0, 512).MaxBy(r => grid.Power.Span[(column * 512) + r]);
            Assert.True(loudest >= previous, $"column {column} peaks at row {loudest} after {previous}");
            previous = loudest;
        }

        // The sweep is logarithmic: 100 Hz to 20 kHz in 2 s, so at half the time it is near 1.4 kHz (row 32 of 43 Hz).
        int middle = grid.Columns / 2;
        int row = Enumerable.Range(0, 512).MaxBy(r => grid.Power.Span[(middle * 512) + r]);
        Assert.InRange(row, 25, 40);
    }
}
