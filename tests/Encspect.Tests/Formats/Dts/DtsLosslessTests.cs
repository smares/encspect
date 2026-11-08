using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The lossless extension's channel set sub-headers, its navigation index and the DTS:X signal after its band data, field by field
// (ETSI TS 102 114 8.3.2, 8.4.2; FFmpeg's dca_xll.c), for the hand-built fixtures; the real files are checked by check_dts_hd_samples.py.
public class DtsLosslessTests
{
    private const string Xll = "DTS/Frame[0]/Extension Substream/Asset 1 Data/XLL";

    private static string Field(Report report, string path) => Fixtures.Resolve(report.Root, path).FirstOrDefault()?.Value?.ToString() ?? "(missing)";

    private static Report Open(string name) => Fixtures.AnalyzeMpeg($"dts/{name}.dts");

    [Fact]
    public void EveryFieldOfAPrimarySetIsRead()
    {
        Report report = Open("xll-5-1-24bit-48k");
        string set = $"{Xll}/Channel Set 1";
        Assert.Equal("6 channels", Field(report, $"{set}/Channels"));
        Assert.Equal("0X3F", Field(report, $"{set}/Residual Channel Encoding").ToUpperInvariant());
        Assert.Equal("24 bits", Field(report, $"{set}/Source PCM Resolution"));
        Assert.Equal("24 bits", Field(report, $"{set}/Storage Bit Width"));
        Assert.Equal("48000 Hz", Field(report, $"{set}/Sampling Frequency"));
        Assert.Equal("1", Field(report, $"{set}/Sampling Frequency Interpolation"));
        Assert.StartsWith("0 (not a member", Field(report, $"{set}/Replacement Set"));
        Assert.Equal("true", Field(report, $"{set}/IsPrimaryChannelSet"));
        Assert.Equal("false", Field(report, $"{set}/HasDownmixCoefficients"));
        Assert.Equal("true", Field(report, $"{set}/IsHierarchicalChannelSet"));
        Assert.Equal("true", Field(report, $"{set}/HasChannelMask"));
        Assert.Equal("front center, front left, front right, back left, back right, LFE", Field(report, $"{set}/Channel Mask/Speakers"));
        Assert.Equal("verified", Field(report, $"{set}/Header CRC/Check"));
        Assert.Equal("verified", Field(report, $"{Xll}/Navigation Index/CRC/Check"));
        Assert.Equal("2 sizes", Field(report, $"{Xll}/Navigation Index/Band Data Sizes"));
    }

    // The primary set mixes to the channels its downmix type names; a set after it has a row for each channel before it, with a scale (9 bits
    // each): the 7.1 file of the FFmpeg archive has 12 and 18, as these do.
    [Fact]
    public void DownmixCoefficientsAreCountedAsFfmpegDoes()
    {
        Report report = Open("xll-7-1-24bit-48k");
        Assert.Equal("1 (Lo/Ro)", Field(report, $"{Xll}/Channel Set 1/Downmix Type"));
        Assert.Equal("12 coefficients", Field(report, $"{Xll}/Channel Set 1/Downmix Coefficients"));
        Assert.Equal("true", Field(report, $"{Xll}/Channel Set 1/IsDownmixEmbedded"));
        Assert.Equal("false", Field(report, $"{Xll}/Channel Set 2/IsPrimaryChannelSet"));
        Assert.Equal("18 coefficients", Field(report, $"{Xll}/Channel Set 2/Downmix Coefficients"));
        Assert.Equal("back left, back right", Field(report, $"{Xll}/Channel Set 2/Channel Mask/Speakers"));
        Assert.EndsWith("side left, side right", Field(report, $"{Xll}/Channel Set 1/Channel Mask/Speakers"), StringComparison.Ordinal);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal((8, 24), (stream.Channels, stream.BitsPerSample));
        Assert.Equal("7.1", stream.ChannelLayoutName);
    }

