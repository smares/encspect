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

    public static TheoryData<string, long> WindowsCuts => new()
    {
        // Ends after frame 30 (B = 2,048, L = 512): min(B, L) is L, and the last block alone would give 512 samples more.
        { "wma-cuts/external-windows-wma92-cbr-stereo-44k-128-cut-30.wma", 60160 },
        // Ends after frame 36 (B = 512, L = 2,048): min(B, L) is B, and the next block size alone would give 768 samples less.
        { "wma-cuts/external-windows-wma92-cbr-stereo-44k-128-cut-36.wma", 72448 },
    };

    [Theory]
    [MemberData(nameof(WindowsCuts))]
    public void CutsEqualTheFigureMicrosoftsDecoderGave(string name, long expected) =>
        Assert.Equal(expected, Assert.Single(Fixtures.AnalyzeMpeg(name).Streams).Samples);

    [Theory]
    [MemberData(nameof(WindowsFigures))]
    public void SamplesEqualTheFigureMicrosoftsDecoderGave(string name, long expected)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Node statistics = Child(stream.Nodes[0], "Statistics");

        Assert.Equal(expected, stream.Samples);
        Assert.Equal(expected, Child(statistics, "Samples").Value!.AsInteger());
        Assert.StartsWith("The samples per channel Microsoft's decoder outputs", Child(statistics, "Samples").Description, StringComparison.Ordinal);
        Assert.Contains("Microsoft's decoder", Child(statistics, "Samples").Description, StringComparison.Ordinal);
        Assert.Equal(Child(statistics, "Frames").Value!.AsInteger() * Child(statistics, "Frame Length").Value!.AsInteger(), Child(statistics, "Coded Samples").Value!.AsInteger());
    }

    // 8 kHz mono, a frame of 512 samples, a bit reservoir and variable block lengths; every block is a whole frame (size index 0).
    private const int F = 512;
    private static readonly StdParameters P = StdParameters.Create(new AudioFormat(0x0161, 1, 8000, 1000, 400, 16), WmaStandardStreams.CodecData(2, 0x0007));
    private static readonly StdParameters Small = StdParameters.Create(new AudioFormat(0x0161, 1, 8000, 1000, 100, 16), WmaStandardStreams.CodecData(2, 0x0007));

    private static byte[] Superframe(StdParameters p, int align, int index, int count, WmaBitWriter body)
    {
        var w = new WmaBitWriter().Write((uint)((index << 4) | count), 8).Write(0, p.BitOffsetBits);
        byte[] data = new byte[align];
        w.ToBytes(align).CopyTo(data, 0);
        int at = 8 + p.BitOffsetBits;
        for (int i = 0; i < body.Length && at + i < align * 8; i++)
        {
            if (body[i])
            {
                data[(at + i) / 8] |= (byte)(0x80 >> ((at + i) % 8));
            }
        }

        return data;
    }

    private static byte[] Superframe(int index, int count, WmaBitWriter body) => Superframe(P, 400, index, count, body);

    // Frames of one block of a whole frame each, the first with the sizes a superframe's first frame states; the last block states lastNext
    // as the size index of the block after it.
    private static WmaBitWriter Frames(StdParameters p, int count, int coefficients = 3, int lastNext = 0)
    {
        var all = new WmaBitWriter();
        int width = p.SizeIndexBits;
        for (int n = 0; n < count; n++)
        {
            if (n == 0)
            {
                all.Write(0, width).Write(0, width);
            }

            all.Write((uint)(n == count - 1 ? lastNext : 0), width);
            WmaBitWriter frame = WmaStandardStreams.MonoFrame(p, 20, coefficients);
            for (int i = 0; i < frame.Length; i++)
            {
                all.Write(frame[i] ? 1u : 0u, 1);
            }
        }

        return all;
    }

    private static WmaBitWriter Frames(int count, int coefficients = 3, int lastNext = 0) => Frames(P, count, coefficients, lastNext);

    private static (WmaStandardStreams.Result Run, Node Statistics) Run(params byte[][] objects) => Run(P, 400, 0x0007, objects);

    private static (WmaStandardStreams.Result Run, Node Statistics) Run(StdParameters p, int align, int options, IEnumerable<byte[]> objects, int version = 2)
    {
        WmaStandardStreams.Result run = WmaStandardStreams.Run(version, 1, 8000, 1000, align, WmaStandardStreams.CodecData(version, options), objects);
        Node stream = run.Parent.Children.Single(c => c.Name == "Windows Media Audio " + version);
        return (run, Child(stream, "Statistics"));
    }

    private static Node Child(Node parent, string name) => parent.Children.Single(c => c.Name == name);

    private static long Value(Node node, string name) => Child(node, name).Value!.AsInteger()!.Value;

    private static void AssertFallback(Node statistics)
    {
        Assert.StartsWith("The valid frames less the first", Child(statistics, "Samples").Description, StringComparison.Ordinal);
        Assert.DoesNotContain(statistics.Children, c => c.Name == "Coded Samples");
    }

    private static void AssertNotCounted(WmaStandardStreams.Result run)
    {
        Finding finding = Assert.Single(run.Findings, f => f.Code == "wma.reservoir-at-end");
        Assert.DoesNotContain("counted in the samples", finding.Message, StringComparison.Ordinal);
    }

    // The superframe states three frames, begun in it; the third is whole in it. The valid frames are two (the analyzer defers the third),
    // and the rule counts three: (3 - 2) * 512.
    [Fact]
    public void TheLastAnnouncedFrameIsCountedWhenItIsWhole()
    {
        var (run, statistics) = Run(Superframe(0, 3, Frames(3)));

        Assert.Equal(F, Value(statistics, "Samples"));
        Assert.Equal(2 * F, Value(statistics, "Coded Samples"));
        Assert.StartsWith("The samples per channel Microsoft's decoder outputs", Child(statistics, "Samples").Description, StringComparison.Ordinal);
        Finding finding = Assert.Single(run.Findings, f => f.Code == "wma.reservoir-at-end");
        Assert.Contains("counted in the samples", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EachFrameTheRuleCountsAddsItsFrameLength()
    {
        var (_, three) = Run(Superframe(0, 3, Frames(3)));
        var (_, four) = Run(Superframe(0, 4, Frames(4)));

        Assert.Equal(F, Value(four, "Samples") - Value(three, "Samples"));
    }

    // The last block is a whole frame and states a block of half a frame after it: min(B, L) is L, so (F - F / 2) / 2 more. The last
    // block alone would add nothing, and the next block size alone the same as min here (the cut fixtures pin the case L above B).
    [Fact]
    public void TheSmallerOfTheLastBlockAndTheNextOneDecidesTheHalfBlockTerm()
    {
        var (_, statistics) = Run(Superframe(0, 3, Frames(3, lastNext: 1)));

        Assert.Equal(F + (F / 4), Value(statistics, "Samples"));
    }

    // As many frames as the superframe holds whole, and one more of which it holds the start: the last announced frame is cut by the end.
    [Fact]
    public void AnAnnouncedFrameThatTheSuperframeCutsIsNotCounted()
    {
        int each = Frames(1, 150).Length;
        int whole = ((400 * 8) - (8 + P.BitOffsetBits)) / each;
        Assert.InRange(whole + 1, 3, 15);
        var (run, statistics) = Run(Superframe(0, whole + 1, Frames(whole + 1, 150)));

        Assert.Equal((whole - 2) * F, Value(statistics, "Samples"));
        Assert.Equal(whole * F, Value(statistics, "Coded Samples"));
        AssertNotCounted(run);
    }

    [Fact]
    public void ASuperframeHeaderThatLeavesNoFrameFallsBackToTheFramesCount()
    {
        var (run, statistics) = Run(Superframe(0, 0, Frames(0)), Superframe(1, 3, Frames(3)));

        Assert.Contains(run.Findings, f => f.Code == "wma.superframe-header");
        AssertFallback(statistics);
        AssertNotCounted(run);
    }

    [Fact]
    public void AShortSuperframeFallsBackToTheFramesCountAndTheFindingSaysSo()
    {
        var (run, statistics) = Run(Superframe(0, 3, Frames(3)), new byte[100], Superframe(2, 3, Frames(3)));

        Assert.Contains(run.Findings, f => f.Code == "wma.superframe-short");
        AssertFallback(statistics);
        AssertNotCounted(run);
    }

    [Fact]
    public void ABitOffsetThatCannotBeHeldFallsBackToTheFramesCount()
    {
        byte[] bad = Superframe(Small, 100, 1, 2, Frames(Small, 2));
        bad[1] = 0xFF;
        bad[2] = 0xE0;
        var (run, statistics) = Run(Small, 100, 0x0007, [Superframe(Small, 100, 0, 3, Frames(Small, 3)), bad, Superframe(Small, 100, 2, 3, Frames(Small, 3))]);

        Assert.Contains(run.Findings, f => f.Code == "wma.bit-offset");
        AssertFallback(statistics);
    }

    [Fact]
    public void MediaObjectsPastTheRecordsLimitFallBackToTheFramesCount()
    {
        // Every superframe begins with the zero bits that finish the frame the one before announced, which read as a short frame.
        byte[] each = Superframe(Small, 100, 0, 3, Frames(Small, 3));
        var (_, within) = Run(Small, 100, 0x0007, Enumerable.Repeat(each, 10));
        var (run, beyond) = Run(Small, 100, 0x0007, Enumerable.Repeat(each, WmaStandardParser.MaxPackets + 2));

        Assert.StartsWith("The samples per channel Microsoft's decoder outputs", Child(within, "Samples").Description, StringComparison.Ordinal);
        Assert.Contains(run.Findings, f => f.Code == "wma.records-limit");
        AssertFallback(beyond);
    }

    [Fact]
    public void FewerThanTwoCompleteFramesFallBackToTheFramesCount()
    {
        int each = Frames(1, 150).Length;
        var (_, statistics) = Run(Small, 100, 0x0007, [Superframe(Small, 100, 0, 2, Frames(Small, 2, 150))]);

        Assert.True(each > 0);
        AssertFallback(statistics);
        Assert.Equal(0, Value(statistics, "Samples"));
    }

    [Fact]
    public void AnInvalidFrameFallsBackToTheFramesCount()
    {
        WmaBitWriter body = Frames(3);
        WmaBitWriter invalid = WmaStandardStreams.MonoFrame(P, 20, 0, -1, P.CoefficientEnds[0]);
        body.Write(0, P.SizeIndexBits);
        for (int i = 0; i < invalid.Length; i++)
        {
            body.Write(invalid[i] ? 1u : 0u, 1);
        }

        var (run, statistics) = Run(Superframe(0, 5, body));

        Assert.Contains(run.Findings, f => f.Code == "wma.frame-invalid");
        AssertFallback(statistics);
    }

    // The records back the rule for version 2 with variable block lengths only.
    [Fact]
    public void AStreamWithoutVariableBlocksFallsBack()
    {
        var plain = StdParameters.Create(new AudioFormat(0x0161, 1, 8000, 1000, 400, 16), WmaStandardStreams.CodecData(2, 0x0003));
        var w = new WmaBitWriter().Write(3, 8).Write(0, plain.BitOffsetBits);
        for (int n = 0; n < 3; n++)
        {
            WmaBitWriter frame = WmaStandardStreams.MonoFrame(plain, 20, 3);
            for (int i = 0; i < frame.Length; i++)
            {
                w.Write(frame[i] ? 1u : 0u, 1);
            }
        }

        var (_, statistics) = Run(plain, 400, 0x0003, [w.ToBytes(400)]);

        AssertFallback(statistics);
    }

    [Fact]
    public void AVersion1StreamWithAReservoirFallsBack()
    {
        var v1 = StdParameters.Create(new AudioFormat(0x0160, 1, 8000, 1000, 400, 16), WmaStandardStreams.CodecData(1, 0x0007));
        var w = new WmaBitWriter().Write(3, 8).Write(0, v1.BitOffsetBits);
        w.Write(0, v1.SizeIndexBits).Write(0, v1.SizeIndexBits);
        for (int n = 0; n < 3; n++)
        {
            w.Write(0, v1.SizeIndexBits);
            WmaBitWriter frame = WmaStandardStreams.MonoFrame(v1, 20, 3);
            for (int i = 0; i < frame.Length; i++)
            {
                w.Write(frame[i] ? 1u : 0u, 1);
            }
        }

        var (_, statistics) = Run(v1, 400, 0x0007, [w.ToBytes(400)], version: 1);

        AssertFallback(statistics);
    }

    [Fact]
    public void AStreamWithoutABitReservoirKeepsTheFramesCount()
    {
        var plain = StdParameters.Create(new AudioFormat(0x0161, 1, 8000, 1000, 400, 16), WmaStandardStreams.CodecData(2, 0x0001));
        byte[] data = new byte[400];
        Array.Fill(data, (byte)'N');
        WmaBitWriter frame = WmaStandardStreams.MonoFrame(plain, 20, 3);
        frame.ToBytes((frame.Length + 7) / 8).CopyTo(data, 0);
        var (_, statistics) = Run(plain, 400, 0x0001, [data, data, data]);

        Assert.Equal(2 * F, Value(statistics, "Samples"));
        AssertFallback(statistics);
    }
}
