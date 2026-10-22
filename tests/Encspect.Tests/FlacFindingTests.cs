using System.Text;
using Encspect.Core;
using Encspect.Formats.Flac;
using Encspect.Host;

namespace Encspect.Tests;

// One crafted file for each finding the FLAC parsers emit that no fixture's manifest lists, and the finding that must come out.
// FindingCoverageTests checks that every code the parsers emit is asserted somewhere.
public class FlacFindingTests
{
    private const uint Serial = 0x464C4143;

    private static byte[] Comment(string vendor, params string[] fields)
    {
        using var stream = new MemoryStream();
        void Text(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(BitConverter.GetBytes(bytes.Length));
            stream.Write(bytes);
        }

        Text(vendor);
        stream.Write(BitConverter.GetBytes(fields.Length));
        foreach (string field in fields)
        {
            Text(field);
        }

        return stream.ToArray();
    }

    // The packets of an Ogg FLAC stream: the mapping header with STREAMINFO, further header packets, then frames.
    private static byte[] OggFlac(int major = 1, int headerPackets = 0, byte[]? first = null, byte[][]? frames = null, long? granule = null, byte[][]? headers = null)
    {
        frames ??= [.. Enumerable.Range(0, 3).Select(i => FlacBuilder.ConstantFrame(i, 4096, 44100, 2, 16))];
        byte[] info = FlacBuilder.Block(0, FlacBuilder.StreamInfo(4096, 44100, 2, 16, 4096L * frames.Length), isLast: headers is null);
        first ??= [0x7F, .. "FLAC"u8, (byte)major, 0, (byte)(headerPackets >> 8), (byte)headerPackets, .. "fLaC"u8, .. info];
        var ogg = new OggBuilder();
        ogg.Page(Serial, 0, 2, first);
        if (headers is not null)
        {
            ogg.Page(Serial, 0, 0, headers);
        }

        long position = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            position += 4096;
            ogg.Page(Serial, i == frames.Length - 1 ? granule ?? position : position, (byte)(i == frames.Length - 1 ? 4 : 0), frames[i]);
        }

        return ogg.Bytes;
    }

    private static readonly Dictionary<string, Func<(byte[] File, Analyzer? Analyzer)>> Crafts = new()
    {
        // Without a tag parser the route has nobody to hand the Vorbis comment to.
        ["flac.comment-not-read"] = () => (FlacBuilder.Stream(3, null, FlacBuilder.Block(4, Comment("vendor"), isLast: true)), new Analyzer([new FlacParser()])),
        ["flac.encoder-other-format"] = () => (FlacBuilder.Stream(3, null, FlacBuilder.Block(4, Comment("reference libFLAC 1.4.3 20230623", "ENCODER=Lavc60.31.102 libmp3lame"), isLast: true)), null),
        ["flac.writer-claim-differs"] = () => ([.. TestFiles.Id3v23(("TSSE", "Lavf60.16.100")), .. FlacBuilder.Stream(3), .. TestFiles.ApeText("Encoder", "LAME 3.100")], null),
        ["flac.writer-other-format"] = () => ([.. FlacBuilder.Stream(3), .. TestFiles.ApeText("Encoder", "LAME 3.100")], null),
        ["flac.resync-limit"] = () =>
        {
            byte[] stream = FlacBuilder.Stream(4);
            byte[] bad = [.. stream[..(stream.Length - 2 * FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16).Length)], .. new byte[100], .. stream[^(2 * FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16).Length)..]];
            return (bad, new Analyzer([new FlacParser { ProbeAllowance = -(10L << 20) }]));
        },
        ["flac.ogg-version"] = () => (OggFlac(major: 2), null),
        ["flac.ogg-header-count"] = () => (OggFlac(headerPackets: 3), null),
        ["flac.ogg-header-invalid"] = () => (OggFlac(first: [0x7F, .. "FLAC"u8, 1, 0, 0, 0, .. "fLaC"u8]), null),
        ["flac.ogg-granule-mismatch"] = () => (OggFlac(granule: 5000), null),
        ["flac.ogg-packet-trailing"] = () => (OggFlac(frames: [FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16), [.. FlacBuilder.ConstantFrame(1, 4096, 44100, 2, 16), 1, 2, 3, 4, 5]]), null),
        ["flac.ogg-packet-invalid"] = () => (OggFlac(frames: [FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16), [1, 2, 3, 4, 5, 6, 7, 8], FlacBuilder.ConstantFrame(2, 4096, 44100, 2, 16)]), null),
        ["flac.metadata-no-last-block"] = () => (OggFlac(headers: [FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16)], frames: [FlacBuilder.ConstantFrame(1, 4096, 44100, 2, 16)]), null),
    };

    public static TheoryData<string> Codes => [.. Crafts.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Codes))]
    public void CraftedFileRaisesTheFinding(string code)
    {
        (byte[] file, Analyzer? custom) = Crafts[code]();
        Analyzer analyzer = custom ?? Analyzer.CreateDefault();
        Report report = analyzer.Analyze("crafted.flac", new MemoryStream(file));

        Assert.Contains(report.Findings, f => f.Code == code);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        TestFiles.CheckInsideParents(report.Root);
    }

    [Fact]
    public void ACodecHandedOnePayloadIsNotAnalyzed()
    {
        var parent = new Node("Parent", 0, 100);
        var payload = new ByteReader(new MemoryStream(new byte[100]));
        var context = new CodecContext(parent, payload);
        new FlacCodecParser().Parse(context);

        Finding finding = Assert.Single(context.Findings);
        Assert.Equal("flac.payload-not-analyzed", finding.Code);
        Assert.Empty(context.Streams);
    }
}
