using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// One damaged or unusual stream for each finding the Windows Media Audio parsers emit: the media objects of a
// fixture with a field changed, handed to the parser as an ASF file would hand them, with the configuration the
// file states (or none, or a damaged one). The finding must come out and nothing may fail. The frames of versions
// 1 and 2 and Professional are not read, so each has one finding that says so; Voice is read, and a rejected configuration says so.
public class WmaFindingTests
{
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
        ["wma.codec-data-size"] = () => Header(new WmaLosslessParser(), Lossless, 0x0163, s => s.Data = s.Data[..10]),
        ["wma.writer-claim-differs"] = () => Header(new WmaStandardParser(), FFmpeg, 0x0161, _ => { }, [new EncoderClaim("Windows Media Format SDK 12", new Node("Claim", 0, 1), "test layout", ClaimRole.Writer) { Confidence = Confidence.Medium, IsLayout = true }, new EncoderClaim("Foo Muxer 1.0", new Node("Claim", 0, 1), "test claim", ClaimRole.Writer)]),
        ["wma.writer-other-format"] = () => Header(new WmaStandardParser(), FFmpeg, 0x0161, _ => { }, [new EncoderClaim("LAME 3.100", new Node("Claim", 0, 1), "test claim", ClaimRole.Writer)]),
        ["wma.encoder-claim-differs"] = () => Header(new WmaStandardParser(), FFmpeg, 0x0161, _ => { }, [new EncoderClaim("LAME 3.100", new Node("Claim", 0, 1), "test claim", ClaimRole.Encoder), new EncoderClaim("Windows Media Audio 9.2", new Node("Claim", 0, 1), "test claim", ClaimRole.Encoder)]),
        ["wma.lossless-bit-depth"] = () => Header(new WmaLosslessParser(), Lossless, 0x0163, s => BinaryPrimitives.WriteUInt16LittleEndian(s.Data, 12)),
        ["wma.lossless-not-read"] = () => Header(new WmaLosslessParser(), Lossless, 0x0163, _ => { }),
        ["wma.no-format"] = () => Run(new WmaProParser(), Load(Pro, 0x0162).Objects, (Stream?)null),
        ["wma.payload-not-analyzed"] = () => PayloadNotAnalyzed(),
        ["wma.pro-not-read"] = () => Header(new WmaProParser(), Pro, 0x0162, _ => { }),
        ["wma.standard-not-read"] = () => Header(new WmaStandardParser(), FFmpeg, 0x0161, _ => { }),
        ["wma.voice-channels"] = () => Header(new WmaVoiceParser(), Voice, 0x000A, s => s.Format = s.Format with { Channels = 2 }),
        ["wma.voice-not-read"] = () => Header(new WmaVoiceParser(), Voice, 0x000A, s => s.Data = s.Data[..10]),
        ["wma.records-limit"] = () => RecordsLimit(),
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

    // The codec data of the three codecs is shown as the container's bytes and nothing is read from it, whatever it holds.
    [Theory]
    [InlineData(FFmpeg, 0x0161)]
    [InlineData(Pro, 0x0162)]
    public void TheFramesOfTheTwoCodecsAreNotReadWhateverTheCodecDataHolds(string fixture, int tag)
    {
        ICodecParser parser = tag switch { 0x0161 => new WmaStandardParser(), _ => new WmaProParser() };
        Stream s = Load(fixture, tag);
        s.Data = [];
        IReadOnlyList<Finding> findings = Run(parser, s.Objects, s);
        Assert.Single(findings, f => f.Code.EndsWith("-not-read", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.Severity != Severity.Info);
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

    private static IReadOnlyList<Finding> PayloadNotAnalyzed()
    {
        var parent = new Node("Stream", 0, 1);
        byte[] bytes = new byte[100];
        var context = new CodecContext(parent, new ByteReader(new MemoryStream(bytes)));
        new WmaProParser().Parse(context);
        return context.Findings;
    }
}
