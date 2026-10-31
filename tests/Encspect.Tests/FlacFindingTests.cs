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
    private static byte[] OggFlac(int major = 1, int headerPackets = 0, byte[]? first = null, byte[][]? frames = null, long? granule = null, byte[][]? headers = null, long granuleOffset = 0)
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
            ogg.Page(Serial, i == frames.Length - 1 ? granule ?? position + granuleOffset : position + granuleOffset, (byte)(i == frames.Length - 1 ? 4 : 0), frames[i]);
        }

        return ogg.Bytes;
    }

    // A mono stream of one frame of 16 samples whose subframe the body writes.
    private static byte[] OneFrameStream(Action<List<bool>> body) =>
        FlacBuilder.Concat(FlacBuilder.Marker, FlacBuilder.Block(0, FlacBuilder.StreamInfo(16, 44100, 1, 16, 16), isLast: true), FlacBuilder.Frame(0, 16, 44100, 1, 16, false, body));

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
        ["flac.sample-rate-zero"] = () => (FlacBuilder.Concat(FlacBuilder.Marker, FlacBuilder.Block(0, FlacBuilder.StreamInfo(4096, 44100, 2, 16, 8192), isLast: true),
            FlacBuilder.ConstantFrame(0, 4096, 0, 2, 16), FlacBuilder.ConstantFrame(1, 4096, 0, 2, 16)), null),
        ["flac.partition-order-too-high"] = () => (OneFrameStream(body =>
        {
            FlacBuilder.Bits(body, 0, 1);
            FlacBuilder.Bits(body, 9, 6);
            FlacBuilder.Bits(body, 0, 1);
            FlacBuilder.Bits(body, 5, 16);
            FlacBuilder.Bits(body, 0, 2);
            FlacBuilder.Bits(body, 4, 4);
            for (int i = 0; i < 16; i++)
            {
                FlacBuilder.Bits(body, 0, 4);
                if (i > 0)
                {
                    FlacBuilder.Bits(body, 1, 1);
                }
            }
        }), null),
        ["flac.residual-out-of-range"] = () => (OneFrameStream(body =>
        {
            FlacBuilder.Bits(body, 0, 1);
            FlacBuilder.Bits(body, 8, 6);
            FlacBuilder.Bits(body, 0, 1);
            FlacBuilder.Bits(body, 1, 2);
            FlacBuilder.Bits(body, 0, 4);
            FlacBuilder.Bits(body, 30, 5);
            FlacBuilder.Bits(body, 1, 4);
            FlacBuilder.Bits(body, 0x3FFFFFFF, 30);
            for (int i = 1; i < 16; i++)
            {
                FlacBuilder.Bits(body, 1, 1);
                FlacBuilder.Bits(body, 0, 30);
            }
        }), null),
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

    // The findings of blocks past the 1,024 the tree lists point to the node that stands for them, which is in the tree.
    [Fact]
    public void FindingsOnBlocksThatAreNotListedPointToTheNodeThatStandsForThem()
    {
        var blocks = new List<byte>();
        blocks.AddRange(FlacBuilder.Block(0, FlacBuilder.StreamInfo(4096, 44100, 2, 16, 4096 * 2)));
        for (int i = 0; i < 1100; i++)
        {
            blocks.AddRange(FlacBuilder.Block(1, new byte[4]));
        }

        blocks.AddRange(FlacBuilder.Block(0, FlacBuilder.StreamInfo(4096, 44100, 2, 16, 4096 * 2)));
        blocks.AddRange(FlacBuilder.Block(3, new byte[18]));
        blocks.AddRange(FlacBuilder.Block(3, new byte[18]));
        blocks.AddRange(FlacBuilder.Block(1, [1, 2, 3, 4], isLast: true));
        byte[] file = [.. FlacBuilder.Marker, .. blocks, .. FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16), .. FlacBuilder.ConstantFrame(1, 4096, 44100, 2, 16)];

        Report report = Analyzer.CreateDefault().Analyze("many.flac", new MemoryStream(file));

        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        foreach (string code in (string[])["flac.streaminfo-misplaced", "flac.metadata-block-repeated", "flac.padding-not-zero"])
        {
            Finding finding = Assert.Single(report.Findings, f => f.Code == code);
            Assert.Equal("Further Metadata Blocks", finding.Node!.Name);
        }

        TestFiles.CheckInsideParents(report.Root);
    }

    // RFC 9639, section 10.1: a stream joined mid-way may start its granule positions above the samples counted.
    [Fact]
    public void AGranuleOffsetFromTheFirstAudioPageIsNotAMismatch()
    {
        Report offset = Analyzer.CreateDefault().Analyze("live.oga", new MemoryStream(OggFlac(granuleOffset: 100000)));
        Assert.DoesNotContain(offset.Findings, f => f.Code == "flac.ogg-granule-mismatch");

        Report broken = Analyzer.CreateDefault().Analyze("broken.oga", new MemoryStream(OggFlac(granuleOffset: 100000, granule: 5000)));
        Assert.Contains(broken.Findings, f => f.Code == "flac.ogg-granule-mismatch");
    }

    // The faults of the syntax that no fixture reaches: a predictor order above the block size, and a frame that is not over after 16 MiB.
    [Fact]
    public void AnOrderAboveTheBlockSizeMakesTheFrameUnreadable()
    {
        byte[] file = [.. OneFrameStream(body =>
        {
            FlacBuilder.Bits(body, 0, 1);
            FlacBuilder.Bits(body, 32 + 19, 6);
            FlacBuilder.Bits(body, 0, 1);
        }), .. FlacBuilder.ConstantFrame(1, 16, 44100, 1, 16)];

        Report report = Analyzer.CreateDefault().Analyze("order.flac", new MemoryStream(file));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "flac.subframe-invalid");
        Assert.Contains("predictor order above the block size", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFrameThatIsNotOverAfter16MebibytesIsSkipped()
    {
        byte[] head = FlacBuilder.Frame(0, 16, 44100, 1, 16, false, body =>
        {
            FlacBuilder.Bits(body, 0, 1);
            FlacBuilder.Bits(body, 9, 6);
            FlacBuilder.Bits(body, 0, 1);
            FlacBuilder.Bits(body, 0, 16);
            FlacBuilder.Bits(body, 0, 2);
            FlacBuilder.Bits(body, 0, 4);
            FlacBuilder.Bits(body, 0, 4);
        })[..14];
        byte[] file = [.. FlacBuilder.Marker, .. FlacBuilder.Block(0, FlacBuilder.StreamInfo(16, 44100, 1, 16, 16), isLast: true), .. head, .. new byte[(17 << 20)]];

        Report report = Analyzer.CreateDefault().Analyze("long.flac", new MemoryStream(file));

        // No frame follows within 16 MiB, so the walk that never ends is not a frame and the bytes are skipped.
        Assert.Contains(report.Findings, f => f.Code == "flac.data-skipped");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    private static Node OggFlacStream(Node root) => Find(root) ?? throw new InvalidOperationException("No FLAC stream in the tree.");

    private static Node? Find(Node node)
    {
        if (node.Children.Any(c => c.Name == "Mapping Header"))
        {
            return node;
        }

        return node.Children.Select(Find).FirstOrDefault(n => n is not null);
    }

    // A constant frame whose first subframe header says verbatim instead: the frame's header is intact, its subframes read past the end of the data.
    private static byte[] Overrunning(long number)
    {
        byte[] frame = FlacBuilder.ConstantFrame(number, 4096, 44100, 2, 16);
        frame[10] = 0x02;
        return frame;
    }

    // A damaged frame whose walk runs past the end of the file must not take the frames after it: they are found by searching.
    [Fact]
    public void ADamagedFrameThatRunsPastTheEndOfTheFileKeepsTheFramesAfterIt()
    {
        byte[] file = FlacBuilder.Concat(FlacBuilder.Marker, FlacBuilder.Block(0, FlacBuilder.StreamInfo(4096, 44100, 2, 16, 4096 * 6), isLast: true),
            FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16), FlacBuilder.ConstantFrame(1, 4096, 44100, 2, 16), Overrunning(2),
            FlacBuilder.ConstantFrame(3, 4096, 44100, 2, 16), FlacBuilder.ConstantFrame(4, 4096, 44100, 2, 16), FlacBuilder.ConstantFrame(5, 4096, 44100, 2, 16));

        Report report = Analyzer.CreateDefault().Analyze("swallow.flac", new MemoryStream(file));

        Assert.DoesNotContain(report.Findings, f => f.Code is "flac.frame-cut" or "parse.failed");
        Assert.Contains(report.Findings, f => f.Code == "flac.subframe-invalid");
        Assert.Contains(report.Findings, f => f.Code == "flac.crc16-mismatch");
        Assert.Equal(4096L * 6, report.Streams.Single().Samples);
    }

    [Fact]
    public void AFrameThatReallyIsCutByTheEndOfTheFileStaysCut()
    {
        byte[] file = FlacBuilder.Concat(FlacBuilder.Marker, FlacBuilder.Block(0, FlacBuilder.StreamInfo(4096, 44100, 2, 16, 4096 * 2), isLast: true),
            FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16), Overrunning(1));

        Report report = Analyzer.CreateDefault().Analyze("cut.flac", new MemoryStream(file));

        Assert.Contains(report.Findings, f => f.Code == "flac.frame-cut");
    }

    // RFC 9639, section 10.1: a packet is one frame, so a walk that overruns its packet is damaged syntax and the CRC-16 at the packet's end is compared.
    [Fact]
    public void AnOggPacketWhoseFrameOverrunsItIsUnreadableAndItsCrcIsCompared()
    {
        Report report = Analyzer.CreateDefault().Analyze("overrun.oga", new MemoryStream(OggFlac(frames:
            [FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16), Overrunning(1), FlacBuilder.ConstantFrame(2, 4096, 44100, 2, 16)])));

        Assert.DoesNotContain(report.Findings, f => f.Code is "flac.frame-cut" or "parse.failed");
        Assert.Contains(report.Findings, f => f.Code == "flac.subframe-invalid");
        Assert.Contains(report.Findings, f => f.Code == "flac.crc16-mismatch");
    }

    // The header packets past the 1,024th block are counted under one node, as in a native file, whatever their number.
    [Fact]
    public void OggHeaderPacketsPastTheCapAreCountedAndNotListed()
    {
        const int Headers = 3000;
        byte[] info = FlacBuilder.Block(0, FlacBuilder.StreamInfo(4096, 44100, 2, 16, 4096 * 2), isLast: false);
        var ogg = new OggBuilder();
        ogg.Page(Serial, 0, 2, [0x7F, .. "FLAC"u8, 1, 0, 0, 0, .. "fLaC"u8, .. info]);
        for (int at = 0; at < Headers; at += 200)
        {
            int count = Math.Min(200, Headers - at);
            ogg.Page(Serial, 0, 0, [.. Enumerable.Range(at, count).Select(i => FlacBuilder.Block(1, [], isLast: i == Headers - 1))]);
        }

        ogg.Page(Serial, 4096, 0, FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16));
        ogg.Page(Serial, 8192, 4, FlacBuilder.ConstantFrame(1, 4096, 44100, 2, 16));

        Report report = Analyzer.CreateDefault().Analyze("headers.oga", new MemoryStream(ogg.Bytes));

        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Node stream = OggFlacStream(report.Root);
        Node further = Assert.Single(stream.Children, c => c.Name == "Further Metadata Blocks");
        Assert.Equal(NodeValue.Integer(Headers + 1 - Encspect.Formats.Flac.Metadata.MaxListedBlocks, "blocks", "block"), further.Value);
        Assert.Equal(Encspect.Formats.Flac.Metadata.MaxListedBlocks, stream.Children.Count(c => c.Name == "Metadata Block"));
        Assert.Contains(report.Findings, f => f.Code == "flac.metadata-blocks-not-listed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        TestFiles.CheckInsideParents(report.Root);
    }

    // Bytes after a frame in a packet are one finding with a count and one node, not a node for each packet.
    [Fact]
    public void TrailingBytesInManyOggPacketsAreOneFindingAndOneNode()
    {
        byte[][] frames = [.. Enumerable.Range(0, 300).Select(i => (byte[])[.. FlacBuilder.ConstantFrame(i, 4096, 44100, 2, 16), 1])];
        var ogg = new OggBuilder();
        byte[] info = FlacBuilder.Block(0, FlacBuilder.StreamInfo(4096, 44100, 2, 16, 4096L * frames.Length), isLast: true);
        ogg.Page(Serial, 0, 2, [0x7F, .. "FLAC"u8, 1, 0, 0, 0, .. "fLaC"u8, .. info]);
        for (int at = 0; at < frames.Length; at += 100)
        {
            ogg.Page(Serial, 4096L * (at + 100), (byte)(at + 100 == frames.Length ? 4 : 0), frames[at..(at + 100)]);
        }

        Report report = Analyzer.CreateDefault().Analyze("trailing.oga", new MemoryStream(ogg.Bytes));

        Node stream = OggFlacStream(report.Root);
        Assert.Single(stream.Children, c => c.Name == "Trailing Data");
        Finding finding = Assert.Single(report.Findings, f => f.Code == "flac.ogg-packet-trailing");
        Assert.Equal(300, finding.Count);
        Assert.Equal("Trailing Data", finding.Node!.Name);
    }

    // Bytes after a metadata block in a header packet, the mapping packet's included, are reported as after a frame.
    [Fact]
    public void BytesAfterAMetadataBlockInAHeaderPacketAreAFinding()
    {
        byte[] info = FlacBuilder.Block(0, FlacBuilder.StreamInfo(4096, 44100, 2, 16, 4096 * 2), isLast: false);
        foreach (bool inMapping in new[] { true, false })
        {
            var ogg = new OggBuilder();
            ogg.Page(Serial, 0, 2, [0x7F, .. "FLAC"u8, 1, 0, 0, 0, .. "fLaC"u8, .. info, .. inMapping ? new byte[] { 9, 9, 9 } : []]);
            ogg.Page(Serial, 0, 0, [.. FlacBuilder.Block(1, [0, 0], isLast: true), .. inMapping ? [] : new byte[] { 7 }]);
            ogg.Page(Serial, 4096, 0, FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16));
            ogg.Page(Serial, 8192, 4, FlacBuilder.ConstantFrame(1, 4096, 44100, 2, 16));

            Report report = Analyzer.CreateDefault().Analyze("block.oga", new MemoryStream(ogg.Bytes));

            Finding finding = Assert.Single(report.Findings, f => f.Code == "flac.ogg-packet-trailing");
            Assert.Contains("metadata block", finding.Message, StringComparison.Ordinal);
            Assert.NotNull(finding.Path);
        }
    }

    // A metadata block over the 1 MiB that is read, in a packet that spans pages, is a node over the whole block.
    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public void AnOggMetadataBlockOverOneMebibyteIsOneNodeOverAllOfIt(int type, bool isComment)
    {
        byte[] body = isComment ? [.. new byte[4], .. BitConverter.GetBytes(0), .. new byte[(1 << 20) + 5000]] : new byte[(1 << 20) + 5000];
        byte[] info = FlacBuilder.Block(0, FlacBuilder.StreamInfo(4096, 44100, 2, 16, 4096 * 2), isLast: false);
        var ogg = new OggBuilder();
        ogg.Page(Serial, 0, 2, [0x7F, .. "FLAC"u8, 1, 0, 0, 0, .. "fLaC"u8, .. info]);
        ogg.SpanningPacket(Serial, FlacBuilder.Block(type, body, isLast: true), 0);
        ogg.Page(Serial, 4096, 0, FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16));
        ogg.Page(Serial, 8192, 4, FlacBuilder.ConstantFrame(1, 4096, 44100, 2, 16));

        Report report = Analyzer.CreateDefault().Analyze("big.oga", new MemoryStream(ogg.Bytes));

        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Node stream = OggFlacStream(report.Root);
        Node block = stream.Children.Where(c => c.Name == "Metadata Block").ElementAt(1);
        Assert.True(block.Length > body.Length);
        Assert.NotNull(block.Ranges);
        Assert.Equal(block.Offset + block.Length, block.Ranges![^1].End);
        TestFiles.CheckInsideParents(report.Root);
    }

    // Pages the Ogg layer lost before the first frame are named as a cause of its starting above sample 0.
    [Fact]
    public void AFirstFrameAfterLostPagesIsNamedForBothCauses()
    {
        byte[] file = OggFlac();
        int page = 0;
        int at = -1;
        for (int i = 0; i + 4 <= file.Length && page < 2; i++)
        {
            if (file.AsSpan(i, 4).SequenceEqual("OggS"u8))
            {
                page++;
                at = i;
            }
        }

        file[at + 28] ^= 0xFF;

        Report report = Analyzer.CreateDefault().Analyze("lost.oga", new MemoryStream(file));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "flac.first-sample-not-zero");
        Assert.Contains("lost", finding.Message, StringComparison.Ordinal);
        Assert.Contains("cut out", finding.Message, StringComparison.Ordinal);
    }
}
