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

        Assert.Contains("Verified: not verified (frame CRC-16 40 of 40, STREAMINFO MD5 not compared: decoding was not asked for)", Text(ReportWith(skipped)));
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
            (DecodeStatus.Decoded, "a frame was not decoded, or the data ends inside it"),
        })
        {
            var verification = new StreamVerification { Decoding = status, Checksums = [Tally("MD5", ChecksumCoverage.DecodedAudio, 1, 0, all: true)] };
            Assert.Equal($"not verified (MD5 not compared: {reason})", VerificationText.Describe(verification));
        }
    }

    [Fact]
    public void ACheckedChecksumThatCoversNoAudioAndAStreamWithoutChecksumsAreSaid()
    {
        var header = new StreamVerification { Decoding = DecodeStatus.Decoded, Checksums = [Tally("major sync CRC-16", ChecksumCoverage.Header, 3, 3)] };
        Assert.Equal("not verified (major sync CRC-16 3 of 3, no checksum that was compared covers all of the audio)", VerificationText.Describe(header));

        Assert.Equal("not verified (the stream stores no checksum)", VerificationText.Describe(new StreamVerification { Decoding = DecodeStatus.NotRequested }));

        var partial = new StreamVerification { Decoding = DecodeStatus.NotRequested, Checksums = [Tally("frame CRC-16", ChecksumCoverage.CodedData, 40, 39, all: true)] };
        Assert.Equal("not verified (frame CRC-16 39 of 40 compared)", VerificationText.Describe(partial));
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
        Assert.Equal("not verified (block header CRC not compared: decoding was not asked for)", VerificationText.Describe(hybrid with { Decoding = DecodeStatus.NotRequested, Checksums = [Tally("block header CRC", ChecksumCoverage.DecodedAudio, 120, 0, all: true)] }));
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

    private static (int Code, string Output, string Errors) Run(params string[] args)
    {
        using var stdout = new MemoryStream();
        var stderr = new StringWriter();
        int code = CommandLine.Run(args, stdout, stderr);
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
}
