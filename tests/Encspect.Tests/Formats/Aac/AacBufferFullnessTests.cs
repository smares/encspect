using Encspect.Core;
using Encspect.Formats.Aac;

namespace Encspect.Tests;

// The buffer fullness of mixed ADTS headers, which only crafted input reaches.
public class AacBufferFullnessTests
{
    private static IEnumerable<Node> Descendants(Node node) => new[] { node }.Concat(node.Children.SelectMany(Descendants));

    private static Node Named(Node root, string name) => Descendants(root).First(n => n.Name == name);

    // AAC buffer fullness

    private static Node CoverAac(string name, NodeValue? value, NodeKind kind, string description) => new(name, 0, 10, value) { Kind = kind, Description = description };

    [Fact]
    public void Frames_that_signal_a_variable_bitrate_are_no_fullness_and_stay_out_of_the_series()
    {
        var figures = new StreamFigures(44100);
        int[] fullness = [100, StreamFigures.Variable, 300, StreamFigures.Variable, 200];
        for (int i = 0; i < fullness.Length; i++)
        {
            figures.Add(i * 100, 100, 1024);
            figures.SetFullness(fullness[i]);
        }

        Node statistics = figures.Statistics(CoverAac, "Frame", "Frames", out Node bitrate);
        List<Series> series = figures.Series(bitrate, CancellationToken.None);

        Assert.Equal("100 words per channel", Named(statistics, "Minimum Buffer Fullness").Value!.ToString());
        Assert.Equal("300 words per channel", Named(statistics, "Maximum Buffer Fullness").Value!.ToString());
        Assert.Equal(200.0, Named(statistics, "Average Buffer Fullness").Value!.AsDouble());
        Assert.Equal("2 frames", Named(statistics, "Frames Signaling Variable Bitrate").Value!.ToString());
        Series perFrame = series.Single(s => s.Name == "Buffer Fullness");
        Assert.Equal([100.0, 300.0, 200.0], perFrame.Values.ToArray());
        Assert.Equal([0.0, 200.0, 400.0], perFrame.X.ToArray());
        // All five frames start in the first second, and its mean leaves the two that state no fullness out.
        Series overTime = series.Single(s => s.Name == "Buffer Fullness Over Time");
        Assert.Equal([200.0], overTime.Values.ToArray());
    }

    [Fact]
    public void A_stream_whose_frames_all_signal_a_variable_bitrate_has_no_fullness_series()
    {
        var figures = new StreamFigures(44100);
        for (int i = 0; i < 3; i++)
        {
            figures.Add(i * 100, 100, 1024);
            figures.SetFullness(StreamFigures.Variable);
        }

        Node statistics = figures.Statistics(CoverAac, "Frame", "Frames", out Node bitrate);

        Assert.DoesNotContain(statistics.Children, c => c.Name.Contains("Buffer Fullness", StringComparison.Ordinal));
        Assert.Equal("3 frames", Named(statistics, "Frames Signaling Variable Bitrate").Value!.ToString());
        Assert.DoesNotContain(figures.Series(bitrate, CancellationToken.None), s => s.Name.StartsWith("Buffer Fullness", StringComparison.Ordinal));
    }
}
