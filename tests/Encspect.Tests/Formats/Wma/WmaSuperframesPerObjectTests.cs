using Encspect.Core;

namespace Encspect.Tests.Formats.Wma;

// A media object holds one superframe or several whole ones of the block alignment's size, read in order (docs/design/wma-standard-format.md, "Superframes"): the files of
// Microsoft's version 1 encoder hold three or four in each. What is left after the last whole one is not a superframe.
public class WmaSuperframesPerObjectTests
{
    private const int Align = 100;

    private static byte[][] Superframes(int count, int seed)
    {
        var random = new Random(seed);
        return [.. Enumerable.Range(0, count).Select(_ =>
        {
            byte[] bytes = new byte[Align];
            random.NextBytes(bytes);
            return bytes;
        })];
    }

    private static byte[][] Joined(byte[][] objects, int per) =>
        [.. objects.Chunk(per).Select(c => c.SelectMany(b => b).ToArray())];

    private static WmaStandardStreams.Result Run(int options, IEnumerable<byte[]> objects, bool isLastCut = false) =>
        WmaStandardStreams.Run(2, 1, 8000, 1000, Align, WmaStandardStreams.CodecData(2, options), objects, isLastCut: isLastCut);

    // Without the bit reservoir and with it (a frame that spans objects continues in the next superframe of the same object): the objects joined read as the superframes alone.
    [Theory]
    [InlineData(0x0001, 2)]
    [InlineData(0x0003, 2)]
    [InlineData(0x0003, 3)]
    public void WholeSuperframesInOneObjectReadAsConsecutiveSuperframes(int options, int per)
    {
        byte[][] alone = Superframes(12, options + per);
        WmaStandardStreams.Result expected = Run(options, alone);
        WmaStandardStreams.Result joined = Run(options, Joined(alone, per));

        Assert.Equal(expected.Findings.Select(f => (f.Code, f.Count)), joined.Findings.Select(f => (f.Code, f.Count)));
        Assert.Equal(expected.Streams[0].Samples, joined.Streams[0].Samples);
        Assert.Equal(12, WmaStandardStreams.FindAll(joined.Parent, "Superframe").Count());
    }

    // The bytes after the last whole superframe are reported once, at that superframe, which lies a block alignment after the object's start.
    [Fact]
    public void TheBytesAfterTheLastWholeSuperframeAreFoundAtThatSuperframe()
    {
        byte[][] objects = [.. Joined(Superframes(2, 1), 2).Select(o => (byte[])[.. o, .. new byte[10]])];
        WmaStandardStreams.Result result = Run(0x0001, objects);

        Finding found = Assert.Single(result.Findings, f => f.Code == "wma.superframe-long");
        Assert.Equal(1000 + Align, found.Node!.Offset);
        Assert.DoesNotContain(result.Findings, f => f.Code == "wma.superframe-short");
    }

    // The rest of an object that was cut is a superframe that lost its end: it is read as a short one (the frames it declares are lost, the bit reservoir is emptied), not set aside.
    [Fact]
    public void TheRestOfACutObjectIsAShortSuperframe()
    {
        byte[][] objects = [.. Superframes(2, 5), .. Joined(Superframes(3, 6), 3).Select(o => o[..(2 * Align + 40)])];

        WmaStandardStreams.Result cut = Run(0x0003, objects, isLastCut: true);
        WmaStandardStreams.Result whole = Run(0x0003, objects);

        Assert.Contains(cut.Findings, f => f.Code == "wma.superframe-short");
        Assert.DoesNotContain(cut.Findings, f => f.Code == "wma.superframe-long");
        Assert.DoesNotContain(whole.Findings, f => f.Code == "wma.superframe-short");
        Assert.Contains(whole.Findings, f => f.Code == "wma.superframe-long");

        // The cut rest declares frames that are lost, and the reservoir ends empty: no bits are left at the end for a frame to be built from the cut bytes.
        Assert.True(cut.Streams[0].LostSamples > whole.Streams[0].LostSamples, $"{cut.Streams[0].LostSamples} against {whole.Streams[0].LostSamples}");
        Assert.DoesNotContain(cut.Findings, f => f.Code == "wma.reservoir-at-end");
    }
}
