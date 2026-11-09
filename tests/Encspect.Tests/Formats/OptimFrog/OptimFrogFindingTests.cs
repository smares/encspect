using Encspect.Core;
using Encspect.Formats.OptimFrog;
using Encspect.Host;
using Builder = Encspect.Tests.OptimFrogBuilder;

namespace Encspect.Tests;

// What the OptimFROG format parser does with input the fixtures do not hold: how its sniff ranks a main block, the end the blocks declare, the walk's
// rule for unknown blocks and tags after the blocks, and each finding the walk and the checks across blocks raise. FindingCoverageTests checks that every
// code the parser emits is asserted somewhere.
public class OptimFrogFindingTests
{
    private static readonly OptimFrogParser Parser = new();

    private static SniffResult Sniff(byte[] data) => Parser.Sniff(new SniffContext(new ByteReader(new MemoryStream(data))));

    private static long? DeclaredEnd(byte[] data) => Parser.DeclaredEnd(new SniffContext(new ByteReader(new MemoryStream(data))));

    private static Report Analyze(byte[] data, AnalysisOptions? options = null) => Analyzer.CreateDefault().Analyze("test.ofr", new MemoryStream(data), options);

    private static Finding Single(Report report, string code) => Assert.Single(report.Findings, f => f.Code == code);

    private static string[] Codes(Report report) => [.. report.Findings.Select(f => f.Code).Distinct().Order(StringComparer.Ordinal)];

