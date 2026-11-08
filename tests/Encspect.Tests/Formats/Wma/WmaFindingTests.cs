using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// One damaged or unusual stream for each finding the Windows Media Audio parsers emit: the media objects of a
// fixture with a field changed, handed to the parser as an ASF file would hand them, with the configuration the
// file states (or none, or a damaged one). The finding must come out and nothing may fail. The streams of versions 1 and 2 are built in
// WmaStandardStreams, the findings of Professional and Lossless come from the streams of WmaProTests and WmaLosslessTests changed the same way,
// and Voice is read from the 8 kHz fixture with a field changed.
public class WmaFindingTests
{
    private const string FFmpeg = "asf/external-ffmpeg-wmav2-stereo-44k-128k.wma";
    private const string Pro = "asf/external-windows-wma10pro-vbr-stereo-44k-q50.wma";
    private const string Windows = "asf/external-windows-wma92-cbr-stereo-44k-128.wma";
    private const string Lossless = "asf/external-windows-wma92lossless-stereo-44k-16.wma";
    private const string Voice = "asf/external-windows-wmavoice-cbr-mono-8k-5.wma";

    private sealed class Stream(List<byte[]> objects, AudioFormat format, byte[] data)
    {
        public List<byte[]> Objects { get; set; } = objects;

        public AudioFormat Format { get; set; } = format;

        public byte[] Data { get; set; } = data;
    }

