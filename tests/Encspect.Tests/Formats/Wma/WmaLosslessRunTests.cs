using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// Windows Media Audio Lossless: which frames of a saved run are decoded, what a frame that runs past the kept bits and an end trim of F or more
// do, and what the arithmetic coding flag leaves behind (docs/design/wma-lossless-format.md, "Frames in packets", "Frame", "Validity rules and
// damage"). Streams are the committed fixture with changed bits, or frames written bit by bit for the frame reader.
public class WmaLosslessRunTests
{
    // Bit positions in the packets of the 16-bit stereo fixture (docs/design/wma-lossless-format.md, "Example"): the header is 23 bits, frame 0
    // is 37,493 bits long, frame 1 36,834, frame 2 36,678 (4,028 of them in packet 1), and frame 9 starts 7,613 bits into packet 3's payload.
    private const int Header = 23;
    private const int Frame0Bits = 37_493;

    private sealed class Bitstream
    {
        private readonly List<byte> _bytes = [];
        private int _count;

        public int Length => _count;

        public void Put(int width, uint value)
        {
            for (int i = width - 1; i >= 0; i--)
            {
                if ((_count & 7) == 0)
                {
                    _bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(0x80 >> (_count & 7));
                }

                _count++;
            }
        }

        public void Zeros(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Put(1, 0);
            }
        }

