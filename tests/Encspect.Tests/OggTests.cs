using Encspect.Core;
using Encspect.Formats.Ogg;
using Encspect.Host;
using Encspect.Tags.VorbisComment;

namespace Encspect.Tests;

// The Ogg parser's sync and declared end, the packets it hands a codec with their granule
// positions, and the Vorbis comment through both forms of the ParseTag route.
public class OggTests
{
    // A narrowband frame of submode 1 (43 bits) and its terminator.
    private static readonly byte[] Frame = [0x08, 0, 0, 0, 0, 0x0F];

    private static string OggFixture(string name) => Path.Combine(Fixtures.FixtureDirectory, "ogg", name);

    private static int Sniff(byte[] data) => new OggParser().Sniff(new SniffContext(new ByteReader(new MemoryStream(data))));

    [Fact]
    public void SniffingNeedsAChainOfPagesThatPassTheirCrc()
    {
        byte[] file = File.ReadAllBytes(OggFixture("external-speexenc-wb.spx"));
        Assert.Equal(100, Sniff(file));
        Assert.Equal(60, Sniff([.. new byte[100], .. file]));

        // A page that passes its CRC but is followed by neither a page nor the end is no chain.
        byte[] lone = new OggBuilder().Page(1, 0, 2, Frame).Bytes;
        Assert.Equal(100, Sniff(lone));
        Assert.Equal(0, Sniff([.. lone, .. "not a page"u8]));

        // A capture pattern and version in other data, with a CRC that does not hold.
        byte[] fake = [.. lone];
        fake[^1] ^= 1;
        Assert.Equal(0, Sniff([.. fake, .. fake]));
        Assert.Equal(60, Sniff([.. fake, .. lone]));
        var random = new Random(3533);
        byte[] noise = new byte[8192];
        random.NextBytes(noise);
        for (int i = 0; i < noise.Length - 32; i += 512)
        {
            "OggS\0"u8.CopyTo(noise.AsSpan(i));
        }

        Assert.Equal(0, Sniff(noise));
    }

    [Fact]
    public void TheCrcIsRfc3533s()
    {
        // Every page of an encoder's file passes the CRC the parser computes.
        byte[] file = File.ReadAllBytes(OggFixture("external-speexenc-wb.spx"));
        Report report = Analyzer.CreateDefault().Analyze("wb.spx", new MemoryStream(file));
        Node pages = report.Root.Children.Single().Children.Single(c => c.Name == "Pages");
        Assert.All(pages.Children, page =>
        {
            Node crc = page.Children.Single(c => c.Name == "CRC");
            Assert.Equal(crc.Value, crc.Children.Single().Value);
        });

        // And the test builder's, which writes them, agrees with the stored value.
        uint stored = BitConverter.ToUInt32(file, 22);
        byte[] zeroed = [.. file[..(int)pages.Children[0].Length]];
        zeroed.AsSpan(22, 4).Clear();
        Assert.Equal(stored, OggBuilder.Crc(zeroed));
    }

    [Fact]
    public void TheDeclaredEndIsWhereTheChainOfPagesEnds()
    {
        byte[] file = File.ReadAllBytes(OggFixture("external-speexenc-wb.spx"));
        long? End(byte[] data) => new OggParser().DeclaredEnd(new SniffContext(new ByteReader(new MemoryStream(data))));

        Assert.Equal(file.Length, End([.. file, .. new byte[128]]));
        Assert.Null(End([.. new byte[10], .. file]));

        // A last page cut short is not part of the chain, so a tag after the cut is the file's.
        byte[] cut = file[..^200];
        Report report = Analyzer.CreateDefault().Analyze("cut.spx", new MemoryStream([.. cut, .. Id3v1()]));
        Assert.Equal(["Ogg", "ID3v1"], report.Root.Children.Select(c => c.Name));
        Assert.Contains(report.Findings, f => f.Code == "ogg.page-truncated");
    }

