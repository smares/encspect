using System.Text;
using System.Text.Json;
using Encspect.Cli;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Measurement;

// --measure, --measure-grids and --hide measurements on the command line, and the text, JSON and CSV writers of the measurements.
public class MeasurementOutputTests
{
    private static readonly string Brickwall = Path.Combine(Fixtures.FixtureDirectory, "measurement", "synthetic-lines-16000-44100.flac");

    private static (int Code, string Output, string Errors) Run(params string[] args)
    {
        using var stdout = new MemoryStream();
        var stderr = new StringWriter();
        int code = CommandLine.Run(args, stdout, stderr);
        return (code, Encoding.UTF8.GetString(stdout.ToArray()), stderr.ToString());
    }

    [Fact]
    public void MeasureWritesTheMeasuredLinesAfterTheVerifiedLine()
    {
        (int code, string output, _) = Run("--measure", "--hide", "frames,tags", Brickwall);

        Assert.Equal(CommandLine.Success, code);
        string[] lines = output.ReplaceLineEndings("\n").Split('\n');
        int verified = Array.FindIndex(lines, l => l.TrimStart().StartsWith("Verified: ", StringComparison.Ordinal));
        Assert.StartsWith("Measured: 1.0 s of 1 channel", lines[verified + 1].TrimStart());
        Assert.Matches(@"^\s+Spectral Cutoff: 16,0\d\d Hz \(a step of 81\.6 dB, transition from 1[56],\d{3} Hz, in 20 of 20 frames with signal\)$", lines[verified + 2]);
        Assert.Equal(lines[verified].Length - lines[verified].TrimStart().Length, lines[verified + 1].Length - lines[verified + 1].TrimStart().Length);
        Assert.Equal(2, lines[verified + 2].Length - lines[verified + 2].TrimStart().Length - (lines[verified + 1].Length - lines[verified + 1].TrimStart().Length));
    }

    [Fact]
    public void WithoutMeasureTheOutputHasNoMeasurements()
    {
        (_, string output, _) = Run("--verify", Brickwall);

        Assert.DoesNotContain("Measured:", output);
        Assert.DoesNotContain("Spectral Cutoff", output);
    }

    [Fact]
    public void MeasureImpliesVerify()
    {
        (_, string output, _) = Run("--measure", Brickwall);

        Assert.Contains("Verified: yes (", output);
    }

    [Fact]
    public void HideMeasurementsLeavesThemOutOfTheTextAndTheJson()
    {
        (_, string text, _) = Run("--measure", "--hide", "measurements", Brickwall);
        (_, string json, _) = Run("--measure", "--json", "--hide", "measurements", Brickwall);

        Assert.DoesNotContain("Measured:", text);
        Assert.Contains("Verified: yes", text);
        Assert.DoesNotContain("\"measurements\"", json);
        Assert.Contains("\"verification\"", json);
    }

    [Fact]
    public void TheJsonHoldsTheFiguresAsIntegersAndPlacedDecimalsAndNoGridsByDefault()
    {
        (_, string output, _) = Run("--measure", "--json", Brickwall);

        JsonElement measurements = JsonDocument.Parse(output).RootElement[0].GetProperty("streams")[0].GetProperty("measurements");
        Assert.Equal("complete", measurements.GetProperty("status").GetString());
        Assert.Equal(44100, measurements.GetProperty("samples").GetInt64());
        JsonElement spectrum = measurements.GetProperty("spectrum");
        Assert.Equal((4096, 512, 4, 20), (spectrum.GetProperty("fftSize").GetInt32(), spectrum.GetProperty("rowCount").GetInt32(), spectrum.GetProperty("binsPerRow").GetInt32(), spectrum.GetProperty("frames").GetInt32()));
        Assert.Equal(10, spectrum.GetProperty("rows").GetProperty("valuesPerUnit").GetInt32());
        Assert.Equal(512, spectrum.GetProperty("rows").GetProperty("combined").GetArrayLength());
        Assert.Equal(1, spectrum.GetProperty("rows").GetProperty("perChannel").GetArrayLength());
        JsonElement cutoff = spectrum.GetProperty("cutoff");
        Assert.True(cutoff.GetProperty("isFound").GetBoolean());
        Assert.Equal("Hz", cutoff.GetProperty("frequency").GetProperty("unit").GetString());
        Assert.Contains("\"value\": 81.6", cutoff.GetRawText());
        Assert.Equal("dB", cutoff.GetProperty("step").GetProperty("unit").GetString());
        Assert.False(measurements.TryGetProperty("spectrogram", out _));
        Assert.False(measurements.TryGetProperty("waveform", out _));
    }

