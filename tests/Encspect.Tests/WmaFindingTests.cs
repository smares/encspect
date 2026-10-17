using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// One damaged or unusual stream for each finding the Windows Media Audio parsers emit: the media objects of a
// fixture with a field changed, handed to the parser as an ASF file would hand them, with the configuration the
// file states (or none, or a damaged one). The finding must come out and nothing may fail.
public class WmaFindingTests
{
    private const string Reservoir = "asf/external-windows-wma92-vbr-stereo-44k-q75.wma";
    private const string FFmpeg = "asf/external-ffmpeg-wmav2-stereo-44k-128k.wma";
    private const string Pro = "asf/external-windows-wma10pro-vbr-stereo-44k-q50.wma";
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
        ["wma.bit-offset"] = () => Standard(Reservoir, 0x0161, s =>
        {
            // A superframe of 100 bytes that states a bit offset of 0x7FFF.
            s.Format = s.Format with { BlockAlign = 100 };
            s.Objects[0] = With(s.Objects[0][..100], o => { o[0] = 0x03; o[1] = 0xFF; o[2] = 0xFF; });
        }),
        ["wma.codec-data-size"] = () => Standard(FFmpeg, 0x0161, s => s.Data = s.Data[..7]),
        ["wma.writer-claim-differs"] = () => Standard(FFmpeg, 0x0161, _ => { }, [new EncoderClaim("Windows Media Format SDK 12", new Node("Claim", 0, 1), "test layout", ClaimRole.Writer) { Confidence = Confidence.Medium, IsLayout = true }, new EncoderClaim("Foo Muxer 1.0", new Node("Claim", 0, 1), "test claim", ClaimRole.Writer)]),
        ["wma.writer-other-format"] = () => Standard(FFmpeg, 0x0161, _ => { }, [new EncoderClaim("LAME 3.100", new Node("Claim", 0, 1), "test claim", ClaimRole.Writer)]),
        ["wma.encoder-claim-differs"] = () => Standard(FFmpeg, 0x0161, _ => { }, [new EncoderClaim("LAME 3.100", new Node("Claim", 0, 1), "test claim", ClaimRole.Encoder)]),
        ["wma.format-unsupported"] = () => Standard(FFmpeg, 0x0161, s => s.Format = s.Format with { BlockAlign = 0 }),
        ["wma.frame-filler"] = () => Standard("asf/external-windows-wma92-cbr-stereo-44k-128.wma", 0x0161, _ => { }),
        ["wma.frame-invalid"] = () => Standard(FFmpeg, 0x0161, s => s.Objects[0] = With(s.Objects[0], o => Array.Fill(o, (byte)0xFF, 4, 60))),
        ["wma.frame-stuffing"] = () => Standard("asf/external-windows-wma92-cbr-stereo-44k-128.wma", 0x0161, _ => { }),
        ["wma.frame-unfinished"] = () => Unfinished(),
        ["wma.lossless-bit-depth"] = () => Header(new WmaLosslessParser(), Lossless, 0x0163, s => BinaryPrimitives.WriteUInt16LittleEndian(s.Data, 12)),
        ["wma.lossless-not-read"] = () => Header(new WmaLosslessParser(), Lossless, 0x0163, _ => { }),
        ["wma.no-format"] = () => Run(new WmaProParser(), Load(Pro, 0x0162).Objects, (Stream?)null),
        ["wma.no-length-prefix"] = () => Header2(Pro, 0x0162, s => BinaryPrimitives.WriteUInt16LittleEndian(s.Data.AsSpan(14), 0x00A1)),
        ["wma.packet-long"] = () => Header2(Pro, 0x0162, s => s.Objects[0] = [.. s.Objects[0], .. new byte[40]]),
        ["wma.packet-sequence"] = () => Header2(Pro, 0x0162, s => s.Objects[1] = With(s.Objects[1], o => o[0] = (byte)((o[0] & 0x0F) | 0xE0))),
        ["wma.packet-short"] = () => Header2(Pro, 0x0162, s => s.Objects[0] = s.Objects[0][..40]),
        ["wma.payload-not-analyzed"] = () => PayloadNotAnalyzed(),
        ["wma.reservoir-at-end"] = () => Standard(Reservoir, 0x0161, s => s.Objects.RemoveRange(2, s.Objects.Count - 2)),
        ["wma.reservoir-length"] = () => ReservoirLength(),
        ["wma.superframe-header"] = () => Standard(Reservoir, 0x0161, s => s.Objects[0] = With(s.Objects[0], o => o[0] = 0x00)),
        ["wma.superframe-long"] = () => Standard(FFmpeg, 0x0161, s => s.Objects[0] = [.. s.Objects[0], .. new byte[20]]),
        ["wma.superframe-short"] = () => Standard(FFmpeg, 0x0161, s => s.Objects[0] = s.Objects[0][..^10]),
        ["wma.variable-blocks-disabled"] = () => Standard(FFmpeg, 0x0161, s => BinaryPrimitives.WriteUInt16LittleEndian(s.Data.AsSpan(4), 0x000D)),
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

