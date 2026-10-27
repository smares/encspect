using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Object audio in E-AC-3 (ETSI TS 103 420): the addbsi extension, the EMDF containers in the auxiliary
// data and skip fields with their JOC and OAMD payloads, dec3's extension, and what each finding
// reports. The hand-built fixtures come from make_ac3_fixtures.py (build_object_audio).
public class ObjectAudioTests
{
    private static string? Value(Report report, string path) => Fixtures.Resolve(report.Root, path).FirstOrDefault()?.Value?.ToString();

    private static Report Analyze(string fixture) => Fixtures.AnalyzeMpeg($"ac3/{fixture}");

    [Fact]
    public void JocStreamHasObjectAudioAndItsObjectCount()
    {
        Report report = Analyze("joc-atmos.eac3");
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.True(stream.HasObjectAudio);
        Assert.Equal(15, stream.JocObjectCount);
        Assert.Empty(report.Findings);
        string json = Fixtures.ToJson(report);
        Assert.Contains("\"hasObjectAudio\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"jocObjectCount\": 15", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ac3/bsi-fields.ac3")]
    [InlineData("ac3/substreams.eac3")]
    [InlineData("ac3/external-ffmpeg-eac3-5.1-256k.eac3")]
    public void StreamsWithoutObjectAudioSayNothingOfIt(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.All(report.Streams, s =>
        {
            Assert.False(s.HasObjectAudio);
            Assert.Null(s.JocObjectCount);
        });
        string json = Fixtures.ToJson(report);
        Assert.DoesNotContain("hasObjectAudio", json, StringComparison.Ordinal);
        Assert.DoesNotContain("jocObjectCount", json, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Code.Contains("joc", StringComparison.Ordinal) || f.Code.Contains("emdf", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSummaryAndTheCsvCarryObjectAudio()
    {
        ReportSummary atmos = ReportSummary.From(Analyze("joc-atmos.eac3"));
        Assert.True(atmos.HasObjectAudio);
        Assert.Equal(15, atmos.JocObjectCount);
        ReportSummary plain = ReportSummary.From(Fixtures.AnalyzeMpeg("ac3/substreams.eac3"));
        Assert.False(plain.HasObjectAudio);
        Assert.Null(plain.JocObjectCount);

        var writer = new StringWriter();
        ReportCsvWriter.Write([Analyze("joc-atmos.eac3"), Fixtures.AnalyzeMpeg("ac3/substreams.eac3")], writer);
        string[] lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(",Has Object Audio,JOC Object Count,Is Protected,Is Verified,Protection Scheme,DRM Systems,Spectral Cutoff (Hz),Sample Peak (dBFS)", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(",true,15,false,,,,,", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(",false,,false,,,,,", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void AdditionalInfoGivesTheExtensionFlagAndTheComplexityIndex()
    {
        Report report = Analyze("joc-atmos.eac3");
        const string info = "AC-3/Frame[1]/Bit Stream Information/Additional Info";
        Assert.Equal("01 10", Value(report, info));
        Assert.Equal("0x00", Value(report, info + "/Reserved"));
        Assert.Equal("true", Value(report, info + "/HasExtensionTypeA"));
        Assert.Equal("16", Value(report, info + "/Complexity Index Type A"));
        Node flag = Fixtures.Resolve(report.Root, info + "/HasExtensionTypeA").Single();
        Assert.Equal(NodeValue.Boolean(true), flag.Value);
        Assert.Equal(1, flag.BitLength);
        Assert.Equal(NodeValue.Integer(16), Fixtures.Resolve(report.Root, info + "/Complexity Index Type A").Single().Value);
    }

    [Fact]
    public void AnEmdfContainerShowsItsFieldsAndPayloads()
    {
        Report report = Analyze("joc-atmos.eac3");
        const string skip = "AC-3/Frame[1]/Audio Block[2]/Skip Field";
        const string container = skip + "/EMDF Container";
        Assert.Equal("393 bytes", Value(report, skip));
        Assert.Equal("0x5838", Value(report, container + "/Sync Word"));
        Assert.Equal("389 bytes", Value(report, container + "/Container Length"));
        Assert.Equal("0", Value(report, container + "/Version"));
        Assert.Equal("0", Value(report, container + "/Key ID"));
        Assert.Equal("11 (object audio metadata)", Value(report, container + "/Payload[0]"));
        Assert.Equal("14 (joint object coding)", Value(report, container + "/Payload[1]"));
        Assert.Equal("53 bytes", Value(report, container + "/Payload[0]/Payload Size"));
        Assert.Equal("320 bytes", Value(report, container + "/Payload[1]/Payload Size"));
        Assert.Equal("0 (end of payloads)", Value(report, container + "/Payload ID"));
        Assert.Equal("2 (32 bits)", Value(report, container + "/Protection/Primary Protection Length"));
        Assert.Equal("1 (8 bits)", Value(report, container + "/Protection/Secondary Protection Length"));
        Assert.Equal("32 bits", Value(report, container + "/Protection/Primary Protection Bits"));
        Assert.Equal("8 bits", Value(report, container + "/Protection/Secondary Protection Bits"));

        const string config = container + "/Payload[1]/Payload Configuration";
        Assert.Equal("false", Value(report, config + "/HasSampleOffset"));
        Assert.Equal("false", Value(report, config + "/HasDuration"));
        Assert.Equal("true", Value(report, config + "/HasGroupId"));
        Assert.Equal("0", Value(report, config + "/Group ID"));
        Assert.Equal("false", Value(report, config + "/HasCodecData"));
        Assert.Null(Value(report, config + "/Codec Data"));
        Assert.Equal("false", Value(report, config + "/DiscardsUnknownPayload"));
        Assert.Equal("true", Value(report, config + "/IsFrameAligned"));
        Assert.Equal("false", Value(report, config + "/CreatesDuplicate"));
        Assert.Equal("false", Value(report, config + "/RemovesDuplicate"));
        Assert.Equal("0", Value(report, config + "/Priority"));
        Assert.StartsWith("0 (", Value(report, config + "/Processing Allowed"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheJocHeaderAndTheOamdObjectCountAreRead()
    {
        Report report = Analyze("joc-atmos.eac3");
        const string container = "AC-3/Frame[1]/Audio Block[2]/Skip Field/EMDF Container";
        Assert.Equal("3 (5.X with 90 degree phase shift)", Value(report, container + "/Payload[1]/JOC Header/Downmix Configuration"));
        Assert.Equal("15 objects", Value(report, container + "/Payload[1]/JOC Header/JOC Object Count"));
        Assert.Equal("0 (none)", Value(report, container + "/Payload[1]/JOC Header/Extension Configuration"));
        Assert.Equal("4", Value(report, container + "/Payload[1]/JOC Info/Clip Gain X Bits"));
        Assert.Equal("0", Value(report, container + "/Payload[1]/JOC Info/Clip Gain Y Bits"));
        Assert.Equal("1", Value(report, container + "/Payload[1]/JOC Info/Sequence Count"));
        Assert.Equal("0", Value(report, container + "/Payload[0]/OAMD Version"));
        Assert.Equal("16 objects", Value(report, container + "/Payload[0]/Object Count"));
    }

    [Fact]
    public void AContainerAfterTheLastBlockIsFoundInTheUnusedBits()
    {
        Report report = Analyze("joc-after-blocks.eac3");
        const string container = "AC-3/Frame[1]/Auxiliary Data/EMDF Container";
        Assert.Equal("147 bytes", Value(report, container + "/Container Length"));
        Assert.Equal("15 objects", Value(report, container + "/Payload[1]/JOC Header/JOC Object Count"));
        Assert.Equal("15 objects", Value(report, container + "/Payload[0]/Object Count"));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void ConfigurationOptionsKeysAndProtectionOfEveryLengthAreRead()
    {
        Report report = Analyze("emdf-payloads.eac3");
        const string first = "AC-3/Frame/Auxiliary Data/EMDF Container[0]";
        Assert.Equal("7", Value(report, first + "/Key ID"));
        Assert.Equal("2", Value(report, first + "/Key ID Extension"));
        Assert.Equal("true", Value(report, first + "/Payload[0]/Payload Configuration/HasCodecData"));
        Assert.Equal("0x5A", Value(report, first + "/Payload[0]/Payload Configuration/Codec Data"));
        Assert.Equal("1 (program loudness data)", Value(report, first + "/Payload[0]/Payload ID"));
        Assert.Equal("3 (E-AC-3 substream structure)", Value(report, first + "/Payload[1]/Payload ID"));
        Assert.Equal("31 (extended)", Value(report, first + "/Payload[2]/Payload ID"));
        Assert.Equal("5", Value(report, first + "/Payload[2]/Payload ID Extension"));
        Assert.Equal("6 (external data)", Value(report, first + "/Payload[3]/Payload ID"));
        Assert.Equal("300 samples", Value(report, first + "/Payload[1]/Payload Configuration/Sample Offset"));
        Assert.Equal("5000 samples", Value(report, first + "/Payload[1]/Payload Configuration/Duration"));
        Assert.Equal("3", Value(report, first + "/Payload[1]/Payload Configuration/Priority"));
        Assert.StartsWith("2 (", Value(report, first + "/Payload[1]/Payload Configuration/Processing Allowed"), StringComparison.Ordinal);
        Assert.Equal("true", Value(report, first + "/Payload[2]/Payload Configuration/DiscardsUnknownPayload"));
        Assert.Equal("3 (128 bits)", Value(report, first + "/Protection/Primary Protection Length"));
        Assert.Equal("2 (32 bits)", Value(report, first + "/Protection/Secondary Protection Length"));
        Assert.Equal("128 bits", Value(report, first + "/Protection/Primary Protection Bits"));
        Assert.Equal("32 bits", Value(report, first + "/Protection/Secondary Protection Bits"));
        Assert.Equal("14 (joint object coding)", Value(report, "AC-3/Frame/Auxiliary Data/EMDF Container[1]/Payload[1]/Payload ID"));
        Assert.Equal("3 objects", Value(report, "AC-3/Frame/Auxiliary Data/EMDF Container[1]/Payload[1]/JOC Header/JOC Object Count"));
        Assert.Equal("0 (5.X: left, right, center, left surround, right surround)", Value(report, "AC-3/Frame/Auxiliary Data/EMDF Container[1]/Payload[1]/JOC Header/Downmix Configuration"));
    }

    [Fact]
    public void StatisticsCountContainersAndFramesWithObjectAudio()
    {
        Report report = Analyze("joc-atmos.eac3");
        const string statistics = "AC-3/Statistics";
        Assert.Equal("4 containers", Value(report, statistics + "/EMDF Containers"));
        Assert.Equal("4 frames", Value(report, statistics + "/Frames With Object Audio"));
        Assert.Equal("4 frames", Value(report, statistics + "/Frames With Extension Type A"));
        Assert.Equal("15 objects", Value(report, statistics + "/JOC Object Count"));
        Assert.Equal("16", Value(report, statistics + "/Complexity Index Type A"));

        Report mismatch = Analyze("joc-flag-mismatch.eac3");
        Assert.Equal("3 frames", Value(mismatch, statistics + "/Frames With Object Audio"));
        Assert.Equal("3 frames", Value(mismatch, statistics + "/Frames With Extension Type A"));
        Assert.Null(Value(Fixtures.AnalyzeMpeg("ac3/substreams.eac3"), statistics + "/EMDF Containers"));
    }

    [Fact]
    public void Dec3GivesTheExtensionAndAgreesWithTheFrames()
    {
        Report report = Analyze("joc-dec3.mp4");
        const string dec3 = "MP4/moov/trak/mdia/minf/stbl/stsd/ec-3/dec3";
        Assert.Equal("true", Value(report, dec3 + "/HasExtensionTypeA"));
        Assert.Equal("16", Value(report, dec3 + "/Complexity Index Type A"));
        Assert.Empty(report.Findings);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.True(stream.HasObjectAudio);
        Assert.Equal(15, stream.JocObjectCount);
    }

    [Theory]
    [InlineData("joc-dec3-absent.mp4", "ac3.config-differs", Severity.Warning)]
    [InlineData("joc-dec3-clear.mp4", "ac3.config-differs", Severity.Warning)]
    [InlineData("joc-dec3-complexity.mp4", "ac3.config-differs", Severity.Warning)]
    [InlineData("joc-dec3-unmatched.mp4", "ac3.config-differs", Severity.Warning)]
    [InlineData("joc-dec3-unmatched.mp4", "ac3.config-reserved", Severity.Warning)]
    [InlineData("joc-flag-mismatch.eac3", "ac3.extension-without-joc", Severity.Warning)]
    [InlineData("joc-flag-mismatch.eac3", "ac3.joc-without-extension", Severity.Warning)]
    [InlineData("emdf-truncated.eac3", "ac3.emdf-truncated", Severity.Warning)]
    [InlineData("emdf-length-long.eac3", "ac3.emdf-length", Severity.Warning)]
    [InlineData("emdf-length-short.eac3", "ac3.emdf-length", Severity.Warning)]
    [InlineData("emdf-payload-size.eac3", "ac3.emdf-payload-size", Severity.Warning)]
    [InlineData("emdf-reserved.eac3", "ac3.emdf-reserved", Severity.Warning)]
    [InlineData("emdf-padding.eac3", "ac3.emdf-reserved", Severity.Warning)]
    [InlineData("emdf-version.eac3", "ac3.emdf-version-not-read", Severity.Info)]
    [InlineData("joc-reserved.eac3", "ac3.joc-reserved", Severity.Warning)]
    [InlineData("joc-short.eac3", "ac3.object-payload-short", Severity.Warning)]
    [InlineData("joc-incomplete.eac3", "ac3.object-audio-incomplete", Severity.Warning)]
    public void EachFindingIsReportedAtANodeOfTheTree(string fixture, string code, Severity severity)
    {
        Report report = Analyze(fixture);
        Finding finding = report.Findings.First(f => f.Code == code);
        Assert.Equal(severity, finding.Severity);
        Node node = report.Root;
        foreach (int index in finding.Path!)
        {
            node = node.Children[index];
        }

        Assert.Equal(finding.Node!.Name, node.Name);
        Assert.Equal((finding.Node.Offset, finding.Node.Length), (node.Offset, node.Length));
    }

    [Fact]
    public void AFlagInTheCoreAndTheDependentFrameIsJudgedOverTheAccessUnit()
    {
        Report both = Analyze("joc-flag-both.eac3");
        Assert.Empty(both.Findings);
        Assert.Equal("8 frames", Value(both, "AC-3/Statistics/Frames With Extension Type A"));
        Assert.Equal("4 frames", Value(both, "AC-3/Statistics/Frames With Object Audio"));
        Assert.Empty(Analyze("joc-after-blocks.eac3").Findings);
    }

    [Fact]
    public void FramesWhoseBlocksAreNotReadStillShowTheirContainerAndAgreeWithDec3()
    {
        Report report = Analyze("joc-aht.mp4");
        Assert.Equal(["ac3.aht-not-read"], report.Findings.Select(f => f.Code));
        Node statistics = Assert.Single(report.Streams).Nodes[0].Children.Single(c => c.Name == "Statistics");
        Assert.Equal("4 containers", statistics.Children.Single(c => c.Name == "EMDF Containers").Value?.ToString());
        Assert.Equal("4 frames", statistics.Children.Single(c => c.Name == "Frames With Object Audio").Value?.ToString());
        Assert.Equal(4, Assert.Single(report.Streams).JocObjectCount);
    }

    [Fact]
    public void RandomBytesAndLoneSyncWordsAreNoContainers()
    {
        Report report = Analyze("emdf-noise.eac3");
        Assert.Empty(report.Findings);
        Assert.False(Assert.Single(report.Streams).HasObjectAudio);
        Assert.Null(Value(report, "AC-3/Statistics/EMDF Containers"));
    }

    [Fact]
    public void ADamagedContainerKeepsTheOtherFramesObjectAudio()
    {
        Report report = Analyze("emdf-truncated.eac3");
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.True(stream.HasObjectAudio);
        Assert.Equal(4, stream.JocObjectCount);
        Assert.Equal("3 frames", Value(report, "AC-3/Statistics/Frames With Object Audio"));
    }

    [Fact]
    public void ATruncatedFrameInsideTheContainerIsAFrameFindingNotAnException()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ac3", "joc-atmos.eac3"));
        for (int cut = data.Length - 1; cut > data.Length - 700; cut -= 7)
        {
            Report report = Analyzer.CreateDefault().Analyze("cut", new MemoryStream(data[..cut]));
            Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        }
    }
}
