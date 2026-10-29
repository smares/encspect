using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The decision whether an ID3v2.4 tag stores plain frame sizes, and whether a tag that sets the
// extended header flag starts with a frame (docs/tags/id3.md); the fixtures cover the main cases.
public class Id3v2FrameSizeTests
{
    private static byte[] Syncsafe(int value) => [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    private static byte[] Frame(string id, int length, bool plain, byte fill = (byte)'a')
    {
        byte[] size = plain ? [(byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length] : Syncsafe(length);
        return [.. Encoding.ASCII.GetBytes(id), .. size, 0, 0, .. Enumerable.Repeat(fill, length)];
    }

    private static byte[] Tag(int major, byte flags, byte[] body, int? declared = null) =>
        [.. "ID3"u8.ToArray(), (byte)major, 0, flags, .. Syncsafe(declared ?? body.Length), .. body];

    private static Report Analyze(byte[] data) => Analyzer.CreateDefault().Analyze("test.mp3", new MemoryStream(data));

    private static bool Has(Report report, string code) => report.Findings.Any(f => f.Code == code);

    private static string Codes(Report report) => string.Join(", ", report.Findings.Select(f => f.Code));

    [Fact]
    public void PlainSizesWithAnIdNoVersionDeclaresAreStillRead()
    {
        byte[] body = [.. Frame("ABCD", 300, plain: true), .. Frame("TPE1", 6, plain: true, 0), .. new byte[8]];
        Report report = Analyze(Tag(4, 0, body));

        Assert.True(Has(report, "id3v2.frame-size-not-syncsafe"), Codes(report));
        Assert.False(Has(report, "id3v2.frame-id-invalid"), Codes(report));
    }

    [Fact]
    public void SynchsafeTagWithGarbageAfterTheFramesStaysSynchsafe()
    {
        byte[] body = [.. Frame("TIT2", 130, plain: false), .. new byte[] { 0, 0, 0, 0, 7, 0, 0, 0 }];
        Report report = Analyze(Tag(4, 0, body));

        Assert.False(Has(report, "id3v2.frame-size-not-syncsafe"));
        Assert.True(Has(report, "id3v2.padding-not-zero"));
    }

    [Fact]
    public void SynchsafeTagCutByTheEndOfTheFileStaysSynchsafe()
    {
        byte[] body = [.. Frame("TIT2", 7, plain: false), .. Frame("PRIV", 300, plain: false)];
        Report report = Analyze(Tag(4, 0, body, declared: body.Length + 100)[..(10 + 17 + 10 + 120)]);

        Assert.True(Has(report, "id3v2.truncated"));
        Assert.False(Has(report, "id3v2.frame-size-not-syncsafe"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(40)]
    public void TagsWithoutFramesAreSynchsafe(int padding)
    {
        Report report = Analyze(Tag(4, 0, new byte[padding]));

        Assert.False(Has(report, "id3v2.frame-size-not-syncsafe"));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void ExtendedHeaderFlagBeforeAnExperimentalFrameIsReported(int major)
    {
        byte[] body = [.. Frame("XTST", 20, plain: major == 3), .. new byte[8]];
        Report report = Analyze(Tag(major, 0x40, body));

        Assert.True(Has(report, "id3v2.extended-header-missing"));
    }

    [Fact]
    public void ExtendedHeaderFlagInVersion2MeansCompression()
    {
        byte[] body = [.. "TT2"u8.ToArray(), 0, 0, 3, 0, 0, 0, .. new byte[8]];
        Report report = Analyze(Tag(2, 0x40, body));

        Assert.False(Has(report, "id3v2.extended-header-missing"));
        Assert.True(Has(report, "id3v2.compressed-tag"));
    }

    [Fact]
    public void RealExtendedHeaderIsNotMistakenForAFrame()
    {
        byte[] extended = [0, 0, 0, 6, 0, 0, 0, 0, 0, 0];
        byte[] body = [.. extended, .. Frame("TIT2", 10, plain: false), .. new byte[4]];
        Report report = Analyze(Tag(3, 0x40, body));

        Assert.False(Has(report, "id3v2.extended-header-missing"));
    }
}
