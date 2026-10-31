using Encspect.Core;
using Encspect.Formats.Asf;
using Encspect.Formats.Wma;
using Encspect.Host;

namespace Encspect.Tests;

// Windows Media Audio Lossless: short and cut media objects, the bits that finish a frame whose start was lost, the end of the stream and the length
// prefix (docs/design/wma-lossless-format.md, "Media objects and packets", "Sequence numbers and loss", "Frame").
public class WmaLosslessPacketTests
{
    private const int Header = 23;

    private static CodecContext Run(Action<WmaStream> change, string fixture = WmaStream.Lossless)
    {
        WmaStream stream = WmaStream.LoadLossless(fixture);
        change(stream);
        return stream.RunLossless();
    }

    // The last media object of the 16-bit stereo fixture, cut 6,000 bytes in. A decoder has no minimum packet size and reads the bytes it has: ffmpeg
    // outputs 43 frames and 88,064 samples, where leaving the object out gives 42 frames and 86,016.
    [Fact]
    public void ACutLastPacketIsReadAsFarAsItGoes()
    {
        CodecContext context = Run(s => s.Objects[^1] = s.Objects[^1][..6000]);
        Assert.Equal(88_064, context.Streams[0].Samples);
        Assert.Equal("43", WmaStream.Statistic(WmaStream.StreamNode(context), "Frames"));
        Finding shortPacket = Assert.Single(context.Findings, f => f.Code == "wma.packet-short");
        Assert.Equal(Severity.Info, shortPacket.Severity);
        Assert.DoesNotContain("rejects", shortPacket.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(context.Findings, f => f.Code == "wma.packet-overread");
        Assert.Contains(context.Findings, f => f.Code == "wma.frame-invalid" && f.Message.Contains("it needs more bits than it has", StringComparison.Ordinal));
    }

    // The same through the ASF layer: the file ends 6,000 bytes into the last media object, which the layer hands to the Lossless parser after
    // its finding for the incomplete object.
    [Fact]
    public void AFileCutInsideTheLastMediaObjectIsReadToItsEnd()
    {
        (byte[] properties, _) = AsfBuilder.Take(WmaStream.Lossless, 0x0163);
        List<byte[]> objects = AsfBuilder.Objects(WmaStream.Lossless, 0x0163);
        int size = 23 + objects[0].Length;
        var packets = new List<byte[]>();
        for (int i = 0; i < objects.Count; i++)
        {
            byte[] packet = AsfBuilder.Packet(size, 1, i & 0xFF, 0, (uint)objects[i].Length, (uint)(i * 46));
            objects[i].CopyTo(packet, 23);
            packets.Add(packet);
        }

        byte[] file = AsfBuilder.Assemble(properties, size, packets);
        byte[] cut = file[..^(objects[^1].Length - 6000)];
        Report report = Analyzer.CreateDefault().Analyze("cut.wma", new MemoryStream(cut));
        Assert.Equal(88_064, Assert.Single(report.Streams).Samples);
        Finding incomplete = Assert.Single(report.Findings, f => f.Code == "asf.object-incomplete");
        Assert.Contains("handed to the codec's parser", incomplete.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }

    // A media object with fewer bits than the packet header is read past its end, which counts as a loss: the frames of the packet before it, read
    // when it arrives, are still read, and the packet after it follows the loss.
    [Fact]
    public void AMediaObjectShorterThanItsHeaderIsAnOverread()
    {
        CodecContext context = Run(s => s.Objects[5] = s.Objects[5][..2]);
        Finding finding = Assert.Single(context.Findings, f => f.Code == "wma.packet-overread");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("fewer bits than the 23", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(context.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }

    // Block alignment 1: every media object is shorter than its header (10 bits).
    [Fact]
    public void ObjectsOfOneByteGiveAnOverreadFinding()
    {
        List<byte[]> objects = [.. Enumerable.Range(0, 20).Select(i => new byte[] { (byte)(i << 4) })];
        CodecContext context = LosslessBuilder.Run(objects, 2, 1);
        Finding finding = Assert.Single(context.Findings, f => f.Code == "wma.packet-overread");
        Assert.Equal(20, finding.Count);
    }

    // Packet 2 of the 5.1 fixture states sequence number 3: the frames that start in packet 1 are dropped, and the bits that finish the frame
    // before in packet 3 are the end of a frame whose start was lost. A decoder reads them as a frame and outputs the same samples (ffmpeg: 5,568);
    // Encspect lists them as no frame.
    [Fact]
    public void TheBitsThatFinishALostFrameAreNoFrame()
    {
        CodecContext context = Run(s => s.Objects[2][0] = (byte)((s.Objects[2][0] & 0x0F) | 0x30), WmaStream.LosslessFive);
        Assert.Equal(5_568, context.Streams[0].Samples);
        Node frames = WmaStream.Child(WmaStream.StreamNode(context), "Frames");
        // The first frame listed is the first that starts after the gap, which cannot be read before a seekable subframe has come; the five bits
        // before it are not shown as an invalid frame whose tile header lays out subframes the stream cannot have.
        Assert.StartsWith("invalid", frames.Children[0].Value!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(context.Findings, f => f.Message.Contains("tile header", StringComparison.Ordinal));
        Assert.Contains(context.Findings, f => f.Code == "wma.frame-invalid" && f.Message.Contains("seekable subframe that has not come", StringComparison.Ordinal));
        Finding gap = Assert.Single(context.Findings, f => f.Code == "wma.packet-sequence");
        Assert.Contains("frames that start in the packet before it", gap.Message, StringComparison.Ordinal);
    }

    // After the first frame, a frame rejected for its end trim in the flush after the last packet outputs nothing and ends the reading: the frame
    // that follows it is not decoded.
    [Fact]
    public void ARejectedTrimEndsTheFlush()
    {
        var w = new LosslessBits();
        LosslessBuilder.SeekableFrame(w, 1);
        w.PutBit(true);
        // A frame of an end trim of F, which stops after its trim fields, and a frame of six bits after it.
        w.Put(1, 1);
        w.Put(1, 1);
        w.Put(1, 0);
        w.Put(1, 1);
        w.Put(12, 2048);
        LosslessBuilder.TinyFrame(w, 1);
        w.PutBit(false);

        const int blockAlignment = 100;
        int width = LosslessBuilder.WidthOf(blockAlignment);
        byte[] packet = new byte[blockAlignment];
        var header = new LosslessBits();
        header.Put(4, 0);
        header.Put(2, 0);
        header.Put(width, 0);
        long at = 0;
        for (long b = 0; b < header.Count; b++, at++)
        {
            Set(packet, at, header.Bit(b));
        }

        for (long b = 0; b < w.Count; b++, at++)
        {
            Set(packet, at, w.Bit(b));
        }

        CodecContext context = LosslessBuilder.Run([packet], 1, blockAlignment);
        Assert.Equal(2048, context.Streams[0].Samples);
        Assert.Equal("2", WmaStream.Statistic(WmaStream.StreamNode(context), "Frames"));
        Assert.Contains(context.Findings, f => f.Code == "wma.frame-invalid" && f.Message.Contains("trim at the end", StringComparison.Ordinal));
        Assert.DoesNotContain("reads no more frames", context.Findings.First(f => f.Code == "wma.frame-invalid").Message, StringComparison.Ordinal);
    }

    private static void Set(byte[] bytes, long bit, bool isSet)
    {
        if (isSet)
        {
            bytes[bit >> 3] |= (byte)(0x80 >> (int)(bit & 7));
        }
    }

    // The same stream where a later packet follows: the rejected frame is read in a call after the header and the frame after it is read from the
    // bit where its trim ended.
    [Fact]
    public void ARejectedTrimOutsideTheFlushReadsOn()
    {
        var w = new LosslessBits();
        LosslessBuilder.SeekableFrame(w, 1);
        w.PutBit(true);
        w.Put(1, 1);
        w.Put(1, 1);
        w.Put(1, 0);
        w.Put(1, 1);
        w.Put(12, 2048);
        LosslessBuilder.TinyFrame(w, 1);
        w.PutBit(false);
        // A second packet that states the count of the first frame's rest as 0 and holds a tiny frame, so the first packet's run is read when it arrives.
        const int blockAlignment = 100;
        int width = LosslessBuilder.WidthOf(blockAlignment);
        byte[] first = new byte[blockAlignment];
        byte[] second = new byte[blockAlignment];
        var header = new LosslessBits();
        header.Put(4, 0);
        header.Put(2, 0);
        header.Put(width, 0);
        long at = 0;
        for (long b = 0; b < header.Count; b++, at++)
        {
            Set(first, at, header.Bit(b));
        }

        for (long b = 0; b < w.Count; b++, at++)
        {
            Set(first, at, w.Bit(b));
        }

        var header2 = new LosslessBits();
        header2.Put(4, 1);
        header2.Put(2, 0);
        header2.Put(width, 0);
        at = 0;
        for (long b = 0; b < header2.Count; b++, at++)
        {
            Set(second, at, header2.Bit(b));
        }

        var tiny = new LosslessBits();
        LosslessBuilder.TinyFrame(tiny, 1);
        tiny.PutBit(false);
        for (long b = 0; b < tiny.Count; b++, at++)
        {
            Set(second, at, tiny.Bit(b));
        }

        CodecContext context = LosslessBuilder.Run([first, second], 1, blockAlignment);
        Assert.Equal("4", WmaStream.Statistic(WmaStream.StreamNode(context), "Frames"));
        Assert.Equal(3 * 2048L, context.Streams[0].Samples);
    }

    // Two ways to disagree with the frame length: a frame that runs past its stated length and one that ends short of it. A decoder checks both
    // the same way, and both count F less the end trim (100 here) when the frame is the first of its call.
    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void BothFailuresOfTheLengthCheckCountTheSamePrefixFrame(int delta)
    {
        Assert.Equal(1948, PrefixSamples(delta));
    }

    // A frame that fails the check after another frame of the packet returns nothing.
    [Fact]
    public void AFailingPrefixFrameAfterAnotherReturnsNoFrame()
    {
        Assert.Equal(1948, PrefixSamples(0, 5));
        Assert.Equal(2 * 1948, PrefixSamples(0, 0));
    }

    private static long PrefixSamples(params int[] deltas)
    {
        const int blockAlignment = 200;
        int width = LosslessBuilder.WidthOf(blockAlignment);
        var w = new LosslessBits();
        for (int i = 0; i < deltas.Length; i++)
        {
            PrefixFrame(w, width, deltas[i], i + 1 < deltas.Length);
        }

        byte[] packet = new byte[blockAlignment];
        var header = new LosslessBits();
        header.Put(4, 0);
        header.Put(2, 0);
        header.Put(width, 0);
        long at = 0;
        for (long b = 0; b < header.Count; b++, at++)
        {
            Set(packet, at, header.Bit(b));
        }

        for (long b = 0; b < w.Count; b++, at++)
        {
            Set(packet, at, w.Bit(b));
        }

        return LosslessBuilder.Run([packet], 1, blockAlignment, flags: 0x0041).Streams[0].Samples;
    }

    // Channel 0 has two cascaded filters and channel 1 one: the figure shows the range, not the floor of the average.
    [Fact]
    public void CascadedFiltersPerChannelShowTheRange()
    {
        var probe = new LosslessBits();
        LosslessBuilder.SeekableFrame(probe, 2, filters: [2, 1]);
        long bits = probe.Count + 1;
        List<byte[]> objects = LosslessBuilder.Pack(200, 1, _ => bits, (w, _) => LosslessBuilder.SeekableFrame(w, 2, filters: [2, 1]));
        CodecContext context = LosslessBuilder.Run(objects, 2, 200);
        Assert.Equal("1 to 2", WmaStream.Statistic(WmaStream.StreamNode(context), "Cascaded Filters per Channel"));
    }

    // The codec data's first byte is the sample size; the second is shown apart and not read.
    [Fact]
    public void TheSampleSizeNodeIsTheLowByte()
    {
        WmaStream stream = WmaStream.LoadLossless();
        stream.Data[1] = 0x01;
        CodecContext context = stream.RunLossless();
        Node size = Find(context.Parent, "Bits Per Sample");
        Assert.Equal(16L, size.Value!.AsInteger());
        Assert.Equal(1, size.Length);
        Node high = Find(context.Parent, "Sample Size High Byte");
        Assert.Equal(size.Offset + 1, high.Offset);
        Assert.Equal(1, high.Length);
    }

    // An object that is incomplete because the fragments that would finish it never arrive is dropped, as the ASF demuxer drops it; only one
    // that the file ends inside is handed over.
    [Fact]
    public void AnObjectWhoseFragmentsNeverArriveIsDropped()
    {
        (byte[] properties, _) = AsfBuilder.Take(WmaStream.Lossless, 0x0163);
        List<byte[]> objects = AsfBuilder.Objects(WmaStream.Lossless, 0x0163);
        int size = 23 + objects[0].Length;
        var packets = new List<byte[]>();
        for (int i = 0; i < objects.Count; i++)
        {
            uint stated = (uint)objects[i].Length + (i == objects.Count - 1 ? 500u : 0);
            byte[] packet = AsfBuilder.Packet(size, 1, i & 0xFF, 0, stated, (uint)(i * 46));
            objects[i].CopyTo(packet, 23);
            packets.Add(packet);
        }

        Report report = Analyzer.CreateDefault().Analyze("never.wma", new MemoryStream(AsfBuilder.Assemble(properties, size, packets)));
        Assert.Contains(report.Findings, f => f.Code == "asf.object-incomplete");
        Assert.Equal(86_016, Assert.Single(report.Streams).Samples);
    }

    private static Node Find(Node node, string name)
    {
        if (node.Name == name)
        {
            return node;
        }

        foreach (Node child in node.Children)
        {
            if (child.Name is "Frames" or "Packets")
            {
                continue;
            }

            Node? found = FindOrNull(child, name);
            if (found is not null)
            {
                return found;
            }
        }

        throw new InvalidOperationException(name);
    }

    private static Node? FindOrNull(Node node, string name)
    {
        try
        {
            return Find(node, name);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void PrefixFrame(LosslessBits w, int width, int delta, bool more)
    {
        var body = new LosslessBits();
        LosslessBuilder.SeekableFrame(body, 1, 100);
        w.Put(width, (ulong)(width + body.Count + 2 + delta));
        for (long b = 0; b < body.Count; b++)
        {
            w.PutBit(body.Bit(b));
        }

        w.PutBit(false);
        w.PutBit(more);
    }
}