    private static readonly Dictionary<string, Func<IReadOnlyList<Finding>>> Runs = new()
    {
        ["wma.codec-data-size"] = () => Header(new WmaLosslessParser(), Lossless, 0x0163, s => s.Data = s.Data[..10]),
        ["wma.writer-claim-differs"] = () => Header(new WmaStandardParser(), FFmpeg, 0x0161, _ => { }, [new EncoderClaim("Windows Media Format SDK 12", new Node("Claim", 0, 1), "test layout", ClaimRole.Writer) { Confidence = Confidence.Medium, IsLayout = true }, new EncoderClaim("Foo Muxer 1.0", new Node("Claim", 0, 1), "test claim", ClaimRole.Writer)]),
        ["wma.writer-other-format"] = () => Header(new WmaStandardParser(), FFmpeg, 0x0161, _ => { }, [new EncoderClaim("LAME 3.100", new Node("Claim", 0, 1), "test claim", ClaimRole.Writer)]),
        ["wma.encoder-claim-differs"] = () => Header(new WmaStandardParser(), FFmpeg, 0x0161, _ => { }, [new EncoderClaim("LAME 3.100", new Node("Claim", 0, 1), "test claim", ClaimRole.Encoder), new EncoderClaim("Windows Media Audio 9.2", new Node("Claim", 0, 1), "test claim", ClaimRole.Encoder)]),
        ["wma.format-unsupported"] = () => Header(new WmaLosslessParser(), Lossless, 0x0163, s => BinaryPrimitives.WriteUInt16LittleEndian(s.Data, 12)),
        ["wma.frame-boundary"] = () => FromStream(WmaStream.LoadLossless(), s => WmaStream.SetBits(s.Objects[1], 6, 17, 4036), new WmaLosslessParser()),
        ["wma.frame-invalid"] = () => FromStream(WmaStream.LoadPro(), s => WmaStream.SetBits(s.Objects[0], 22, 16, WmaStream.GetBits(s.Objects[0], 22, 16) + 1), new WmaProParser()),
        ["wma.frame-unfinished"] = () => FromStream(WmaStream.LoadPro(), s => s.Objects.RemoveAt(s.Objects.Count - 1), new WmaProParser()),
        ["wma.packet-overread"] = () => FromStream(WmaStream.LoadLossless(), s => s.Objects[5] = s.Objects[5][..2], new WmaLosslessParser()),
        ["wma.run-too-long"] = () => LosslessBuilder.Run(LosslessBuilder.RawStream(40_000, 4), 1, 40_000).Findings,
        ["wma.trailer-without-frame"] = () => FromStream(WmaStream.LoadLossless(), s => WmaStream.SetBits(s.Objects[1], 23 + 4028 - 1, 1, 1), new WmaLosslessParser()),
        ["wma.lossless-channel-count"] = () => FromStream(WmaStream.LoadLossless(), s => s.Format = s.Format with { Channels = 6 }, new WmaLosslessParser()),
        ["wma.lossless-spliced"] = () => FromStream(WmaStream.LoadLossless(), s => s.Objects[3][0] |= 0x04, new WmaLosslessParser()),
        ["wma.packet-long"] = () => FromStream(WmaStream.LoadPro(), s => s.Objects[1] = [.. s.Objects[1], .. new byte[50]], new WmaProParser()),
        ["wma.packet-sequence"] = () => FromStream(WmaStream.LoadPro(), s => s.Objects[2][0] = (byte)((s.Objects[2][0] & 0x0F) | 0xA0), new WmaProParser()),
        ["wma.packet-short"] = () => FromStream(WmaStream.LoadPro(), s => s.Objects[2] = s.Objects[2][..100], new WmaProParser()),
        ["wma.records-limit"] = () => FromStream(WmaStream.LoadPro(), s => (s.Format, s.Objects) = (s.Format with { BlockAlign = 64 }, [.. Enumerable.Range(0, 262_144 + 3).Select(_ => new byte[64])]), new WmaProParser()),
        ["wma.no-format"] = () => Run(new WmaProParser(), Load(Pro, 0x0162).Objects, (Stream?)null),
        ["wma.payload-not-analyzed"] = () => PayloadNotAnalyzed(),
        ["wma.superframe-short"] = () => Std(1, 100, 0x0003, [new byte[40]]),
        ["wma.superframe-long"] = () => Std(1, 100, 0x0001, [new byte[110]]),
        ["wma.superframe-header"] = () => Std(1, 100, 0x0003, [new byte[100]]),
        ["wma.bit-offset"] = () => Std(1, 100, 0x0003, [Filled(100, 0x02, 0xFF, 0xE0)]),
        ["wma.reservoir-length"] = () => Std(1, 40_000, 0x0003, [Filled(40_000, 0x02)]),
        ["wma.frame-stuffing"] = () => Header(new WmaStandardParser(), Windows, 0x0161, _ => { }),
        ["wma.frame-filler"] = () => Header(new WmaStandardParser(), Windows, 0x0161, _ => { }),
        ["wma.reservoir-at-end"] = () => Header(new WmaStandardParser(), Windows, 0x0161, _ => { }),
        ["wma.blocks-undetailed"] = () => Std(1, 100, 0x0001, Enumerable.Repeat(new byte[100], 50_000)),
        ["wma.variable-blocks-disabled"] = () => WmaStandardStreams.Run(2, 1, 44100, 4000, 100, WmaStandardStreams.CodecData(2, 0x000D), [new byte[100]]).Findings,
    };

