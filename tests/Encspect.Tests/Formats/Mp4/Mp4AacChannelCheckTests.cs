using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Host;

namespace Encspect.Tests;

// The MP4 parser decides whether an AAC stream is plain mono, and whether its blocks were not read, from the AAC parser's codec names (Mp4Parser.CompareAacChannels
// matches "SBR", "HE-AAC" and "explicitly"), because a parser may not reference another. These tests fail if the names stop carrying what that check relies on.
public class Mp4AacChannelCheckTests
{
    private static string CodecOf(string relativePath)
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, relativePath);
        using FileStream stream = File.OpenRead(path);
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(path), stream);
        return Assert.Single(report.Streams).Codec;
    }

    [Fact]
    public void APlainMonoStreamWhoseBlocksWereReadNamesNeitherSbrNorHeAacNorImplicitSignaling()
    {
        string codec = CodecOf("aac/sample-kit/external-itunes-aac-mono-64.m4a");
        Assert.Equal("AAC LC", codec);
        Assert.DoesNotContain("SBR", codec);
        Assert.DoesNotContain("explicitly", codec);
        Assert.False(codec.StartsWith("HE-AAC", StringComparison.Ordinal));
    }

    [Fact]
    public void AMonoStreamWithSbrNamesHeAac()
    {
        Assert.StartsWith("HE-AAC", CodecOf("aac/apple-he-m4a-cbr-mono44.m4a"), StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamWhoseBlocksWereNotReadNamesTheImplicitSignalingItCouldNotCheck()
    {
        var config = new AacConfig { ObjectType = 2, CoreSampleRate = 44100, ChannelConfiguration = 1, Channels = 1, CoreFrameLength = 1024 };
        Assert.Contains("explicitly", config.Codec);
    }
}
