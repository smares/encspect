using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// MPEG audio that a container hands over as packets (MP4 samples in several stretches, Matroska blocks, the payloads of IEC 61937 bursts) is read as the same bytes in a file are:
// the frames, the frame count and the decoded audio equal those of the stream's own file, and the container's bytes between the packets are no part of it.
public class MpegPacketRouteTests
{
    private static (Report Report, List<PcmHashSink> Sinks) Decode(string name)
    {
        var sinks = new List<PcmHashSink>();
        var options = new AnalysisOptions
        {
            Decoding = new DecodeOptions
            {
                Output = stream =>
                {
                    var sink = new PcmHashSink();
                    sink.Format(stream.Format);
                    sinks.Add(sink);
                    return sink;
                },
            },
        };
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        return (Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options), sinks);
    }

    [Theory]
    [InlineData("matroska/external-ffmpeg-mp2.mka", "mpeg-audio/external-ffmpeg-mp2.mp2")]
    [InlineData("matroska/external-ffmpeg-mp3.mka", "mpeg-audio/external-ffmpeg-cbr.mp3")]
    [InlineData("matroska/external-mkvmerge-mp3.mka", "mpeg-audio/external-ffmpeg-cbr.mp3")]
    public void ATrackDecodesAsItsOwnFileDoes(string container, string file)
    {
        (Report inContainer, List<PcmHashSink> routed) = Decode(container);
        (Report alone, List<PcmHashSink> own) = Decode(file);

        StreamInfo packets = Assert.Single(inContainer.Streams);
        StreamInfo bytes = Assert.Single(alone.Streams);
        Assert.Equal((bytes.Codec, bytes.SampleRate, bytes.Channels, bytes.Samples), (packets.Codec, packets.SampleRate, packets.Channels, packets.Samples));
        Assert.Equal(DecodeStatus.Decoded, packets.Verification!.Decoding);
        Assert.Equal(own[0].Samples, routed[0].Samples);
        Assert.Equal(own[0].Hash(), routed[0].Hash());
    }

    // Two tracks of the same audio, whose frames alternate in the file: each reads as the stream, and they decode alike.
    [Fact]
    public void TheStretchesOfTwoInterleavedTracksAreTwoStreams()
    {
        (Report report, List<PcmHashSink> sinks) = Decode("mp4/external-ffmpeg-mp3-two-tracks-in-mp4.mp4");

        Assert.Equal(2, report.Streams.Count);
        Assert.All(report.Streams, s => Assert.Equal(21 * 1152, s.Samples));
        Assert.Equal(sinks[0].Hash(), sinks[1].Hash());
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("mpeg-audio.", StringComparison.Ordinal) && f.Severity >= Severity.Warning);
        TestFiles.CheckInsideParents(report.Root);
    }
}
