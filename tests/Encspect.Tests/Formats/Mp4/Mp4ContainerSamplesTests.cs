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

    [Fact]
    public void Container_samples_need_a_media_timescale_that_is_the_sample_rate()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "ffmpeg-lc.m4a"));
        Report original = Analyzer.CreateDefault().Analyze("a.m4a", new MemoryStream(file));
        int mdhd = file.AsSpan().IndexOf("mdhd"u8);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(mdhd + 16), 22050);

        Report changed = Analyzer.CreateDefault().Analyze("b.m4a", new MemoryStream(file));

        Assert.Contains(Descendants(original.Root), n => n.Name == "Container Samples");
        Assert.Contains(Descendants(changed.Root), n => n.Name == "Container Duration");
        Assert.DoesNotContain(Descendants(changed.Root), n => n.Name == "Container Samples");
    }

    [Fact]
    public void A_fragmented_file_whose_mdhd_states_no_duration_has_no_container_length()
    {
        Report report = Fixtures.AnalyzeMpeg("mp4/fragmented.mp4");

        Assert.DoesNotContain(Descendants(report.Root), n => n.Name is "Container Duration" or "Container Samples");
    }
}