        public Bits ToBits() => new([.. _bytes, .. new byte[16]], _count);
    }

    private static (LosslessParameters Parameters, LosslessFrameReader Reader, LosslessTally Tally) Reader(int log2Subframes)
    {
        WmaStream stream = WmaStream.LoadLossless();
        byte[] data = (byte[])stream.Data.Clone();
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)((flags & ~0x38) | (log2Subframes << 3)));
        LosslessParameters parameters = LosslessParameters.Create(stream.Format, data);
        Assert.Null(parameters.Unsupported);
        Assert.False(parameters.HasLengthPrefix);
        var tally = new LosslessTally();
        return (parameters, new LosslessFrameReader(parameters, tally), tally);
    }

    // The fields of a seekable subframe up to its raw PCM bit: no filters, one cascaded filter of the smallest order per channel.
    private static void SeekableFields(Bitstream w, LosslessParameters p, bool isArithmetic, int orderCode = 0)
    {
        w.Put(1, 1);
        w.Put(1, isArithmetic ? 1u : 0);
        if (isArithmetic)
        {
            return;
        }

        w.Put(3, 0);
        w.Put(1, 0);
        for (int c = 0; c < p.Channels; c++)
        {
            w.Put(3, 0);
            w.Put(7, (uint)orderCode);
            if (orderCode != 0)
            {
                return;
            }

            w.Put(4, 0);
        }

        w.Put(3, 0);
        w.Put(8, 0);
    }

    // A coded subframe's fields after the seekable ones: the raw PCM bit, the coded channels, the zero low bits flag and, for each coded
    // channel, a transient bit, the initial mean of a seekable subframe and residuals that are all single 0 bits.
    private static void CodedChannels(Bitstream w, LosslessParameters p, bool isSeekable, int length, params bool[] coded)
    {
        w.Put(1, 0);
        foreach (bool isCoded in coded)
        {
            w.Put(1, isCoded ? 1u : 0);
        }

        if (p.HasLpcFlags)
        {
            w.Put(1, 0);
        }

        w.Put(1, 0);
        foreach (bool isCoded in coded)
        {
            if (!isCoded)
            {
                continue;
            }

            w.Put(1, 0);
            if (isSeekable)
            {
                w.Zeros(2 * p.SampleSize);
            }

            w.Zeros(isSeekable ? length - 1 : length);
        }
    }

    // The dynamic range gain that follows the tile header where the stream's flags have it.
    private static void Gain(Bitstream w, LosslessParameters p)
    {
        if (p.HasDynamicRange)
        {
            w.Put(8, 0);
        }
    }

    private static FrameOutcome Frame(LosslessFrameReader reader, Bitstream w)
    {
        Bits bits = w.ToBits();
        return reader.Read(bits, 0, w.Length, 0, new TraceWriter());
    }

    // A seekable subframe that states arithmetic coding stops the frame with the running sums and the filter state cleared, and the decoder
    // stays ready: the next subframe that is not seekable is read. A cascaded filter of an order above 256 makes the decoder wait for the next
    // seekable subframe instead.
    [Fact]
    public void ArithmeticCodingLeavesTheDecoderReady()
    {
        (LosslessParameters p, LosslessFrameReader reader, _) = Reader(0);
        int f = p.FrameLength;

        var first = new Bitstream();
        first.Put(1, 0);
        Gain(first, p);
        first.Put(1, 0);
        SeekableFields(first, p, false);
        CodedChannels(first, p, true, f, true, true);
        first.Put(1, 0);
        Assert.Equal(0, Frame(reader, first).Fault);

        var arithmetic = new Bitstream();
        arithmetic.Put(1, 0);
        Gain(arithmetic, p);
        arithmetic.Put(1, 0);
        SeekableFields(arithmetic, p, true);
        Assert.Equal((int)LosslessFault.ArithmeticCoding, Frame(reader, arithmetic).Fault);

        var plain = new Bitstream();
        plain.Put(1, 0);
        Gain(plain, p);
        plain.Put(1, 0);
        plain.Put(1, 0);
        CodedChannels(plain, p, false, f, true, true);
        plain.Put(1, 0);
        Assert.Equal(0, Frame(reader, plain).Fault);

        var order = new Bitstream();
        order.Put(1, 0);
        Gain(order, p);
        order.Put(1, 0);
        SeekableFields(order, p, false, 32);
        Assert.Equal((int)LosslessFault.FilterOrder, Frame(reader, order).Fault);
        Assert.Equal((int)LosslessFault.WaitingForSeekable, Frame(reader, plain).Fault);
    }

    // The same at the start of a stream: nothing has made the decoder ready, and the arithmetic coding flag does not.
    [Fact]
    public void ArithmeticCodingDoesNotMakeTheDecoderReady()
    {
        (LosslessParameters p, LosslessFrameReader reader, _) = Reader(0);
        var arithmetic = new Bitstream();
        arithmetic.Put(1, 0);
        Gain(arithmetic, p);
        arithmetic.Put(1, 0);
        SeekableFields(arithmetic, p, true);
        Assert.Equal((int)LosslessFault.ArithmeticCoding, Frame(reader, arithmetic).Fault);
        var plain = new Bitstream();
        plain.Put(1, 0);
        Gain(plain, p);
        plain.Put(1, 0);
        plain.Put(1, 0);
        CodedChannels(plain, p, false, p.FrameLength, true, true);
        plain.Put(1, 0);
        Assert.Equal((int)LosslessFault.WaitingForSeekable, Frame(reader, plain).Fault);
    }

    // A frame whose end trim is F or more stops right after its trim fields: no subframe is read, and the frame is 16 bits long here, 24 with the
    // dynamic range gain (the tile header's bit, the trim present bit, the two presence bits and 12 bits of the end trim).
    [Fact]
    public void AnEndTrimOfFOrMoreStopsTheFrameAfterTheTrimFields()
    {
        (LosslessParameters p, LosslessFrameReader reader, LosslessTally tally) = Reader(0);
        var w = new Bitstream();
        w.Put(1, 0);
        Gain(w, p);
        w.Put(1, 1);
        w.Put(1, 0);
        w.Put(1, 1);
        w.Put(12, 2048);
        w.Zeros(200);
        FrameOutcome result = Frame(reader, w);
        Assert.Equal((int)LosslessFault.TrimTooLarge, result.Fault);
        Assert.Equal(p.HasDynamicRange ? 24 : 16, result.Bits);
        Assert.Equal(0, reader.SubframesRead);
        Assert.Equal(0, tally.Subframes);
        Assert.False(result.AreSubframesRead);
        Assert.Equal(2048, p.FrameLength);

        // One sample fewer is a frame of one sample.
        (_, LosslessFrameReader other, _) = Reader(0);
        var ok = new Bitstream();
        ok.Put(1, 0);
        Gain(ok, p);
        ok.Put(1, 1);
        ok.Put(1, 0);
        ok.Put(1, 1);
        ok.Put(12, 2047);
        SeekableFields(ok, p, false);
        CodedChannels(ok, p, true, p.FrameLength, true, true);
        ok.Put(1, 0);
        FrameOutcome good = Frame(other, ok);
        Assert.Equal(0, good.Fault);
        Assert.Equal(2047, good.TrimEnd);
    }

    // The coded-channel share counts the channels each subframe covers: channel 0 has two subframes of 1,024 samples and channel 1 one of 2,048,
    // and every subframe states a coded flag for both channels, of which the other channel's does not count.
    [Fact]
    public void TheCodedChannelShareCountsOnlyTheChannelsASubframeCovers()
    {
        (LosslessParameters p, LosslessFrameReader reader, LosslessTally tally) = Reader(1);
        var w = new Bitstream();
        w.Put(5, 0b01001);
        Gain(w, p);
        w.Put(1, 0);
        // Channel 0, 1,024 samples, seekable; both channels state themselves coded.
        SeekableFields(w, p, false);
        CodedChannels(w, p, true, 1024, true, true);
        // Channel 1, 2,048 samples; only channel 1 is coded.
        w.Put(1, 0);
        CodedChannels(w, p, false, 2048, false, true);
        // Channel 0, the second 1,024 samples; only channel 0 is coded.
        w.Put(1, 0);
        CodedChannels(w, p, false, 1024, true, false);
        w.Put(1, 0);
        FrameOutcome result = Frame(reader, w);
        Assert.Equal(0, result.Fault);
        Assert.Equal(3, tally.Subframes);
        Assert.Equal(3, tally.CodableChannelSubframes);
        Assert.Equal(3, tally.CodedChannels);
    }

    private static CodecContext RunMutated(Action<WmaStream> change, string fixture = WmaStream.Lossless)
    {
        WmaStream stream = WmaStream.LoadLossless(fixture);
        change(stream);
        return stream.RunLossless();
    }

    // Packet 0's frame 0 says that no frame follows it. A decoder ignores that bit for the first frame it reads from a run and reads the bits
    // left as further frames, so all 44 frames and their samples are still read; the finding names the frames' bits.
    [Fact]
    public void TheTrailerBitOfTheFirstFrameOfARunIsIgnored()
    {
        CodecContext context = RunMutated(s =>
        {
            Assert.Equal(1u, WmaStream.GetBits(s.Objects[0], Header + Frame0Bits - 1, 1));
            WmaStream.SetBits(s.Objects[0], Header + Frame0Bits - 1, 1, 0);
        });
        Assert.Equal(88200, context.Streams[0].Samples);
        // The gap lies after frame 0, which says that no frame follows it, and the finding points there, not at the last frame read.
        Finding boundary = Assert.Single(context.Findings, f => f.Code == "wma.frame-boundary");
        Node first = WmaStream.Child(WmaStream.StreamNode(context), "Frames").Children[0];
        Assert.Equal(("Frame", first.Offset, first.Length), (boundary.Node!.Name, boundary.Node.Offset, boundary.Node.Length));
        Assert.DoesNotContain(context.Findings, f => f.Code is "wma.frame-invalid" or "wma.packet-sequence");
        Assert.Equal("44", WmaStream.Statistic(WmaStream.StreamNode(context), "Frames"));
    }

    // A later frame that says that no frame follows it ends the run: the bits left are not read.
    [Fact]
    public void TheTrailerBitOfALaterFrameEndsTheRun()
    {
        CodecContext context = RunMutated(s => WmaStream.SetBits(s.Objects[0], Header + Frame0Bits + 36_834 - 1, 1, 0));
        Assert.Contains(context.Findings, f => f.Code == "wma.frame-boundary");
        Assert.True(int.Parse(WmaStream.Statistic(WmaStream.StreamNode(context), "Frames")) < 44);
    }

    // Packet 1 states fewer bits than finish frame 2, so frame 2 runs past the bits kept for it. That is no loss: the packet after packet 1 is
    // checked against its sequence number. The frame is output whole (it counts its samples) and is still reported as one that needs more
    // bits than it has.
    [Fact]
    public void AFrameThatRunsPastTheKeptBitsIsNoLoss()
    {
        CodecContext context = RunMutated(s =>
        {
            WmaStream.SetBits(s.Objects[1], 6, 17, 4028 - 2000);
            s.Objects[2][0] = (byte)((s.Objects[2][0] & 0x0F) | 0xA0);
        });
        // The changed number is found against packet 1's, and the packet after it against the changed number.
        Finding gap = Assert.Single(context.Findings, f => f.Code == "wma.packet-sequence");
        Assert.Equal(2, gap.Count);
        Assert.Contains("10 for 2", gap.Message, StringComparison.Ordinal);
        Finding invalid = context.Findings.First(f => f.Code == "wma.frame-invalid");
        Assert.Contains("it needs more bits than it has", invalid.Message, StringComparison.Ordinal);
    }

    // Packet 1 states five bits more than finish frame 2 and frame 2 says that a frame follows: those five bits are read as a frame, whose
    // subframe starts with no bit left. That is an error of a later frame, so the loss is pending and the packet after packet 1 is not checked
    // against its sequence number.
    [Fact]
    public void ASubframeThatStartsWithNoBitLeftIsALoss()
    {
        CodecContext context = RunMutated(s =>
        {
            WmaStream.SetBits(s.Objects[1], Header + 4028 - 1, 1, 1);
            WmaStream.SetBits(s.Objects[1], 6, 17, 4028 + 5);
            // The five bits: the tile header of one subframe of F samples, and nothing after it.
            LosslessParameters p = LosslessParameters.Create(s.Format, s.Data);
            Assert.InRange(p.LengthCodeWidth, 0, 4);
            WmaStream.SetBits(s.Objects[1], Header + 4028, 5, 0);
            WmaStream.SetBits(s.Objects[1], Header + 4028, 1, 1);
            WmaStream.SetBits(s.Objects[1], Header + 4029, p.LengthCodeWidth, (1u << p.LengthCodeWidth) - 1);
            s.Objects[2][0] = (byte)((s.Objects[2][0] & 0x0F) | 0xA0);
        });
        // The first mismatch is the one of packet 3, against the changed number: packet 2's own was not checked.
        Finding gap = Assert.Single(context.Findings, f => f.Code == "wma.packet-sequence");
        Assert.Contains("3 for 11", gap.Message, StringComparison.Ordinal);
        Finding invalid = context.Findings.First(f => f.Code == "wma.frame-invalid");
        Assert.Contains("a subframe ends before its zero low bits flag", invalid.Message, StringComparison.Ordinal);
    }

    // Frame 2 of packet 0 states an end trim of 2,048 in place of the fields that follow its tile header (and the dynamic range gain), and a
    // small frame without coded channels follows the trim fields, as a decoder reads on from there. The first is rejected without a loss and
    // the second says that no frame follows, so the packet after packet 1 is checked against its sequence number.
    [Fact]
    public void AnEndTrimOfFOrMoreIsNoLoss()
    {
        Node tile = WmaStream.Child(WmaStream.Child(WmaStream.StreamNode(WmaStream.LoadLossless().RunLossless()), "Frames").Children[2], "Tile Header");
        int tileBits = tile.BitLength ?? (int)tile.Length * 8;
        CodecContext context = RunMutated(s =>
        {
            LosslessParameters p = LosslessParameters.Create(s.Format, s.Data);
            byte[] packet = s.Objects[0];
            int start = Header + Frame0Bits + 36_834;
            uint tileValue = WmaStream.GetBits(packet, start, tileBits);
            int at = start + tileBits + (p.HasDynamicRange ? 8 : 0);
            WmaStream.SetBits(packet, at, 4, 0b1011);
            WmaStream.SetBits(packet, at + 4, 12, 2048);
            at += 16;
            for (int bit = at; bit < packet.Length * 8; bit++)
            {
                WmaStream.SetBits(packet, bit, 1, 0);
            }

            WmaStream.SetBits(packet, at, tileBits, tileValue);
            s.Objects[2][0] = (byte)((s.Objects[2][0] & 0x0F) | 0xA0);
        });
        Finding gap = Assert.Single(context.Findings, f => f.Code == "wma.packet-sequence");
        Assert.Contains("10 for 2", gap.Message, StringComparison.Ordinal);
        Assert.Contains(context.Findings, f => f.Code == "wma.frame-invalid" && f.Message.Contains("its trim at the end is the whole frame or more", StringComparison.Ordinal));
        Assert.DoesNotContain(context.Findings, f => f.Message.Contains("tile header", StringComparison.Ordinal));
        // Encspect reads on from the bit where the trim ended, and the finding says so.
        Assert.DoesNotContain(context.Findings, f => f.Message.Contains("no more frames", StringComparison.Ordinal));
        Assert.Contains(context.Findings, f => f.Message.Contains("reads on from the bit where its trim ended", StringComparison.Ordinal));
    }
}