    [Fact]
    public void PacketsCarryTheirPagesGranulePositions()
    {
        var codec = new RecordingCodec("speex");
        byte[] file = File.ReadAllBytes(OggFixture("bad-crc.spx"));
        new Analyzer(DefaultParsers.Tags, [new OggParser()], [codec]).Analyze("bad-crc.spx", new MemoryStream(file));

        // 2 headers and 30 audio packets on pages of 1, 1, 10, 10 and 10, the fourth page failing its CRC.
        Assert.Equal(22, codec.Packets.Count);
        MappedBytes[] positioned = [.. codec.Packets.Where(p => p.EndPosition is not null)];
        Assert.Equal([0, 0, (10 * 160) - 40, (30 * 160) - 40], positioned.Select(p => p.EndPosition!.Value));
        Assert.All(positioned, p => Assert.Equal(("Granule Position", 8L), (p.EndPositionField!.Name, p.EndPositionField.Length)));
        Assert.Equal([21], Indexes(codec.Packets, p => p.IsEndOfStream));
        Assert.Equal([12], Indexes(codec.Packets, p => p.FollowsGap));
    }

    [Fact]
    public void ACommentThatSpansPagesKeepsItsRanges()
    {
        Report report = Fixtures.AnalyzeMpeg("ogg/packet-spanning-pages.spx");
        Node tag = report.Root.Children.Single().Children.Single(c => c.Name == "Logical Stream").Children.Single(c => c.Kind == NodeKind.Tag);

        Assert.Equal(3, tag.Ranges!.Count);
        Node detection = tag.Children.Single(c => c.Name == "Detection");
        Assert.Equal(tag.Ranges, detection.Ranges);
        TestFiles.CheckInsideParents(tag);
        Assert.Contains("Vorbis Comment", ReportSummary.From(report).Tags);
    }

    [Fact]
    public void AVorbisCommentHeldWholeIsReadFromItsBytes()
    {
        byte[] comment = OggBuilder.VorbisComment("vendor", "TITLE=Chunk");
        var container = new ChunkedContainer(tags: new Dictionary<string, string> { ["VCMT"] = VorbisCommentTagParser.TagName });
        Report report = TestFiles.Analyze(ChunkedContainer.Build("RIFF", "TEST", ("VCMT", comment)), container);

        Node tag = report.Root.Children.Single().Children.Single().Children.Single(c => c.Kind == NodeKind.Tag);
        Assert.Equal("Chunk", tag.Children.Single(c => c.Name == "TITLE").Value?.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Severity >= Severity.Warning);
    }

