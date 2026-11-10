using Encspect.Core;

namespace Encspect.Tests;

// The stereo blocks of WavPack.
public class WavPackStereoBlocksTests
{
    private static IEnumerable<Node> Descendants(Node node) => new[] { node }.Concat(node.Children.SelectMany(Descendants));

    private static Node Named(Node root, string name) => Descendants(root).First(n => n.Name == name);

    // WavPack Stereo Coding

    [Theory]
    [InlineData("wavpack/external-joint-stereo-on.wv", 3, 3)]
    [InlineData("wavpack/external-lossless-51-16.wv", 4, 4)]
    public void The_stereo_blocks_of_every_set_are_counted_the_first_of_a_set_included(string name, int stereoBlocks, int midSide)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        Node coding = Named(report.Root, "Stereo Coding");
        Assert.Equal($"{stereoBlocks} stereo blocks", coding.Value!.ToString());
        Assert.StartsWith($"{midSide} stereo blocks", Named(coding, "Mid/side stereo").Value!.ToString(), StringComparison.Ordinal);
        Assert.Equal(stereoBlocks, Assert.Single(report.Streams).CategorySeries.Single(c => c.Name == "Stereo Coding").Total);
    }
}
