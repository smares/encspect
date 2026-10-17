using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Matroska;
using Encspect.Host;

namespace Encspect.Tests;

// One crafted file for each finding the Matroska parser emits that no fixture's manifest lists, and the finding that must come
// out. FindingCoverageTests checks that every code the parser emits is asserted somewhere.
public class MatroskaFindingTests
{
    private static readonly Dictionary<string, Func<byte[]>> Crafts = new()
    {
        // A block of 70 MiB, past the 64 MiB the parser reads a block up to.
        ["matroska.block-too-large"] = () => MatroskaBuilder.File(MatroskaBuilder.OpusTracks(),
            MatroskaBuilder.Cluster(0, MatroskaBuilder.SimpleBlock(1, 0, new byte[70 << 20]))),
        // More elements than the node budget, as Void elements.
        ["matroska.too-many-elements"] = () => MatroskaBuilder.File([MatroskaBuilder.OpusTracks(), .. Enumerable.Repeat(MatroskaBuilder.Void(0), 260_000)]),
        // A DocType that is neither matroska nor webm, which only a parser handed the file reads.
        ["matroska.doctype-unknown"] = () => MatroskaBuilder.Concat(MatroskaBuilder.Header("mkvfile"), MatroskaBuilder.Segment(MatroskaBuilder.OpusTracks())),
        // A sampling frequency of 44100 Hz for AC-3 frames coded at 48000 Hz.
        ["matroska.sample-rate-differs"] = () =>
        {
            byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "matroska", "external-mkvmerge-ac3.mka"));
            // mkvmerge writes the SamplingFrequency as a 4-byte float.
            byte[] from = [0xB5, 0x84, 0, 0, 0, 0];
            BinaryPrimitives.WriteSingleBigEndian(from.AsSpan(2), 48000f);
            byte[] to = (byte[])from.Clone();
            BinaryPrimitives.WriteSingleBigEndian(to.AsSpan(2), 44100f);
            int at = data.AsSpan().IndexOf(from);
            Assert.True(at > 0, "the fixture has no SamplingFrequency of 48000 Hz");
            to.CopyTo(data, at);
            return data;
        },
    };

    public static TheoryData<string> Codes => [.. Crafts.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Codes))]
    public void CraftedFileRaisesTheFinding(string code)
    {
        Analyzer analyzer = code == "matroska.doctype-unknown" ? new Analyzer([new ForcedMatroska()]) : Analyzer.CreateDefault();
        Report report = analyzer.Analyze("crafted.mka", new MemoryStream(Crafts[code]()));

        Finding finding = Assert.Single(report.Findings, f => f.Code == code);
        Assert.NotNull(finding.Path);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        TestFiles.CheckInsideParents(report.Root);
    }

    // A tag appended to a live recording (Segment and Clusters of unknown size) belongs to the file: the Matroska parser's declared end stops
    // at the last element the schema places there.
    [Theory]
    [InlineData("matroska/live-id3v1.mka", new[] { "ID3v1" })]
    [InlineData("matroska/live-ape-id3v1.mka", new[] { "APE", "ID3v1" })]
    [InlineData("matroska/live-ape-large.mka", new[] { "APE" })]
    [InlineData("matroska/live-ape-binary.mka", new[] { "APE" })]
    [InlineData("matroska/live-ape-footer-only.mka", new[] { "APE" })]
    public void ATagAfterALiveRecordingIsATag(string name, string[] tags)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        Assert.Equal(tags, report.Root.Children.Where(c => c.Kind == NodeKind.Tag).Select(c => c.Name));
        Assert.DoesNotContain(report.Findings, f => f.Code is "matroska.element-truncated" or "matroska.unknown-element" || f.Code.StartsWith("tag.", StringComparison.Ordinal));
        Assert.Single(report.Streams);
    }

    // Whatever bytes a binary APEv2 item holds, the tag after a live recording is a tag: its data may look like Void, CRC-32 or Cluster child
    // elements, even ones that run past the data, and none of them makes the unknown element before it (the header's "APETAGEX") part of the recording.
    [Fact]
    public void ABinaryApeItemIsNeverSwallowedByTheRecording()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "matroska", "live-ape-binary.mka"));
        int tag = original.AsSpan().IndexOf("APETAGEX"u8);
        int value = tag + 32 + (8 + 5 + 1 + 16) + 8 + 17 + 1;
        var random = new Random(7);
        for (int i = 0; i < 400; i++)
        {
            byte[] data = (byte[])original.Clone();
            random.NextBytes(data.AsSpan(value, 4000));
            // Every other case puts an element ID where the walk lands.
            if (i % 2 == 0)
            {
                byte[] ids = [0xEC, 0xBF, 0xE7, 0xA3, 0xA0, 0xA7, 0xAB];
                data[tag + 1368] = ids[random.Next(ids.Length)];
                data[tag + 1369] = (byte)(i % 4 == 0 ? 0x84 : 0x5F);
            }

            Report report = Analyzer.CreateDefault().Analyze("live.mka", new MemoryStream(data));

            Assert.True(report.Root.Children.Any(c => c.Name == "APE"), $"case {i}: {string.Join(", ", report.Root.Children.Select(c => c.Name))}");
        }
    }

    // The same for a tag without a header: its first item's length bytes (a Cluster child ID, then 0x01) read as an element whose size runs far past the
    // data, and the APEv2 footer says where the tag starts.
    [Fact]
    public void AHeaderlessBinaryApeTagIsNeverSwallowedByTheRecording()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "matroska", "live-ape-footer-only.mka"));
        byte[] recording = original[..^(8 + 6 + 0x1A3 + 32)];
        var random = new Random(11);
        foreach (byte id in new byte[] { 0xEC, 0xBF, 0xA3, 0xA0, 0xA7, 0xAB, 0xE7 })
        {
            for (int i = 0; i < 40; i++)
            {
                byte[] value = new byte[id + 0x100];
                random.NextBytes(value);
                byte[] item = [.. BitConverter.GetBytes((uint)value.Length), .. BitConverter.GetBytes(2u), .. "Cover\0"u8, .. value];
                byte[] footer = [.. "APETAGEX"u8, .. BitConverter.GetBytes(2000u), .. BitConverter.GetBytes((uint)(item.Length + 32)), .. BitConverter.GetBytes(1u), .. new byte[12]];

                Report report = Analyzer.CreateDefault().Analyze("live.mka", new MemoryStream([.. recording, .. item, .. footer]));

                Assert.True(report.Root.Children.Any(c => c.Name == "APE"), $"ID 0x{id:X2}, case {i}: {string.Join(", ", report.Root.Children.Select(c => c.Name))}");
            }
        }
    }

    // The Tags element is the file's own tags: the summary lists it and the tags filter hides it, as for MP4's item list.
    [Fact]
    public void TheTagsElementIsATagOfTheFile()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/crc-valid.mka");

        Node tags = Descendants(report.Root).Single(n => n.Name == "Tags");
        Assert.Equal(NodeKind.Tag, tags.Kind);
        Assert.Equal(["Tags"], ReportSummary.From(report).Tags);
        Assert.False(new ReportFilter(ReportSections.Tags).Includes(tags));
        Assert.True(ReportFilter.All.Includes(tags));
    }

    // The findings of the limits point at a node that exists, named and sized as the finding says.
    [Fact]
    public void TheLacedFramesFindingPointsAtTheBlock()
    {
        byte[] cluster = MatroskaBuilder.Cluster(0, [.. Enumerable.Repeat(MatroskaBuilder.EmptyLacedBlock(), 20_000)]);

        Report report = Analyzer.CreateDefault().Analyze("laced.mka", new MemoryStream(MatroskaBuilder.Concat(MatroskaBuilder.Header(), MatroskaBuilder.Segment(cluster))));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "matroska.too-many-elements");
        Node node = NodeOf(report, finding);
        Assert.Equal("SimpleBlock", node.Name);
        Assert.Equal(7, node.Length);
        Assert.Contains($"0x{node.Offset:X}", finding.Message, StringComparison.Ordinal);
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    // The node budget stops the elements outside Clusters and nothing else: the Clusters behind the elements it skipped are read, the skipped
    // elements are one Unparsed Data node that the finding names by where it starts, and what the skipped elements may hold is not reported missing.
    [Fact]
    public void ClustersAreReadPastTheNodeBudget()
    {
        byte[] voids = MatroskaBuilder.Concat([.. Enumerable.Repeat(MatroskaBuilder.Void(0), FileReader.MaxNodes + 10_000)]);
        byte[] cluster = MatroskaBuilder.Cluster(0, MatroskaBuilder.SimpleBlock(1, 0, [0xFC, 0xFF, 0xFE]), MatroskaBuilder.SimpleBlock(1, 20, [0xFC, 0xFF, 0xFE]));
        byte[] file = MatroskaBuilder.File(MatroskaBuilder.Info(), MatroskaBuilder.OpusTracks(), voids, cluster, voids, cluster);

        Report report = Analyzer.CreateDefault().Analyze("budget.mka", new MemoryStream(file));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "matroska.too-many-elements");
        Node node = NodeOf(report, finding);
        Assert.Equal("Unparsed Data", node.Name);
        Assert.Contains($"0x{node.Offset:X}", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Code is "matroska.no-clusters" or "matroska.missing-info" or "matroska.missing-tracks" or "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("Opus", stream.Codec);
        Assert.InRange(stream.Duration, 0.07, 0.09);
        Node segment = Descendants(report.Root).Single(n => n.Name == "Segment");
        Assert.Equal(2, segment.Children.Count(c => c.Name == "Cluster"));
        TestFiles.CheckInsideParents(report.Root);
    }

    // A file of empty Segments is not read on past the node budget, each Segment counting FileReader.SegmentNodeCost of it: the rest is one
    // Unparsed Data node. A live stream of fewer Segments than that is read in full.
    [Fact]
    public void ManySegmentsAreLimitedByTheNodeBudget()
    {
        int limit = (FileReader.MaxNodes / FileReader.SegmentNodeCost) + 100;
        byte[] file = MatroskaBuilder.Concat([MatroskaBuilder.Header(), .. Enumerable.Repeat<byte[]>([0x18, 0x53, 0x80, 0x67, 0x80], limit)]);

        Report report = Analyzer.CreateDefault().Analyze("segments.mka", new MemoryStream(file));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "matroska.too-many-elements");
        Assert.Equal("Unparsed Data", NodeOf(report, finding).Name);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        Assert.Equal(FileReader.MaxNodes / FileReader.SegmentNodeCost, Descendants(report.Root).Count(n => n.Name == "Segment"));
    }

    // The documents of a live stream (RFC 9559 section 23.2), each with its EBML Header (RFC 8794 section 11.1), an Info element, an Opus track and a
    // block: 3,000 of them are all read.
    [Fact]
    public void ThreeThousandDocumentsOfAStreamAreRead()
    {
        byte[] file = MatroskaBuilder.Concat([.. Enumerable.Repeat(MatroskaBuilder.Document(), 3_000)]);

        Report report = Analyzer.CreateDefault().Analyze("stream.mka", new MemoryStream(file));

        Assert.DoesNotContain(report.Findings, f => f.Code is "matroska.too-many-elements" or "parse.failed");
        Assert.Equal(3_000, Descendants(report.Root).Count(n => n.Name == "Segment"));
        Assert.Equal(3_000, report.Streams.Count);
    }

    // The budget runs out inside the Tracks element, whose TrackEntry 1 is skipped: the blocks that name it are not reported as naming no track.
    [Fact]
    public void BlocksOfATrackInSkippedElementsAreNotUnknown()
    {
        byte[] tracks = MatroskaBuilder.Element(0x1654AE6B, MatroskaBuilder.Concat([.. Enumerable.Repeat(MatroskaBuilder.Void(0), FileReader.MaxNodes + 10)]), MatroskaBuilder.OpusTrack(1));
        byte[] cluster = MatroskaBuilder.Cluster(0, MatroskaBuilder.SimpleBlock(1, 0, [0xFC, 0xFF, 0xFE]), MatroskaBuilder.SimpleBlock(1, 20, [0xFC, 0xFF, 0xFE]));

        Report report = Analyzer.CreateDefault().Analyze("tracks.mka", new MemoryStream(MatroskaBuilder.File(MatroskaBuilder.Info(), tracks, cluster)));

        Assert.Contains(report.Findings, f => f.Code == "matroska.too-many-elements");
        Assert.DoesNotContain(report.Findings, f => f.Code is "matroska.block-track-unknown" or "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    // One finding past the listed ones is "1 more finding ... is", not "1 more findings ... are".
    [Fact]
    public void TheCountOfUnlistedFindingsIsSingularForOne()
    {
        byte[] tracks = MatroskaBuilder.Element(0x1654AE6B, MatroskaBuilder.Concat([.. Enumerable.Repeat<byte[]>([0xAE, 0x80], FindingCollector.MaxListed + 1)]));

        Report report = Analyzer.CreateDefault().Analyze("tracks.mka", new MemoryStream(MatroskaBuilder.File(MatroskaBuilder.Info(), tracks)));

        Finding rest = report.Findings.Last(f => f.Code == "matroska.track-not-analyzed");
        Assert.StartsWith("1 more finding of this kind is not listed", rest.Message, StringComparison.Ordinal);
    }

    // A zlib-compressed CodecPrivate may be larger than a frame is allowed to inflate to (a Vorbis comment header with pictures): 2 MiB here.
    [Fact]
    public void ALargeInflatedCodecPrivateIsRead()
    {
        byte[] head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), 312);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48000);
        var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(head);
            zlib.Write(new byte[2 << 20]);
        }

        byte[] encodings = MatroskaBuilder.Element(0x6D80, MatroskaBuilder.Element(0x6240, MatroskaBuilder.Unsigned(0x5031, 0), MatroskaBuilder.Unsigned(0x5032, 2),
            MatroskaBuilder.Unsigned(0x5033, 0), MatroskaBuilder.Element(0x5034, MatroskaBuilder.Unsigned(0x4254, 0))));
        byte[] track = MatroskaBuilder.OpusTrackWithPrivate(1, packed.ToArray(), encodings);
        byte[] cluster = MatroskaBuilder.Cluster(0, MatroskaBuilder.SimpleBlock(1, 0, [0xFC, 0xFF, 0xFE]));

        Report report = Analyzer.CreateDefault().Analyze("private.mka", new MemoryStream(MatroskaBuilder.File(MatroskaBuilder.Info(), MatroskaBuilder.Element(0x1654AE6B, track), cluster)));

        Assert.DoesNotContain(report.Findings, f => f.Code is "matroska.codec-private-invalid" or "matroska.codec-private-not-read" or "parse.failed");
    }

    // Findings that stand for one element each are listed up to a limit, then counted in one more.
    [Fact]
    public void FindingsOfManyTracksAreListedUpToALimit()
    {
        byte[] tracks = MatroskaBuilder.Element(0x1654AE6B, MatroskaBuilder.Concat([.. Enumerable.Repeat<byte[]>([0xAE, 0x80], 5_000)]));

        Report report = Analyzer.CreateDefault().Analyze("tracks.mka", new MemoryStream(MatroskaBuilder.File(MatroskaBuilder.Info(), tracks)));

        Assert.Equal(FindingCollector.MaxListed + 1, report.Findings.Count(f => f.Code == "matroska.track-field"));
        Finding rest = report.Findings.Last(f => f.Code == "matroska.track-field");
        Assert.True(rest.Count > 4_000, $"{rest.Count}");
        Assert.NotNull(rest.Path);
        Assert.Equal(FindingCollector.MaxListed + 1, report.Findings.Count(f => f.Code == "matroska.track-not-analyzed"));
    }

    [Fact]
    public void ALegacyAacIdGetsTheConfigurationItsFieldsGive()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/aac-legacy-sbr.mka");

        Assert.DoesNotContain(report.Findings, f => f.Code == "aac.no-configuration");
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(44100, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        yield return node;
        foreach (Node child in node.Children)
        {
            foreach (Node descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    // The node a finding's path leads to.
    private static Node NodeOf(Report report, Finding finding)
    {
        Node node = report.Root;
        foreach (int index in finding.Path!)
        {
            node = node.Children[index];
        }

        Assert.Equal((finding.Node!.Name, finding.Node.Offset, finding.Node.Length), (node.Name, node.Offset, node.Length));
        return node;
    }

    private sealed class ForcedMatroska : IFormatParser
    {
        private readonly MatroskaParser _parser = new();

        public string Name => _parser.Name;

        public SniffResult Sniff(SniffContext context) => new(100);

        public long? DeclaredEnd(SniffContext context) => _parser.DeclaredEnd(context);

        public void Parse(ParseContext context) => _parser.Parse(context);
    }

    // An element whose ID has eight octets (EBMLMaxIDLength 8) shows its whole ID, in a BlockGroup and directly in a Cluster.
    [Fact]
    public void LongIdsAreShownWhole()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/eight-octet-id.mka");

        var shown = new List<string>();
        var stack = new Stack<Node>([report.Root]);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            if (node.Name == "Unknown Element")
            {
                shown.Add(node.Value!.ToString()!);
            }

            foreach (Node child in node.Children)
            {
                stack.Push(child);
            }
        }

        Assert.Equal(2, shown.Count);
        Assert.All(shown, text => Assert.StartsWith("ID 0x0123456789ABCDEF,", text, StringComparison.Ordinal));
    }
}
