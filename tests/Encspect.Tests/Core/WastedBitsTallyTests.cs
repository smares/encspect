using Encspect.Core;

namespace Encspect.Tests;

// The counts of wasted bits that FLAC and WavPack share, and the rule that reads lossyWAV's pattern from them.
public class WastedBitsTallyTests
{
    // One channel of frames of the given length whose count is counts[i % counts.Length] at frame i.
    private static WastedBitsTally Frames(int length, int frames, params int[] counts)
    {
        var tally = new WastedBitsTally();
        for (int i = 0; i < frames; i++)
        {
            tally.Add(0, (long)i * length, counts[i % counts.Length]);
        }

        return tally;
    }

    [Fact]
    public void ItCountsTheDistributionAndTheChangesOfEachChannel()
    {
        var tally = new WastedBitsTally();
        int[][] counts = [[4, 4, 5, 5, 5], [0, 1, 1, 0, 0]];
        for (int frame = 0; frame < 5; frame++)
        {
            for (int channel = 0; channel < 2; channel++)
            {
                tally.Add(channel, frame * 512L, counts[channel][frame]);
            }
        }

        Assert.Equal((10L, 7L, 3L), (tally.Units, tally.UnitsWithWastedBits, tally.CountOf(0)));
        Assert.Equal((2L, 3L, 2L, 0L), (tally.CountOf(4), tally.CountOf(5), tally.CountOf(1), tally.CountOf(2)));
        Assert.Equal(3L, tally.Changes);
    }

    [Fact]
    public void FramesOfTheBlockWithCountsThatChangeEveryFrameShowThePattern()
    {
        LossyWavPattern? pattern = Frames(512, 60, 4, 6, 5, 7).Pattern(44100);
        Assert.NotNull(pattern);
        Assert.Equal((59L, 59L, 512), (pattern.Changes, pattern.OnGrid, pattern.Grid));
        Assert.Contains("59 changes of the wasted bits between consecutive subframes of a channel, 100.0% of them at one phase of a grid of 512 samples", pattern.Evidence("subframes"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(44099)]
    [InlineData(0)]
    public void OnlyTheRatesTestedCount(int rate) => Assert.Null(Frames(512, 60, 4, 6, 5, 7).Pattern(rate));

    [Fact]
    public void FramesThatDoNotDivideTheBlockShowNothing()
    {
        Assert.Null(Frames(4096, 60, 4, 6, 5, 7).Pattern(44100));
        Assert.Null(Frames(11025, 60, 4, 6, 5, 7).Pattern(44100));
        Assert.Null(Frames(768, 60, 4, 6, 5, 7).Pattern(44100));
    }

    [Fact]
    public void FramesThatDivideTheBlockPutTheChangesOnOnePhaseOrNot()
    {
        // 256-sample frames at 44.1 kHz (a block of 512): changes at every frame lie on both phases, changes only at even frames on one.
        Assert.Null(Frames(256, 80, 4, 6, 5, 7).Pattern(44100));
        Assert.NotNull(Frames(256, 80, 4, 4, 6, 6, 5, 5, 7, 7).Pattern(44100));
    }

    [Fact]
    public void WhenHalfTheUnitsOrFewerAreAboveTheSmallestCountThereIsNoPattern()
    {
        Assert.Null(Frames(512, 80, 4, 4, 4, 5, 4, 4, 4, 5, 4, 5).Pattern(44100));
        Assert.NotNull(Frames(512, 80, 4, 5, 6, 5, 7).Pattern(44100));
    }

    [Fact]
    public void TwentyChangesAreNeeded()
    {
        Assert.Null(Frames(512, 20, 4, 6, 5, 7).Pattern(44100));
        Assert.NotNull(Frames(512, 21, 4, 6, 5, 7).Pattern(44100));
    }

    [Fact]
    public void ABlockOfSeveralFramesAndAGapKeepTheGridOfTheirStarts()
    {
        // WavPack's merge-blocks writes blocks of whole multiples of 512 samples; the start of each block is its index.
        var tally = new WastedBitsTally();
        long position = 0;
        for (int i = 0; i < 60; i++)
        {
            tally.Add(0, position, 4 + ((i * 3) % 5));
            position += 512 * (1 + (i % 3));
        }

        Assert.NotNull(tally.Pattern(48000));
        Assert.Null(tally.Pattern(96000 + 1));
    }

    [Fact]
    public void TheStatisticNeedsAWastedBitAndListsTheCountsThatOccurred()
    {
        var statistics = new Node("Statistics", 0, 10);
        Node Cover(string name, NodeValue? value, string description, string? topic) => new(name, 0, 10, value) { Description = description, Topic = topic };
        var wording = new WastedBitsWording("block", "blocks", "d", "d", "d");
        Assert.Null(Frames(512, 5, 0, 0).AddTo(statistics, Cover, wording, null));
        Assert.Empty(statistics.Children);

        Node node = Assert.IsType<Node>(Frames(512, 4, 0, 1, 1, 3).AddTo(statistics, Cover, wording, "wasted-bits"));
        Assert.Equal("4 blocks", node.Value!.ToString());
        Assert.Equal(["0 Bits", "1 Bit", "3 Bits", "Changes"], node.Children.Select(c => c.Name));
        Assert.Equal(["1 block (25.0%)", "2 blocks (50.0%)", "1 block (25.0%)", "2 changes"], node.Children.Select(c => c.Value!.ToString()));
    }
}