    [Theory]
    [InlineData("xll-2-0-24bit-96k", 96000, 1, 2, "1024 samples", "96000 Hz")]
    [InlineData("xll-2-0-24bit-192k", 192000, 2, 2, "512 samples", "192000 Hz")]
    [InlineData("xll-7-1-24bit-96k", 96000, 1, 8, "1024 samples", "96000 Hz")]
    public void TheRateAndTheBandsOfTheSets(string name, int rate, int bands, int channels, string perSegment, string statistic)
    {
        Report report = Open(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal((rate, channels, 24), (stream.SampleRate, stream.Channels, stream.BitsPerSample));
        Assert.Equal(statistic, Field(report, "DTS/Statistics/Lossless Sampling Frequency"));
        Node? frequencyBands = Fixtures.Resolve(report.Root, $"{Xll}/Channel Set 1/Frequency Bands").FirstOrDefault();
        Assert.Equal(bands > 1 ? "2 bands" : null, frequencyBands?.Value?.ToString());
        Assert.NotEqual("(missing)", Field(report, $"{Xll}/Samples per Segment"));
        _ = perSegment;
    }

    [Theory]
    [InlineData("xll-2-0-16bit-48k", 16, 16)]
    [InlineData("xll-2-0-20bit-48k", 20, 24)]
    public void TheResolutionIsTheBitDepthAndTheWidthIsTheStorage(string name, int resolution, int width)
    {
        Report report = Open(name);
        Assert.Equal(resolution, Assert.Single(report.Streams).BitsPerSample);
        Assert.Equal($"{resolution} bits", Field(report, "DTS/Statistics/Lossless Source PCM Resolution"));
        Assert.Equal($"{width} bits", Field(report, "DTS/Statistics/Lossless Storage Bit Width"));
    }

    // 8.3.2 Table 8-6 and 8-7: the fields are read and shown; ffmpeg refuses them, the analyzer does not.
    [Fact]
    public void InterpolationAndReplacementSetsAreShown()
    {
        Report interpolated = Open("xll-interpolated");
        Assert.Equal("2", Field(interpolated, $"{Xll}/Channel Set 1/Sampling Frequency Interpolation"));
        Report replacement = Open("xll-replacement-set");
        Assert.Equal("1 (member of replacement set 1)", Field(replacement, $"{Xll}/Channel Set 1/Replacement Set"));
        Assert.Equal("true", Field(replacement, $"{Xll}/Channel Set 1/IsActiveReplacementSet"));
    }

    [Fact]
    public void ASetThatIsNotALoudspeakerFeedHasNoMaskAndTheLayoutComesFromTheCore()
    {
        Report report = Open("xll-lt-rt");
        Assert.Equal("(missing)", Field(report, $"{Xll}/Channel Set 1/IsPrimaryChannelSet"));
        Assert.Equal("false", Field(report, $"{Xll}/Channel Set 1/HasMappingCoefficients"));
        Assert.Equal("stereo", Assert.Single(report.Streams).ChannelLayoutName);
        Report polar = Open("xll-no-mask");
        Assert.Equal("false", Field(polar, $"{Xll}/Channel Set 1/HasChannelMask"));
        Assert.Equal("2 speakers", Field(polar, $"{Xll}/Channel Set 1/Speaker Positions"));
    }

    // A set whose fields contradict each other, or whose CRC-16 fails, says nothing about the layer: the stream keeps the descriptor's figures.
    [Theory]
    [InlineData("xll-mask-count", "dts.lossless-inconsistent")]
    [InlineData("xll-resolution-over-width", "dts.lossless-inconsistent")]
    [InlineData("xll-set-bad-crc", "dts.crc-mismatch")]
    public void ADamagedSetIsNotUsedForTheStream(string name, string code)
    {
        Report report = Open(name);
        Assert.Contains(report.Findings, f => f.Code == code);
        Assert.Equal("(missing)", Field(report, "DTS/Statistics/Lossless Channels"));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(("stereo", 24), (stream.ChannelLayoutName, stream.BitsPerSample));
    }

    [Fact]
    public void TheFindingsPointAtTheFieldsTheyConcern()
    {
        string PathOf(string name, string code) => string.Join('/', Open(name).Findings.Single(f => f.Code == code).Path!.Select(i => i.ToString()));
        Report report = Open("xll-set-bad-crc");
        Finding crc = report.Findings.Single(f => f.Code == "dts.crc-mismatch");
        Assert.Equal("Header CRC", Walk(report.Root, crc.Path!).Name);
        Assert.Equal("Channel Mask", Walk(Open("xll-mask-count").Root, Open("xll-mask-count").Findings.Single(f => f.Code == "dts.lossless-inconsistent").Path!).Name);
        Assert.Equal("Source PCM Resolution", Walk(Open("xll-resolution-over-width").Root, Open("xll-resolution-over-width").Findings.Single(f => f.Code == "dts.lossless-inconsistent").Path!).Name);
        Assert.Equal("Downmix Type", Walk(Open("xll-downmix-type-7").Root, Open("xll-downmix-type-7").Findings.Single(f => f.Code == "dts.reserved-value").Path!).Name);
        Assert.Equal("Version", Walk(Open("xll-future-version").Root, Open("xll-future-version").Findings.Single(f => f.Code == "dts.reserved-value").Path!).Name);
        Assert.Equal("CRC", Walk(Open("xll-navigation-bad-crc").Root, Open("xll-navigation-bad-crc").Findings.Single(f => f.Code == "dts.crc-mismatch").Path!).Name);
        Assert.Equal("Frame Size", Walk(Open("xll-frame-size").Root, Open("xll-frame-size").Findings.Single(f => f.Code == "dts.lossless-inconsistent").Path!).Name);
        Assert.Equal("Channel Set 1", Walk(Open("xll-set-size").Root, Open("xll-set-size").Findings.Single(f => f.Code == "dts.substream-overrun").Path!).Name);
        Assert.NotEmpty(PathOf("xll-set-header-overrun", "dts.header-overrun"));
    }

    private static Node Walk(Node root, IReadOnlyList<int> path)
    {
        Node node = root;
        foreach (int index in path)
        {
            node = node.Children[index];
        }

        return node;
    }

    // dca_xll.c: from the end of the band data, aligned to 32 bits from the sync word, the word is DTS:X's or DTS:X IMAX's; MediaInfo reads
    // 0xF14000D1 as DTS:X. The profile names follow FFmpeg's, written out.
    [Theory]
    [InlineData("xll-x", "DTS-HD Master Audio + DTS:X", "0x02000850", true, false)]
    [InlineData("xll-x-imax", "DTS-HD Master Audio + DTS:X IMAX", "0xF14000D0", false, true)]
    [InlineData("xll-x-imax-d1", "DTS-HD Master Audio + DTS:X IMAX", "0xF14000D1", false, true)]
    [InlineData("xll-x-other-word", "DTS-HD Master Audio", "0x12345678", false, false)]
    public void TheWordAfterTheBandDataNamesDtsX(string name, string profile, string word, bool x, bool imax)
    {
        Report report = Open(name);
        Assert.Equal(profile, Assert.Single(report.Streams).Codec);
        Assert.Equal(profile, Field(report, "DTS/Statistics/Profile"));
        Assert.Equal(x ? "true" : "false", Field(report, "DTS/Statistics/HasDtsX"));
        Assert.Equal(imax ? "true" : "false", Field(report, "DTS/Statistics/HasDtsXImax"));
        if (x || imax)
        {
            Assert.Equal(word, Field(report, $"{Xll}/Extension Data/Sync Word").ToUpperInvariant().Replace("0X", "0x"));
        }
        else
        {
            // A word neither tool knows is bytes, not a sync word.
            Assert.Equal("(missing)", Field(report, $"{Xll}/Extension Data"));
            Assert.EndsWith("bytes", Field(report, $"{Xll}/Data After Band Data"), StringComparison.Ordinal);
        }

        Assert.False(Assert.Single(report.Streams).HasObjectAudio);
    }

    [Fact]
    public void AFrameWithoutRoomAfterItsBandDataHasNoExtensionData()
    {
        Report report = Open("xll-5-1-24bit-48k");
        Assert.Equal("(missing)", Field(report, $"{Xll}/Extension Data"));
        Assert.Equal("76 bytes", Field(report, $"{Xll}/Data After Band Data"));
        Assert.Equal("false", Field(report, "DTS/Statistics/HasDtsX"));
        Assert.Equal("DTS-HD Master Audio", Assert.Single(report.Streams).Codec);
    }

    // The common header's reserved bytes and a set's are skipped by their sizes, and the CRC-16 sits at the end of each.
    [Fact]
    public void ReservedBytesBeforeTheCrcAreShown()
    {
        Report report = Open("xll-header-padding");
        Assert.Equal("verified", Field(report, $"{Xll}/Header CRC/Check"));
        Assert.Equal("verified", Field(report, $"{Xll}/Channel Set 1/Header CRC/Check"));
        Assert.Contains("bits", Field(report, $"{Xll}/Reserved"));
        Assert.Empty(Open("xll-header-padding").Findings);
    }

    [Fact]
    public void TheScalableLeastSignificantBitsFlagAddsItsFields()
    {
        Report report = Open("xll-scalable-lsbs");
        Assert.Equal("true", Field(report, $"{Xll}/HasScalableLsbs"));
        Assert.Equal("0 bits", Field(report, $"{Xll}/Fixed LSB Width"));
        Assert.Empty(report.Findings);
    }

    // FFmpeg's hier_ofs counts the channels of the sets before one whether or not they are kept, so the second set of a 7.1 stream whose first set is rejected still has
    // its 18 downmix coefficients and reads its mask from the right bits.
    [Fact]
    public void ARejectedSetStillCountsInTheHierarchy()
    {
        Report report = Open("xll-7-1-hierarchy-after-rejected-set");
        Assert.Equal("18 coefficients", Field(report, $"{Xll}/Channel Set 2/Downmix Coefficients"));
        Assert.Equal("true", Field(report, $"{Xll}/Channel Set 2/HasChannelMask"));
        Assert.Equal("(missing)", Field(report, $"{Xll}/Channel Set 2/Speaker Positions"));
        Assert.Equal("(missing)", Field(report, "DTS/Statistics/Lossless Channels"));
    }

    // dca_xll.c returns at a navigation index whose CRC-16 fails before it compares the word; so does MediaInfo's XLL().
    [Fact]
    public void TheDtsXWordIsNotLookedForAfterAFailingNavigationIndex()
    {
        Report report = Open("xll-x-navigation-bad-crc");
        Assert.Equal("false", Field(report, "DTS/Statistics/HasDtsX"));
        Assert.Equal("(missing)", Field(report, $"{Xll}/Extension Data"));
        Assert.Equal("DTS-HD Master Audio", Assert.Single(report.Streams).Codec);
    }

    // parse_sub_headers refuses a channel set whose resolution is beyond its width before dca_xll.c looks for the word; the analyzer follows
    // FFmpeg here (MediaInfo 24.01 still reads the word).
    [Fact]
    public void TheDtsXWordIsNotLookedForAfterARejectedChannelSet()
    {
        Report report = Open("xll-x-rejected-set");
        Assert.Equal("false", Field(report, "DTS/Statistics/HasDtsX"));
        Assert.Equal("(missing)", Field(report, $"{Xll}/Extension Data"));
        Assert.Contains(report.Findings, f => f.Code == "dts.lossless-inconsistent");
    }

    // The description of a stream names no form and no extension substream figures, so the message says which of the two changed.
    [Fact]
    public void ANewStreamFormIsNamedInTheChangeMessage()
    {
        string directory = Path.Combine(Fixtures.FixtureDirectory, "dts");
        byte[] data = [.. File.ReadAllBytes(Path.Combine(directory, "core-16be.dts")), .. File.ReadAllBytes(Path.Combine(directory, "core-16le.dts"))];
        Report report = Analyzer.CreateDefault().Analyze("forms.dts", new MemoryStream(data));
        string message = report.Findings.Single(f => f.Code == "dts.stream-changed").Message;
        Assert.Contains("from DTS, 48000 Hz, 2/0 (16-bit words, big endian) to DTS, 48000 Hz, 2/0 (16-bit words, little endian)", message, StringComparison.Ordinal);
        Assert.DoesNotContain("extension substream", message, StringComparison.Ordinal);
    }

    // A 32-bit frame size field of 0xFFFFFFFF wraps to 0 once 1 is added; it is no size the common header accepts.
    [Theory]
    [InlineData(0, true)]
    [InlineData(-5, true)]
    [InlineData(int.MinValue, true)]
    [InlineData(1, false)]
    [InlineData(245760, false)]
    [InlineData(245761, true)]
    public void AFrameSizeOutsideOneToTheLimitIsRefused(int frameSize, bool expected) =>
        Assert.Equal(expected, Encspect.Formats.Dts.UnitSyntax.IsXllCommonRefused(1, frameSize, 0, 4));
}
