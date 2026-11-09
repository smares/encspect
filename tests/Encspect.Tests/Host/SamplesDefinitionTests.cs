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
    private static readonly string[] CodedFigures = ["Coded Samples"];

    private sealed record Scan(IReadOnlyList<(long Samples, long Lost, bool IsProtected)> Streams, long Coded, bool IsNotAnalyzed);

    // The figures of a golden read as a stream of tokens, so that the 700 MB of goldens are not parsed into documents: each stream's
    // samples, lostSamples (absent reads 0) and whether it is protected, the largest coded-stage figure of the tree ("Coded Samples"
    // an upper bound where a file has several streams), and whether a finding says a codec is not analyzed.
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
                    isNotAnalyzed |= reader.TokenType == JsonTokenType.String && reader.GetString() is { } code && (code.EndsWith(".codec-not-analyzed", StringComparison.Ordinal) || code == "alac.tuning-invalid"); // file-wide: no fixture has a cookie refused for every frame and a sample the container cut
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

    // Where the decoding is Decoded the decoded samples equal the coded stage less the lost samples, except for the files listed here, each group with its reason.
    // The per-format owners keep their group: a listed file that comes to equal fails the test, so the list never keeps a stale entry.
    private static readonly Dictionary<string, string> EqualityExceptions = new()
    {
        #region FLAC
        ["flac/built-frame-differs-from-streaminfo.decode.json"] = Reasons.flac,
        #endregion

        #region Monkey's Audio
        ["monkeys-audio/built-audio-byte-flipped.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-final-frame-blocks-too-large.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-frame-data-bytes-too-large.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-frame-data-bytes-too-small.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-header-data-too-large.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-old-3800-residual-bit-flipped.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-old-3800-start-bit-outside-word.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-old-3910-residual-bit-flipped.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-old-version-3800.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-old-version-3820-crc.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-old-version-3850.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-old-version-3900.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-residual-bit-flipped.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-seek-entries-decreasing.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-seek-entry-beyond-data.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-seek-first-entry-off-by-two.decode.json"] = Reasons.monkeysaudio,
        ["monkeys-audio/built-seek-first-entry-off.decode.json"] = Reasons.monkeysaudio,
        #endregion

        #region TrueHD
        ["truehd/built-parameters-change.decode.json"] = Reasons.truehd,
        ["truehd/built-splice-channel-count.decode.json"] = Reasons.truehd,
        ["truehd/external-truehd-stereo-44k-24-lossywav.decode.json"] = Reasons.truehd,
        ["truehd/external-truehd-stereo-48k-24-odd-length.decode.json"] = Reasons.truehd,
        #endregion

        #region WavPack
        ["wavpack/built-block-size-too-large.decode.json"] = Reasons.wavpack,
        ["wavpack/built-channel-info-after-bad-block.decode.json"] = Reasons.wavpack,
        ["wavpack/built-leading-block-without-set-md5.decode.json"] = Reasons.wavpack,
        ["wavpack/built-set-channels-missing.decode.json"] = Reasons.wavpack,
        ["wavpack/built-set-without-final-limit.decode.json"] = Reasons.wavpack,
        ["wavpack/built-set-without-initial.decode.json"] = Reasons.wavpack,
        #endregion

        #region WavPack DSD
        ["wavpack/built-dsd-md5-changed.decode.json"] = Reasons.wavpackdsd,
        ["wavpack/built-dsd-rate-overflow.decode.json"] = Reasons.wavpackdsd,
        ["wavpack/built-dsd-stored-mode.decode.json"] = Reasons.wavpackdsd,
        ["wavpack/external-dsd-128.decode.json"] = Reasons.wavpackdsd,
        ["wavpack/external-dsd-64-high-md5.decode.json"] = Reasons.wavpackdsd,
        ["wavpack/external-dsd-64-high.decode.json"] = Reasons.wavpackdsd,
        ["wavpack/external-dsd-64-md5.decode.json"] = Reasons.wavpackdsd,
        ["wavpack/external-dsd-64-mono-md5.decode.json"] = Reasons.wavpackdsd,
        ["wavpack/external-dsd-64-mono.decode.json"] = Reasons.wavpackdsd,
        ["wavpack/external-dsd-64.decode.json"] = Reasons.wavpackdsd,
        #endregion

    };

    private static class Reasons
    {
        public const string flac =
            "the decoder refuses every frame because the frame's block size disagrees with STREAMINFO; the parser reads the frames";

        public const string monkeysaudio =
            "the decoder stops at or refuses a frame that the parser reads without flagging it (units a decoder refuses for a reason the parser does not check show only in the measurements)";

        public const string truehd =
            "the decoder refuses units that the parser reads without flagging them (a change of the stream's parameters, an odd-length end trimmed from the samples)";

        public const string wavpackdsd =
            "the stream decodes to DSD, which counts at the DSD rate and decodes to one eighth";

        public const string wavpack =
            "the decoder decodes only the blocks of sets it accepts; the parser reads the blocks it refuses without flagging them";

    }
    [Fact]
    public void DecodedSamplesEqualTheCodedStageLessTheLostSamplesWhereTheDecodingIsDecodedExceptWhereListed()
    {
        var failures = new List<string>();
        int equal = 0;
        foreach (string path in Goldens(".decode.json"))
        {
            string reportPath = path[..^".decode.json".Length] + ".report.json";
            using JsonDocument decode = JsonDocument.Parse(File.ReadAllText(path));
            if (!File.Exists(reportPath) || !decode.RootElement.TryGetProperty("verification", out JsonElement verification) || verification.GetProperty("decoding").GetString() != "Decoded")
            {
                continue;
            }

            Scan scan = ReadGolden(reportPath);
            if (scan.Streams.Count != 1)
            {
                continue;
            }

            string name = Path.GetRelativePath(Fixtures.FixtureDirectory, path).Replace('\\', '/');
            long decoded = verification.GetProperty("decodedSamples").GetInt64();
            (long samples, long lost, _) = scan.Streams[0];
            long wanted = Math.Max(samples, scan.Coded) - lost;
            bool isListed = EqualityExceptions.ContainsKey(name) || InvariantExceptions.ContainsKey(name);
            if (decoded == wanted)
            {
                equal++;
                if (isListed)
                {
                    failures.Add($"{name}: decoded {decoded} equals the coded stage less the lost samples; remove it from the exceptions");
                }
            }
            else if (!isListed)
            {
                failures.Add($"{name}: decoded {decoded}, coded stage less the lost samples {wanted}, and no exception lists it");
            }
        }

        Assert.True(equal > 600, $"Only {equal} decode goldens were equal.");
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
        int lost = Array.IndexOf(header, "Samples") + 1;
        Assert.Equal("Lost Samples", header[lost]);
        Assert.Equal((1152 + 300).ToString(CultureInfo.InvariantCulture), row[lost]);

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
        #region MPEG audio
        // Rule 2: a frame cut by the end of the data or by the next frame counts its declared 1,152 samples and is lost.
        { "mpeg/truncated-last-frame.mp3", 6912, 1152 },
        { "mpeg/vbr/lame-truncated-last-frame.mp3", 9216, 1152 },
        { "mpeg/cut-frame-stream-change.mp3", 6912, 1152 },
        { "mpeg/damaged-bitrate.mp3", 9216, 1152 },
        #endregion

        #region AC-3 and E-AC-3
        // A cut frame and a frame whose blocks break are lost whole; a failing CRC and an unread coding tool are not.
        { "ac3/cut-frame.ac3", 9216, 1536 },
        { "ac3/truncated.ac3", 9216, 1536 },
        { "ac3/truncated-in-skip.eac3", 6144, 1536 },
        { "ac3/bad-blocks.ac3", 9216, 4608 },
        { "ac3/crc-damaged.ac3", 12288, 0 },
        { "ac3/aht.eac3", 4608, 0 },
        // A frame whose audio blocks run past its end (ac3.block-overrun) is lost like an invalid one.
        { "fuzz/ac3-blocks-past-frame-end.bin", 13824, 1536 },
        { "fuzz/ac3-overrun-after-first-exponent.bin", 23040, 3072 },
        { "fuzz/eac3-blocks-past-frame-end.bin", 27648, 4608 },
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
        { "asf/derived-windows-wma92lossless-stereo-44k-16-damaged.asf", 73864, 28672 },
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
        { "dts/dtshd-stream-size-wrong.dtshd", 2048, 512 },
        // A header that runs past its data breaks the syntax: the unit is lost.
        { "dts/exss-core-header-overrun.dts", 2048, 2048 },
        { "dts/xxch-header-overrun.dts", 1024, 1024 },
        #endregion

        #region Musepack
        // A frame cut by the end of the data counts 1,152 in Coded Samples and is lost (SV8: the 4 frames, 4,608, of the cut packet); samples compose as before.
        { "musepack/sv7-truncated.mpc", 46751, 1152 },
        { "musepack/sv8-truncated.mpc", 45599, 4608 },
        // A damaged frame (resolutions past its length or out of range) is lost; the cap of the header's count leaves the samples.
        { "musepack/sv6-band-overrun.mpc", 32256, 1152 },
        { "musepack/sv7-resolution-invalid.mpc", 66150, 1152 },
        // The SV4 and SV5 last frame is coded, trimmed from the samples and lost only where the data cut it.
        { "musepack/sv5-seek-tag.mpc", 31775, 1152 },
        { "musepack/external-mppsv4-vbr.mpc", 31775, 0 },
        #endregion

        #region ADTS, ADIF and AAC
        // Rule 2: a cut frame counts its declared samples and is lost; rule 3: bytes skipped while searching for sync declare nothing; a block that breaks
        // the syntax (question 8) is lost; a declaration the bytes contradict (question 5) and a finding the reading survives are not.
        { "aac/adts/truncated.aac", 12288, 1024 },
        { "aac/adts/sbr-in-truncated-frame.aac", 4096, 2048 },
        { "aac/adts/damaged-header.aac", 29696, 0 },
        { "aac/adts/blocks-missing.aac", 10240, 0 },
        { "aac/adts/corrupt-syntax.aac", 21504, 6144 },
        { "aac/adts/corrupt-soft.aac", 21504, 0 },
        { "aac/adts/adif.aac", 20480, 0 },
        #endregion

        #region MP4
        // Rule 1: a sample whose first byte is in the file counts and the one the file ends inside is lost; a sample with no byte counts nothing (question 3).
        // Blocks that break the syntax are lost (question 8).
        { "mp4/truncated.m4a", 10240, 1024 },
        { "mp4/sample-outside-mdat.m4a", 20480, 10240 },
        { "mp4/chunk-sample-mismatch.m4a", 10240, 4096 },
        #endregion

        #region ALAC
        // An unreadable frame counts its declared samples (the count its first element states, else the cookie's frame length) and is lost.
        { "alac/built-element-invalid-shift.m4a", 10000, 4096 },
        { "alac/built-residual-run-too-long.m4a", 6004, 100 },

        // A cookie the walk refuses for every frame is a configuration refused as a whole: counted, not lost.
        { "alac/built-tuning-kb-zero.m4a", 10000, 0 },
        #endregion

        #region Opus
        // Trimming: Opus samples are the trimmed stage; the coded stage lies above them, and the packets that break RFC 6716 are lost there.
        { "ogg/opus-packets-corrupt.opus", 24648, 14400 },
        #endregion

        #region Ogg, Speex
        // Question 2 (b): the packets of a page that fails its CRC are handed over and counted, not lost; those of a truncated page with bytes
        // present are counted and the one the data cuts is lost; a packet left unfinished at the end of the stream is cut.
        { "ogg/bad-crc.spx", 4760, 0 },
        { "ogg/truncated-page.spx", 6897, 320 },
        { "ogg/huge-lacing.spx", 920, 160 },
        { "ogg/eos-inside-packet.spx", 1720, 160 },

        // Question 11 and 5 (b): an empty, invalid or overrunning packet counts its frames per packet and is lost; one with fewer frames counts those present.
        { "ogg/frames-corrupt.spx", 3000, 960 },

        // An empty packet on a truncated page does not stop the packets after it; the packet the data cuts is lost.
        { "ogg/truncated-page-empty-packet.spx", 1920, 640 },

        // A packet cut by a truncated page: a Vorbis block whose mode reads is lost, and an Opus code 0 packet reads as a valid shorter one, so only
        // the container shows the cut.
        { "ogg/vorbis-cut-last-packet.ogg", 40960, 1024 },
        { "ogg/opus-cut-last-packet.opus", 28488, 960 },
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

    [Fact]
    public void AStreamChangeAfterACutFrameLeavesTheNextStreamWhole()
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, "mpeg", "cut-frame-stream-change.mp3"));
        Assert.Equal([(6912L, 1152L), (11520L, 0L)], report.Streams.Select(s => (s.Samples, s.LostSamples)));
    }

    [Theory]
    [MemberData(nameof(RuleCases))]
    public void ARuleCaseHoldsItsFigures(string fixture, long samples, long lost)
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, fixture);
        Report report = Analyzer.CreateDefault().AnalyzeFile(path);
        StreamInfo stream = report.Streams[0];
        Assert.Equal((samples, lost), (stream.Samples, stream.LostSamples));
    }

    // Every stream's lost samples are the Lost Samples node of its tree, directly after Samples, also where no frame was read.
    [Theory]
    [MemberData(nameof(RuleCases))]
    public void ALostSamplesNodeStatesTheStreamsLostSamples(string fixture, long samples, long lost)
    {
        Assert.True(samples >= 0);
        string path = Path.Combine(Fixtures.FixtureDirectory, fixture);
        Report report = Analyzer.CreateDefault().AnalyzeFile(path);
        StreamInfo stream = report.Streams[0];
        Node? node = Find(stream.Nodes[0], "Lost Samples");
        if (lost == 0)
        {
            Assert.Null(node);
            return;
        }

        Assert.NotNull(node);
        Assert.Equal(lost, node.Value!.AsInteger());
        Node? statistics = ParentOf(stream.Nodes[0], "Lost Samples");
        Assert.NotNull(statistics);
        int at = statistics.Children.ToList().FindIndex(c => c.Name == "Lost Samples");
        Assert.True(at > 0 && statistics.Children[at - 1].Name == "Samples", $"{fixture}: Lost Samples does not follow Samples.");
    }

    private static Node? ParentOf(Node node, string name)
    {
        foreach (Node child in node.Children)
        {
            if (child.Name == name)
            {
                return node;
            }

            if (child.Kind != NodeKind.Frame && ParentOf(child, name) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }

    private static Node? Find(Node node, string name)
    {
        foreach (Node child in node.Children)
        {
            if (child.Name == name)
            {
                return child;
            }

            if (child.Kind != NodeKind.Frame && Find(child, name) is { } inner)
            {
                return inner;
            }
        }

        return null;
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

    // A container that hands a block cut by the end of the file (MappedBytes.IsCut) to a codec parser, of whatever codec: the parser reports no
    // bug, counts the packet where it can and keeps its series whole (an empty cut packet once broke the AAC coding series).
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(20)]
    [InlineData(400)]
    public void AMatroskaFileCutInsideItsLastBlockProducesNoParserBug(int cut)
    {
        int analyzed = 0;
        foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "matroska")).Where(f => f.EndsWith(".mka", StringComparison.Ordinal) || f.EndsWith(".mkv", StringComparison.Ordinal) || f.EndsWith(".webm", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length <= cut)
            {
                continue;
            }

            Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(path), new MemoryStream(data, 0, data.Length - cut), new AnalysisOptions { Decoding = new DecodeOptions() });
            Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
            Assert.All(report.Streams, s => Assert.True(s.LostSamples >= 0));
            analyzed++;
        }

        Assert.True(analyzed > 20);
    }

    // The end of the last SimpleBlock or Block of a Matroska file whose elements all state their sizes, or -1.
    private static long LastBlockEnd(byte[] data)
    {
        static (long Id, long Size, int Header) Element(byte[] d, long at)
        {
            int idWidth = d[at] >= 0x80 ? 1 : d[at] >= 0x40 ? 2 : d[at] >= 0x20 ? 3 : 4;
            long id = 0;
            for (int i = 0; i < idWidth; i++)
            {
                id = (id << 8) | d[at + i];
            }

            int sizeWidth = 1;
            while (sizeWidth <= 8 && (d[at + idWidth] & (0x80 >> (sizeWidth - 1))) == 0)
            {
                sizeWidth++;
            }

            long size = d[at + idWidth] & (0xFF >> sizeWidth);
            bool unknown = size == (0xFF >> sizeWidth);
            for (int i = 1; i < sizeWidth; i++)
            {
                unknown &= d[at + idWidth + i] == 0xFF;
                size = (size << 8) | d[at + idWidth + i];
            }

            return (id, unknown ? -1 : size, idWidth + sizeWidth);
        }

        long position = 0;
        long last = -1;
        while (position < data.Length)
        {
            (long id, long size, int header) = Element(data, position);
            if (size < 0)
            {
                return -1;
            }

            if (id == 0x18538067)
            {
                position += header;
                continue;
            }

            if (id == 0x1F43B675)
            {
                long end = position + header + size;
                long inner = position + header;
                while (inner < end)
                {
                    (long childId, long childSize, int childHeader) = Element(data, inner);
                    if (childSize < 0)
                    {
                        return -1;
                    }

                    if (childId == 0xA3)
                    {
                        last = inner + childHeader + childSize;
                    }
                    else if (childId == 0xA0)
                    {
                        // The Block inside the group; DiscardPadding and BlockDuration may follow it.
                        long groupEnd = inner + childHeader + childSize;
                        for (long item = inner + childHeader; item < groupEnd;)
                        {
                            (long itemId, long itemSize, int itemHeader) = Element(data, item);
                            if (itemId == 0xA1)
                            {
                                last = item + itemHeader + itemSize;
                            }

                            item += itemHeader + itemSize;
                        }
                    }

                    inner += childHeader + childSize;
                }
            }

            position += header + size;
        }

        return last;
    }

    // A block the file cuts: its frame is handed to the codec parser cut, which counts it by its declaration and loses it. A fixed-size lace
    // (mkvmerge's AC-3, E-AC-3 and DTS) takes its frame size from the block's declared size, so the frames before the cut stay whole.
    [Theory]
    [InlineData("external-mkvmerge-ac3.mka", 1536)]
    [InlineData("external-mkvmerge-eac3.mka", 1536)]
    [InlineData("external-mkvmerge-dts.mka", 512)]
    [InlineData("external-mkvmerge-timestamp-scale.mka", 1536)]
    [InlineData("external-mkvmerge-vorbis.mka", -1)]
    [InlineData("external-mkvmerge-opus.mka", -1)]
    [InlineData("external-mkvmerge-aac.mka", 1024)]
    [InlineData("external-mkvmerge-tta.mka", -1)]
    [InlineData("external-ffmpeg-tta.mka", -1)]
    public void ACutMatroskaBlockIsLostOnceWhateverTheLacingAndCodec(string name, int frameSamples)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "matroska", name));
        long end = LastBlockEnd(data);
        Assert.True(end > 0, name);
        StreamInfo whole = Analyzer.CreateDefault().Analyze(name, new MemoryStream(data)).Streams.Single();
        Report report = Analyzer.CreateDefault().Analyze(name, new MemoryStream(data, 0, (int)end - 3));
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        StreamInfo cut = report.Streams.Single();
        Assert.True(cut.LostSamples > 0, $"{name}: nothing is lost");
        Assert.True(cut.LostSamples <= cut.Samples + whole.Samples);
        if (frameSamples > 0)
        {
            Assert.Equal(frameSamples, cut.LostSamples);
            Assert.Equal(whole.Samples, cut.Samples);
        }
    }

    [Fact]
    public void ACutAacBlockInMatroskaIsCountedAndLost()
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, "matroska", "aac-delay.mka");
        byte[] data = File.ReadAllBytes(path);
        StreamInfo whole = Analyzer.CreateDefault().Analyze("whole.mka", new MemoryStream(data)).Streams.Single();
        Report report = Analyzer.CreateDefault().Analyze("cut.mka", new MemoryStream(data, 0, data.Length - 3));
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        StreamInfo cut = report.Streams.Single();
        Assert.Equal(whole.Samples, cut.Samples);
        Assert.Equal(1024, cut.LostSamples);
    }

    private static Report AnalyzeCut(string fixture, int cut)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        return Analyzer.CreateDefault().Analyze(Path.GetFileName(fixture), new MemoryStream(data, 0, data.Length - cut));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void AnAdifStreamWhoseLastBlockBreaksCountsItsDeclaredSamplesAndLosesThem(int cut)
    {
        StreamInfo whole = AnalyzeCut("aac/adts/adif.aac", 0).Streams.Single();
        Report report = AnalyzeCut("aac/adts/adif.aac", cut);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        StreamInfo stream = report.Streams.Single();
        Assert.Equal(whole.Samples, stream.Samples);
        Assert.Equal(1024, stream.LostSamples);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    public void AnAlacSampleTheFileEndsInsideIsLostWithoutSyntaxFindingsAndStaysOutOfTheFrameFigures(int cut)
    {
        const string fixture = "alac/built-end-missing.m4a";
        StreamInfo whole = AnalyzeCut(fixture, 0).Streams.Single();
        Report report = AnalyzeCut(fixture, cut);
        StreamInfo stream = report.Streams.Single();
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "alac.frame-overrun" or "alac.element-invalid");
        // The cut sample is the last, whose element states its 1,808 samples where the header is in the bytes present; with less than the header, the cookie's 4,096.
        Assert.Equal(whole.Samples, stream.Samples);
        Assert.Equal(1808, stream.LostSamples);
        Assert.Equal(FrameCount(AnalyzeCut(fixture, 0)) - 1, FrameCount(report));
    }

    private static IEnumerable<Node> Nodes(Node node) => node.Children.SelectMany(c => Nodes(c).Prepend(c));

    // The Frames figure of the statistics: the units counted, which leave out a sample the container cut.
    private static long FrameCount(Report report) =>
        long.Parse(Nodes(report.Root).First(n => n.Name == "Statistics").Children.First(c => c.Name == "Frames").Value!.ToString()!.Split(' ')[0], CultureInfo.InvariantCulture);

    [Fact]
    public void TheTopicOfLostSamplesIsServed()
    {
        Assert.NotNull(TopicLibrary.Find(StreamInfo.LostSamplesTopic));
        Assert.Contains("units", StreamInfo.LostSamplesDescription, StringComparison.Ordinal);
    }
}
