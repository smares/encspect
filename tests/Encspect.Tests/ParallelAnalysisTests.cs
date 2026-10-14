using System.Collections.Concurrent;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// One analyzer, and so one instance of every parser, serves all threads (DefaultParsers), so
// parsers keep no per-call state; reports read on several threads at once build the same nodes.
[Collection("Thread hammers")]
public class ParallelAnalysisTests
{
    // One report written on eight threads at once (four, and fewer writes, on a pull request), by the JSON, text
    // and CSV writers mixed, with collections going on in the background, writes what one thread writes. One
    // fixture of each format and tag.
    [Theory]
    [InlineData("mpeg/external-blade-flags.mp3")]
    [InlineData("layer2/ancillary-not-multichannel.mp2")]
    [InlineData("tags/ape-131-collision.mp3")]
    [InlineData("adts/stereo.aac")]
    [InlineData("aac/ffmpeg-lc.m4a")]
    [InlineData("aac/fdkaac-he-v2.m4a")]
    [InlineData("aac/ffmpeg-fragmented.mp4")]
    [InlineData("ogg/external-ffmpeg-libvorbis.ogg")]
    [InlineData("ogg/external-ffmpeg-libopus.opus")]
    [InlineData("ogg/external-speexenc-wb-vbr.spx")]
    [InlineData("musepack/external-mpc2sv8.mpc")]
    [InlineData("musepack/external-mpcenc-standard.mpc")]
    [InlineData("wavpack/external-lossless-16-mono.wv")]
    [InlineData("asf/external-ffmpeg-wmav2-stereo-44k-128k.wma")]
    [InlineData("asf/external-windows-wma10pro-cbr-stereo-44k-64.wma")]
    [InlineData("asf/external-windows-wma92lossless-stereo-44k-16.wma")]
    [InlineData("asf/external-windows-wmavoice-cbr-mono-16k-16.wma")]
    [InlineData("ac3/external-ffmpeg-stereo-192k.ac3")]
    [InlineData("ac3/external-ffmpeg-eac3-5.1-256k.eac3")]
    [InlineData("ac3/external-ffmpeg-mp4-5.1-448k.mp4")]
    [InlineData("dts/external-ffmpeg-stereo-44k-256k.dts")]
    [InlineData("dts/external-ffmpeg-mp4-stereo-22k-192k.mp4")]
    public void OneReportWrittenOnSeveralThreadsAtOnce(string fixture)
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, fixture));
        string[] expected = [.. Enumerable.Range(0, 3).Select(w => ReportRendering.Write(report, w))];

        int threads = ThreadTestScope.Full ? 8 : 4;
        int writes = ThreadTestScope.Full ? 24 : 6;
        using var collector = new ReportRendering.Collector();
        string[] written = [.. Enumerable.Range(0, writes).AsParallel().AsOrdered().WithDegreeOfParallelism(threads).Select(i => ReportRendering.Write(report, i))];

        for (int i = 0; i < written.Length; i++)
        {
            Assert.True(written[i] == expected[i % 3], $"{fixture}: writer {i % 3} on thread {i} wrote something else. {ReportRendering.Differences(report, Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, fixture)))}");
        }
    }

    // A node's children are held weakly and built again when read after a collection, or by a second thread that
    // lost the race to publish them: building must give the same nodes every time (the DTS frames' CRC verdicts
    // were consumed by the first build, so a second one read "not checked").
    [Theory]
    [InlineData("xxch-bad-crc.dts")]
    [InlineData("xbr-bad-crc.dts")]
    [InlineData("xxch.dts")]
    public void DtsFrameNodesBuiltAgainMatchTheFirst(string name)
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, "dts", name));
        string expected = Fixtures.ToJson(report);

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Equal(expected, Fixtures.ToJson(report));
        }

        string[] written = Enumerable.Range(0, 16).AsParallel().WithDegreeOfParallelism(8).Select(_ =>
        {
            GC.Collect();
            return Fixtures.ToJson(report);
        }).ToArray();
        Assert.All(written, json => Assert.Equal(expected, json));
    }
}
