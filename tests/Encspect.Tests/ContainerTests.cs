using System.Text;
using System.Text.RegularExpressions;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The host's routes for containers (ParseStream, ParsePackets, ParseTag) and the end a container
// declares, through the test-only containers in TestContainers.
public partial class ContainerTests
{
    // The RIFF header and the chunk header before the first chunk's data.
    private const long FirstChunkData = 20;

    private static readonly byte[] Blade = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "mpeg", "external-blade-flags.mp3"));

    [GeneratedRegex("0x[0-9A-F]+")]
    private static partial Regex HexNumber();

    [Theory]
    [InlineData("mpeg/external-blade-flags.mp3")]
    [InlineData("mpeg/mpeg1-layer3-crc-mismatch.mp3")]
    [InlineData("mpeg/damaged-bitrate.mp3")]
    [InlineData("mpeg/channel-mode-change.mp3")]
    [InlineData("mpeg/truncated-last-frame.mp3")]
    [InlineData("mpeg/trailing-garbage.mp3")]
    [InlineData("mpeg/stream-change-after-gap.mp3")]
    [InlineData("mpeg/leading-junk.mp3")]
    [InlineData("layer2/crc-mismatch.mp2")]
    public void MpegPayloadIsAnalyzedAsTheSameBytesInAFile(string fixture)
    {
        byte[] audio = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        Report alone = Analyzer.CreateDefault().Analyze("alone", new MemoryStream(audio));
        Report wrapped = TestFiles.Analyze(ChunkedContainer.Build("RIFF", "TEST", ("mpga", audio)), new ChunkedContainer());

        Node chunk = Chunk(wrapped, "mpga");
        Assert.Equal(alone.Root.Children.Count, chunk.Children.Count);
        for (int i = 0; i < chunk.Children.Count; i++)
        {
            CheckShifted(alone.Root.Children[i], chunk.Children[i]);
        }

        List<Finding> mpeg = wrapped.Findings.Where(f => f.Node is { } node && chunk.Offset <= node.Offset && node.End <= chunk.End).ToList();
        Assert.Equal(alone.Findings.Count, mpeg.Count);
        Assert.Equal(alone.Findings.Count, wrapped.Findings.Count);
        foreach ((Finding expected, Finding actual) in alone.Findings.Zip(mpeg))
        {
            Assert.Equal((expected.Severity, expected.Code, expected.Count), (actual.Severity, actual.Code, actual.Count));
            Assert.True(SameExceptOffsets(expected.Message, actual.Message), $"{expected.Message} | {actual.Message}");
            if (expected.Node == alone.Root)
            {
                Assert.Same(chunk, actual.Node);
            }
            else
            {
                Assert.Equal((expected.Node!.Name, expected.Node.Offset + FirstChunkData, expected.Node.Length), (actual.Node!.Name, actual.Node.Offset, actual.Node.Length));
            }

            Node found = TestFiles.Follow(wrapped.Root, actual.Path!);
            Assert.Equal((actual.Node!.Name, actual.Node.Offset, actual.Node.Length), (found.Name, found.Offset, found.Length));
        }

        Assert.Equal(alone.Streams.Count, wrapped.Streams.Count);
        foreach ((StreamInfo expected, StreamInfo actual) in alone.Streams.Zip(wrapped.Streams))
        {
            Assert.Equal(
                (expected.Codec, expected.SampleRate, expected.Channels, expected.ChannelMode, expected.BitsPerSample, expected.Samples, expected.Duration, expected.AverageBitrate),
                (actual.Codec, actual.SampleRate, actual.Channels, actual.ChannelMode, actual.BitsPerSample, actual.Samples, actual.Duration, actual.AverageBitrate));
            Assert.Equal((expected.IsVbr, expected.Encoder, expected.EncoderConfidence), (actual.IsVbr, actual.Encoder, actual.EncoderConfidence));
            Assert.All(actual.NodePaths, path => Assert.NotNull(path));
            Assert.Equal(expected.Series.Count, actual.Series.Count);
            foreach ((Series a, Series b) in expected.Series.Zip(actual.Series))
            {
                double shift = a.Axis == SeriesAxis.FrameOffset ? FirstChunkData : 0;
                Assert.Equal((a.Name, a.Unit, a.Axis), (b.Name, b.Unit, b.Axis));
                Assert.Equal(a.Values.ToArray(), b.Values.ToArray());
                Assert.Equal(a.X.ToArray().Select(x => x + shift), b.X.ToArray());
            }
        }
    }

    [Fact]
    public void AnId3ChunkGoesToTheTagParserAndItsClaimToTheCodec()
    {
        byte[] tag = TestFiles.Id3v23(("TIT2", "Title"), ("TIT2", "Again"), ("TSSE", "BladeEnc 0.94.2"));
        byte[] file = ChunkedContainer.Build("RIFF", "TEST", ("id3 ", tag), ("mpga", Blade));

        Report report = TestFiles.Analyze(file, new ChunkedContainer());

        Node chunk = Chunk(report, "id3 ");
        Node shown = Assert.Single(chunk.Children);
        Assert.Equal(("ID3v2", NodeKind.Tag, chunk.Offset + 8, (long)tag.Length), (shown.Name, shown.Kind, shown.Offset, shown.Length));
        Assert.Equal("Medium", shown.Children.Single(c => c.Name == "Detection").Value?.ToString());
        Assert.DoesNotContain(report.Root.Children, c => c.Kind == NodeKind.Tag);
        Assert.Equal(["ID3v2"], ReportSummary.From(report).Tags);

        Finding duplicate = Assert.Single(report.Findings, f => f.Code == "id3v2.frame-duplicate");
        Node frame = TestFiles.Follow(report.Root, duplicate.Path!);
        Assert.Equal((duplicate.Node!.Name, duplicate.Node.Offset), (frame.Name, frame.Offset));
        Assert.True(shown.Offset <= frame.Offset && frame.End <= shown.End);

        // BladeEnc alone is low confidence; the tag's claim agrees with it.
        Assert.Equal(Confidence.Low, Analyzer.CreateDefault().Analyze("blade.mp3", new MemoryStream(Blade)).Streams[0].EncoderConfidence);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(("BladeEnc", Confidence.Medium), (stream.Encoder, stream.EncoderConfidence));
        AssertPathsResolve(report);
    }

    [Fact]
    public void AClaimThatDiffersPointsAtTheFrameInsideTheContainer()
    {
        byte[] file = ChunkedContainer.Build("RIFF", "TEST", ("id3 ", TestFiles.Id3v23(("TSSE", "LAME3.99"))), ("mpga", Blade));

        Report report = TestFiles.Analyze(file, new ChunkedContainer());

        Finding differs = Assert.Single(report.Findings, f => f.Code == "mpeg.encoder-claim-differs");
        Node frame = TestFiles.Follow(report.Root, differs.Path!);
        Assert.Equal("TSSE", frame.Name);
        Node chunk = Chunk(report, "id3 ");
        Assert.True(chunk.Offset < frame.Offset && frame.End <= chunk.End);
        Assert.Equal(Confidence.Low, report.Streams[0].EncoderConfidence);
        AssertPathsResolve(report);
    }

    [Fact]
    public void ATrailingId3ChunkStaysInTheContainer()
    {
        Report report = TestFiles.Analyze(TestFiles.AiffWithId3Chunk(), new ChunkedContainer());

        Node form = Assert.Single(report.Root.Children);
        Assert.Equal(("FORM", 0L, 0x142L), (form.Name, form.Offset, form.Length));
        Node chunk = form.Children[^1];
        Assert.Equal("ID3 ", chunk.Value?.ToString());
        Node tag = Assert.Single(chunk.Children);
        Assert.Equal(("ID3v2", NodeKind.Tag, 0x106L, 60L), (tag.Name, tag.Kind, tag.Offset, tag.Length));
        Assert.Equal("High", tag.Children.Single(c => c.Name == "Detection").Value?.ToString());

        Finding nearMiss = Assert.Single(report.Findings, f => f.Code == "tag.near-miss");
        Assert.Equal(Severity.Info, nearMiss.Severity);
        Assert.Contains("part of the Test chunks data, which by its own size extends to 0x142", nearMiss.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Severity >= Severity.Warning);
        Assert.Equal(["ID3v2"], ReportSummary.From(report).Tags);
        AssertPathsResolve(report);
    }

    // What the contract review found: without a declared end the host takes the chunk for a tag at
    // the end of the file and cuts the container short.
    [Fact]
    public void WithoutADeclaredEndTheChunkIsCarvedOut()
    {
        Report report = TestFiles.Analyze(TestFiles.AiffWithId3Chunk(), new ChunkedContainer(declaresEnd: false));

        Assert.Contains(report.Root.Children, c => c is { Kind: NodeKind.Tag, Name: "ID3v2", Offset: 0x106 });
        Assert.Contains(report.Findings, f => f.Code == "test.container-truncated");
    }

    [Fact]
    public void ATagAfterTheDeclaredEndBelongsToTheFile()
    {
        Report report = TestFiles.Analyze(TestFiles.AiffWithId3Chunk(formSize: 0xFE - 8), new ChunkedContainer());

        Assert.Equal(
            [("FORM", 0L, 0xFEL), ("Trailing Data", 0xFEL, 8L), ("ID3v2", 0x106L, 60L)],
            report.Root.Children.Select(c => (c.Name, c.Offset, c.Length)));
        Assert.Equal(NodeKind.Tag, report.Root.Children[^1].Kind);
        Assert.DoesNotContain(report.Findings, f => f.Code == "tag.near-miss");
    }

    [Fact]
    public void ATagAppendedAfterTheContainerBelongsToTheFile()
    {
        byte[] id3v1 = [.. "TAG"u8, .. Encoding.ASCII.GetBytes("Appended".PadRight(30, '\0')), .. new byte[95]];
        byte[] file = [.. ChunkedContainer.Build("RIFF", "TEST", ("mpga", Blade)), .. id3v1];

        Report report = TestFiles.Analyze(file, new ChunkedContainer());

        Assert.Equal(["RIFF", "ID3v1"], report.Root.Children.Select(c => c.Name));
        Assert.Single(report.Streams);
        Assert.DoesNotContain(report.Findings, f => f.Code is "test.container-truncated" or "tag.near-miss");
    }

    [Fact]
    public void FailuresInRoutedParsersBecomeFindingsAndTheContainerCarriesOn()
    {
        var codecs = new Dictionary<string, string> { ["boom"] = "explode", ["trnc"] = "truncate", ["invd"] = "invalid", ["mpga"] = "mpeg-audio", ["what"] = "unknown" };
        var tags = new Dictionary<string, string> { ["bang"] = "Explosive" };
        byte[] file = ChunkedContainer.Build("RIFF", "TEST", ("boom", [1, 2]), ("trnc", [1, 2]), ("invd", [1, 2]), ("bang", [1, 2]), ("what", [1, 2]), ("mpga", Blade));

        Report report = TestFiles.Analyze(
            file,
            new ChunkedContainer(codecs: codecs, tags: tags),
            [
                .. DefaultParsers.Codecs,
                new ActionCodec("explode", _ => throw new NullReferenceException("codec bug")),
                new ActionCodec("truncate", context => context.Payload!.ReadBytes(3)),
                new ActionCodec("invalid", _ => throw new InvalidDataException("The payload is invalid.")),
            ],
            [.. DefaultParsers.Tags, new ActionTagParser("Explosive", _ => throw new IndexOutOfRangeException("tag bug"))]);

        Assert.Equal(("boom", "The explode codec parser failed parsing the data at 0xC: NullReferenceException: codec bug"), Single(report, "parse.failed", "explode"));
        Assert.Equal(("trnc", "The truncate data at 0x16 ended in the middle of a structure."), Single(report, "parse.truncated"));
        Assert.Equal(("invd", "The payload is invalid."), Single(report, "parse.invalid"));
        Assert.Equal(("bang", "The Explosive parser failed parsing a tag at 0x32: IndexOutOfRangeException: tag bug"), Single(report, "parse.failed", "Explosive"));
        Assert.Equal(("bang", "Explosive candidate at 0x32 rejected: the parser failed with IndexOutOfRangeException."), Single(report, "tag.rejected"));
        Assert.Equal(("what", "No codec parser for unknown."), Single(report, "test.codec-unknown"));
        Assert.Single(report.Streams);
        AssertPathsResolve(report);
    }

    [Fact]
    public void ATagRouteThatFindsNoTagSaysSo()
    {
        Report report = TestFiles.Analyze(ChunkedContainer.Build("RIFF", "TEST", ("id3 ", [1, 2, 3, 4])), new ChunkedContainer());

        Assert.Equal(("id3 ", "No ID3v2 tag starts at 0x14, where the container places one."), Single(report, "tag.not-found"));
        Assert.Empty(Chunk(report, "id3 ").Children);
    }

    [Fact]
    public void RoutesThatNestTooDeepEndInAFinding()
    {
        var loop = new ActionCodec("loop", context => context.ParseStream("loop", context.Parent, context.Payload!));
        byte[] file = ChunkedContainer.Build("RIFF", "TEST", ("loop", [1, 2]));

        Report report = TestFiles.Analyze(file, new ChunkedContainer(codecs: new Dictionary<string, string> { ["loop"] = "loop" }), [loop]);

        Finding deep = Assert.Single(report.Findings);
        Assert.Equal(("parse.too-deep", Severity.Error), (deep.Code, deep.Severity));
        Assert.Contains("more than 8 levels", deep.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("tag")]
    [InlineData("packets")]
    public void CancellationPassesThroughTheRoutes(string route)
    {
        using var cancellation = new CancellationTokenSource();
        var options = new AnalysisOptions { CancellationToken = cancellation.Token };
        void Cancel(CancellationToken token)
        {
            cancellation.Cancel();
            Assert.True(token.IsCancellationRequested);
        }

        Func<Report> analyze = route switch
        {
            "stream" => () => TestFiles.Analyze(
                ChunkedContainer.Build("RIFF", "TEST", ("mpga", [1, 2])),
                new ChunkedContainer(),
                [new ActionCodec("mpeg-audio", context => { Cancel(context.CancellationToken); context.Payload!.ReadByte(); })],
                options: options),
            "tag" => () => TestFiles.Analyze(
                ChunkedContainer.Build("RIFF", "TEST", ("id3 ", [1, 2])),
                new ChunkedContainer(),
                tags: [new ActionTagParser("ID3v2", context => { Cancel(context.CancellationToken); context.Reader.ReadByte(); return null; })],
                options: options),
            _ => () => TestFiles.Analyze(
                PagedContainer.Build(4, new byte[300], new byte[300]).File,
                new PagedContainer(),
                [new ActionCodec(PacketCodec.CodecId, context => { Cancel(context.CancellationToken); _ = context.Packets!.Count(); })],
                options: options),
        };

        Assert.Throws<OperationCanceledException>(() => analyze());
    }

    [Fact]
    public void PacketsThatSpanPagesMapBackToTheFile()
    {
        var random = new Random(3);
        byte[][] packets = [Bytes(random, 100), Bytes(random, 600), Bytes(random, 10), Bytes(random, 300), Bytes(random, 255)];
        (byte[] file, List<List<ByteRange>> expected) = PagedContainer.Build(2, packets);
        var codec = new PacketCodec();

        Report report = TestFiles.Analyze(file, new PagedContainer(), [codec]);

        Node[] nodes = report.Root.Children.Single().Children.Where(c => c.Name == "Packet").ToArray();
        Assert.Equal(packets.Length, codec.Packets.Count);
        Assert.Equal(packets.Length, nodes.Length);
        for (int i = 0; i < packets.Length; i++)
        {
            List<ByteRange> ranges = Merge(expected[i]);
            Assert.Equal(packets[i], codec.Packets[i].Span.ToArray());
            Assert.Equal(ranges, codec.Packets[i].FileRanges());
            Assert.Equal(ranges, TestFiles.RangesOf(nodes[i]));
            Assert.Equal((ranges[0].Offset, ranges[^1].End), (nodes[i].Offset, nodes[i].End));
            Assert.Equal(packets[i], ranges.SelectMany(r => file.AsSpan((int)r.Offset, (int)r.Length).ToArray()).ToArray());
            Assert.Equal(file[(int)ranges[0].Offset], nodes[i].Children.Single(c => c.Name == "Type").Value!.AsInteger());
        }

        // Packets 1 and 3 span pages; 4 is 255 bytes and an empty segment on the next page.
        Assert.Equal([false, true, false, true, false], nodes.Select(n => n.Ranges is not null));

        Assert.Equal([1, 3], codec.Fields.Select(f => codec.Packets.IndexOf(f.Packet)));
        foreach ((MappedBytes packet, BitReader bits, long startBit) in codec.Fields)
        {
            int index = codec.Packets.IndexOf(packet);
            Node field = nodes[index].Children.Single(c => c.Name == "Field");
            IReadOnlyList<ByteRange> ranges = packet.FileRanges();
            int last = (int)ranges[0].Length - 1;
            Assert.Equal(((packets[index][last] & 0xF) << 8) | packets[index][last + 1], field.Value!.AsInteger());
            Assert.Equal((4, 12), (field.BitOffset, field.BitLength));
            Assert.Equal([new ByteRange(ranges[0].End - 1, 1), new ByteRange(ranges[1].Offset, 1)], field.Ranges);
            Assert.Equal(ranges[0].End - 1, bits.FileOffsetOf((int)startBit));
            Assert.Equal(ranges[1].Offset, bits.FileOffsetOf((int)startBit + 4));
            Assert.Equal(packet.FileEnd, bits.FileOffsetOf(bits.Length));
        }

        Finding straddles = Assert.Single(report.Findings);
        Assert.Equal("test.field-straddles-pages", straddles.Code);
        Node found = TestFiles.Follow(report.Root, straddles.Path!);
        Assert.Equal((straddles.Node!.Name, straddles.Node.Offset, straddles.Node.Length), (found.Name, found.Offset, found.Length));
        Assert.NotNull(found.Ranges);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Same(nodes[0], TestFiles.Follow(report.Root, stream.NodePaths.Single()!));
        TestFiles.CheckInsideParents(report.Root);

        string json = Fixtures.ToJson(report);
        Assert.Contains($"\"ranges\": [\n", json, StringComparison.Ordinal);
        Assert.Contains($"\"offset\": {nodes[1].Ranges![1].Offset},\n", json, StringComparison.Ordinal);
        var text = new StringWriter();
        ReportTextWriter.Write(report, text, showOffsets: true);
        IReadOnlyList<ByteRange> spanning = nodes[1].Ranges!;
        Assert.Contains(
            $"Packet: 600 bytes [0x{nodes[1].Offset:X}, {nodes[1].Length} bytes in 0x{spanning[0].Offset:X} {spanning[0].Length} bytes, 0x{spanning[1].Offset:X} {spanning[1].Length} bytes]",
            text.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ACodecWithoutPacketSupportSaysSo()
    {
        Report report = TestFiles.Analyze(PagedContainer.Build(4, new byte[10]).File, new PagedContainer("mpeg-audio"));

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("mpeg.packets-not-analyzed", "Pages"), (finding.Code, finding.Node?.Name));
        Assert.Empty(report.Streams);
    }

    private static byte[] Bytes(Random random, int count)
    {
        var bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }

    private static List<ByteRange> Merge(List<ByteRange> ranges)
    {
        var merged = new List<ByteRange>();
        foreach (ByteRange range in ranges.Where(r => r.Length > 0))
        {
            if (merged.Count > 0 && merged[^1].End == range.Offset)
            {
                merged[^1] = merged[^1] with { Length = merged[^1].Length + range.Length };
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private static Node Chunk(Report report, string id) =>
        report.Root.Children.Single(c => c.Name is "RIFF" or "FORM").Children.Single(c => c.Value?.ToString() == id);

    // The chunk ID of the one finding with that code (and text in its message), and its message.
    private static (string? Chunk, string Message) Single(Report report, string code, string? containing = null)
    {
        Finding finding = Assert.Single(report.Findings, f => f.Code == code && (containing is null || f.Message.Contains(containing, StringComparison.Ordinal)));
        return (finding.Node?.Value?.ToString(), finding.Message);
    }

    private static void AssertPathsResolve(Report report)
    {
        Assert.All(report.Findings, finding =>
        {
            Assert.NotNull(finding.Path);
            Node found = TestFiles.Follow(report.Root, finding.Path);
            Assert.Equal((finding.Node!.Name, finding.Node.Offset, finding.Node.Length), (found.Name, found.Offset, found.Length));
        });
        Assert.All(report.Streams, stream => Assert.All(stream.NodePaths, path => Assert.NotNull(path)));
        TestFiles.CheckInsideParents(report.Root);
    }

    // The same node, moved by the offset of the payload in the container.
    private static void CheckShifted(Node expected, Node actual)
    {
        Assert.Equal((expected.Name, expected.Kind, expected.Offset + FirstChunkData, expected.Length), (actual.Name, actual.Kind, actual.Offset, actual.Length));
        Assert.Equal((expected.BitOffset, expected.BitLength, expected.Value?.Kind, expected.Value?.Unit), (actual.BitOffset, actual.BitLength, actual.Value?.Kind, actual.Value?.Unit));
        Assert.True(SameExceptOffsets(expected.Value?.ToString(), actual.Value?.ToString()), $"{expected.Name}: {expected.Value} | {actual.Value}");
        Assert.Null(actual.Ranges);
        Assert.Equal(expected.Children.Count, actual.Children.Count);
        for (int i = 0; i < expected.Children.Count; i++)
        {
            CheckShifted(expected.Children[i], actual.Children[i]);
        }
    }

    // Equal, but for hexadecimal numbers that are offsets, which are FirstChunkData larger.
    private static bool SameExceptOffsets(string? expected, string? actual)
    {
        if (expected == actual)
        {
            return true;
        }

        if (expected is null || actual is null || HexNumber().Replace(expected, "#") != HexNumber().Replace(actual, "#"))
        {
            return false;
        }

        return HexNumber().Matches(expected).Zip(HexNumber().Matches(actual)).All(pair =>
        {
            long a = Convert.ToInt64(pair.First.Value[2..], 16);
            long b = Convert.ToInt64(pair.Second.Value[2..], 16);
            return a == b || a + FirstChunkData == b;
        });
    }
}
