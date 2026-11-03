using System.Globalization;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;
using Encspect.Tests.Measurement;

namespace Encspect.Tests;

// What a stream's samples and lost samples mean (docs/design/samples-definition.md): the figure's output, the checks every golden must
// pass, the invariant between lost samples and what the decoder decodes, and one committed fixture per case of the rule, pinned by value.
// The per-format commits add their fixtures to RuleCases as they change the golden.
public class SamplesDefinitionTests
{
    private static readonly string[] CodedFigures = ["Coded Samples", "Decoded Samples"];

    private sealed record Scan(IReadOnlyList<(long Samples, long Lost, bool IsProtected)> Streams, long Coded, bool IsNotAnalyzed);

    // The figures of a golden read as a stream of tokens, so that the 700 MB of goldens are not parsed into documents: each stream's
    // samples, lostSamples (absent reads 0) and whether it is protected, the largest coded-stage figure of the tree ("Coded Samples" or
    // "Decoded Samples", an upper bound where a file has several streams), and whether a finding says a codec is not analyzed.
    private static Scan ReadGolden(string path)
    {
        var reader = new Utf8JsonReader(File.ReadAllBytes(path), new JsonReaderOptions { MaxDepth = 4 * Node.MaxDepth });
        var streams = new List<(long, long, bool)>();
        long samples = 0;
        long lost = 0;
        bool isProtected = false;
        long coded = 0;
        bool isNotAnalyzed = false;
        string top = string.Empty;
        bool isCodedName = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                string name = reader.GetString()!;
                int depth = reader.CurrentDepth;
                if (depth == 2 && reader.TokenType == JsonTokenType.PropertyName)
                {
                    top = name;
                }

                if (top == "streams" && depth == 4)
                {
                    reader.Read();
                    switch (name)
                    {
                        case "samples" when reader.TokenType == JsonTokenType.Number:
                            samples = reader.GetInt64();
                            break;
                        case "lostSamples" when reader.TokenType == JsonTokenType.Number:
                            lost = reader.GetInt64();
                            break;
                        case "isProtected":
                            isProtected = reader.TokenType == JsonTokenType.True;
                            break;
                        default:
                            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                            {
                                reader.Skip();
                            }

                            break;
                    }
                }
                else if (top == "root" && name == "name")
                {
                    reader.Read();
                    isCodedName = reader.TokenType == JsonTokenType.String && CodedFigures.Contains(reader.GetString());
                }
                else if (top == "root" && name == "value" && isCodedName)
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Number)
                    {
                        coded = Math.Max(coded, reader.GetInt64());
                    }

