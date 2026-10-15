using System.Buffers.Binary;
using System.Text;
using Encspect.Core;
using Encspect.Formats.DtsHd;
using Encspect.Host;

namespace Encspect.Tests;

// What the DTS-HD file parser promises that no one fixture shows: the sniff's classes against damage and against the other formats, the
// end of the chunk chain, the BUILDVER claim, and a forced analysis of data that is no .dtshd file.
public class DtsHdTests
{
    private static SniffResult Sniff(byte[] data) => new DtsHdParser().Sniff(new SniffContext(new ByteReader(new MemoryStream(data)), "dtshd"));

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "dts", name));

    private static byte[] Chunk(string id, byte[] payload, ulong? size = null)
    {
        var bytes = new byte[16 + payload.Length];
        Encoding.ASCII.GetBytes(id, bytes);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), size ?? (ulong)payload.Length);
        payload.CopyTo(bytes, 16);
        return bytes;
    }

    private static readonly byte[] Header = Chunk("DTSHDHDR", new byte[16]);

    // The signature and a chain of chunks that fit take 100, with the chunks counted and where they end; a signature alone is a file whose
    // first chunk the data cuts short (80), a chain that breaks after the first chunk 80, and a first chunk with a size FFmpeg refuses 70.
    [Fact]
    public void TheClassFollowsTheChainOfChunks()
    {
        byte[] chain = [.. Header, .. Chunk("FILEINFO", new byte[8]), .. Chunk("STRMDATA", new byte[32])];
        SniffResult whole = Sniff(chain);
        Assert.Equal((100, 0, 3, 0, chain.Length), (whole.Class, whole.Offset, whole.Frames, whole.VerifiedFrames, (int)whole.End));
        Assert.Equal(100, Sniff(chain[..^10]).Class);
        Assert.Equal(2, Sniff(chain[..^10]).Frames);
        Assert.Equal(100, Sniff(Header).Class);
        Assert.Equal(80, Sniff("DTSHDHDR"u8.ToArray()).Class);
        Assert.Equal(80, Sniff([.. Header, .. new byte[40]]).Class);
        Assert.Equal(70, Sniff(Chunk("DTSHDHDR", new byte[16], 3)).Class);
        Assert.Equal(70, Sniff(Chunk("DTSHDHDR", new byte[16], 1UL << 62)).Class);
        Assert.Equal(0, Sniff([.. new byte[4], .. chain]).Class);
        Assert.Equal(0, Sniff([]).Class);
    }

    [Fact]
    public void ALongChainCountsNoMoreThanTheSniffLimit()
    {
        byte[] chain = [.. Header, .. Enumerable.Range(0, 40).SelectMany(_ => Chunk("UNKNOWN_", new byte[4]))];
        Assert.Equal(SniffResult.MaxFrames, Sniff(chain).Frames);
    }

    [Fact]
    public void TheDeclaredEndIsWhereTheChunksEnd()
    {
        byte[] chain = [.. Header, .. Chunk("STRMDATA", new byte[32])];
        var parser = new DtsHdParser();
        Assert.Equal(chain.Length, parser.DeclaredEnd(new SniffContext(new ByteReader(new MemoryStream([.. chain, .. new byte[7]])))));
        Assert.Equal(chain.Length, parser.DeclaredEnd(new SniffContext(new ByteReader(new MemoryStream(chain)))));
        Assert.Null(parser.DeclaredEnd(new SniffContext(new ByteReader(new MemoryStream(new byte[100])))));
    }

    // The parser reads a chunk of under 4 bytes, with a finding, so the chain goes on past it; a stream byte pattern after it is no tag.
    [Fact]
    public void TheDeclaredEndWalksPastAChunkUnderFourBytes()
    {
        byte[] chain = [.. Header, .. Chunk("FILEINFO", [], 0), .. Chunk("STRMDATA", new byte[32])];
        Assert.Equal(chain.Length, new DtsHdParser().DeclaredEnd(new SniffContext(new ByteReader(new MemoryStream(chain)))));
        byte[] tagged = [.. Header, .. Chunk("FILEINFO", [], 0), .. Chunk("STRMDATA", [.. new byte[200], .. "TAG"u8.ToArray(), .. new byte[125]])];
        Report report = Analyzer.CreateDefault().Analyze("tagged.dtshd", new MemoryStream(tagged));
        Assert.DoesNotContain(report.Root.Children, c => c.Kind == NodeKind.Tag);
    }

    // An ID3v1 tag after the chunks reads as a chunk ID and, with a five-character title padded with nulls, as a size of zero; no chunk header
    // follows it, so the chain ends before it and the tag is found. A six-character title makes the size too large to be one.
    [Theory]
    [InlineData("dtshd-id3v1-five-char-title")]
    [InlineData("dtshd-id3v1-six-char-title")]
    public void AnId3v1TagAfterTheChunksIsATagNotAChunk(string name)
    {
        byte[] data = Fixture(name + ".dtshd");
        Assert.Equal(data.Length - 128, new DtsHdParser().DeclaredEnd(new SniffContext(new ByteReader(new MemoryStream(data)))));
        Report report = Analyzer.CreateDefault().Analyze(name + ".dtshd", new MemoryStream(data));
        Assert.Contains(report.Root.Children, c => c.Kind == NodeKind.Tag && c.Offset == data.Length - 128);
        Assert.DoesNotContain(report.Findings, f => f.Code is "tag.near-miss" or "dtshd.trailing-data" or "dtshd.chunk-size");
    }

    // A chunk of under 4 bytes that is the last one of the file is still a chunk.
    [Fact]
    public void ASmallLastChunkIsPartOfTheChain()
    {
        byte[] chain = [.. Header, .. Chunk("STRMDATA", new byte[32]), .. Chunk("FILEINFO", [], 0)];
        Assert.Equal(chain.Length, new DtsHdParser().DeclaredEnd(new SniffContext(new ByteReader(new MemoryStream(chain)))));
    }

    // The signature is the first eight bytes, as A/103 E.2.2 and FFmpeg's probe have it: no other format's file, and none of the DTS
    // fixtures that are not .dtshd files, sniffs as one.
    [Fact]
    public void NoOtherFileSniffsAsDtsHd()
    {
        var parser = new DtsHdParser();
        int checkedFiles = 0;
        foreach (string path in Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*", SearchOption.AllDirectories))
        {
            if (path.EndsWith(".json", StringComparison.Ordinal) || path.EndsWith(".dtshd", StringComparison.Ordinal))
            {
                continue;
            }

            using FileStream stream = File.OpenRead(path);
            Assert.True(parser.Sniff(new SniffContext(new ByteReader(stream))).Class == 0, path);
            checkedFiles++;
        }

        Assert.True(checkedFiles > 500, $"{checkedFiles} files");
    }

    // The container takes the stream chunk's frames, which the DTS parser also finds further in (class 50), whatever the order of the parsers,
    // and the DTS parser's chain is the chunk's own bytes, so the report has no alternatives.
    [Theory]
    [InlineData("dtshd-all-chunks.dtshd")]
    [InlineData("dtshd-lossless.dtshd")]
    [InlineData("dtshd-core.dtshd")]
    public void TheContainerBeatsTheStreamInIt(string name)
    {
        byte[] data = Fixture(name);
        Assert.Equal("DTS-HD", DetectionOrderTests.Detect(DefaultParsers.Formats, data, ".dtshd"));
        Assert.Equal("DTS-HD", DetectionOrderTests.Detect([.. DefaultParsers.Formats.Reverse()], data, ".dts"));
        Report report = Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));
        Assert.Null(report.Detection);
    }

    // Junk in front hides the signature, and the stream in the file is read as the DTS file it is.
    [Fact]
    public void JunkInFrontOfTheFileLeavesTheStreamToTheDtsParser()
    {
        byte[] data = [.. Enumerable.Range(0, 30).Select(i => (byte)(0x80 | i)), .. Fixture("dtshd-lossless.dtshd")];
        Assert.Equal("DTS", DetectionOrderTests.Detect(DefaultParsers.Formats, data, ".dtshd"));
    }

    // BUILDVER is the encoder version identification of Table E.1: the DTS parser names the encoder from it as written, with low
    // confidence, and the claim's evidence points at the chunk.
    [Fact]
    public void BuildVersionIsALowConfidenceEncoderClaim()
    {
        Report report = Fixtures.AnalyzeMpeg("dts/dtshd-all-chunks.dtshd");
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("DTS-HD Encoder hand-built 1.2.3 (Jan  1 2026)", stream.Encoder);
        Assert.Equal(Confidence.Low, stream.EncoderConfidence);
        Node build = report.Root.Children.Single(c => c.Name == "BUILDVER");
        Node evidence = Fixtures.Resolve(report.Root, "STRMDATA/DTS/Encoder/Evidence").Single();
        Assert.Contains($"at 0x{build.Children.Single(c => c.Name == "Text").Offset:X}", evidence.Value?.ToString());
    }

    [Fact]
    public void ABuildVersionBeforeTheStreamNamesTheEncoderToo()
    {
        byte[] stream = Fixture("core-16be.dts");
        byte[] data = [.. Header, .. Chunk("BUILDVER", "Encoder 7\0\0\0"u8.ToArray()), .. Chunk("STRMDATA", stream)];
        Report report = Analyzer.CreateDefault().Analyze("early.dtshd", new MemoryStream(data));
        Assert.Equal("Encoder 7", Assert.Single(report.Streams).Encoder);
    }

    // A first chunk that is not DTSHDHDR cannot be detected, but a forced analysis reports it; a size of 2^61 or more, FFmpeg's limit, ends
    // the chunks; and data that is no .dtshd file at all gives findings and never parse.failed.
    [Fact]
    public void AForcedAnalysisOfOtherDataGivesFindings()
    {
        var analyzer = new Analyzer(DefaultParsers.Tags, DefaultParsers.Formats, DefaultParsers.Codecs);
        var options = new AnalysisOptions { ForcedFormat = "DTS-HD" };
        Report first = analyzer.Analyze("first.dtshd", new MemoryStream([.. Chunk("FILEINFO", "abc\0"u8.ToArray()), .. Header]), options);
        Assert.Contains(first.Findings, f => f.Code == "dtshd.header-first");
        Assert.Contains(first.Findings, f => f.Code == "dtshd.no-stream-data");
        Report huge = analyzer.Analyze("huge.dtshd", new MemoryStream([.. Header, .. Chunk("STRMDATA", new byte[8], 1UL << 62)]), options);
        Assert.Contains(huge.Findings, f => f.Code == "dtshd.chunk-header");
        foreach (byte[] other in new[] { Fixture("core-16be.dts"), new byte[3], new byte[300], "DTSHDHDR"u8.ToArray(), [.. Header, 1, 2, 3] })
        {
            Report report = analyzer.Analyze("other.bin", new MemoryStream(other), options);
            Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
            Assert.NotEmpty(report.Findings);
        }
    }

    [Fact]
    public void WithoutACodecParserTheStreamIsNotAnalyzed()
    {
        var analyzer = new Analyzer(DefaultParsers.Tags, [new DtsHdParser()], []);
        Report report = analyzer.Analyze("lonely.dtshd", new MemoryStream(Fixture("dtshd-core.dtshd")));
        Assert.Contains(report.Findings, f => f.Code == "dtshd.stream-not-analyzed");
        Assert.Empty(report.Streams);
    }

    // A file of a million empty chunks is not a million nodes: the chunks are read up to a limit and the rest is reported as not read.
    [Fact]
    public void ManyChunksAreReadUpToALimit()
    {
        byte[] data = [.. Header, .. Enumerable.Range(0, 3000).SelectMany(_ => Chunk("UNKNOWN_", new byte[4])), .. Chunk("STRMDATA", Fixture("core-16be.dts"))];
        Report report = Analyzer.CreateDefault().Analyze("many.dtshd", new MemoryStream(data));
        Assert.Contains(report.Findings, f => f.Code == "dtshd.chunk-count");
        Assert.True(report.Root.Children.Count <= DtsHdParser.MaxChunks + 1);
        // The stream chunk lies beyond the limit: the report says the limit was reached, not that the file holds no audio.
        Assert.DoesNotContain(report.Findings, f => f.Code == "dtshd.no-stream-data");
        Assert.Equal("DTS-HD", report.Root.Value?.ToString());
    }

    // A finding that can occur at every chunk is reported once, with its count, at the first case.
    [Fact]
    public void FindingsAtEveryChunkAreReportedOnceWithACount()
    {
        byte[] stream = Fixture("core-16be.dts");
        byte[] data = [.. Header, .. Enumerable.Range(0, 500).SelectMany(_ => Chunk("STRMDATA", stream)), .. Enumerable.Range(0, 500).SelectMany(_ => Chunk("FILEINFO", [], 0))];
        Report report = Analyzer.CreateDefault().Analyze("repeated.dtshd", new MemoryStream(data));
        Finding repeated = report.Findings.Single(f => f.Code == "dtshd.stream-repeated");
        Assert.Equal(499, repeated.Count);
        Assert.Equal(500, report.Findings.Single(f => f.Code == "dtshd.chunk-size").Count);
        Assert.True(report.Findings.Count < 12);
    }

    // The reserved values of four tables are four findings, one for each field, though they have one code.
    [Fact]
    public void EachReservedValueIsReportedForItsField()
    {
        var header = new byte[14];
        header[4] = 0xC0;
        header[9] = 9;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), 0x0108);
        var presentation = new byte[24];
        presentation[1] = 0x01;
        Report report = Analyzer.CreateDefault().Analyze("reserved.dtshd", new MemoryStream([.. Chunk("DTSHDHDR", header), .. Chunk("AUPR-HDR", presentation), .. Chunk("STRMDATA", Fixture("core-16be.dts"))]));
        string[] messages = [.. report.Findings.Where(f => f.Code == "dtshd.reserved-value").Select(f => f.Message)];
        Assert.Equal(4, messages.Length);
        foreach (string wanted in new[] { "reference clock code of the time code is 3", "time code frame rate 9 is reserved", "Bitw_Stream_Metadata has the reserved flags 0x0100", "Bitw_Aupres_Metadata has the reserved flags 0x0100" })
        {
            Assert.Contains(messages, m => m.Contains(wanted, StringComparison.Ordinal));
        }
    }

    // Findings of one code about different fields or causes stay apart; the same one at every chunk is one finding with its count.
    [Fact]
    public void FindingsOfOneCodeAboutDifferentCausesStayApart()
    {
        byte[] data = [.. Header, .. Chunk("FILEINFO", [], 0), .. Chunk("TIMECODE", new byte[8]), .. Chunk("STRMDATA", Fixture("core-16be.dts"))];
        Report report = Analyzer.CreateDefault().Analyze("causes.dtshd", new MemoryStream(data));
        Assert.True(report.Findings.Count(f => f.Code == "dtshd.chunk-size") >= 2);
    }

    [Fact]
    public void ASecondStreamChunkAndASecondHeaderAreReported()
    {
        byte[] stream = Fixture("core-16be.dts");
        byte[] data = [.. Header, .. Header, .. Chunk("STRMDATA", stream), .. Chunk("STRMDATA", stream)];
        Report report = Analyzer.CreateDefault().Analyze("twice.dtshd", new MemoryStream(data));
        Assert.Contains(report.Findings, f => f.Code == "dtshd.header-repeated");
        Assert.Contains(report.Findings, f => f.Code == "dtshd.stream-repeated");
        Assert.Single(report.Streams);
    }

    // Every chunk the standard defines has a node for each field it states, in the order of the tables, with the speakers a mask names.
    [Fact]
    public void TheFieldsOfTheChunksAreRead()
    {
        Report report = Fixtures.AnalyzeMpeg("dts/dtshd-all-chunks.dtshd");
        string Field(string path) => Fixtures.Resolve(report.Root, path).Single().Value?.ToString() ?? string.Empty;
        Assert.Equal("1", Field("DTSHDHDR/Version"));
        Assert.Equal("48000 Hz", Field("DTSHDHDR/Time Code/Reference Clock Code"));
        Assert.Equal("172801234 samples", Field("DTSHDHDR/Time Code/Time Stamp"));
        Assert.Equal("2 (24 frames per second)", Field("DTSHDHDR/Time Code Frame Rate"));
        Assert.Equal("true", Field("DTSHDHDR/Stream Metadata/HasExtensionSubstream"));
        Assert.Equal("false", Field("DTSHDHDR/Stream Metadata/HasNavigationTable"));
        Assert.Equal("1 extension substream", Field("DTSHDHDR/Extension Substreams"));
        Assert.Equal("384 kbps", Field("CORESSMD/Bit Rate"));
        Assert.Equal("1200 kbps", Field("EXTSS_MD/Peak Bit Rate"));
        Assert.Equal("256 kb", Field("EXTSS_MD/Peak Bit Rate Smoothing Buffer Size"));
        Assert.Equal("true", Field("AUPR-HDR/Presentation Metadata/HasLossless"));
        Assert.Equal("false", Field("AUPR-HDR/Presentation Metadata/IsBackwardCompatibleCoreInExtension"));
        Assert.Equal("4 frames", Field("AUPR-HDR/Frames"));
        Assert.Equal("2048 samples", Field("AUPR-HDR/Original Samples"));
        Assert.Equal("front center, front left, front right, back left, back right, LFE", Field("AUPR-HDR/Channel Mask/Speakers"));
        Assert.Equal("Main audio", Field("AUPRINFO/Text"));
        Assert.Equal("2 frames", Field("NAVI-TBL/Interval"));
        Assert.Equal("172800512 samples", Field("TIMECODE/Reference"));
        Assert.Equal("2", Field("TIMECODE/Frame Rate")[..1]);
    }

    [Fact]
    public void TheBackwardCompatibleCoreFieldsFollowTheirFlags()
    {
        byte[] presentation = new byte[1 + 2 + 3 + 4 + 2 + 5 + 2 + 2 + 3 + 2 + 2 + 1];
        presentation[2] = 0x07;
        presentation[3] = 0x00;
        presentation[4] = 0xBB;
        presentation[5] = 0x80;
        presentation[^1] = 5;
        Report report = Analyzer.CreateDefault().Analyze("core.dtshd", new MemoryStream([.. Header, .. Chunk("AUPR-HDR", presentation), .. Chunk("STRMDATA", Fixture("core-16be.dts"))]));
        Assert.NotEmpty(Fixtures.Resolve(report.Root, "AUPR-HDR/Core Bit Rate"));
        Assert.Equal("5 percent", Fixtures.Resolve(report.Root, "AUPR-HDR/LSB Trim").Single().Value?.ToString());
    }
}