    // Voice streams: the 8 kHz fixture with a field changed.
    private static readonly Dictionary<string, Func<IReadOnlyList<Finding>>> VoiceRuns = new()
    {
        ["wma.voice-block-alignment"] = () => VoiceRun(s => s.Format = s.Format with { BlockAlign = 0 }),
        ["wma.voice-cache-overflow"] = () => VoiceRun(s =>
        {
            // A packet of 400 bytes that states one superframe and holds nothing else: its tail is more than the 256 bytes of FFmpeg's cache.
            s.Format = s.Format with { BlockAlign = 400 };
            s.Objects = [new byte[400]];
            SetBits(s.Objects[0], 5, 6, 1);
        }),
        ["wma.voice-channels"] = () => VoiceRun(s => s.Format = s.Format with { Channels = 2 }),
        ["wma.voice-denoise-strength"] = () => VoiceRun(s => SetBits(s.Data, (18 * 8) + 2, 4, 12)),
        ["wma.voice-frame-type"] = () => VoiceRun(s => SetBits(s.Objects[0], 72, 14, 0x3FFF)),
        ["wma.voice-frame-type-overlap"] = () => VoiceRun(s => s.Data = VoiceTable(s.Data, [0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5])),
        ["wma.voice-frame-type-table"] = () => VoiceRun(s => s.Data = VoiceTable(s.Data, [0, 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4])),
        ["wma.voice-frame-type-unreachable"] = () => VoiceRun(s => s.Data = VoiceTable(s.Data, [0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 7, 7])),
        ["wma.voice-lsp-flag-changes"] = () => VoiceRun(s => SetBits(s.Objects[1], 4, 1, 0)),
        ["wma.voice-music"] = () => VoiceRun(s => SetBits(s.Objects[0], 22, 1, 0)),
        ["wma.voice-not-read"] = () => VoiceRun(s => s.Format = s.Format with { BlockAlign = 0 }),
        ["wma.voice-packet-header"] = () => VoiceRun(s =>
        {
            // Blocks of 2 bytes whose count says that another 6-bit value follows, with no room for it.
            s.Format = s.Format with { BlockAlign = 2 };
            s.Objects = [[0x07, 0xE0]];
        }),
        ["wma.voice-packet-sequence"] = () => VoiceRun(s => SetBits(s.Objects[2], 0, 4, 9)),
        ["wma.voice-sample-count"] = () => VoiceRun(s =>
        {
            SetBits(s.Objects[0], 23, 1, 1);
            SetBits(s.Objects[0], 24, 12, 4000);
        }),
        ["wma.voice-sample-rate"] = () => VoiceRun(s => s.Format = s.Format with { SampleRate = 100 }),
        ["wma.voice-spillover-after-music"] = () => VoiceRun(s => SetBits(s.Objects[0], 22, 1, 0)),
        ["wma.voice-spillover-orphan"] = () => VoiceRun(s => SetBits(s.Objects[0], 11, 11, 5)),
        ["wma.voice-superframe-overrun"] = () => VoiceRun(s => SetBits(s.Objects[1], 11, 11, 100)),
        ["wma.voice-superframe-padding"] = () => VoiceRun(s => SetBits(s.Objects[1], 11, 11, 330)),
        ["wma.voice-unfinished"] = () => VoiceRun(s => s.Objects = s.Objects.Take(1).ToList()),
    };