                    isCodedName = false;
                }
                else if (top == "findings" && name == "code")
                {
                    reader.Read();
                    isNotAnalyzed |= reader.TokenType == JsonTokenType.String && reader.GetString()!.EndsWith(".codec-not-analyzed", StringComparison.Ordinal);
                }
            }
            else if (reader.TokenType == JsonTokenType.StartObject && top == "streams" && reader.CurrentDepth == 3)
            {
                (samples, lost, isProtected) = (0, 0, false);
            }
            else if (reader.TokenType == JsonTokenType.EndObject && top == "streams" && reader.CurrentDepth == 3)
            {
                streams.Add((samples, lost, isProtected));
            }
        }

        return new Scan(streams, coded, isNotAnalyzed);
    }

    public static IEnumerable<string> Goldens(string suffix) =>
        Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*" + suffix, SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    [Fact]
    public void EveryGoldensLostSamplesLieBetweenZeroAndTheCodedStageAndAreZeroWhereNothingIsRead()
    {
        var failures = new List<string>();
        int files = 0;
        foreach (string path in Goldens(".report.json"))
        {
            files++;
            Scan scan = ReadGolden(path);
            foreach ((long samples, long lost, bool isProtected) in scan.Streams)
            {
                long coded = Math.Max(samples, scan.Coded);
                if (lost < 0 || lost > coded)
                {
                    failures.Add($"{Path.GetRelativePath(Fixtures.FixtureDirectory, path)}: lostSamples {lost} outside 0 to {coded}");
                }
                else if (lost > 0 && (isProtected || scan.IsNotAnalyzed))
                {
                    failures.Add($"{Path.GetRelativePath(Fixtures.FixtureDirectory, path)}: lostSamples {lost} on a stream no parser reads");
                }
            }
        }

        Assert.True(files > 2000, "The golden reports were not found.");
        Assert.Empty(failures);
    }

    // The invariant of the design, "Invariant and test", read from the goldens without a decode pass: what a decoder decoded cannot be more
    // than the coded stage less the lost samples. Equality holds where the decoding is Decoded, the stream is not DSD, nothing is decoded
    // that the parser did not flag and no unit repeats; the exceptions are named by the per-format commits as they change the goldens.
    [Fact]
    public void DecodedSamplesNeverExceedTheCodedStageLessTheLostSamples()
    {
        var failures = new List<string>();
        int compared = 0;
        foreach (string path in Goldens(".decode.json"))
        {
            string reportPath = path[..^".decode.json".Length] + ".report.json";
            if (!File.Exists(reportPath))
            {
                continue;
            }

            using JsonDocument decode = JsonDocument.Parse(File.ReadAllText(path));
            if (!decode.RootElement.TryGetProperty("verification", out JsonElement verification))
            {
                continue;
            }

            Scan scan = ReadGolden(reportPath);
            if (scan.Streams.Count != 1)
            {
                continue;
            }

            compared++;
            long decoded = verification.GetProperty("decodedSamples").GetInt64();
            (long samples, long lost, _) = scan.Streams[0];
            long coded = Math.Max(samples, scan.Coded);
            if (decoded > coded - lost)
            {
                failures.Add($"{Path.GetRelativePath(Fixtures.FixtureDirectory, path)}: decoded {decoded}, coded stage {coded}, lost {lost}");
            }
        }

        Assert.True(compared > 700, $"Only {compared} decode goldens were compared.");
        Assert.Empty(failures);
    }

    [Fact]
    public void LostSamplesAreWrittenAboveZeroOnlyAndTheSummaryAndCsvCarryThem()
    {
        StreamInfo Stream(long lost, int? group = null) => new()
        {
            Nodes = [],
            Codec = "MPEG-1 Layer III",
            SampleRate = 44100,
            Channels = 2,
            Samples = 5760,
            LostSamples = lost,
            Duration = 0.13,
            PlaybackGroup = group,
        };

        string Json(params StreamInfo[] streams) => Fixtures.ToJson(new Report("x", new Node("File", 0, 1), [], streams));

        Assert.DoesNotContain("lostSamples", Json(Stream(0)), StringComparison.Ordinal);
        string json = Json(Stream(1152));
        Assert.Matches(@"""samples"": 5760,\s+""lostSamples"": 1152,\s+""duration""", json);

        // The summary adds the lost samples of the streams whose samples it adds: a playback group counts its longest stream once.
        var report = new Report("x", new Node("File", 0, 1), [], [Stream(1152), Stream(100, group: 1), Stream(300, group: 1) with { Duration = 0.5 }]);
        ReportSummary summary = ReportSummary.From(report);
        Assert.Equal(1152 + 300, summary.LostSamples);
        Assert.Equal(5760 * 2, summary.Samples);

        var csv = new StringWriter();
        ReportCsvWriter.Write([report], csv);
        string[] lines = csv.ToString().Split("\r\n");
        string[] header = lines[0].Split(',');
        string[] row = lines[1].Split(',');
        Assert.Equal("Lost Samples", header[^1]);
        Assert.Equal((1152 + 300).ToString(CultureInfo.InvariantCulture), row[^1]);

        var none = new StringWriter();
        ReportCsvWriter.Write([new Report("x", new Node("File", 0, 1), [])], none);
        Assert.EndsWith(",", none.ToString().Split("\r\n")[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AMeasuredStreamWhoseFirstAndLastUnitsAreLostCountsThemInTheLostSamples()
    {
        var format = new PcmFormat(44100, 1, 16, SampleFormat.Integer);
        int[] block = MeasurementSignals.ToIntegers(MeasurementSignals.Noise(4096, 3), 16);

        // Units of 4,096 samples from 8,192 to 28,672: the first and the last were not decoded, one in the middle was skipped.
        StreamMeasurements Run(MeasurementStatus status, long? start, long? end)
        {
            var measurer = new StreamMeasurer(format, new MeasureOptions(), CancellationToken.None, _ => true);
            measurer.Write(new PcmBlock(12288, block.Length, 1, block.Length, block));
            measurer.Write(new PcmBlock(20480, block.Length, 1, block.Length, block));
            if (start is { } from && end is { } to)
            {
                measurer.SetCodedRange(from, to);
            }

            return measurer.Result(status, null);
        }

        StreamMeasurements m = Run(MeasurementStatus.Incomplete, 8192, 28672);
        Assert.Equal((12288L, 2 * 4096L), (m.FirstPosition, m.Samples));
        Assert.Equal((4096L, 4096L, 4096L * 3), (m.LeadingLostSamples, m.TrailingLostSamples, m.LostSamples));
        Assert.Equal(4096, m.InnerLostSamples);
        Assert.Equal(1 + 1 + 1, m.Gaps);
        Assert.Equal(28672, m.FirstPosition + m.Samples + m.LostSamples - m.LeadingLostSamples);

        // The same stream is measured as it was without the span, and when the decoding was stopped the unmeasured end is not lost.
        StreamMeasurements without = Run(MeasurementStatus.Incomplete, null, null);
        Assert.Equal((4096L, 0L, 0L, 1), (without.LostSamples, without.LeadingLostSamples, without.TrailingLostSamples, without.Gaps));
        StreamMeasurements stopped = Run(MeasurementStatus.Stopped, 8192, 28672);
        Assert.Equal((4096L, 0L, 0L), (stopped.LostSamples, stopped.LeadingLostSamples, stopped.TrailingLostSamples));

        // A span that the blocks cover leaves the figures alone.
        StreamMeasurements covered = Run(MeasurementStatus.Complete, 12288, 24576);
        Assert.Equal((4096L, 0L, 0L), (covered.LostSamples, covered.LeadingLostSamples, covered.TrailingLostSamples));
    }

    // One committed fixture per case of the rule, pinned by value: (fixture, streams' samples, streams' lost samples). The core commit
    // pins the cases the definition leaves as they are; the per-format commits add the rest with their goldens.
    public static TheoryData<string, long, long> RuleCases => new()
    {
        // Rule 2: a cut ADTS frame counts its declared samples (the lost samples come with the ADTS commit).
        { "adts/truncated.aac", 12288, 0 },

        #region AC-3 and E-AC-3
        // A cut frame and a frame whose blocks break are lost whole; a failing CRC and an unread coding tool are not.
        { "ac3/cut-frame.ac3", 9216, 1536 },
        { "ac3/truncated.ac3", 9216, 1536 },
        { "ac3/truncated-in-skip.eac3", 6144, 1536 },
        { "ac3/bad-blocks.ac3", 9216, 4608 },
        { "ac3/crc-damaged.ac3", 12288, 0 },
        { "ac3/aht.eac3", 4608, 0 },
        { "wav/built-iec61937-cut-in-payload.wav", 7680, 1536 },
        { "wav/built-iec61937-cut-odd-payload.wav", 7680, 1536 },
        #endregion

        #region WAV, Wave64 and AIFF
        // Whole sample frames of the bytes present: a partial frame counts nothing and nothing is lost.
        { "wav/built-partial-sample-frame.wav", 49, 0 },
        { "wav/built-w64-partial-sample-frame.w64", 49, 0 },
        { "wav/built-truncated-data.wav", 40, 0 },
        { "aiff/built-truncated.aiff", 40, 0 },
        { "aiff/built-sound-data-short.aiff", 50, 0 },
        #endregion

        #region TrueHD and MLP
        // A cut unit, units that break the syntax and the units that wait for the next major sync are lost at the samples of the sync in force.
        { "truehd/built-unit-cut.thd", 3200, 40 },
        { "truehd/built-sync-crc-mismatch.thd", 3200, 640 },
        { "truehd/built-mlp-sync-crc-mismatch.mlp", 2560, 640 },
        { "truehd/built-parity-mismatch.thd", 3200, 480 },
        { "truehd/built-directory-end-past-unit.thd", 3200, 320 },
        { "truehd/built-block-size-invalid.thd", 1920, 640 },
        // Question 12 (b): units that use what the analyzer leaves aside and skipped bytes count nothing; a failing checksum alone is not lost.
        { "truehd/built-mlp-group-rates.mlp", 1280, 0 },
        { "truehd/built-data-skipped.thd", 2760, 0 },
        { "truehd/built-substream-crc-mismatch.thd", 3200, 0 },
        // An end of stream marker's drop is a trim, not a loss.
        { "truehd/external-truehd-stereo-48k-24-odd-length.thd", 7213, 0 },
        #endregion

        #region ASF and WMA
        // Standard WMA, off Microsoft's rule: an invalid frame and the frames its superframe declares after it count a frame length and are lost.
        { "asf/writer-stamp-last.asf", 4096, 4096 },
        { "asf/writer-stamp-not-last.asf", 4096, 4096 },
        { "asf/fragments-in-two-streams.asf", 4096, 4096 },
        // Voice: a cut, broken or dropped superframe counts its stated samples, or 480, and is lost.
        // The container hands a cut media object over flagged cut, and the codec parser counts what the bytes present declare: the cut Voice
        // object finishes the superframe before it, the frames a cut standard object declares by its first byte are lost, and so is the Lossless
        // frame that starts at a declared start and runs past the cut.
        { "asf/voice-truncated.asf", 11060, 0 },
        { "asf/derived-windows-wma92-stereo-44k-128-object-cut.asf", 135168, 6144 },
        { "asf/derived-windows-wma92lossless-stereo-44k-16-object-cut.asf", 88064, 2048 },
        // Damage: an invalid standard frame and the frames its superframe declares after it; the Lossless frames that start where a packet's header
        // points or after a valid frame and cannot be used after a lost packet.
        { "asf/derived-windows-wma92-stereo-44k-128-damaged.asf", 133120, 14336 },
        { "asf/derived-windows-wma92lossless-stereo-44k-16-damaged.asf", 51336, 6144 },
        { "asf/voice-invalid-superframes.asf", 11520, 1440 },
        { "asf/voice-object-not-whole.asf", 8160, 480 },
        { "asf/voice-spillover.asf", 8160, 960 },
        // Voice objects that declare no unit the file holds (an orphan spillover, a short packet) count nothing and lose nothing.
        { "asf/voice-object-short.asf", 8160, 0 },
        #endregion

        #region DTS and DTS-HD
        { "dts/cut-frame.dts", 3072, 512 },
        { "dts/truncated.dts", 3072, 512 },
        { "dts/fsize-too-large.dts", 3072, 512 },
        { "dts/exss-overrun.dts", 2048, 512 },
        { "dts/dtshd-stream-truncated.dtshd", 2048, 512 },
        #endregion

        // Rule 3: bytes skipped while searching for sync declare nothing.
        { "adts/damaged-header.aac", 29696, 0 },

        // Trimming: Opus samples are the trimmed stage; the coded stage lies above them.
        { "ogg/opus-packets-corrupt.opus", 24648, 0 },
    };

    [Theory]
    [MemberData(nameof(RuleCases))]
    public void ARuleCaseHoldsItsFigures(string fixture, long samples, long lost)
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, fixture);
        Report report = Analyzer.CreateDefault().AnalyzeFile(path);
        StreamInfo stream = report.Streams[0];
        Assert.Equal((samples, lost), (stream.Samples, stream.LostSamples));
    }

    // Coded Samples stands next to a trimmed Samples (TrueHD: the 27 samples an end of stream marker drops).
    [Fact]
    public void TrueHdCodedSamplesStandNextToItsTrimmedSamples()
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, "truehd", "external-truehd-stereo-48k-24-odd-length.thd"));
        Node statistics = Fixtures.Resolve(report.Root, "Dolby TrueHD/Statistics").Single();
        Assert.Equal(7240L, statistics.Children.Single(c => c.Name == "Coded Samples").Value?.AsInteger());
        Assert.Equal(7213L, report.Streams[0].Samples);
    }

    [Fact]
    public void TheTopicOfLostSamplesIsServed()
    {
        Assert.NotNull(TopicLibrary.Find(StreamInfo.LostSamplesTopic));
        Assert.Contains("units", StreamInfo.LostSamplesDescription, StringComparison.Ordinal);
    }
}
