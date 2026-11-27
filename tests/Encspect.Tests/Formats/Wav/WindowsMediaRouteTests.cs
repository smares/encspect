using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Formats.Wav;

// The Windows Media Audio tags in a WAVE file: the data chunk is cut into blocks of the block alignment and read by the parsers ASF and Matroska's A_MS/ACM route to.
public class WindowsMediaRouteTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "wav", name));

    private static Report Analyze(byte[] data) => Analyzer.CreateDefault().Analyze("wma.wav", new MemoryStream(data));

    [Theory]
    [InlineData("external-wmav1-in-wav.wav", "Windows Media Audio 1")]
    [InlineData("external-wmav2-in-wav.wav", "Windows Media Audio 2")]
    public void TheDataChunkIsReadByTheWindowsMediaAudioParser(string name, string codec)
    {
        Report report = Analyze(Fixture(name));

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(codec, stream.Codec);
        Assert.Equal(44100, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
        Assert.True(stream.Samples > 0);
        Assert.Empty(report.Findings);
        TestFiles.CheckInsideParents(report.Root);
    }

    [Fact]
    public void WithoutABlockAlignmentTheBlocksCannotBeCutAndTheCodingIsNotAnalyzed()
    {
        byte[] data = Fixture("external-wmav2-in-wav.wav");
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 0);

        Report report = Analyze(data);

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal("wav.coding-not-analyzed", finding.Code);
        Assert.Contains("block alignment", finding.Message, StringComparison.Ordinal);
    }
}
