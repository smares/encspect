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

    // mkvmerge laces ALAC as 8, 8, 8 and 1 frames in four blocks: the old average step of a block put the length 31 % past the 100,000 samples.
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
        Assert.InRange(container, samples - (samples / 50), samples + (samples / 50));
    }
}