    [Fact]
    public void ATagParserThatDoesNotReadMappedBytesFindsNoTag()
    {
        byte[] file = File.ReadAllBytes(OggFixture("speex-narrowband.spx"));
        var tags = new ITagParser[] { new ActionTagParser(VorbisCommentTagParser.TagName, _ => throw new InvalidOperationException("not this route")) };
        Report report = new Analyzer(tags, DefaultParsers.Formats, DefaultParsers.Codecs).Analyze("nb.spx", new MemoryStream(file));

        Assert.Contains(report.Findings, f => f.Code == "tag.not-found");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    [Fact]
    public void TheSummaryAndTheCsvComeFromTheStreams()
    {
        Report report = Fixtures.AnalyzeMpeg("ogg/external-speexenc-wb.spx");
        var writer = new StringWriter();
        ReportCsvWriter.Write([report], writer);

        Assert.Equal("external-speexenc-wb.spx,Ogg,1,Speex wideband,1.500,24000,28.0,16000,Mono,mono (front center),speexenc 1.2.1,High,Vorbis Comment,0,0,0",
            writer.ToString().Split("\r\n")[1]);

        ReportSummary chained = ReportSummary.From(Fixtures.AnalyzeMpeg("ogg/external-chained.spx"));
        Assert.Equal((2, "Speex narrowband", "speexenc 1.2.1"), (chained.Streams, chained.Codec, chained.Encoder));
        Assert.Equal(1.5 + (23857 / 16000.0), chained.Duration!.Value, 9);
    }

    // Vorbis and Opus name their codec, encoder and channel layout in the summary; Opus always
    // decodes at 48 kHz, whatever the input's rate (44100 Hz here), which its header keeps apart.
    [Fact]
    public void VorbisAndOpusSummariesComeFromTheirStreams()
    {
        Report[] reports = [.. new[] { "external-oggenc-51.ogg", "external-opusenc-cvbr.opus", "external-ffmpeg-opus.opus" }.Select(n => Fixtures.AnalyzeMpeg("ogg/" + n))];
        var writer = new StringWriter();
        ReportCsvWriter.Write(reports, writer);
        string[] lines = writer.ToString().Split("\r\n");

        Assert.Equal("external-oggenc-51.ogg,Ogg,1,Vorbis,1.000,48000,113.8,48000,5.1,\"5.1 (front left, front center, front right, back left, back right, LFE)\",libvorbis 1.3.7,High,Vorbis Comment,0,0,0", lines[1]);
        Assert.StartsWith("external-opusenc-cvbr.opus,Ogg,1,Opus,1.500,72000,", lines[2], StringComparison.Ordinal);
        Assert.EndsWith(",48000,Stereo,\"stereo (front left, front right)\",opusenc 0.2 (libopus 1.4),High,Vorbis Comment,0,0,0", lines[2], StringComparison.Ordinal);
        Assert.EndsWith(",48000,Stereo,\"stereo (front left, front right)\",FFmpeg (Lavc60.31.102 opus),Medium,Vorbis Comment,0,0,0", lines[3], StringComparison.Ordinal);
        Node head = reports[1].Root.Children.Single().Children.Single(c => c.Name == "Logical Stream").Children.Single(c => c.Name == "Opus Head");
        Assert.Equal("44100 Hz", head.Children.Single(c => c.Name == "Input Sample Rate").Value?.ToString());
        Assert.False(reports[2].Streams.Single().IsVbr);
    }

    [Fact]
    public void CancelingMidFileStopsTheAnalysis()
    {
        byte[] file = File.ReadAllBytes(OggFixture("external-speexenc-wb.spx"));
        byte[] chain = [.. Enumerable.Repeat(file, 400).SelectMany(b => b)];
        using var cancellation = new CancellationTokenSource();
        var progress = new SyncProgress(value =>
        {
            if (value >= chain.Length / 2)
            {
                cancellation.Cancel();
            }
        });

        Assert.Throws<OperationCanceledException>(() => Analyzer.CreateDefault().Analyze(
            "chain.spx", new MemoryStream(chain), new AnalysisOptions { CancellationToken = cancellation.Token, BytesRead = progress }));
    }

    [Fact]
    public void ManyStreamsAndLinksStayShallow()
    {
        var builder = new OggBuilder();
        for (uint link = 0; link < 2000; link++)
        {
            builder.Page(link, 0, 2 | 4, OggBuilder.SpeexHeader(8000, 0)[..40]);
        }

        Report report = Analyzer.CreateDefault().Analyze("links.ogg", new MemoryStream(builder.Bytes));
        Node ogg = report.Root.Children.Single();
        Assert.Equal(2000, ogg.Children.Count(c => c.Name == "Logical Stream"));
        Assert.Equal("2000", ogg.Children.Last(c => c.Name == "Logical Stream").Children.Single(c => c.Name == "Chain Link").Value?.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "parse.too-deep");
    }

    public static TheoryData<string> FuzzFindings()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "ogg", "fuzz")).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    // Inputs the fuzzer found, each a hang or a parser exception before its fix: a Speex header
    // with two billion frames per packet once made the per-second series count centuries, and a
    // Vorbis setup header cut inside a codebook's multiplicands left that node outside its group.
    [Theory]
    [MemberData(nameof(FuzzFindings))]
    public async Task FuzzFindingsAreAnalyzedQuickly(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ogg", "fuzz", name));
        Task<Report> work = Task.Run(() => Analyzer.CreateDefault().Analyze(name, new MemoryStream(data)));
        Assert.True(await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(20))) == work, $"{name} took longer than 20 s.");

        Report report = await work;
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        TestFiles.CheckInsideParents(report.Root);
    }

    private static int[] Indexes(List<MappedBytes> packets, Func<MappedBytes, bool> predicate) =>
        [.. packets.Select((p, i) => (p, i)).Where(x => predicate(x.p)).Select(x => x.i)];

    private static byte[] Id3v1()
    {
        byte[] tag = new byte[128];
        "TAGTitle"u8.CopyTo(tag);
        return tag;
    }

    // Keeps the packets an Ogg stream hands over, under a codec ID.
    private sealed class RecordingCodec(string id) : ICodecParser
    {
        public List<MappedBytes> Packets { get; } = [];

        public string Id => id;

        public void Parse(CodecContext context) => Packets.AddRange(context.Packets!);
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