    [Fact]
    public void MeasureGridsAddsTheSpectrogramAndTheWaveformAsBase64()
    {
        (int code, string output, _) = Run("--measure-grids", "--json", Brickwall);

        Assert.Equal(CommandLine.Success, code);
        JsonElement measurements = JsonDocument.Parse(output).RootElement[0].GetProperty("streams")[0].GetProperty("measurements");
        JsonElement grid = measurements.GetProperty("spectrogram");
        Assert.Equal(512, grid.GetProperty("rows").GetInt32());
        Assert.Equal(200, grid.GetProperty("valueOffset").GetInt32());
        Assert.Equal(grid.GetProperty("columns").GetInt32() * 512, Convert.FromBase64String(grid.GetProperty("power").GetString()!).Length);
        JsonElement wave = measurements.GetProperty("waveform");
        Assert.Equal(wave.GetProperty("columns").GetInt32() * 2, Convert.FromBase64String(wave.GetProperty("minimum").GetString()!).Length);
    }

    [Fact]
    public void TheGridsAreForTheJsonOnly()
    {
        (int text, _, string textErrors) = Run("--measure-grids", Brickwall);
        (int csv, _, _) = Run("--measure-grids", "--csv", Brickwall);

        Assert.Equal(CommandLine.UsageError, text);
        Assert.Contains("--measure-grids is for the JSON", textErrors);
        Assert.Equal(CommandLine.UsageError, csv);
    }

    [Fact]
    public void MeasureTakesAListOfKindsOrNone()
    {
        (int named, string withKinds, _) = Run("--measure", "spectrum", Brickwall);
        (int inline, string inlineKinds, _) = Run("--measure=spectrum", Brickwall);
        (int unknown, _, string errors) = Run("--measure=timbre", Brickwall);

        Assert.Equal(CommandLine.Success, named);
        Assert.Contains("Spectral Cutoff:", withKinds);
        Assert.Equal(CommandLine.Success, inline);
        Assert.Contains("Spectral Cutoff:", inlineKinds);
        Assert.Equal(CommandLine.UsageError, unknown);
        Assert.Contains("Unknown measurement: timbre. Measurements: spectrum, levels, loudness, true-peak.", errors);
    }

    [Fact]
    public void ExplainDescribesTheMeasuredLinesWithTheirTopics()
    {
        (_, string text, _) = Run("--measure", "--explain", "--hide", "frames,tags", Brickwall);
        (_, string json, _) = Run("--measure", "--json", "--explain", Brickwall);

        Assert.Contains("What the host computed from the stream's decoded audio", text);
        Assert.Contains("[topic:measurements]", text);
        Assert.Contains("[topic:spectral-cutoff]", text);
        JsonElement measurements = JsonDocument.Parse(json).RootElement[0].GetProperty("streams")[0].GetProperty("measurements");
        Assert.Equal("measurements", measurements.GetProperty("topic").GetString());
        Assert.Equal("spectral-cutoff", measurements.GetProperty("spectrum").GetProperty("cutoff").GetProperty("topic").GetString());
    }

