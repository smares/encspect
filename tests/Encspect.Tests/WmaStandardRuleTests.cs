using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// The samples of standard Windows Media Audio streams: the rule fitted to the output of Microsoft's decoder in black-box runs
// (docs/development.md, "Differences found", standard WMA; the records of runs 41252306359, 41375872453, 41409006544 and 41412867847).
public class WmaStandardRuleTests
{
    public static TheoryData<string, long> WindowsFigures => new()
    {
        { "asf/external-windows-wma92-cbr-mono-44k-48.wma", 133632 },
        { "asf/external-windows-wma92-cbr-stereo-22k-20-sdk.wma", 66816 },
        { "asf/external-windows-wma92-cbr-stereo-22k-32.wma", 66816 },
        { "asf/external-windows-wma92-cbr-stereo-44k-128-sdk.wma", 133632 },
        { "asf/external-windows-wma92-cbr-stereo-44k-128.wma", 133632 },
        { "asf/external-windows-wma92-cbr-stereo-44k-192.wma", 133632 },
        { "asf/external-windows-wma92-cbr-stereo-44k-32-sdk.wma", 133632 },
        { "asf/external-windows-wma92-cbr-stereo-44k-48-sdk.wma", 133632 },
        { "asf/external-windows-wma92-cbr-stereo-44k-64.wma", 133632 },
        { "asf/external-windows-wma92-cbr-stereo-48k-128.wma", 145408 },
        { "asf/external-windows-wma92-vbr-stereo-44k-q10.wma", 133632 },
        { "asf/external-windows-wma92-vbr-stereo-44k-q25.wma", 133632 },
        { "asf/external-windows-wma92-vbr-stereo-44k-q75-tagged.wma", 90112 },
        { "asf/external-windows-wma92-vbr-stereo-44k-q75.wma", 133632 },
        { "asf/external-windows-wma92-vbr-stereo-44k-q90.wma", 133632 },
        { "asf/external-remux-ffmpeg-wma92-cbr-stereo-22k-20-sdk.wma", 66816 },
        { "asf/external-remux-ffmpeg-wma92-cbr-stereo-22k-32.wma", 66816 },
    };

    [Theory]
    [MemberData(nameof(WindowsFigures))]
    public void SamplesEqualTheFigureMicrosoftsDecoderGave(string name, long expected)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Node statistics = Child(stream.Nodes[0], "Statistics");

