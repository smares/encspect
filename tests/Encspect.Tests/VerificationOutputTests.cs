using System.Text;
using System.Text.Json;
using Encspect.Cli;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// How the verification of a stream reads in the text tree, in JSON and in CSV, and what --verify does.
public class VerificationOutputTests
{
    private static ChecksumTally Tally(string name, ChecksumCoverage covers, long stored, long checkedValues, bool all = false, long failed = 0) =>
        new() { Name = name, Covers = covers, CoversAllAudio = all, Stored = stored, Checked = checkedValues, Failed = failed };

    private static Report ReportWith(params StreamVerification?[] verifications)
    {
        var root = new Node("File", 0, 100, "Test");
        var streams = new List<StreamInfo>();
        foreach (StreamVerification? verification in verifications)
        {
            Node node = root.Add("Stream", 0, 100);
            streams.Add(new StreamInfo { Nodes = [node], Codec = "Test", SampleRate = 44100, Channels = 2, Verification = verification });
        }

        return new Report("test.bin", root, [], streams);
    }

    private static readonly StreamVerification Holds = new()
    {
        Decoding = DecodeStatus.Decoded,
        DecodedSamples = 400,
        Checksums =
        [
            Tally("frame header CRC-8", ChecksumCoverage.Header, 40, 40),
            Tally("frame CRC-16", ChecksumCoverage.CodedData, 40, 40, all: true),
            Tally("STREAMINFO MD5", ChecksumCoverage.DecodedAudio, 1, 1, all: true),
        ],
    };

    private static string Text(Report report)
    {
        var writer = new StringWriter();
        ReportTextWriter.Write(report, writer, showOffsets: false);
        return writer.ToString().ReplaceLineEndings("\n");
    }

    [Fact]
    public void TheTextTreeSaysYesWithWhatWasComparedUnderTheStream()
    {
        string text = Text(ReportWith(Holds));

        Assert.Contains("    Stream\n      Verified: yes (frame header CRC-8 40 of 40, frame CRC-16 40 of 40, STREAMINFO MD5 1 of 1)\n", text);
        Assert.True(Holds.IsVerified);
    }

    [Fact]
    public void TheTextTreeSaysNoWhereAChecksumDiffers()
    {
        var failed = Holds with { Checksums = [Holds.Checksums[0], Tally("STREAMINFO MD5", ChecksumCoverage.DecodedAudio, 1, 1, all: true, failed: 1)] };

        Assert.Contains("Verified: no (frame header CRC-8 40 of 40, STREAMINFO MD5 1 of 1 differ)", Text(ReportWith(failed)));
    }

    [Fact]
    public void TheTextTreeSaysWhyNotWhereDecodingWasNotAskedFor()
    {
        var skipped = new StreamVerification
        {
            Decoding = DecodeStatus.NotRequested,
            Checksums = [Holds.Checksums[1], Tally("STREAMINFO MD5", ChecksumCoverage.DecodedAudio, 1, 0, all: true)],
        };

        Assert.Contains("Verified: unknown (frame CRC-16 40 of 40, STREAMINFO MD5 not compared: decoding was not asked for)", Text(ReportWith(skipped)));
    }

    [Fact]
    public void TheReasonFollowsTheStatus()
    {
        foreach ((DecodeStatus status, string reason) in new[]
        {
            (DecodeStatus.NoDecoder, "no decoder handles this coding"),
            (DecodeStatus.NotPossible, "the stream cannot be decoded"),
            (DecodeStatus.Stopped, "decoding was stopped"),
            (DecodeStatus.Failed, "the decoder failed"),
            (DecodeStatus.Decoded, "the decoded stream is incomplete: a frame was not decoded, a frame is missing, or the samples differ from the stated count"),
        })
        {
            var verification = new StreamVerification { Decoding = status, Checksums = [Tally("MD5", ChecksumCoverage.DecodedAudio, 1, 0, all: true)] };
            Assert.Equal($"unknown (MD5 not compared: {reason})", VerificationText.Describe(verification));
        }
    }