    [Fact]
    public void TheCsvFillsIsVerifiedAndTheCutoffOnlyWithMeasure()
    {
        (_, string measured, _) = Run("--measure", "--csv", Brickwall);
        (_, string plain, _) = Run("--csv", Brickwall);

        string[] header = measured.Split("\r\n")[0].Split(',');
        string[] row = measured.Split("\r\n")[1].Split(',');
        int cutoff = Array.IndexOf(header, "Spectral Cutoff (Hz)");
        Assert.True(cutoff > 0);
        Assert.Matches(@"^16,?0\d\d$".Replace(",?", string.Empty), row[cutoff]);
        Assert.Equal("true", row[Array.IndexOf(header, "Is Verified")]);
        string[] plainRow = plain.Split("\r\n")[1].Split(',');
        Assert.Equal(string.Empty, plainRow[cutoff]);
        Assert.Equal(string.Empty, plainRow[Array.IndexOf(header, "Is Verified")]);
    }

    // A measured file whose peak is negative reads back as a number (-8.7, not '-8.7), and one whose name starts with = keeps the guard.
    [Fact]
    public void TheCsvWritesANegativePeakAsANumberAndGuardsAFormulaInAName()
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, "measurement", "lossy-lame-b64.flac");
        (_, string measured, _) = Run("--measure", "--csv", path);
        string[] header = measured.Split("\r\n")[0].Split(',');
        string[] row = measured.Split("\r\n")[1].Split(',');
        int peak = Array.IndexOf(header, "Sample Peak (dBFS)");
        Assert.Equal(new[] { "Sample Peak (dBFS)", "Maximum True Peak Level (dBTP)", "Integrated Loudness (LUFS)", "Loudness Range (LU)" }, header[peak..]);
        Assert.Equal("-8.7", row[peak]);
        Assert.False(double.IsNaN(double.Parse(row[peak], System.Globalization.CultureInfo.InvariantCulture)));

        using FileStream stream = File.OpenRead(path);
        Report report = Analyzer.CreateDefault().Analyze("=HYPERLINK(1).flac", stream, new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } });
        var csv = new StringWriter();
        ReportCsvWriter.Write([report], csv, isVerifying: true);
        string[] lines = csv.ToString().Split("\r\n");
        Assert.StartsWith("'=HYPERLINK(1).flac,", lines[1]);
        Assert.Contains(",-8.7,", lines[1]);

        // Silence has no peak: the cell is empty, and without --measure so are both measured cells.
        (_, string silent, _) = Run("--measure", "--csv", Path.Combine(Fixtures.FixtureDirectory, "measurement", "synthetic-levels-silent.flac"));
        Assert.Equal(string.Empty, silent.Split("\r\n")[1].Split(',')[peak]);
        (_, string plain, _) = Run("--csv", path);
        Assert.Equal(string.Empty, plain.Split("\r\n")[1].Split(',')[peak]);
    }


    private static string Fixture(string name) => Path.Combine(Fixtures.FixtureDirectory, "measurement", name);

    [Fact]
    public void LoudnessAndTruePeakHaveTheirOwnKindsAndLines()
    {
        (int code, string output, _) = Run("--measure", "loudness,true-peak", "--hide", "frames,tags,statistics", Fixture("synthetic-loudness-sine-mono-48000.flac"));

        Assert.Equal(CommandLine.Success, code);
        Assert.DoesNotContain("Spectral Cutoff", output);
        Assert.DoesNotContain("Sample Peak", output);
        Assert.Matches(@"Integrated Loudness: -23\.0 LUFS", output);
        Assert.Matches(@"Loudness Range: 0\.0 LU", output);
        Assert.Matches(@"Maximum True Peak Level: -20\.0 dBTP", output);
        Assert.Matches(@"Maximum Momentary Loudness: -23\.0 LUFS", output);
        Assert.Matches(@"Maximum Short-Term Loudness: -23\.0 LUFS", output);

        // The bext chunk's order: integrated loudness, loudness range, true peak, then the maxima.
        Assert.True(output.IndexOf("Loudness Range", StringComparison.Ordinal) < output.IndexOf("Maximum True Peak Level", StringComparison.Ordinal));
        Assert.True(output.IndexOf("Maximum True Peak Level", StringComparison.Ordinal) < output.IndexOf("Maximum Momentary Loudness", StringComparison.Ordinal));
    }

    [Fact]
    public void AStreamTooShortSaysWhyAndATruePeakAboveFullScaleCarriesASign()
    {
        (_, string output, _) = Run("--measure", "--hide", "frames,tags,statistics", Fixture("synthetic-sine-bin-center.flac"));
        Assert.Contains("Loudness Range: none (the stream is shorter than 3 s)", output);
        Assert.Contains("Maximum Short-Term Loudness: none (the stream is shorter than 3 s)", output);
        Assert.Equal("+0.4", MeasurementText.SignedTenths(4));
        Assert.Equal("0.0", MeasurementText.SignedTenths(0));
        Assert.Equal("-0.4", MeasurementText.SignedTenths(-4));
        Assert.Equal("-inf", MeasurementText.SignedTenths(MeasurementLimits.NoLevel));
    }

    [Fact]
    public void ARateBelow8kHzAndUnstatedPositionsAreSaidInPlaceOfTheLoudness()
    {
        (_, string low, _) = Run("--measure", "--hide", "frames,tags,statistics", Fixture("synthetic-white-2000.flac"));
        Assert.Contains("Loudness: not measured (loudness needs a sample rate of 8,000 Hz or more)", low);
        Assert.Contains("Maximum True Peak Level:", low);
        (_, string many, _) = Run("--measure", "--hide", "frames,tags,statistics", Fixture("synthetic-pcm-channels-33.wav"));
        Assert.Contains("Loudness: not measured (the channel positions are not stated)", many);
    }

    [Fact]
    public void TheJsonCarriesTheLoudnessWithItsSeriesAndTheTruePeak()
    {
        (_, string json, _) = Run("--measure", "--json", "--explain", Fixture("synthetic-loudness-steps-8000.flac"));
        JsonElement m = JsonDocument.Parse(json).RootElement[0].GetProperty("streams")[0].GetProperty("measurements");
        JsonElement loudness = m.GetProperty("loudness");

        Assert.Equal(1.0m, loudness.GetProperty("channelWeights")[0].GetDecimal());
        Assert.Equal("LUFS", loudness.GetProperty("integratedLoudness").GetProperty("unit").GetString());
        Assert.Equal("loudness", loudness.GetProperty("integratedLoudness").GetProperty("topic").GetString());
        Assert.Equal("loudness-range", loudness.GetProperty("loudnessRange").GetProperty("topic").GetString());
        Assert.InRange(loudness.GetProperty("loudnessRange").GetProperty("value").GetDecimal(), 9.0m, 11.0m);
        Assert.Equal("LU", loudness.GetProperty("loudnessRange").GetProperty("unit").GetString());
        JsonElement series = loudness.GetProperty("shortTerm");
        Assert.Equal(1, series.GetProperty("stepsPerPoint").GetInt32());
        Assert.Equal(series.GetProperty("x").GetArrayLength(), series.GetProperty("values").GetArrayLength());
        Assert.Equal(3.0m, series.GetProperty("x")[0].GetDecimal());
        Assert.False(string.IsNullOrEmpty(series.GetProperty("description").GetString()));
        Assert.True(loudness.GetProperty("gatingBlocks").GetProperty("aboveAbsoluteGate").GetInt64() > 0);
        JsonElement truePeak = m.GetProperty("truePeak");
        Assert.Equal(4, truePeak.GetProperty("oversampling").GetInt32());
        Assert.Equal("dBTP", truePeak.GetProperty("maximumTruePeakLevel").GetProperty("unit").GetString());
        Assert.Equal("sample-peak-and-true-peak", truePeak.GetProperty("topic").GetString());

        (_, string brief, _) = Run("--measure", "--json", Fixture("synthetic-sine-bin-center.flac"));
        JsonElement shortLoudness = JsonDocument.Parse(brief).RootElement[0].GetProperty("streams")[0].GetProperty("measurements").GetProperty("loudness");
        Assert.Equal("tooShort", shortLoudness.GetProperty("loudnessRange").GetProperty("absence").GetString());
        Assert.False(shortLoudness.TryGetProperty("shortTerm", out _));
        (_, string low, _) = Run("--measure", "--json", Fixture("synthetic-white-2000.flac"));
        Assert.Equal("loudness needs a sample rate of 8,000 Hz or more", JsonDocument.Parse(low).RootElement[0].GetProperty("streams")[0].GetProperty("measurements").GetProperty("loudness").GetProperty("notMeasured").GetString());
    }

    [Fact]
    public void TheCsvWritesTheLoudnessColumnsAsNumbersAndLeavesThemEmptyWithoutMeasure()
    {
        string path = Fixture("synthetic-loudness-sine-mono-48000.flac");
        (_, string measured, _) = Run("--measure", "--csv", path);
        string[] header = measured.Split("\r\n")[0].Split(',');
        string[] row = measured.Split("\r\n")[1].Split(',');
        Assert.Equal("-23.0", row[Array.IndexOf(header, "Integrated Loudness (LUFS)")]);
        Assert.Equal("-20.0", row[Array.IndexOf(header, "Maximum True Peak Level (dBTP)")]);
        Assert.Equal("0.0", row[Array.IndexOf(header, "Loudness Range (LU)")]);

        (_, string levels, _) = Run("--measure=levels", "--csv", path);
        Assert.Equal(string.Empty, levels.Split("\r\n")[1].Split(',')[Array.IndexOf(header, "Integrated Loudness (LUFS)")]);
        (_, string silent, _) = Run("--measure", "--csv", Fixture("synthetic-levels-silent.flac"));
        string[] silentRow = silent.Split("\r\n")[1].Split(',');
        Assert.Equal(string.Empty, silentRow[Array.IndexOf(header, "Integrated Loudness (LUFS)")]);
        Assert.Equal(string.Empty, silentRow[Array.IndexOf(header, "Maximum True Peak Level (dBTP)")]);
    }

    [Fact]
    public void ExplainNamesTheTopicsOfTheLoudnessLines()
    {
        (_, string text, _) = Run("--measure", "--explain", "--hide", "frames,tags,statistics", Fixture("synthetic-loudness-sine-mono-48000.flac"));
        foreach (string topic in new[] { "loudness", "loudness-range", "sample-peak-and-true-peak" })
        {
            Assert.Contains($"[topic:{topic}]", text);
            Assert.False(string.IsNullOrEmpty(TopicLibrary.Find(topic)?.Title), topic);
        }
    }

    [Fact]
    public void ChannelsOfAFivePointOneAreWeightedAndTheLfeIsLeftOut()
    {
        (_, string json, _) = Run("--measure", "--json", Fixture("synthetic-loudness-5-1-surround-pair-8000.flac"));
        JsonElement weights = JsonDocument.Parse(json).RootElement[0].GetProperty("streams")[0].GetProperty("measurements").GetProperty("loudness").GetProperty("channelWeights");
        Assert.Equal([1.0m, 1.0m, 1.0m, 0m, 1.41m, 1.41m], weights.EnumerateArray().Select(w => w.GetDecimal()));
    }

    [Fact]
    public void TheLevelsLinesAndJsonCarryTheirDescriptionsAndTopics()
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, "measurement", "synthetic-levels-dc-offset.flac");
        (_, string text, _) = Run("--measure=levels", "--explain", "--hide", "frames,tags,statistics", path);
        (_, string json, _) = Run("--measure=levels", "--json", "--explain", path);

        foreach (string topic in new[] { "levels", "dc-offset", "clipping", "digital-silence", "bit-depth-usage" })
        {
            Assert.Contains($"[topic:{topic}]", text);
        }

        Assert.Contains("Sample Peak: -28.7 dBFS (front left -28.7, front right -38.3)", text);
        Assert.Contains("DC Offset: 3.051758% of full scale (-30.3 dBFS; front left 3.051758%, front right -0.915527%)", text);
        JsonElement levels = JsonDocument.Parse(json).RootElement[0].GetProperty("streams")[0].GetProperty("measurements").GetProperty("levels");
        Assert.Equal("integer", levels.GetProperty("format").GetString());
        Assert.Equal(3.051758m, levels.GetProperty("dcOffset").GetProperty("value").GetDecimal());
        Assert.Equal("%", levels.GetProperty("dcOffset").GetProperty("unit").GetString());
        Assert.Equal(2, levels.GetProperty("dcOffset").GetProperty("perChannel").GetArrayLength());
        Assert.Equal("dc-offset", levels.GetProperty("dcOffset").GetProperty("topic").GetString());
        Assert.Equal("dc-offset", levels.GetProperty("dcOffsetLevel").GetProperty("topic").GetString());
        Assert.False(string.IsNullOrEmpty(levels.GetProperty("dcOffsetLevel").GetProperty("description").GetString()));
        Assert.Equal("digital-silence", levels.GetProperty("zeroSamples").GetProperty("topic").GetString());
        Assert.False(JsonDocument.Parse(json).RootElement[0].GetProperty("streams")[0].GetProperty("measurements").TryGetProperty("spectrum", out _));
    }

    [Fact]
    public void ASilentChannelsLevelsAreNullInTheJsonAndMinusInfinityInTheText()
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, "measurement", "synthetic-levels-silent.flac");
        (_, string text, _) = Run("--measure=levels", "--hide", "frames,tags,statistics", path);
        (_, string json, _) = Run("--measure=levels", "--json", path);

        Assert.Contains("Sample Peak: -inf dBFS", text);
        Assert.Contains("Low Bits Unused: not defined (every sample is 0)", text);
        JsonElement levels = JsonDocument.Parse(json).RootElement[0].GetProperty("streams")[0].GetProperty("measurements").GetProperty("levels");
        Assert.Equal(JsonValueKind.Null, levels.GetProperty("samplePeak").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, levels.GetProperty("bitDepth").GetProperty("lowBitsUnused").ValueKind);
        Assert.Equal(8192, levels.GetProperty("zeroSamples").GetProperty("value").GetInt64());
    }

    [Theory]
    [InlineData("1-2", false)]
    [InlineData("1.2.3", false)]
    [InlineData("-", false)]
    [InlineData("-.5", false)]
    [InlineData("5.", false)]
    [InlineData("--1", false)]
    [InlineData("1e5", false)]
    [InlineData("-0.3", true)]
    [InlineData("17054", true)]
    [InlineData("0.0", true)]
    public void OnlyAnOptionalMinusDigitsAndAFractionReadAsANumber(string text, bool isNumber)
    {
        Assert.Equal(isNumber, ReportCsvWriter.IsNumber(text));
    }

    [Fact]
    public void ANumericFieldSkipsTheFormulaGuardAndATextFieldDoesNot()
    {
        // A negative loudness or peak is a number: -0.3, not '-0.3 (steps 2 and 5 write such columns through the same path).
        Assert.Equal("-0.3", ReportCsvWriter.Cell("-0.3", isNumeric: true));
        Assert.Equal("17054", ReportCsvWriter.Cell("17054", isNumeric: true));
        Assert.Equal("'-0.3", ReportCsvWriter.Cell("-0.3", isNumeric: false));

        // A text field that starts a formula keeps the guard, also when flagged as a number.
        Assert.Equal("'=1+1", ReportCsvWriter.Cell("=1+1", isNumeric: false));
        Assert.Equal("'=1+1", ReportCsvWriter.Cell("=1+1", isNumeric: true));
        Assert.Equal("'-1+1", ReportCsvWriter.Cell("-1+1", isNumeric: true));
        Assert.Equal("'+5", ReportCsvWriter.Cell("+5", isNumeric: true));
        Assert.Equal("'@x", ReportCsvWriter.Cell("@x", isNumeric: true));
    }

    [Fact]
    public void AnAnalysisThatMeasuredNothingWritesNoMeasurementsAnywhere()
    {
        (_, string json, _) = Run("--json", "--verify", Brickwall);

        Assert.DoesNotContain("measurements", json);
    }

    [Fact]
    public void TheTextOfAFileWithoutAnyMeasurementIsUnchanged()
    {
        (_, string before, _) = Run(Brickwall);

        Assert.DoesNotContain("Measured", before);
    }

    // Every figure the writers print carries a description and a topic, as nodes do (DescriptionTests): one or two sentences, no verdict,
    // and an article that exists.
    [Theory]
    [MemberData(nameof(MeasurementFixtureTests.Names), MemberType = typeof(MeasurementFixtureTests))]
    public void EveryMeasuredLineHasADescriptionAndATopicThatExists(string name)
    {
        StreamMeasurements m = MeasurementFixtureTests.Analyze(name, new MeasureOptions()).Streams.Single().Measurements!;

        foreach (MeasurementLine line in MeasurementText.Lines(m, isLossy: false))
        {
            Assert.False(string.IsNullOrWhiteSpace(line.Description), $"{name}: {line.Name} has no description");
            Assert.EndsWith(".", line.Description);
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(line.Description, @"\b(good|bad|excellent|poor|transparent|fake|transcoded|lossy source)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase), line.Description);
            Assert.NotNull(line.Topic);
            Assert.NotNull(TopicLibrary.Find(line.Topic));
        }
    }

    // The Zero Samples line names the count of each channel only where the channels differ (docs/cli.md).
    [Theory]
    [MemberData(nameof(MeasurementFixtureTests.Names), MemberType = typeof(MeasurementFixtureTests))]
    public void ZeroSamplesNamesTheChannelsOnlyWhereTheirCountsDiffer(string name)
    {
        StreamMeasurements m = MeasurementFixtureTests.Analyze(name, new MeasureOptions { Include = MeasurementKinds.Levels }).Streams.Single().Measurements!;
        if (m.Levels is not { Channels.Count: > 1 } levels)
        {
            return;
        }

        string text = MeasurementText.Lines(m, isLossy: false).Single(l => l.Figure == MeasurementFigure.ZeroSamples).Text;
        Assert.Equal(levels.Channels.Any(c => c.ZeroSamples != levels.Channels[0].ZeroSamples), text.Contains('('));
    }

    [Fact]
    public void TheTextNeverCallsACutoffAVerdict()
    {
        foreach (string name in new[] { "lossy-lame-b64", "lossy-lame-v0", "synthetic-white-44100", "synthetic-white-2000" })
        {
            StreamMeasurements m = MeasurementFixtureTests.Analyze(name, new MeasureOptions()).Streams.Single().Measurements!;
            string text = string.Join('\n', MeasurementText.Lines(m, isLossy: false).Select(l => l.Text));

            Assert.DoesNotMatch(@"(?i)transcod|fake|upsampl|lossy source|genuine|authentic", text);
        }
    }

    [Fact]
    public void TheWritersPlaceDecimalsWithoutFormattingAFloat()
    {
        Assert.Equal("60.4", MeasurementText.Tenths(604));
        Assert.Equal("0.3", MeasurementText.Tenths(3));
        Assert.Equal("-0.3", MeasurementText.Tenths(-3));
        Assert.Equal("0.0", MeasurementText.Tenths(0));
        Assert.Equal("-inf", MeasurementText.Tenths(int.MinValue));
        Assert.Equal("1,234.5", MeasurementText.Tenths(12345));
        Assert.Equal("17,054 Hz", MeasurementText.Hertz(17054));
        Assert.Equal("300.0", MeasurementText.Seconds(13230000, 44100));
        Assert.Equal("0.0", MeasurementText.Seconds(100, 44100));
    }
}
