using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The class of each coding by its name, the words it reads in, and how the verification's IsLossy stands to it.
public class CodingClassTests
{
    [Theory]
    [InlineData("PCM", CodingClass.Lossless)]
    [InlineData("PCM (floating-point)", CodingClass.Lossless)]
    [InlineData("FLAC", CodingClass.Lossless)]
    [InlineData("Apple Lossless", CodingClass.Lossless)]
    [InlineData("Monkey's Audio", CodingClass.Lossless)]
    [InlineData("TTA", CodingClass.Lossless)]
    [InlineData("TAK", CodingClass.Lossless)]
    [InlineData("WavPack lossless", CodingClass.Lossless)]
    [InlineData("Dolby TrueHD", CodingClass.Lossless)]
    [InlineData("MLP", CodingClass.Lossless)]
    [InlineData("Windows Media Audio Lossless", CodingClass.Lossless)]
    [InlineData("DTS-HD Master Audio", CodingClass.Lossless)]
    [InlineData("DTS-HD Master Audio + DTS:X", CodingClass.Lossless)]
    [InlineData("A-law", CodingClass.Lossy)]
    [InlineData("Mu-law", CodingClass.Lossy)]
    [InlineData("IMA ADPCM", CodingClass.Lossy)]
    [InlineData("Microsoft ADPCM", CodingClass.Lossy)]
    [InlineData("MACE 3-to-1", CodingClass.Lossy)]
    [InlineData("MPEG-1 Layer III", CodingClass.Lossy)]
    [InlineData("MPEG-1 audio", CodingClass.Lossy)]
    [InlineData("AAC LC", CodingClass.Lossy)]
    [InlineData("HE-AAC v2", CodingClass.Lossy)]
    [InlineData("xHE-AAC (USAC)", CodingClass.Lossy)]
    [InlineData("AC-3", CodingClass.Lossy)]
    [InlineData("E-AC-3", CodingClass.Lossy)]
    [InlineData("DTS", CodingClass.Lossy)]
    [InlineData("DTS-HD High Resolution Audio", CodingClass.Lossy)]
    [InlineData("Vorbis", CodingClass.Lossy)]
    [InlineData("Opus", CodingClass.Lossy)]
    [InlineData("Speex wideband", CodingClass.Lossy)]
    [InlineData("Musepack SV8", CodingClass.Lossy)]
    [InlineData("Windows Media Audio 2", CodingClass.Lossy)]
    [InlineData("Windows Media Audio Voice", CodingClass.Lossy)]
    [InlineData("WavPack hybrid", CodingClass.Hybrid)]
    [InlineData("WavPack lossy", CodingClass.Hybrid)]
    public void ACodingHasTheClassItsDefinitionGives(string codec, CodingClass expected) => Assert.Equal(expected, CodingClasses.OfName(codec));

    [Theory]
    [InlineData("Other")]
    [InlineData("Video")]
    [InlineData("Encrypted audio")]
    [InlineData("MPEG-4 audio")]
    [InlineData("SLS")]
    [InlineData("SLS non-core")]
    [InlineData("A coding nobody listed")]
    public void ACodingWhoseClassTheStreamAloneDoesNotSettleHasNone(string codec) => Assert.Null(CodingClasses.OfName(codec));

    [Fact]
    public void OptimFrogIsHybridOnlyWhereTheStreamMakesTheLossyPartClaim()
    {
        Node node = new("OptimFROG", 0, 10);
        var plain = new StreamInfo { Nodes = [node], Codec = "OptimFROG" };
        var dual = plain with { Claims = [new StreamClaim(StreamClaim.LossyPart, "DualStream", Confidence.Low, "the reserved field")] };

        Assert.Equal(CodingClass.Lossless, CodingClasses.Of(plain));
        Assert.Equal(CodingClass.Hybrid, CodingClasses.Of(dual));
        Assert.Null(CodingClasses.Of(plain with { IsCorrectionFile = true }));
        Assert.Null(CodingClasses.Of(plain with { Kind = StreamKind.Video }));
    }

    [Fact]
    public void AClassThatAParserStatedIsKept() =>
        Assert.Equal(CodingClass.Lossy, CodingClasses.Of(new StreamInfo { Nodes = [new Node("X", 0, 1)], Codec = "FLAC", CodingClass = CodingClass.Lossy }));