    // The standard decoder's other findings: a Pro stream that is Lossless-like in its header, and the codec data of the
    // Professional and Lossless parsers.
    [Theory]
    [InlineData(Pro, 0x0162)]
    [InlineData(Lossless, 0x0163)]
    public void ShortCodecDataOfTheOtherParsersIsFound(string fixture, int tag)
    {
        ICodecParser parser = tag == 0x0162 ? new WmaProParser() : new WmaLosslessParser();
        Stream s = Load(fixture, tag);
        s.Data = s.Data[..10];
        Assert.Contains(Run(parser, s.Objects, s), f => f.Code == "wma.codec-data-size");
    }

    // A superframe with no frame ending in it, between the one where a frame begins and the one where it ends:
    // FFmpeg's wmadec.c adds 8 * buf_size - 8 to last_superframe_len there (bits to a count of bytes), so a frame that
    // spans such a superframe is not rebuilt right by it; the analysis keeps the superframe's align - 1 bytes.
    [Fact]
    public void AFrameEndsAfterASuperframeInsideIt()
    {
        Stream s = Load(Reservoir, 0x0161);
        int align = s.Format.BlockAlign;
        // The straddling frame of the first two superframes, with a middle superframe put between them whose body
        // starts with the bits the second one has for the frame, so that the frame ends inside it.
        byte[] second = s.Objects[1];
        int bitOffsetBits = ByteOffsetBits(s.Format);
        int bitOffset = (int)BitsAt(second, 8, bitOffsetBits + 3);
        byte[] middle = new byte[align];
        middle[0] = (byte)((second[0] & 0xF0) | 0);
        for (int i = 0; i < bitOffset; i++)
        {
            int from = 8 + 3 + bitOffsetBits + i;
            if ((second[from / 8] & (0x80 >> (from % 8))) != 0)
            {
                middle[1 + (i / 8)] |= (byte)(0x80 >> (i % 8));
            }
        }

        var objects = new List<byte[]> { s.Objects[0], middle, s.Objects[1] };
        var parent = new Node("Stream", 0, 1);
        var context = new CodecContext(parent, objects.Select((o, i) => new MappedBytes(o, i * 10_000L)), CancellationToken.None, null, null, Configuration(s, parent));
        new WmaStandardParser().Parse(context);
        Assert.DoesNotContain(context.Findings, f => f.Code is "wma.frame-invalid" or "wma.superframe-header" or "wma.bit-offset");
        Node inside = Find(parent, "Superframes Inside a Frame")!;
        Assert.Equal(1L, inside.Value?.AsInteger());
        Assert.Contains(context.Findings, f => f.Code is "wma.frame-stuffing" or "wma.frame-filler");
    }

