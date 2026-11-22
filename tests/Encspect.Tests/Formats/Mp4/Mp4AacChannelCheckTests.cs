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

    // The mp4a entry of a fixture with its entry version and that of its sample description box (0 or 1, where 1 is ISO/IEC 14496-12's AudioSampleEntryV1) and its channel count patched.
    private static Report Patched(string relativePath, ushort version, ushort channels)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, relativePath));
        // ISO's version 1 entry needs a sample description box of version 1 as well; with version 0 the entry is read as QuickTime's.
        file[file.AsSpan().IndexOf("stsd"u8) + 4] = (byte)version;
        int at = file.AsSpan().IndexOf("mp4a"u8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(at + 12), version);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(at + 20), channels);
        return Analyzer.CreateDefault().Analyze("patched.m4a", new MemoryStream(file));
    }

    private static Finding[] Differences(Report report) => [.. report.Findings.Where(f => f.Code == "mp4.channels-differ")];

    [Fact]
    public void AnAudioSampleEntryV1WhoseCountDiffersFromAPlainStreamsIsReported()
    {
        Finding finding = Assert.Single(Differences(Patched("aac/ffmpeg-lc.m4a", 1, 6)));
        Assert.Contains("must be correct (ISO/IEC 14496-12)", finding.Message);
        Assert.Contains("states 6 channels", finding.Message);
        Assert.Contains("has 2", finding.Message);
    }

    [Fact]
    public void AVersion0EntryOfMoreChannelsOverAStereoStreamIsNotReported()
    {
        Assert.Empty(Differences(Patched("aac/ffmpeg-lc.m4a", 0, 6)));
    }

    [Fact]
    public void AnAudioSampleEntryV1IsComparedWithTheTwoChannelsOfParametricStereoFoundInTheBlocks()
    {
        Finding finding = Assert.Single(Differences(Patched("mp4/implicit-ps.m4a", 1, 1)));
        Assert.Contains("has 2", finding.Message);
    }

    [Fact]
    public void AnAudioSampleEntryV1OverAMonoStreamWithSbrIsNotComparedBecauseItsOutputCountIsNotSettled()
    {
        Assert.Empty(Differences(Patched("aac/apple-he-m4a-cbr-mono44.m4a", 1, 3)));
    }

    [Fact]
    public void AStreamWhoseBlocksWereNotReadNamesTheImplicitSignalingItCouldNotCheck()
    {
        var config = new AacConfig { ObjectType = 2, CoreSampleRate = 44100, ChannelConfiguration = 1, Channels = 1, CoreFrameLength = 1024 };
        Assert.Contains("explicitly", config.Codec);
        Assert.Contains(" with SBR", (config with { ObjectType = 39, Sbr = true, Ps = false }).Codec);
    }
}