    // The files that raise each code, for FindingCoverageTests to find the code in this file and for the tests below to hold each to its finding.
    [Fact]
    public void AStandardFileRaisesNoFinding()
    {
        Report report = Analyze(Builder.Standard(3));

        Assert.Empty(report.Findings);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("OptimFROG", stream.Codec);
        Assert.Equal(44100, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
        Assert.Equal(16, stream.BitsPerSample);
        Assert.Equal(3 * 88200 / 2, stream.Samples);
        Assert.Equal("OptimFROG 5.100", stream.Encoder);
        Assert.Equal(true, stream.Verification!.IsVerified);
    }

    [Fact]
    public void AMainBlockWithAKnownBlockAfterItIsCertain()
    {
        byte[] data = Builder.Standard();

        SniffResult result = Sniff(data);

        Assert.Equal(100, result.Class);
        Assert.Equal(data.Length, result.End);
    }

    [Fact]
    public void AMainBlockAloneWithValidFieldsIsLikely()
    {
        Assert.Equal(80, Sniff(Builder.MainBlock()).Class);
        Assert.Equal(80, Sniff([.. Builder.MainBlock(), .. "junk that is not a block"u8]).Class);
    }

    [Theory]
    [InlineData(11, 3, 1, 44100u)]
    [InlineData(17, 11, 1, 44100u)]
    [InlineData(17, 3, 2, 44100u)]
    [InlineData(17, 3, 1, 0u)]
    public void AMainBlockWhoseFieldsCannotBeOneIsPossible(int size, int sampleType, int channelConfiguration, uint rate)
    {
        byte[] main = Builder.MainBlock(sampleType: sampleType, channelConfiguration: channelConfiguration, rate: rate, size: size);

        Assert.Equal(70, Sniff(main).Class);
    }

    [Fact]
    public void TheSignatureAloneIsPossible()
    {
        Assert.Equal(70, Sniff("OFR "u8.ToArray()).Class);
        Assert.Equal(70, Sniff([.. "OFR "u8, 17, 0, 0, 0, 1, 2, 3]).Class);
    }

    [Fact]
    public void ABlockFurtherInIsNoMatch()
    {
        Assert.False(Sniff([.. new byte[10], .. Builder.Standard()]).IsMatch);
        Assert.False(Sniff("OFR"u8.ToArray()).IsMatch);
        Assert.False(Sniff(new byte[100]).IsMatch);
        Assert.False(Sniff("OFRZ and more bytes of a text"u8.ToArray()).IsMatch);
    }

    [Fact]
    public void ADeclaredEndIsTheEndOfTheLastBlock()
    {
        byte[] file = Builder.Standard(2);

        Assert.Equal(file.Length, DeclaredEnd(file));
        Assert.Equal(file.Length, DeclaredEnd([.. file, .. TestFiles.ApeText("Title", "Song")]));
        Assert.Equal(file.Length, DeclaredEnd([.. file, .. new byte[500]]));
        Assert.Null(DeclaredEnd(Builder.Comp(88200)));
        Assert.Null(DeclaredEnd("OFR "u8.ToArray()));
    }

    [Fact]
    public void ADeclaredEndLiesPastTheDataOfAFileCutInABlock()
    {
        byte[] file = Builder.File(Builder.MainBlock(), Builder.Head(), Builder.Comp(88200, 500));

        Assert.Equal(file.Length, DeclaredEnd(file[..^100]));
    }

    [Fact]
    public void AnUnknownBlockFollowedByAValidOneIsWalkedAndReported()
    {
        byte[] file = Builder.File(Builder.MainBlock(), Builder.Head(), Builder.Block("XTRA", [1, 2, 3, 4, 5]), Builder.Comp(88200), Builder.Tail());

        Report report = Analyze(file);

        Finding unknown = Single(report, "optimfrog.block-unknown");
        Assert.Equal(Severity.Info, unknown.Severity);
        Assert.Equal(1, report.Streams.Single().Verification!.Checksums[0].Checked);
        Assert.DoesNotContain(report.Findings, f => f.Code != "optimfrog.block-unknown");
    }

    [Fact]
    public void AnUnknownBlockAtTheEndOfTheDataIsWalked()
    {
        byte[] file = [.. Builder.Standard(), .. Builder.Block("XTRA", [1, 2, 3])];

        Report report = Analyze(file);

        Single(report, "optimfrog.block-unknown");
        Assert.DoesNotContain(report.Findings, f => f.Code == "optimfrog.data-after-blocks");
    }

    [Fact]
    public void AnUnknownBlockWithNothingValidAfterItEndsTheWalkBeforeIt()
    {
        byte[] file = [.. Builder.Standard(), .. Builder.Block("XTRA", [1, 2, 3]), .. new byte[20]];

        Report report = Analyze(file);

        Finding after = Single(report, "optimfrog.data-after-blocks");
        Assert.Equal(Severity.Warning, after.Severity);
        Assert.DoesNotContain(report.Findings, f => f.Code == "optimfrog.block-unknown");
        Assert.Equal(Builder.Standard().Length, DeclaredEnd(file));
    }

    [Fact]
    public void AnUnknownBlockWhoseLengthRunsPastTheDataIsNotFollowed()
    {
        byte[] file = [.. Builder.Standard(), .. "XTRA"u8, 255, 255, 0, 0, 1, 2, 3];

        Report report = Analyze(file);

        Single(report, "optimfrog.data-after-blocks");
        Assert.DoesNotContain(report.Findings, f => f.Code is "optimfrog.block-cut" or "optimfrog.block-unknown");
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("ape")]
    public void ATagAfterTheBlocksIsTheHostsAndNoBlock(string kind)
    {
        byte[] tag = kind == "tag" ? Builder.Id3v1("Song", "Artist") : TestFiles.ApeText("Title", "Song");
        byte[] file = [.. Builder.Standard(), .. tag];

        Report report = Analyze(file);

        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("optimfrog.", StringComparison.Ordinal));
        Assert.Contains(report.Root.Children, n => n.Name is "ID3v1" or "APE" or "APEv2");
    }

