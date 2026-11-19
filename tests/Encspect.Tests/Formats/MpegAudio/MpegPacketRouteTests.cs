using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// MPEG audio that a container hands over as packets (MP4 samples in several stretches, Matroska blocks, the payloads of IEC 61937 bursts) is read as the same bytes in a file are:
// the frames, the frame count and the decoded audio equal those of the stream's own file, and the container's bytes between the packets are no part of it.
public class MpegPacketRouteTests
{
    private static (Report Report, List<PcmHashSink> Sinks) Decode(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        return Decode(Path.GetFileName(name), stream);
    }

    private static (Report Report, List<PcmHashSink> Sinks) Decode(string name, Stream stream)
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
        return (Analyzer.CreateDefault().Analyze(name, stream, options), sinks);
    }

    // The payloads of the bursts of an IEC 61937 file as the elementary stream they make: each payload's 16-bit words are swapped back from its own start (an odd payload
    // takes the byte after it, which is stuffing).
    private static byte[] Payloads(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), new MemoryStream(file));
        using var stream = new MemoryStream();
        foreach (Node burst in Fixtures.Resolve(report.Root, "RIFF/Data Chunk/IEC 61937 Bursts").Single().Children.Where(c => c.Name.StartsWith("Burst ", StringComparison.Ordinal)))
        {
            Node payload = burst.Children.Single(c => c.Name == "Burst Payload");
            byte[] bytes = new byte[payload.Length + (payload.Length & 1)];
            file.AsSpan((int)payload.Offset, bytes.Length).CopyTo(bytes);
            for (int i = 0; i + 1 < bytes.Length; i += 2)
            {
                (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
            }

            stream.Write(bytes, 0, FrameLength(bytes));
        }

        return stream.ToArray();
    }

    // The bytes of the frame a payload starts with (MPEG-1 and MPEG-2 layers II and III): a payload is as long as its words, which a frame of an odd length does not fill.
    private static int FrameLength(byte[] header)
    {
        int[] bitrates = (header[1] & 0x18) == 0x18 ? ((header[1] & 6) == 4 ? [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384] : [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320])
            : [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        int rate = new[] { 44100, 48000, 32000 }[(header[2] >> 2) & 3] >> ((header[1] & 0x18) == 0x18 ? 0 : 1);
        int slots = (header[1] & 0x18) != 0x18 && (header[1] & 6) == 2 ? 72 : 144;
        return (slots * 1000 * bitrates[header[2] >> 4] / rate) + ((header[2] >> 1) & 1);
    }

    // The bursts' payloads are the stream of an MP2 or MP3 file: the route reads them, at either parity of the data chunk's offset, as that file is read and decodes them to the same audio.
    [Theory]
    [InlineData("wav/external-iec61937-mp2-stereo-192k.wav", "es.mp2")]
    [InlineData("wav/external-iec61937-mp2-lsf-24000.wav", "es.mp2")]
    [InlineData("wav/external-iec61937-mp3-stereo-128k.wav", "es.mp3")]
    [InlineData("wav/built-iec61937-mp3-odd-data-offset.wav", "es.mp3")]
    public void TheBurstsOfAnIecFileDecodeAsTheirPayloadsDoAsAFile(string container, string name)
    {
        (Report inBursts, List<PcmHashSink> routed) = Decode(container);
        (Report alone, List<PcmHashSink> own) = Decode(name, new MemoryStream(Payloads(container)));

        StreamInfo packets = Assert.Single(inBursts.Streams);
        StreamInfo bytes = Assert.Single(alone.Streams);
        Assert.StartsWith("MPEG-", packets.Codec, StringComparison.Ordinal);
        Assert.Equal((bytes.Codec, bytes.SampleRate, bytes.Channels, bytes.Samples), (packets.Codec, packets.SampleRate, packets.Channels, packets.Samples));
        Assert.Equal(DecodeStatus.Decoded, packets.Verification!.Decoding);
        Assert.True(routed[0].Samples > 0);
        Assert.Equal(own[0].Samples, routed[0].Samples);
        Assert.Equal(own[0].Hash(), routed[0].Hash());
    }

    // Packets in which no frame is found are no stream, and the container says so: the carrier is not left as PCM with nothing said.
    [Fact]
    public void BurstsWhosePayloadsHoldNoFrameAreAFinding()
    {
        string name = "wav/external-iec61937-mp3-stereo-128k.wav";
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        Report report = Analyzer.CreateDefault().Analyze("x.wav", new MemoryStream(file));
        foreach (Node burst in Fixtures.Resolve(report.Root, "RIFF/Data Chunk/IEC 61937 Bursts").Single().Children.Where(c => c.Name.StartsWith("Burst ", StringComparison.Ordinal)))
        {
            Node payload = burst.Children.Single(c => c.Name == "Burst Payload");
            file.AsSpan((int)payload.Offset, (int)payload.Length).Fill(0x55);
        }

        Report damaged = Analyzer.CreateDefault().Analyze("x.wav", new MemoryStream(file));
        Finding finding = Assert.Single(damaged.Findings, f => f.Code == "mpeg-audio.packets-no-frame");
        Assert.Equal(Severity.Warning, finding.Severity);
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
