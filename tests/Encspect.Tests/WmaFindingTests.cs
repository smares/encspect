using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// One damaged or unusual stream for each finding the Windows Media Audio parsers emit: the media objects of a
// fixture with a field changed, handed to the parser as an ASF file would hand them, with the configuration the
// file states (or none, or a damaged one). The finding must come out and nothing may fail. The frames of Voice are not
// read, so it has one finding that says so; the streams of versions 1 and 2 are built in WmaStandardStreams, and the
// findings of Professional and Lossless come from the streams of WmaProTests and WmaLosslessTests changed the same way.
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
        ["wma.voice-not-read"] = () => Header(new WmaVoiceParser(), Voice, 0x000A, _ => { }),
    };

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

    // The codec data of the two codecs is shown as the container's bytes and nothing is read from it, whatever it holds.
    [Theory]
    [InlineData(Voice, 0x000A)]
    public void TheFramesOfVoiceAreNotReadWhateverTheCodecDataHolds(string fixture, int tag)
    {
        ICodecParser parser = new WmaVoiceParser();
        Stream s = Load(fixture, tag);
        s.Data = [];
        IReadOnlyList<Finding> findings = Run(parser, s.Objects, s);
        Assert.Single(findings, f => f.Code.EndsWith("-not-read", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.Severity != Severity.Info);
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
