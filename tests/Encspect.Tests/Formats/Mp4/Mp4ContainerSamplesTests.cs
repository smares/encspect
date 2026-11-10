using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// MP4 Container Samples and Container Duration.
public class Mp4ContainerSamplesTests
{
    private static IEnumerable<Node> Descendants(Node node) => new[] { node }.Concat(node.Children.SelectMany(Descendants));

    private static Node Named(Node root, string name) => Descendants(root).First(n => n.Name == name);

    // MP4 Container Samples

    // The statement is the mdhd duration in its timescale, counted at the stream's output rate: a timescale of half the rate doubles the units' seconds.
    [Fact]
    public void Container_samples_are_the_mdhd_length_at_the_stream_rate()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "ffmpeg-lc.m4a"));
        Report original = Analyzer.CreateDefault().Analyze("a.m4a", new MemoryStream(file));
        int mdhd = file.AsSpan().IndexOf("mdhd"u8);
        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(mdhd + 16));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(mdhd + 16), timescale * 2);

        Report changed = Analyzer.CreateDefault().Analyze("b.m4a", new MemoryStream(file));

        long before = Named(original.Root, "Container Samples").Value!.AsInteger()!.Value;
        Assert.Equal(before / 2.0, Named(changed.Root, "Container Samples").Value!.AsInteger()!.Value, 1);
    }

    // The timescale of an HE-AAC track is the core rate, but the figure is at the output rate that Samples counts at.
    [Fact]
    public void An_he_aac_track_counts_its_container_samples_at_the_output_rate()
    {
        Report report = Fixtures.AnalyzeMpeg("mp4/he-aac-mono.m4a");

        StreamInfo stream = Assert.Single(report.Streams);
        long container = Named(report.Root, "Container Samples").Value!.AsInteger()!.Value;
        Assert.InRange(container, stream.Samples - 2048, stream.Samples + 2048);
    }

    [Fact]
    public void A_fragmented_file_whose_mdhd_states_no_duration_has_no_container_length()
    {
        Report report = Fixtures.AnalyzeMpeg("mp4/fragmented.mp4");

        Assert.DoesNotContain(Descendants(report.Root), n => n.Name is "Container Duration" or "Container Samples");
    }
}
