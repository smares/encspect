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
    // Fixtures whose cut units Encspect's decoder decodes in full (question 4 of the design), with the reason; the per-format commits add theirs.
    private static readonly Dictionary<string, string> InvariantExceptions = new()
    {
        #region Monkey's Audio
        ["monkeys-audio/built-frame-data-bytes-too-large.decode.json"] = "frame 2 is flagged cut from the descriptor's size and decodes in full",
        ["monkeys-audio/built-seek-first-entry-off-by-two.decode.json"] = "frame 2 is flagged cut from the seek table and decodes in full",
        ["monkeys-audio/built-frame-data-bytes-too-small.decode.json"] = "frame 2 ends in a partial word past the descriptor's size, is flagged cut and decodes in full",
        #endregion
    };

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
            if (decoded > coded - lost && !InvariantExceptions.ContainsKey(Path.GetRelativePath(Fixtures.FixtureDirectory, path).Replace('\\', '/')))
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

        // Rule 3: bytes skipped while searching for sync declare nothing.
        { "adts/damaged-header.aac", 29696, 0 },

        // Trimming: Opus samples are the trimmed stage; the coded stage lies above them, and the packets that break RFC 6716 are lost there.
        { "ogg/opus-packets-corrupt.opus", 24648, 14400 },

        #region Ogg, Speex
        // Question 2 (b): the packets of a page that fails its CRC are handed over and counted, not lost; those of a truncated page with bytes
        // present are counted and the one the data cuts is lost; a packet left unfinished at the end of the stream is cut.
        { "ogg/bad-crc.spx", 4760, 0 },
        { "ogg/truncated-page.spx", 6897, 320 },
        { "ogg/huge-lacing.spx", 920, 160 },
        { "ogg/eos-inside-packet.spx", 1720, 160 },

        // Question 11 and 5 (b): an empty, invalid or overrunning packet counts its frames per packet and is lost; one with fewer frames counts those present.
        { "ogg/frames-corrupt.spx", 3000, 960 },
        #endregion

        #region Matroska
        // A block that states more than the file holds hands its last frame over cut: the Opus parser counts it and records it lost.
        { "matroska/live-truncated-unknown-element.mka", 5448, 960 },
        #endregion

        #region FLAC
        // A cut frame counts its block size and is lost; an unreadable frame stays in the samples and is lost.
        { "flac/built-cut-mid-frame.flac", 16384, 4096 },
        { "flac/built-damaged-frame-after-frame.flac", 28096, 4096 },
        { "flac/built-subframe-faults.flac", 4352, 2048 },
        #endregion

        #region TTA
        // A cut frame counts its frame length and is lost; frames with no byte in the file count nothing.
        { "tta/built-cut-mid-frame.tta", 92160, 46080 },
        { "tta/built-seek-entry-too-large.tta", 100000, 7840 },
        #endregion

        #region WavPack
        // A set with a cut block is lost once, by its first block's samples; the samples stay.
        { "wavpack/built-truncated-last-block.wv", 24000, 6488 },
        { "wavpack/built-block-size-too-small.wv", 24000, 11025 },
        #endregion

        #region Monkey's Audio
        // Frames whose first byte is in the file count; the cut one is lost, and so is a frame too short for its start fields.
        { "monkeys-audio/built-cut-mid-frame.ape", 147456, 73728 },
        { "monkeys-audio/built-frame-too-short-for-crc.ape", 160000, 12544 },
        { "monkeys-audio/built-cut-in-seek-table.ape", 0, 0 },
        #endregion
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

    [Fact]
    public void TheTopicOfLostSamplesIsServed()
    {
        Assert.NotNull(TopicLibrary.Find(StreamInfo.LostSamplesTopic));
        Assert.Contains("units", StreamInfo.LostSamplesDescription, StringComparison.Ordinal);
    }
}