    [Fact]
    public void ACheckedChecksumThatCoversNoAudioAndAStreamWithoutChecksumsAreSaid()
    {
        var header = new StreamVerification { Decoding = DecodeStatus.Decoded, Checksums = [Tally("major sync CRC-16", ChecksumCoverage.Header, 3, 3)] };
        Assert.Equal("unknown (major sync CRC-16 3 of 3, no checksum that was compared covers all of the audio)", VerificationText.Describe(header));

        Assert.Equal("unknown (the stream stores no checksum)", VerificationText.Describe(new StreamVerification { Decoding = DecodeStatus.NotRequested }));

        var partial = new StreamVerification { Decoding = DecodeStatus.NotRequested, Checksums = [Tally("frame CRC-16", ChecksumCoverage.CodedData, 40, 39, all: true)] };
        Assert.Equal("unknown (frame CRC-16 39 of 40 compared)", VerificationText.Describe(partial));
    }

    [Fact]
    public void ALossyStreamThatIsVerifiedSaysSo()
    {
        var hybrid = new StreamVerification
        {
            Decoding = DecodeStatus.Decoded,
            IsLossy = true,
            Checksums = [Tally("block header CRC", ChecksumCoverage.DecodedAudio, 120, 120, all: true)],
        };

        Assert.Equal("yes (lossy stream; block header CRC 120 of 120)", VerificationText.Describe(hybrid));
        Assert.Equal("unknown (block header CRC not compared: decoding was not asked for)", VerificationText.Describe(hybrid with { Decoding = DecodeStatus.NotRequested, Checksums = [Tally("block header CRC", ChecksumCoverage.DecodedAudio, 120, 0, all: true)] }));
    }

    [Fact]
    public void ALossyStreamWhoseDigestIsOfTheLosslessAudioSaysWhy()
    {
        var hybrid = new StreamVerification
        {
            Decoding = DecodeStatus.Decoded,
            IsLossy = true,
            Checksums = [Tally("block header CRC", ChecksumCoverage.DecodedAudio, 120, 120, all: true), Tally("MD5", ChecksumCoverage.DecodedAudio, 1, 0, all: true)],
        };

        Assert.Equal("unknown (block header CRC 120 of 120, MD5 not compared: the stream decodes to lossy audio, and the checksum is of the lossless audio)", VerificationText.Describe(hybrid));
    }