        Assert.Equal(expected, stream.Samples);
        Assert.Equal(expected, Child(statistics, "Samples").Value!.AsInteger());
        Assert.StartsWith("Counted by the rule", Child(statistics, "Samples").Description, StringComparison.Ordinal);
        Assert.Contains("Microsoft's decoder", Child(statistics, "Samples").Description, StringComparison.Ordinal);
        Assert.True(Child(statistics, "Coded Samples").Value!.AsInteger() <= expected);
    }

    // 8 kHz mono, one block of 512 samples per frame, a bit reservoir; every frame is the same, MonoFrame(p, 20, 3).
    private static readonly StdParameters P = StdParameters.Create(new AudioFormat(0x0161, 1, 8000, 1000, 400, 16), WmaStandardStreams.CodecData(2, 0x0003));

    private static byte[] Superframe(int index, int count, int continuationBits, WmaBitWriter body)
    {
        var w = new WmaBitWriter().Write((uint)((index << 4) | count), 8).Write((uint)continuationBits, P.BitOffsetBits);
        for (int i = 0; i < continuationBits; i++)
        {
            w.Write(0, 1);
        }

        byte[] data = new byte[400];
        w.ToBytes(400).CopyTo(data, 0);
        int at = 8 + P.BitOffsetBits + continuationBits;
        for (int i = 0; i < body.Length && at + i < 400 * 8; i++)
        {
            if (body[i])
            {
                data[(at + i) / 8] |= (byte)(0x80 >> ((at + i) % 8));
            }
        }

        return data;
    }

    private static WmaBitWriter Frames(int count, int coefficients = 3)
    {
        var all = new WmaBitWriter();
        for (int n = 0; n < count; n++)
        {
            WmaBitWriter frame = WmaStandardStreams.MonoFrame(P, 20, coefficients);
            for (int i = 0; i < frame.Length; i++)
            {
                all.Write(frame[i] ? 1u : 0u, 1);
            }
        }

        return all;
    }

    private static (WmaStandardStreams.Result Run, Node Statistics) Run(params byte[][] objects)
    {
        WmaStandardStreams.Result run = WmaStandardStreams.Run(2, 1, 8000, 1000, 400, WmaStandardStreams.CodecData(2, 0x0003), objects);
        Node stream = run.Parent.Children.Single(c => c.Name == "Windows Media Audio 2");
        return (run, Child(stream, "Statistics"));
    }

    private static Node Child(Node parent, string name) => parent.Children.Single(c => c.Name == name);

    private static long Value(Node node, string name) => Child(node, name).Value!.AsInteger()!.Value;

    // The superframe states three frames, begun in it; the third is whole in it. The valid frames are two (the analyzer defers the third),
    // and the rule counts three: (3 - 2) * 512.
    [Fact]
    public void TheLastAnnouncedFrameIsCountedWhenItIsWhole()
    {
        var (run, statistics) = Run(Superframe(0, 3, 0, Frames(3)));

        Assert.Equal(512, Value(statistics, "Samples"));
        Assert.Equal(512, Value(statistics, "Coded Samples"));
        Assert.StartsWith("Counted by the rule", Child(statistics, "Samples").Description, StringComparison.Ordinal);
        Finding finding = Assert.Single(run.Findings, f => f.Code == "wma.reservoir-at-end");
        Assert.Contains("announces a frame it holds whole", finding.Message, StringComparison.Ordinal);
    }

    // Four frames: the valid frames are three, the rule counts four.
    [Fact]
    public void EachFrameTheRuleCountsAddsItsFrameLength()
    {
        var (_, three) = Run(Superframe(0, 3, 0, Frames(3)));
        var (_, four) = Run(Superframe(0, 4, 0, Frames(4)));

        Assert.Equal(512, Value(four, "Samples") - Value(three, "Samples"));
    }

    // The same stream with the third frame cut by the end of the superframe: it is not counted, K is 2 and the rule gives (2 - 2) * 512.
    [Fact]
    public void AnAnnouncedFrameThatTheSuperframeCutsIsNotCounted()
    {
        // As many frames as the superframe holds whole, and one more of which it holds the start: the last announced frame is cut by the end.
        int each = WmaStandardStreams.MonoFrame(P, 20, 150).Length;
        int whole = ((400 * 8) - (8 + P.BitOffsetBits)) / each;
        Assert.InRange(whole + 1, 3, 15);
        var (_, statistics) = Run(Superframe(0, whole + 1, 0, Frames(whole + 1, 150)));

        Assert.Equal((whole - 2) * 512, Value(statistics, "Samples"));
        Assert.Equal((whole - 1) * 512, Value(statistics, "Coded Samples"));
    }

    [Fact]
    public void ASuperframeHeaderThatLeavesNoFrameFallsBackToTheFramesCount()
    {
        var (run, statistics) = Run(Superframe(0, 0, 0, Frames(0)), Superframe(1, 3, 0, Frames(3)));

        Assert.Contains(run.Findings, f => f.Code == "wma.superframe-header");
        Assert.StartsWith("The valid frames less the first", Child(statistics, "Samples").Description, StringComparison.Ordinal);
        Assert.DoesNotContain(statistics.Children, c => c.Name == "Coded Samples");
    }

    [Fact]
    public void AShortSuperframeFallsBackToTheFramesCount()
    {
        var (run, statistics) = Run(Superframe(0, 3, 0, Frames(3)), new byte[100]);

        Assert.Contains(run.Findings, f => f.Code == "wma.superframe-short");
        Assert.StartsWith("The valid frames less the first", Child(statistics, "Samples").Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidFrameFallsBackToTheFramesCount()
    {
        WmaBitWriter body = Frames(3);
        WmaBitWriter invalid = WmaStandardStreams.MonoFrame(P, 20, 0, -1, P.CoefficientEnds[0]);
        for (int i = 0; i < invalid.Length; i++)
        {
            body.Write(invalid[i] ? 1u : 0u, 1);
        }

        var (run, statistics) = Run(Superframe(0, 5, 0, body));

        Assert.Contains(run.Findings, f => f.Code == "wma.frame-invalid");
        Assert.StartsWith("The valid frames less the first", Child(statistics, "Samples").Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamWithoutABitReservoirKeepsTheFramesCount()
    {
        byte[] data = new byte[400];
        Array.Fill(data, (byte)'N');
        WmaBitWriter frame = WmaStandardStreams.MonoFrame(P, 20, 3);
        frame.ToBytes((frame.Length + 7) / 8).CopyTo(data, 0);
        WmaStandardStreams.Result run = WmaStandardStreams.Run(2, 1, 8000, 1000, 400, WmaStandardStreams.CodecData(2, 0x0001), [data, data, data]);
        Node stream = run.Parent.Children.Single(c => c.Name == "Windows Media Audio 2");

        Assert.Equal(2 * 512, Value(Child(stream, "Statistics"), "Samples"));
    }
}