    [Theory]
    [InlineData(CodingClass.Lossless, null, "lossless coding")]
    [InlineData(CodingClass.Lossy, null, "lossy coding")]
    [InlineData(CodingClass.Hybrid, null, "hybrid coding")]
    [InlineData(CodingClass.Hybrid, CorrectionStatus.Consistent, "hybrid coding (lossless with its correction file)")]
    [InlineData(CodingClass.Hybrid, CorrectionStatus.Missing, "hybrid coding (lossy without its correction file)")]
    [InlineData(CodingClass.Hybrid, CorrectionStatus.Inconsistent, "hybrid coding (lossy without its correction file)")]
    [InlineData(CodingClass.Hybrid, CorrectionStatus.Unchecked, "hybrid coding (lossy without its correction file)")]
    [InlineData(CodingClass.Hybrid, CorrectionStatus.NotWritten, "hybrid coding (lossy, no correction file written)")]
    public void TheCodingLineNamesTheCodingAndTheHybridsState(CodingClass codingClass, CorrectionStatus? status, string expected)
    {
        var stream = new StreamInfo
        {
            Nodes = [new Node("X", 0, 1)],
            Codec = "X",
            CodingClass = codingClass,
            Correction = status is { } s ? new CorrectionPairing { Status = s } : null,
        };

        Assert.Equal(expected, CodingText.Describe(stream));
    }

    [Fact]
    public void AStreamWithoutAClassHasNoCodingLine() =>
        Assert.Null(CodingText.Describe(new StreamInfo { Nodes = [new Node("X", 0, 1)], Codec = "Other" }));

    [Fact]
    public void JsonAndCsvWriteTheClassAsAWord()
    {
        Assert.Equal("lossless", CodingText.Word(CodingClass.Lossless));
        Assert.Equal("lossy", CodingText.Word(CodingClass.Lossy));
        Assert.Equal("hybrid", CodingText.Word(CodingClass.Hybrid));
        Assert.Null(CodingText.Word(null));
    }

    private static Report ReportOf(StreamInfo Make(Node node))
    {
        var root = new Node("File", 0, 100, "Test");
        Node node = root.Add("Stream", 0, 100);
        return new Report("test.bin", root, [], [Make(node)]);
    }

    [Fact]
    public void TheTextTreeStatesTheClassWithTheProcessorClaimBesideItAndBeforeVerified()
    {
        Report report = ReportOf(node => new StreamInfo
        {
            Nodes = [node], Codec = "FLAC", CodingClass = CodingClass.Lossless, Processor = "lossyWAV 1.4.2", ProcessorConfidence = Confidence.High,
            Verification = new StreamVerification
            {
                Decoding = DecodeStatus.Decoded,
                Checksums = [new ChecksumTally { Name = "STREAMINFO MD5", Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = true, Stored = 1, Checked = 1 }],
            },
        });
        var writer = new StringWriter();
        ReportTextWriter.Write(report, writer, showOffsets: false);
        string text = writer.ToString().ReplaceLineEndings("\n");

        Assert.Contains("    Stream\n      Coding: lossless coding\n        Processor: lossyWAV 1.4.2 (high)\n      Verified: yes (the decoded audio matches its stored checksum; STREAMINFO MD5 1 of 1)\n", text);
    }

    [Fact]
    public void TheTextTreeLeavesTheCodingLineOutForAStreamWithoutAClass()
    {
        Report report = ReportOf(node => new StreamInfo { Nodes = [node], Codec = "Other" });
        var writer = new StringWriter();
        ReportTextWriter.Write(report, writer, showOffsets: false);

        Assert.DoesNotContain("Coding:", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCsvPutsTheClassBesideTheCodecAndJsonAfterIt()
    {
        Report report = ReportOf(node => new StreamInfo { Nodes = [node], Codec = "Opus", CodingClass = CodingClass.Lossy });
        var csv = new StringWriter();
        ReportCsvWriter.Write([report], csv);
        string[] lines = csv.ToString().ReplaceLineEndings("\n").Split('\n');

        Assert.StartsWith("Path,Format,Streams,Codec,Coding Class,Duration (s)", lines[0], StringComparison.Ordinal);
        Assert.Contains(",Opus,lossy,", lines[1], StringComparison.Ordinal);

        using var output = new MemoryStream();
        ReportJsonWriter.Write([report], output);
        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        string[] names = document.RootElement[0].GetProperty("streams")[0].EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal("lossy", document.RootElement[0].GetProperty("streams")[0].GetProperty("codingClass").GetString());
        Assert.Equal(Array.IndexOf(names, "codec") + 1, Array.IndexOf(names, "codingClass"));
    }
}