    [Fact]
    public void AHybridWavPackFileWithAnMd5SaysItNeedsTheCorrectionFile()
    {
        Report report = Analyzer.CreateDefault().Analyze("hybrid.wv", File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, "wavpack", "external-hybrid-b4-md5.wv")), new AnalysisOptions { Decoding = new DecodeOptions() });
        string text = VerificationText.Describe(report.Streams.Single().Verification!);
        Assert.Contains("MD5 not compared: the stream decodes to lossy audio, and the checksum is of the lossless audio", text);
        Assert.DoesNotContain("incomplete", text);
    }

    // Nothing decoded, so the block CRC and the digest are both uncompared for that reason, not because the digest is of lossless audio.
    [Fact]
    public void ALossyStreamThatDecodedNothingDoesNotBlameTheLosslessDigest()
    {
        var refused = new StreamVerification
        {
            Decoding = DecodeStatus.Decoded,
            IsLossy = true,
            Checksums = [Tally("block header CRC", ChecksumCoverage.DecodedAudio, 120, 0, all: true), Tally("MD5", ChecksumCoverage.DecodedAudio, 1, 0, all: true)],
        };

        string text = VerificationText.Describe(refused);
        Assert.DoesNotContain("lossy audio", text);
        Assert.Contains("block header CRC not compared: the decoded stream is incomplete", text);
        Assert.Contains("MD5 not compared: the decoded stream is incomplete", text);
    }

    [Fact]
    public void AHybridWavPackFileWhoseBlocksAreAllRefusedDoesNotBlameTheLosslessDigest()
    {
        List<byte[]> blocks = WavPackBuilder.Blocks("external-hybrid-b4-md5.wv");
        byte[] first = WavPackBuilder.Join(blocks[0], [WavPackBuilder.Sub(0x0D, [1]), .. WavPackBuilder.Parts(blocks[0])]);
        byte[] data = WavPackBuilder.Concat([first, .. blocks.Skip(1)]);

        Report report = Analyzer.CreateDefault().Analyze("refused.wv", new MemoryStream(data), new AnalysisOptions { Decoding = new DecodeOptions() });
        string text = VerificationText.Describe(report.Streams.Single().Verification!);

        Assert.DoesNotContain("decodes to lossy audio", text);
    }

    [Fact]
    public void AStreamWithoutVerificationGetsNoLine()
    {
        Assert.DoesNotContain("Verified", Text(ReportWith((StreamVerification?)null)));
    }

    private static JsonElement StreamJson(Report report)
    {
        using var stream = new MemoryStream();
        ReportJsonWriter.Write([report], stream);
        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement[0].GetProperty("streams")[0].Clone();
    }

    [Fact]
    public void JsonWritesTheVerificationAfterTheProtectionWithItsChecksums()
    {
        JsonElement stream = StreamJson(ReportWith(Holds));

        JsonElement verification = stream.GetProperty("verification");
        Assert.True(verification.GetProperty("isVerified").GetBoolean());
        Assert.False(verification.TryGetProperty("isLossy", out _));
        Assert.Equal("decoded", verification.GetProperty("decoding").GetString());
        Assert.Equal(400, verification.GetProperty("decodedSamples").GetInt64());
        JsonElement md5 = verification.GetProperty("checksums")[2];
        Assert.Equal("STREAMINFO MD5", md5.GetProperty("name").GetString());
        Assert.Equal("decodedAudio", md5.GetProperty("covers").GetString());
        Assert.True(md5.GetProperty("coversAllAudio").GetBoolean());
        Assert.Equal((1, 1, 0), (md5.GetProperty("stored").GetInt32(), md5.GetProperty("checked").GetInt32(), md5.GetProperty("failed").GetInt32()));
        Assert.Equal("header", verification.GetProperty("checksums")[0].GetProperty("covers").GetString());
        Assert.Equal("codedData", verification.GetProperty("checksums")[1].GetProperty("covers").GetString());
        Assert.Equal(1, ReportJsonWriter.SchemaVersion);
    }

    [Fact]
    public void JsonWritesTheVerificationDirectlyAfterTheProtection()
    {
        var root = new Node("File", 0, 100, "Test");
        Node node = root.Add("Stream", 0, 100);
        var stream = new StreamInfo
        {
            Nodes = [node],
            Codec = "Test",
            Protection = new StreamProtection { Scheme = "Test scheme", IsEncrypted = false, Systems = [] },
            Encoder = "Encoder 1",
            Verification = Holds,
        };

        using var output = new MemoryStream();
        ReportJsonWriter.Write([new Report("test.bin", root, [], [stream])], output);
        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        string[] names = document.RootElement[0].GetProperty("streams")[0].EnumerateObject().Select(p => p.Name).ToArray();

        int protection = Array.IndexOf(names, "protection");
        Assert.True(protection > 0);
        Assert.Equal("verification", names[protection + 1]);
        Assert.Equal("encoder", names[protection + 2]);
    }

    [Fact]
    public void JsonLeavesOutIsVerifiedWhereItIsNullAndWritesIsLossyWhereTrue()
    {
        var unknown = new StreamVerification { Decoding = DecodeStatus.NotRequested, IsLossy = true, Checksums = [Tally("MD5", ChecksumCoverage.DecodedAudio, 1, 0, all: true)] };

        JsonElement verification = StreamJson(ReportWith(unknown)).GetProperty("verification");

        Assert.False(verification.TryGetProperty("isVerified", out _));
        Assert.True(verification.GetProperty("isLossy").GetBoolean());
        Assert.Equal("notRequested", verification.GetProperty("decoding").GetString());
    }

    [Fact]
    public void JsonNamesEveryStatus()
    {
        foreach ((DecodeStatus status, string name) in new[]
        {
            (DecodeStatus.NotRequested, "notRequested"),
            (DecodeStatus.NoDecoder, "noDecoder"),
            (DecodeStatus.NotPossible, "notPossible"),
            (DecodeStatus.Decoded, "decoded"),
            (DecodeStatus.Stopped, "stopped"),
            (DecodeStatus.Failed, "failed"),
        })
        {
            JsonElement verification = StreamJson(ReportWith(new StreamVerification { Decoding = status })).GetProperty("verification");
            Assert.Equal(name, verification.GetProperty("decoding").GetString());
        }
    }

    [Fact]
    public void JsonWritesNoVerificationForAStreamWithout()
    {
        Assert.False(StreamJson(ReportWith((StreamVerification?)null)).TryGetProperty("verification", out _));
    }

    private static string[] CsvRow(Report report, bool isVerifying)
    {
        var writer = new StringWriter();
        ReportCsvWriter.Write([report], writer, isVerifying);
        string[] lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] header = lines[0].Split(',');
        Assert.Equal("Is Verified", header[Array.IndexOf(header, "Is Protected") + 1]);
        return [header[Array.IndexOf(header, "Is Verified")], lines[1].Split(',')[Array.IndexOf(header, "Is Verified")]];
    }

    [Fact]
    public void CsvFillsIsVerifiedOnlyForAnAnalysisThatAskedForDecoding()
    {
        Assert.Equal("", CsvRow(ReportWith(Holds), isVerifying: false)[1]);
        Assert.Equal("true", CsvRow(ReportWith(Holds), isVerifying: true)[1]);
    }

    [Fact]
    public void CsvIsFalseWhereAStreamIsFalseTrueWhereEveryAudioStreamIsTrueAndEmptyOtherwise()
    {
        var failed = Holds with { Checksums = [Tally("MD5", ChecksumCoverage.DecodedAudio, 1, 1, all: true, failed: 1)] };
        var unknown = new StreamVerification { Decoding = DecodeStatus.NotRequested, Checksums = [Tally("MD5", ChecksumCoverage.DecodedAudio, 1, 0, all: true)] };

        Assert.Equal("false", CsvRow(ReportWith(Holds, failed), true)[1]);
        Assert.Equal("false", CsvRow(ReportWith(unknown, failed), true)[1]);
        Assert.Equal("true", CsvRow(ReportWith(Holds, Holds), true)[1]);
        Assert.Equal("", CsvRow(ReportWith(Holds, unknown), true)[1]);
        Assert.Equal("", CsvRow(ReportWith(Holds, null), true)[1]);
        Assert.Equal("", CsvRow(ReportWith(), true)[1]);
    }

    [Fact]
    public void TheSummaryCarriesTheVerdict()
    {
        Assert.True(ReportSummary.From(ReportWith(Holds)).IsVerified);
        Assert.Null(ReportSummary.From(ReportWith((StreamVerification?)null)).IsVerified);
    }

    // The command line.

    private static (int Code, string Output, string Errors) Run(params string[] args) => RunWith(null, args);

    private static (int Code, string Output, string Errors) RunWith(Analyzer? analyzer, params string[] args)
    {
        using var stdout = new MemoryStream();
        var stderr = new StringWriter();
        int code = CommandLine.Run(args, stdout, stderr, analyzer);
        return (code, Encoding.UTF8.GetString(stdout.ToArray()), stderr.ToString());
    }

    private static string Fixture() => Path.Combine(Fixtures.FixtureDirectory, "mpeg", "external-lame-tagged.mp3");

    [Fact]
    public void VerifyIsAnOptionOfTheUsageAndRunsTheAnalysisWithDecodingOn()
    {
        Assert.Contains("--verify", CommandLine.Usage);

        // No format has a decoder yet, so nothing changes in the report; the run succeeds, and so does every output.
        foreach (string[] args in new[] { new[] { "--verify" }, ["--verify", "--json"], ["--verify", "--ndjson"], ["--verify", "--csv"] })
        {
            (int code, string output, string errors) = Run([.. args, Fixture()]);
            Assert.Equal((0, ""), (code, errors));
            Assert.NotEmpty(output);
        }
    }

    [Fact]
    public void VerifyWithCsvKeepsIsVerifiedEmptyForAFormatWithoutVerification()
    {
        (_, string output, _) = Run("--verify", "--csv", Fixture());

        string[] lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] header = lines[0].Split(',');
        int column = Array.IndexOf(header, "Is Verified");
        Assert.True(column > 0);
        Assert.Equal("", lines[1].Split(',')[column]);
    }

    [Fact]
    public void VerifyReportsNoDecoderForAStreamThatAsksForOneOnlyWhenAFormatDoes()
    {
        (_, string output, _) = Run("--verify", "--json", Fixture());

        Assert.DoesNotContain("decode.", output);
    }

    [Fact]
    public void VerifyDoesNotChangeTheReportOfAFileThatHasNoVerification()
    {
        (_, string plain, _) = Run("--json", Fixture());
        (_, string verified, _) = Run("--verify", "--json", Fixture());

        Assert.Equal(plain, verified);
    }

    // The decoder this analyzer has decides what --verify changes: with it the stream is decoded and its MD5 compared.
    private static Analyzer AnalyzerWithADecoder() => new(
        [],
        [new DecodeRouteTests.FrameParser { Stored = [new ChecksumTally { Name = "stream MD5", Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = true, Stored = 1 }] }],
        [],
        [new DecodeRouteTests.ScriptedDecoder { OnFinish = _ => [new ChecksumResult("stream MD5", 1, 0)] }]);

    private static string Sample()
    {
        string path = Path.Combine(Path.GetTempPath(), $"encspect-verify-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    [Fact]
    public void VerifySetsDecodingOnTheAnalysisAndWithoutItNothingIsDecoded()
    {
        string path = Sample();
        try
        {
            string Decoding(params string[] args)
            {
                (int code, string output, _) = RunWith(AnalyzerWithADecoder(), [.. args, "--json", path]);
                Assert.Equal(0, code);
                using JsonDocument document = JsonDocument.Parse(output);
                JsonElement verification = document.RootElement[0].GetProperty("streams")[0].GetProperty("verification");
                return $"{verification.GetProperty("decoding").GetString()} {verification.TryGetProperty("isVerified", out JsonElement v) && v.GetBoolean()}";
            }

            Assert.Equal("notRequested False", Decoding());
            Assert.Equal("decoded True", Decoding("--verify"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VerifyFillsIsVerifiedInTheCsvOnlyWithVerify()
    {
        string path = Sample();
        try
        {
            string Column(params string[] args)
            {
                (_, string output, _) = RunWith(AnalyzerWithADecoder(), [.. args, "--csv", path]);
                string[] lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                return lines[1].Split(',')[Array.IndexOf(lines[0].Split(','), "Is Verified")];
            }

            Assert.Equal("", Column());
            Assert.Equal("true", Column("--verify"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VerifyShowsTheVerdictInTheTextTree()
    {
        string path = Sample();
        try
        {
            (_, string plain, _) = RunWith(AnalyzerWithADecoder(), path);
            (_, string verified, _) = RunWith(AnalyzerWithADecoder(), "--verify", path);

            Assert.Contains("Verified: unknown (", plain);
            Assert.Contains("stream MD5 not compared: decoding was not asked for", plain);
            Assert.Contains("Verified: yes (", verified);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