    // The finding is built when the findings are flushed, after the whole stream: its node must still be the frame it describes.
    [Theory]
    [InlineData("asf/external-windows-wma92-cbr-stereo-44k-128.wma", "wma.frame-stuffing")]
    [InlineData("asf/external-windows-wma92-cbr-stereo-44k-128-sdk.wma", "wma.frame-filler")]
    public void AFillerFindingPointsAtTheFrameInTheSuperframeItNames(string fixture, string code)
    {
        Stream s = Load(fixture, 0x0161);
        Finding finding = Run(new WmaStandardParser(), s.Objects, s).First(f => f.Code == code);
        Match match = Regex.Match(finding.Message, @"in the superframe at 0x([0-9A-F]+)");
        Assert.True(match.Success, finding.Message);
        long superframe = Convert.ToInt64(match.Groups[1].Value, 16);
        // The frame began in the superframe before the one the message names (the test places superframes 10,000 bytes apart) and ends there;
        // the last frame of the stream, where the finding used to point, is far from both.
        Node node = finding.Node!;
        Assert.True(node.Offset >= superframe - 10_000 && node.End <= superframe + s.Format.BlockAlign, $"{node.Offset}+{node.Length} is not at the superframe at {superframe}");
    }

    private static int ByteOffsetBits(AudioFormat format)
    {
        // wma.c: the width of the bit offset field follows the bitrate and the frame length.
        var p = StandardParameters.Create(2, format.Channels, format.SampleRate, format.BytesPerSecond, format.BlockAlign, 0x000F, 10);
        return p.ByteOffsetBits;
    }

    private static uint BitsAt(byte[] data, int position, int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++)
        {
            int at = position + i;
            value = (value << 1) | (uint)((data[at / 8] >> (7 - (at % 8))) & 1);
        }

        return value;
    }

    private static Node? Find(Node node, string name)
    {
        if (node.Name == name)
        {
            return node;
        }

        foreach (Node child in node.Children)
        {
            if (Find(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
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
    internal static void SetBits(byte[] data, int position, int count, uint value)
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

    private static byte[] With(byte[] data, Action<byte[]> change)
    {
        byte[] copy = (byte[])data.Clone();
        change(copy);
        return copy;
    }

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

    private static IReadOnlyList<Finding> Standard(string fixture, int tag, Action<Stream> change, IReadOnlyList<EncoderClaim>? claims = null)
    {
        Stream s = Load(fixture, tag);
        change(s);
        return Run(new WmaStandardParser(), s.Objects, s, claims);
    }

    private static IReadOnlyList<Finding> Header(ICodecParser parser, string fixture, int tag, Action<Stream> change)
    {
        Stream s = Load(fixture, tag);
        change(s);
        return Run(parser, s.Objects, s);
    }

    private static IReadOnlyList<Finding> Header2(string fixture, int tag, Action<Stream> change) => Header(new WmaProParser(), fixture, tag, change);

    private static IReadOnlyList<Finding> PayloadNotAnalyzed()
    {
        var parent = new Node("Stream", 0, 1);
        byte[] bytes = new byte[100];
        var context = new CodecContext(parent, new ByteReader(new MemoryStream(bytes)));
        new WmaProParser().Parse(context);
        return context.Findings;
    }

    // The first prefix of a Professional stream that ends with a frame whose trailer bit says another follows.
    private static IReadOnlyList<Finding> Unfinished()
    {
        Stream s = Load(Pro, 0x0162);
        for (int count = 1; count <= s.Objects.Count; count++)
        {
            IReadOnlyList<Finding> findings = Run(new WmaProParser(), s.Objects.Take(count).ToList(), s);
            if (findings.Any(f => f.Code == "wma.frame-unfinished"))
            {
                return findings;
            }
        }

        return [];
    }

    // A block alignment of more than 32,768 bytes with a superframe whose frames end early leaves a reservoir longer than the decoder keeps.
    private static IReadOnlyList<Finding> ReservoirLength()
    {
        Stream s = Load(Reservoir, 0x0161);
        s.Format = s.Format with { BlockAlign = 40_000 };
        byte[] big = new byte[40_000];
        Array.Copy(s.Objects[0], big, s.Objects[0].Length);
        return Run(new WmaStandardParser(), [big, big], s);
    }
}