    public static TheoryData<string> VoiceCodes => [.. VoiceRuns.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(VoiceCodes))]
    public void VoiceStreamGivesTheFinding(string code)
    {
        IReadOnlyList<Finding> findings = VoiceRuns[code]();
        Assert.Contains(findings, f => f.Code == code);
        Assert.DoesNotContain(findings, f => f.Code == "parse.failed");
    }

    // Codes that Professional and Lossless emit as well, here from streams of versions 1 and 2.
    private static readonly Dictionary<string, Func<IReadOnlyList<Finding>>> StandardRuns = new()
    {
        ["wma.frame-invalid"] = () => Std(1, 100, 0x0001, [Enumerable.Repeat((byte)0xFF, 100).ToArray()]),
        ["wma.records-limit"] = () => Std(1, 100, 0x0001, Enumerable.Repeat(new byte[100], WmaStandardParser.MaxPackets + 1)),
        ["wma.format-unsupported"] = () => WmaStandardStreams.Run(2, 1, 8000, 1000, 0, WmaStandardStreams.CodecData(2, 1), [new byte[100]]).Findings,
    };

    public static TheoryData<string> StandardCodes => [.. StandardRuns.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(StandardCodes))]
    public void StandardStreamGivesTheFinding(string code)
    {
        IReadOnlyList<Finding> findings = StandardRuns[code]();
        Assert.Contains(findings, f => f.Code == code);
        Assert.DoesNotContain(findings, f => f.Code == "parse.failed");
    }

    public static TheoryData<string> Codes => [.. Runs.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Codes))]
    public void StreamGivesTheFinding(string code)
    {
        IReadOnlyList<Finding> findings = Runs[code]();
        Assert.Contains(findings, f => f.Code == code);
        Assert.DoesNotContain(findings, f => f.Code == "parse.failed");
    }



    // A Voice media object shorter than the block alignment is the rest of a packet with no header, and so are the bytes before the whole
    // blocks of a longer one (a decoder cuts blocks from the end); a media object of two blocks is read as two packets.
    [Fact]
    public void VoiceMediaObjectsOfOtherSizesAreCutIntoBlocks()
    {
        Assert.Contains(VoiceRun(s => s.Objects[3] = s.Objects[3][..50]), f => f.Code == "wma.packet-short" && f.Message.Contains("block alignment of 187", StringComparison.Ordinal));
        Assert.Contains(VoiceRun(s => s.Objects[3] = [.. s.Objects[3], .. new byte[20]]), f => f.Code == "wma.packet-long" && f.Message.Contains("20 bytes before its whole blocks", StringComparison.Ordinal));

        Stream pair = Load(Voice, 0x000A);
        var packets = new List<MappedBytes> { new MappedBytes(pair.Objects[0].Concat(pair.Objects[1]).ToArray(), 200) };
        var parent = new Node("Stream", 0, 1_000_000);
        var context = new CodecContext(parent, packets, CancellationToken.None, null, null, Configuration(pair, parent));
        new WmaVoiceParser().Parse(context);
        Assert.Equal(2, parent.Children.Single(c => c.Name == "Windows Media Audio Voice").Children.Single(c => c.Name == "Packets").Children.Count);
        Assert.DoesNotContain(context.Findings, f => f.Code is "wma.packet-short" or "wma.packet-long");
    }

    // Four superframes of 87 bits fill a block of 46 bytes after its header of 20 bits, and the header counts a fifth: a decoder reads the
    // four, finds the packet used up and goes on with the next, so that no superframe is reported as running past the packet.
    [Fact]
    public void ASuperframeThatEndsWithItsBlockLeavesNoOverrun()
    {
        IReadOnlyList<Finding> findings = VoiceRun(s =>
        {
            var body = new VoiceBitWriter();
            for (int i = 0; i < 4; i++)
            {
                body.Append(VoiceBuilder.Superframe());
            }

            s.Format = s.Format with { BlockAlign = 46 };
            s.Objects = [VoiceBuilder.Packet(46, 0, true, 5, 0, body), VoiceBuilder.Packet(46, 1, true, 0, 0, new VoiceBitWriter())];
        });

        Assert.DoesNotContain(findings, f => f.Code is "wma.voice-superframe-overrun" or "parse.failed");
    }

    [Fact]
    public void MoreMediaObjectsThanTheVoiceReaderReadsGiveTheRecordsLimit()
    {
        Assert.Contains(RecordsLimit(), f => f.Code == "wma.records-limit");
    }

    // The container's reader rejects a rate of 0 or less, so a stored rate that reads as negative is reported as 0.
    [Fact]
    public void ANegativeSampleRateIsReportedAsZero()
    {
        Finding finding = Assert.Single(VoiceRun(s => s.Format = s.Format with { SampleRate = int.MinValue }), f => f.Code == "wma.voice-sample-rate");
        Assert.Contains("0 Hz", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("-", finding.Message, StringComparison.Ordinal);
    }

    // The bit of the packet where the superframe that goes on into the next packet starts: its speech bit, which clear makes a superframe of music.
    private static int TailBit(int packetIndex)
    {
        Node packet = VoiceNodeOf(_ => { }).Children.Single(c => c.Name == "Packets").Children[packetIndex];
        Node tail = packet.Children.Last(c => c.Name is "Superframe Start" or "Unfinished Superframe");
        return (int)(((tail.Offset - packet.Offset) * 8) + (tail.BitOffset ?? 0));
    }

    private static Node VoicePacket(Node voice, int index) => voice.Children.Single(c => c.Name == "Packets").Children[index];

    private static string? Statistic(Node voice, string name) =>
        voice.Children.Single(c => c.Name == "Statistics").Children.SingleOrDefault(c => c.Name == name)?.Value?.ToString();

    // The bits after the last superframe are counted for a last start that runs out of bits, and for the bits a last superframe leaves; a last
    // superframe that fails for another reason (here: music) is dropped as a whole and leaves none.
    [Fact]
    public void BitsAfterTheLastSuperframeAreCountedOnlyForAnOverrun()
    {
        const string Name = "Bits After the Last Superframe";
        Assert.Equal("874 bits", Statistic(VoiceNodeOf(_ => { }), Name));
        Assert.NotNull(Statistic(VoiceNodeOf(s => s.Objects = s.Objects.Take(1).ToList()), Name));
        Assert.Null(Statistic(VoiceNodeOf(s => SetBits(s.Objects[13], TailBit(13), 1, 0)), Name));
    }

    // The value of the skipped spillover is the bits the packet holds, not the bits its header states.
    [Fact]
    public void SkippedSpilloverCountsTheBitsThePacketHolds()
    {
        Node packet = VoicePacket(VoiceNodeOf(s => SetBits(s.Objects[0], 11, 11, 1510)), 0);
        Node skipped = packet.Children.Single(c => c.Name == "Skipped Spillover");
        Assert.Equal(1510, packet.Children.Single(c => c.Name == "Spillover Bits").Value!.AsInteger());
        Assert.Equal(skipped.BitLength, skipped.Value!.AsInteger());
        Assert.Equal((187 * 8) - 22, skipped.Value!.AsInteger());
    }

    // The bits a late resync skipped and the bits after a failure are one run of unread bits, so one node.
    [Fact]
    public void UnreadBitsThatFollowOneAnotherAreOneNode()
    {
        // A start that is 1,578 bits long, finished by 100 spillover bits of a packet that counts no superframe: the music bit of the zeros fails it.
        Node packet = VoicePacket(VoiceNodeOf(s =>
        {
            s.Format = s.Format with { BlockAlign = 200 };
            s.Objects = [new byte[200], new byte[200]];
            SetBits(s.Objects[0], 5, 6, 1);
            SetBits(s.Objects[1], 11, 11, 100);
        }), 1);
        // The bits the resync skips and the rest of the packet are 1,478 bits in all.
        Node unread = Assert.Single(packet.Children, c => c.Name == "Unread Bits");
        Assert.Equal(1478, unread.Value!.AsInteger());
        Assert.Equal(unread.BitLength, unread.Value!.AsInteger());
        Assert.Equal("Unread Bits", packet.Children[^1].Name);
    }

    private static byte[] Filled(int length, params byte[] start)
    {
        byte[] bytes = new byte[length];
        start.CopyTo(bytes, 0);
        return bytes;
    }

    // Mono 8 kHz at 1,000 bytes/s: no noise substitution, a 11-bit bit offset.
    private static IReadOnlyList<Finding> Std(int channels, int align, int options, IEnumerable<byte[]> objects) =>
        WmaStandardStreams.Run(2, channels, 8000, 1000, align, WmaStandardStreams.CodecData(2, options), objects).Findings;

    private static Stream Load(string fixture, int tag)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        int at = file.AsSpan().IndexOf(new Guid("B7DC0791-A9B7-11CF-8EE6-00C00C205365").ToByteArray());
        ReadOnlySpan<byte> p = file.AsSpan(at);
        int typeLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[64..]);
        var format = new AudioFormat(tag, BinaryPrimitives.ReadUInt16LittleEndian(p[80..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(p[82..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(p[86..]), BinaryPrimitives.ReadUInt16LittleEndian(p[90..]), BinaryPrimitives.ReadUInt16LittleEndian(p[92..]));
        return new Stream(AsfBuilder.Objects(fixture, tag), format, p.Slice(78 + 18, typeLength - 18).ToArray());
    }

    private static CodecConfiguration Configuration(Stream s, Node parent) =>
        new(new MappedBytes(s.Data, 100), parent) { Format = s.Format };

    private static IReadOnlyList<Finding> Run(ICodecParser parser, IReadOnlyList<byte[]> objects, Stream? s, IReadOnlyList<EncoderClaim>? claims = null)
    {
        var parent = new Node("Stream", 0, 1_000_000);
        var packets = objects.Select((o, i) => new MappedBytes(o, 200 + (i * 10_000L))).ToList();
        CodecConfiguration? configuration = s is null ? null : Configuration(s, parent);
        var context = new CodecContext(parent, packets, CancellationToken.None, claims, null, configuration);
        parser.Parse(context);
        return context.Findings;
    }

    private static IReadOnlyList<Finding> Header(ICodecParser parser, string fixture, int tag, Action<Stream> change, IReadOnlyList<EncoderClaim>? claims = null)
    {
        Stream s = Load(fixture, tag);
        change(s);
        return Run(parser, s.Objects, s, claims);
    }

    // More media objects than the Voice reader reads: tiny ones, so the walk costs nothing.
    private static IReadOnlyList<Finding> RecordsLimit()
    {
        Stream s = Load(Voice, 0x000A);
        var parent = new Node("Stream", 0, 1_000_000);
        byte[] tiny = new byte[4];
        IEnumerable<MappedBytes> packets = Enumerable.Range(0, WmaVoiceParser.MaxPackets + 5).Select(i => new MappedBytes(tiny, 200 + (i * 8L)));
        var context = new CodecContext(parent, packets, CancellationToken.None, null, null, Configuration(s, parent));
        new WmaVoiceParser().Parse(context);
        return context.Findings;
    }

    // A damaged copy of the 8 kHz Voice fixture: 14 packets of 187 bytes, each with a 22-bit header (sequence number, residual LSPs flag,
    // superframe count, 11 bits of spillover) and then, from bit 22 of the first, a superframe (speech bit, sample count flag, 48 bits of
    // residual LSPs, and the first frame's type code from bit 72).
    private static IReadOnlyList<Finding> VoiceRun(Action<Stream> change)
    {
        Stream s = Load(Voice, 0x000A);
        s.Objects = [.. s.Objects.Select(o => (byte[])o.Clone())];
        s.Data = (byte[])s.Data.Clone();
        change(s);
        return Run(new WmaVoiceParser(), s.Objects, s);
    }

    // The Voice stream node of a damaged copy of the 8 kHz fixture, in a parent that starts at offset 0; the media objects lie at 200 + 10,000 * index.
    private static Node VoiceNodeOf(Action<Stream> change)
    {
        Stream s = Load(Voice, 0x000A);
        s.Objects = [.. s.Objects.Select(o => (byte[])o.Clone())];
        s.Data = (byte[])s.Data.Clone();
        change(s);
        var parent = new Node("Stream", 0, 1_000_000);
        var packets = s.Objects.Select((o, i) => new MappedBytes(o, 200 + (i * 10_000L))).ToList();
        new WmaVoiceParser().Parse(new CodecContext(parent, packets, CancellationToken.None, null, null, Configuration(s, parent)));
        return parent.Children.Single(c => c.Name == "Windows Media Audio Voice");
    }

    // The codec data with the groups of its frame type table, 3 bits for each of the 17 frame types, from byte 22.
    private static byte[] VoiceTable(byte[] data, int[] groups)
    {
        byte[] copy = (byte[])data.Clone();
        Array.Clear(copy, 22, 24);
        for (int i = 0; i < groups.Length; i++)
        {
            SetBits(copy, (22 * 8) + (i * 3), 3, (uint)groups[i]);
        }

        return copy;
    }

    // The count bits at the bit position of the data, most significant first, set to the value.
    private static void SetBits(byte[] data, int position, int count, uint value)
    {
        for (int i = 0; i < count; i++)
        {
            int at = position + i;
            byte mask = (byte)(0x80 >> (at % 8));
            if (((value >> (count - 1 - i)) & 1) != 0)
            {
                data[at / 8] |= mask;
            }
            else
            {
                data[at / 8] &= (byte)~mask;
            }
        }
    }

    private static IReadOnlyList<Finding> FromStream(WmaStream stream, Action<WmaStream> change, ICodecParser parser)
    {
        change(stream);
        return stream.Run(parser).Findings;
    }

    private static IReadOnlyList<Finding> PayloadNotAnalyzed()
    {
        var parent = new Node("Stream", 0, 1);
        byte[] bytes = new byte[100];
        var context = new CodecContext(parent, new ByteReader(new MemoryStream(bytes)));
        new WmaProParser().Parse(context);
        return context.Findings;
    }
}
