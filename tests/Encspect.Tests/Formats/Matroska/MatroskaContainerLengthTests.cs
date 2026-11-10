using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// "Container Samples" of a Matroska track is the length its blocks' timestamps give: a laced block holds several frames, so the last block's
// step is its frames' share, not an average block.
public class MatroskaContainerLengthTests
{
    private static long? ContainerSamples(Node node)
    {
        if (node.Name == "Container Samples")
        {
            return node.Value?.AsInteger();
        }

        return node.Children.Select(ContainerSamples).FirstOrDefault(v => v is not null);
    }

    // mkvmerge laces ALAC as 8, 8, 8 and 1 frames in four blocks: the average step of a block put the length 31 % past the 100,000 samples.
    [Theory]
    [InlineData("alac/external-mkvmerge-16-stereo.mka", 100000)]
    [InlineData("alac/external-mkvmerge-24-stereo.mka", 60000)]
    [InlineData("alac/external-mkvmerge-16-6ch.mka", 30000)]
    public void ALacedTrackHasTheLengthOfItsFramesToTheTimestampPrecision(string fixture, long samples)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        Report report = Analyzer.CreateDefault().Analyze("laced.mka", new MemoryStream(file));

        Assert.Equal(samples, Assert.Single(report.Streams).Samples);
        long container = ContainerSamples(report.Root) ?? throw new InvalidOperationException("no Container Samples");
        // The last block's frame is whole in the container's count, though the source ends inside it: up to one ALAC frame of 4,096 samples more.
        Assert.InRange(container, samples - 100, samples + 4096);
    }

    // The container's own figure: the CodecDelay and the DiscardPadding are the codec's trims, which Container Samples does not take off.
    [Fact]
    public void ContainerSamplesTakeOffNoCodecTrims()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/external-ffmpeg-encoding-tool.mka");

        long samples = Assert.Single(report.Streams).Samples;
        long container = ContainerSamples(report.Root) ?? throw new InvalidOperationException("no Container Samples");
        Assert.True(container > samples + 300, $"Container Samples {container} should keep the pre-skip that Samples {samples} drops");
    }

    // STREAMINFO in a Matroska track is the codec's header: the FLAC statistics carry no Container Samples beside the container's.
    [Fact]
    public void AFlacTrackHasOneContainerSamples()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/external-ffmpeg-flac.mka");

        Assert.Single(Count(report.Root, "Container Samples"));
    }

    private static IEnumerable<Node> Count(Node node, string name) =>
        (node.Name == name ? [node] : Enumerable.Empty<Node>()).Concat(node.Children.SelectMany(c => Count(c, name)));
}