    [Fact]
    public void AnId3v1TagWithAShortTitleIsNoBlock()
    {
        byte[] tag = Builder.Id3v1("A", "B");
        byte[] file = [.. Builder.Standard(), .. tag];

        Report report = Analyze(file);

        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("optimfrog.", StringComparison.Ordinal));
    }

    [Fact]
    public void AnId3v2TagBeforeTheFileIsTheHostsToo()
    {
        Report report = Analyze([.. TestFiles.Id3v23(("TIT2", "Song")), .. Builder.Standard()]);

        Assert.Empty(report.Findings);
        Assert.Single(report.Streams);
    }

    [Fact]
    public void AFileCutInADataBlockIsReportedAndItsCrcIsNotCompared()
    {
        byte[] file = Builder.File(Builder.MainBlock(), Builder.Head(), Builder.Comp(88200, 500));
        byte[] cut = file[..^100];

        Report report = Analyze(cut);

        Finding finding = Single(report, "optimfrog.block-cut");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("COMP", finding.Message);
        Assert.DoesNotContain(report.Findings, f => f.Code == "optimfrog.block-crc-mismatch");
        ChecksumTally tally = report.Streams.Single().Verification!.Checksums.Single(c => c.Name == "block CRC-32");
        Assert.Equal(1, tally.Stored);
        Assert.Equal(0, tally.Checked);
        Assert.Null(report.Streams.Single().Verification!.IsVerified);
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    [Fact]
    public void AFileCutInTheMainBlockIsReadAsFarAsItGoes()
    {
        Report report = Analyze(Builder.MainBlock()[..16], new AnalysisOptions { ForcedFormat = "OptimFROG" });

        Single(report, "optimfrog.block-cut");
        Assert.Empty(report.Streams);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    [Fact]
    public void AFailingCrcIsReportedOnceWithACount()
    {
        byte[] file = Builder.File(Builder.MainBlock(88200 * 3), Builder.Head(), Builder.Comp(88200, crc: 1), Builder.Comp(88200, seed: 1), Builder.Comp(88200, crc: 2, seed: 2), Builder.Tail());

        Report report = Analyze(file);

        Finding finding = Single(report, "optimfrog.block-crc-mismatch");
        Assert.Contains("data block 0", finding.Message);
        Assert.Contains("1 more block differs", finding.Message);
        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Equal(false, verification.IsVerified);
        Assert.Equal(2, verification.Checksums[0].Failed);
    }

    [Fact]
    public void AMissingMiddleBlockIsASamplesMismatch()
    {
        byte[] file = Builder.File(Builder.MainBlock(88200 * 3), Builder.Head(), Builder.Comp(88200), Builder.Comp(88200, seed: 2), Builder.Tail());

        Report report = Analyze(file);

        Finding finding = Single(report, "optimfrog.samples-mismatch");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("176400", finding.Message);
        Assert.Null(report.Streams.Single().Verification!.IsVerified);
    }

    [Fact]
    public void ABlockThatDiffersFromTheMainBlockIsReported()
    {
        byte[] file = Builder.File(Builder.MainBlock(88200 * 2), Builder.Head(), Builder.Comp(88200), Builder.Comp(88200, sampleType: 5, seed: 1), Builder.Tail());

        Report report = Analyze(file);

        Single(report, "optimfrog.block-differs");
        Assert.Null(report.Streams.Single().Verification!.IsVerified);
    }

    [Fact]
    public void AMissingHeaderAndFooterAreReported()
    {
        Report report = Analyze(Builder.File(Builder.MainBlock(), Builder.Comp(88200)));

        Single(report, "optimfrog.head-missing");
        Single(report, "optimfrog.tail-missing");
    }

    [Fact]
    public void ABlockOutOfOrderIsReported()
    {
        byte[] file = Builder.File(Builder.MainBlock(88200), Builder.Tail(), Builder.Head(), Builder.Comp(88200));

        Report report = Analyze(file);

        Finding finding = Single(report, "optimfrog.block-out-of-order");
        Assert.Contains("HEAD", finding.Message);
        Assert.Contains("more block", finding.Message);
    }

    [Fact]
    public void ASecondMainBlockOrHeaderBlockIsOutOfOrder()
    {
        Report report = Analyze(Builder.File(Builder.MainBlock(), Builder.Head(), Builder.Head(), Builder.Comp(88200), Builder.Tail()));

        Single(report, "optimfrog.block-out-of-order");
    }

    [Theory]
    [InlineData("main")]
    [InlineData("comp")]
    [InlineData("md5")]
    public void ABlockWhoseLengthBreaksTheSpecificationIsReadAsFarAsItHoldsFields(string kind)
    {
        byte[] file = kind switch
        {
            "main" => [.. Builder.Block("OFR ", new byte[12]), .. Builder.File(Builder.Head(), Builder.Comp(88200), Builder.Tail())],
            "comp" => Builder.File(Builder.MainBlock(), Builder.Head(), Builder.Block("COMP", new byte[10]), Builder.Tail()),
            _ => Builder.File(Builder.MainBlock(), Builder.Head(), Builder.Comp(88200), Builder.Tail(), Builder.Md5(8)),
        };

        Report report = Analyze(file, new AnalysisOptions { ForcedFormat = "OptimFROG" });

        Finding finding = Single(report, "optimfrog.block-size-invalid");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    [Theory]
    [InlineData("sample-type", 11, 1, 0x2582, 0x0A)]
    [InlineData("channel-configuration", 3, 2, 0x2582, 0x0A)]
    [InlineData("build-system", 3, 1, 0x258A, 0x0A)]
    [InlineData("mode", 3, 1, 0x2582, 15 << 3 | 2)]
    [InlineData("speedup", 3, 1, 0x2582, 0x0A | 5)]
    public void AnUndefinedValueIsNamedWithAnInfoFinding(string problem, int sampleType, int configuration, int encoder, int compression)
    {
        byte[] file = Builder.File(Builder.MainBlock(sampleType: sampleType, channelConfiguration: configuration, encoderId: encoder, compression: compression), Builder.Head(),
            Builder.Comp(88200, sampleType: sampleType, channelConfiguration: configuration, encoderId: encoder), Builder.Tail());

        Report report = Analyze(file);

        Finding finding = Single(report, "optimfrog.value-undefined");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("not defined", finding.Message);
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void AnUnknownBuildSystemOfFifteenIsDefined()
    {
        Report report = Analyze(Builder.File(Builder.MainBlock(encoderId: 0x258F), Builder.Head(), Builder.Comp(88200, encoderId: 0x258F), Builder.Tail()));

        Assert.Empty(report.Findings);
    }

    [Fact]
    public void ASampleRateOfZeroIsReported()
    {
        Report report = Analyze(Builder.File(Builder.MainBlock(rate: 0), Builder.Head(), Builder.Comp(88200), Builder.Tail()), new AnalysisOptions { ForcedFormat = "OptimFROG" });

        Single(report, "optimfrog.sample-rate-zero");
        Assert.Equal(0, report.Streams.Single().Duration);
    }

    [Fact]
    public void ATotalTheChannelsDoNotDivideIsReported()
    {
        Report report = Analyze(Builder.File(Builder.MainBlock(88201), Builder.Head(), Builder.Comp(88201), Builder.Tail()));

        Single(report, "optimfrog.samples-channels-mismatch");
    }

    [Fact]
    public void TheFloatEncodersMainBlockIsReadAsTheMainBlock()
    {
        byte[] file = Builder.File(Builder.MainBlock(sampleType: 8, id: "OFRX"), Builder.Head(), Builder.Comp(88200, sampleType: 8), Builder.Block("DETA", new byte[70]), Builder.Tail());

        Report report = Analyze(file);

        Assert.Equal(["optimfrog.block-unknown", "optimfrog.main-block-variant"], Codes(report));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(32, stream.BitsPerSample);
        Assert.Equal(100, Sniff(file).Class);
    }

    [Fact]
    public void AnAdditionalFieldInTheMainBlockIsSkipped()
    {
        Report report = Analyze(Builder.File(Builder.MainBlock(extra: 4), Builder.Head(), Builder.Comp(88200), Builder.Tail()));

        Assert.Empty(report.Findings);
        Node main = report.Root.Children.Single(c => c.Name == "OptimFROG").Children.First(c => c.Name == "Main Block");
        Assert.Equal(4, main.Children.Single(c => c.Name == "Additional Fields").Length);
    }

    [Fact]
    public void ForcingTheParserOnDataWithNoBlockGivesAFinding()
    {
        byte[] data = new byte[300];
        new Random(5).NextBytes(data);
        data[0] = 0x80;

        Report report = Analyze(data, new AnalysisOptions { ForcedFormat = "OptimFROG" });

        Assert.Contains(report.Findings, f => f.Code == "optimfrog.main-block-missing" && f.Severity == Severity.Warning);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.Empty(report.Streams);
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    [Fact]
    public void ForcingTheParserOnAFileThatBeginsWithAnotherBlockGivesAFinding()
    {
        Report report = Analyze(Builder.File(Builder.Head(), Builder.Comp(88200)), new AnalysisOptions { ForcedFormat = "OptimFROG" });

        Single(report, "optimfrog.main-block-missing");
        Assert.Empty(report.Streams);
    }

    [Fact]
    public void BlocksPastTheRecordsAreCountedAndTheirCrcsCompared()
    {
        int count = Records.MaxBlocks + 10;
        byte[] data = new byte[Builder.MainBlock().Length + (count * 22)];
        Builder.MainBlock(count).CopyTo(data, 0);
        byte[] block = Builder.Comp(1, 0);
        for (int i = 0; i < count; i++)
        {
            block.CopyTo(data, Builder.MainBlock().Length + (i * 22));
        }

        data[^1] ^= 1;

        Report report = Analyze(data);

        Finding limit = Single(report, "optimfrog.blocks-limit");
        Assert.Contains($"{count + 0} blocks", limit.Message.Replace(",", string.Empty, StringComparison.Ordinal));
        Finding mismatch = Single(report, "optimfrog.block-crc-mismatch");
        Assert.Contains($"data block {count - 1} ", mismatch.Message);
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        Assert.Equal(false, report.Streams.Single().Verification!.IsVerified);
        Node stream = report.Root.Children.Single(c => c.Name == "OptimFROG");
        Assert.Equal(Records.MaxBlocks, stream.Children.Count(c => c.Name == "Data Block"));
    }

    // The stored WAV header.

    [Fact]
    public void ARiffHeaderIsNamedAndItsChunksAreListed()
    {
        byte[] wav = Builder.Wav(beforeData: Builder.Chunk("LIST", "INFOabcd"u8.ToArray()));
        Report report = Analyze(Builder.File(Builder.MainBlock(), Builder.Head(wav), Builder.Comp(88200), Builder.Tail(Builder.Chunk("id3 ", new byte[11]))));

        Assert.Empty(report.Findings);
        Node stream = report.Root.Children.Single(c => c.Name == "OptimFROG");
        Node head = stream.Children.Single(c => c.Name == "Header Block").Children.Single(c => c.Name == "WAV Header Data");
        Assert.Contains("RIFF", head.Value!.ToString());
        Assert.Equal(["fmt ", "LIST", "data"], head.Children.Single(c => c.Name == "Chunks").Children.Select(c => c.Children[0].Value!.ToString()));
        Assert.Equal("id3 ", stream.Children.Single(c => c.Name == "Footer Block").Children.Single(c => c.Name == "WAV Terminating Data")
            .Children.Single().Children.Single().Children[0].Value!.ToString());
    }

    [Fact]
    public void AHeaderWhoseFormatStatesOtherChannelsIsReported()
    {
        Report report = Analyze(Builder.File(Builder.MainBlock(), Builder.Head(Builder.Wav(channels: 1)), Builder.Comp(88200), Builder.Tail()));

        Finding finding = Single(report, "optimfrog.wav-channels-differ");
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    [Fact]
    public void AChannelMaskThatNamesOtherSpeakersThanTheChannelsIsReported()
    {
        byte[] wav = Builder.Wav(format: Builder.Format(2, 44100, tag: 0xFFFE, mask: 0x3F));
        Report report = Analyze(Builder.File(Builder.MainBlock(), Builder.Head(wav), Builder.Comp(88200), Builder.Tail()));

        Finding finding = Single(report, "optimfrog.channel-mask-mismatch");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("stereo (front left, front right)", report.Streams.Single().ChannelLayout is { } layout ? ChannelLayouts.Describe(layout) : null);
    }

    [Fact]
    public void AChannelMaskSetsTheLayout()
    {
        byte[] wav = Builder.Wav(format: Builder.Format(2, 44100, tag: 0xFFFE, mask: 0x600));
        Report report = Analyze(Builder.File(Builder.MainBlock(), Builder.Head(wav), Builder.Comp(88200), Builder.Tail()));

        Assert.Empty(report.Findings);
        Assert.Contains("side", ChannelLayouts.Describe(report.Streams.Single().ChannelLayout!));
    }

    [Theory]
    [InlineData("cut-in-chunk")]
    [InlineData("no-format")]
    [InlineData("format-short")]
    [InlineData("extensible")]
    public void ADamagedStoredHeaderIsReportedWithoutStoppingTheWalk(string problem)
    {
        byte[] wav = problem switch
        {
            "cut-in-chunk" => [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, .. "LIST"u8, 100, 0, 0, 0, 1, 2],
            "no-format" => [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, .. "data"u8, 0, 0, 0, 0],
            "format-short" => [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, .. "fmt "u8, 8, 0, 0, 0, 1, 0, 2, 0, 0, 0, 0, 0],
            _ => Builder.Wav(format: Builder.Format(2, 44100, tag: 0xFFFE, cbSize: 4)),
        };

        Report report = Analyze(Builder.File(Builder.MainBlock(), Builder.Head(wav), Builder.Comp(88200), Builder.Tail()));

        Finding finding = Single(report, "optimfrog.wav-header-damaged");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(1, report.Streams.Single().Verification!.Checksums[0].Checked);
    }

    [Fact]
    public void AHeaderThatIsNoRiffFileIsShownAsBytes()
    {
        Report report = Analyze(Builder.File(Builder.MainBlock(), Builder.Head(new byte[44]), Builder.Comp(88200), Builder.Tail()));

        Assert.Empty(report.Findings);
        Node head = report.Root.Children.Single(c => c.Name == "OptimFROG").Children.Single(c => c.Name == "Header Block").Children.Single(c => c.Name == "WAV Header Data");
        Assert.Empty(head.Children);
    }

    [Fact]
    public void LossyWavsFactTextNamesTheProcessor()
    {
        byte[] fact = Builder.Chunk("fact", [.. "lossyWAV 1.4.2 @ 08/11/2026 08:37:19, --quality standard\r\n"u8, 0, 0]);
        byte[] wav = Builder.Wav(beforeData: fact);
        Report report = Analyze(Builder.File(Builder.MainBlock(), Builder.Head(wav), Builder.Comp(88200), Builder.Tail()));

        Assert.Empty(report.Findings);
        StreamInfo stream = report.Streams.Single();
        Assert.Equal("lossyWAV 1.4.2", stream.Processor);
        Assert.Equal(Confidence.High, stream.ProcessorConfidence);
    }

    [Fact]
    public void AFactTextOfNoKnownFormIsReported()
    {
        byte[] fact = Builder.Chunk("fact", [.. "lossyWAV is a program"u8, 0, 0]);
        Report report = Analyze(Builder.File(Builder.MainBlock(), Builder.Head(Builder.Wav(beforeData: fact)), Builder.Comp(88200), Builder.Tail()));

        Single(report, "optimfrog.header-fact-text-unrecognized");
    }

    // The writer's claims from tags.

    [Fact]
    public void ADisagreeingWriterClaimIsReported()
    {
        byte[] file = Builder.Standard();

        Report report = Analyze([.. TestFiles.Id3v23(("TSSE", "Lavf60.16.100")), .. file, .. TestFiles.ApeText("Encoder", "LAME 3.100")]);

        Assert.Contains(report.Findings, f => f.Code == "optimfrog.writer-claim-differs");
    }
}
