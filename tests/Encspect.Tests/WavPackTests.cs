using System.Buffers.Binary;
using System.Globalization;
using Encspect.Core;
using Encspect.Formats.WavPack;
using Encspect.Host;

namespace Encspect.Tests;

// What the fixtures' manifests do not pin: how a stream is recognized, tags inside the blocks, files
// of many blocks and sub-blocks, what the statistics derive from the configuration and the hybrid
// profile, and damage the hand-built fixtures do not make.
public class WavPackTests
{
    private static readonly WavPackParser Parser = new();

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "wavpack", name));

    private static Report Analyze(byte[] data, string name = "test.wv") => Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));

    private static Report AnalyzeFixture(string name) => Analyze(Fixture(name), name);

    private static int Sniff(byte[] data) => Parser.Sniff(new SniffContext(new ByteReader(new MemoryStream(data))));

    private static Node Stream(Report report) => report.Root.Children.Single(n => n.Kind == NodeKind.Stream);

    private static Node Statistics(Report report) => Stream(report).Children.Single(c => c.Kind == NodeKind.Statistics);

    private static string? Statistic(Report report, string name) => Statistics(report).Children.FirstOrDefault(c => c.Name == name)?.Value?.ToString();

    // The blocks of a file as the report finds them.
    private static List<byte[]> Blocks(byte[] data) =>
        [.. Stream(Analyze(data)).Children.Where(c => c.Name is "Block" or "Metadata Block").Select(c => data[(int)c.Offset..(int)c.End])];

    [Fact]
    public void ABlockThatFillsTheDataIsCertain()
    {
        byte[] file = Fixture("external-lossless-16-mono.wv");
        byte[] first = Blocks(file)[0];
        Assert.Equal(100, Sniff(first));
        Assert.Equal(100, Sniff(file));
        Assert.Equal(90, Sniff(first[..^100]));
        Assert.Equal(80, Sniff([.. first, .. new byte[40]]));
        Assert.Equal(60, Sniff([.. new byte[100], .. file]));
        Assert.Equal(0, Sniff([.. new byte[100], .. first, .. new byte[40]]));
        Assert.Equal(0, Sniff(new byte[31]));
        Assert.Equal(0, Sniff("wvpk"u8.ToArray()));
    }

    [Fact]
    public void HeadersThatFailLibwavpacksChecksAreNoBlocks()
    {
        byte[] first = Blocks(Fixture("external-lossless-16-mono.wv"))[0];
        foreach ((int at, byte value) in new (int, byte)[] { (4, 0x01), (6, 0x10), (7, 0x01), (9, 0x05), (22, 0x03), (23, 0x01) })
        {
            byte[] damaged = (byte[])first.Clone();
            damaged[at] = value;
            Assert.True(Sniff(damaged) != 100, $"byte {at} = {value}");
        }
    }

    // A header or chain of blocks is never taken from the other formats' fixtures.
    [Fact]
    public void OtherFormatsAreNotWavPack()
    {
        string wavpack = Path.Combine(Fixtures.FixtureDirectory, "wavpack");
        foreach (string path in Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*", SearchOption.AllDirectories))
        {
            if (path.StartsWith(wavpack, StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal) || Path.GetFileName(path).StartsWith("wavpack-", StringComparison.Ordinal))
            {
                continue;
            }

            using FileStream stream = File.OpenRead(path);
            var reader = new ByteReader(stream);
            Assert.True(Parser.Sniff(new SniffContext(reader)) == 0, path);
        }
    }

    // A tag that an ID3v2.4 SEEK frame finds between the blocks ends the stream the blocks declare, as
    // for any format that states its own end: the stream is read up to the tag, and the host reports
    // the bytes after it.
    [Fact]
    public void ATagBetweenBlocksEndsTheDeclaredStream()
    {
        List<byte[]> blocks = Blocks(Fixture("external-lossless-16-stereo.wv"));
        byte[] data = TaggedBetween(blocks, 0);

        Report report = Analyze(data);

        Assert.Equal(["ID3v2", "WavPack", "ID3v2", "Data"], report.Root.Children.Select(c => c.Name));
        Assert.Equal(1, Stream(report).Children.Count(c => c.Name == "Block"));
        Assert.Contains(report.Findings, f => f.Code == "layout.data-between-tags");
        Assert.DoesNotContain(report.Findings, f => f.Code is "wavpack.data-skipped" or "parse.failed");
    }

    // Where the data does not start with a block, the stream declares no end, and the parser skips the
    // tag and reads the blocks on both sides of it.
    [Fact]
    public void ATagBetweenBlocksIsSkippedWhereNoEndIsDeclared()
    {
        List<byte[]> blocks = Blocks(Fixture("external-lossless-16-stereo.wv"));
        byte[] junk = new byte[100];
        Array.Fill(junk, (byte)0x90);
        byte[] data = TaggedBetween(blocks, junk.Length, junk);

        Report report = Analyze(data);

        Assert.Equal(["ID3v2", "WavPack", "ID3v2"], report.Root.Children.Select(c => c.Name));
        Node stream = Stream(report);
        Assert.Equal(3, stream.Children.Count(c => c.Name == "Block"));
        Assert.Equal(1, stream.Children.Count(c => c.Name == "Skipped Data"));
        Assert.Equal(["wavpack.data-skipped"], report.Findings.Where(f => f.Code.StartsWith("wavpack.", StringComparison.Ordinal)).Select(f => f.Code));
        Assert.Equal(24000, report.Streams.Single().Samples);
    }

    // An ID3v2.4 tag whose SEEK frame points past the first block (and the bytes in front of it), then
    // a second tag, then the other blocks.
    private static byte[] TaggedBetween(List<byte[]> blocks, int skip, byte[]? before = null)
    {
        int offset = skip + blocks[0].Length;
        byte[] first = Id3v24(Frame("SEEK", [0, 0, (byte)(offset >> 8), (byte)offset]));
        byte[] second = Id3v24(Frame("TIT2", [3, .. "Second tag"u8]));
        return [.. first, .. before ?? [], .. blocks[0], .. second, .. blocks[1], .. blocks[2]];
    }

    [Fact]
    public void HybridAndCorrectionFilesAreTwoFiles()
    {
        byte[] main = Fixture("external-hybrid-b4-correction.wv");
        byte[] correction = Fixture("external-hybrid-b4-correction-wvc.wvc");
        Report mixed = Analyze([.. main, .. correction]);
        Assert.Contains(mixed.Findings, f => f.Code == "wavpack.mixed-streams");

        Report alone = Analyze(correction, "correction.wvc");
        Assert.Equal("WavPack correction data", alone.Streams.Single().Codec);
        Assert.Equal("true", Statistic(alone, "IsCorrectionFile"));
        Assert.Equal("true", Statistic(alone, "HasCorrectionFile"));
        Assert.Equal("false", Statistic(alone, "IsLossless"));
        Assert.Equal(["wavpack.correction-file"], alone.Findings.Select(f => f.Code));

        Report hybrid = Analyze(main);
        Assert.Equal("WavPack hybrid", hybrid.Streams.Single().Codec);
        Assert.Equal("true", Statistic(hybrid, "IsHybrid"));
        Assert.Equal("false", Statistic(hybrid, "IsLossless"));
        Assert.Equal("true", Statistic(hybrid, "HasCorrectionFile"));
        Assert.Equal("false", Statistic(Analyze(Fixture("external-hybrid-lossy-b4.wv")), "HasCorrectionFile"));
        Assert.Equal("false", Statistic(Analyze(Fixture("external-lossless-16-stereo.wv")), "HasCorrectionFile"));
    }

    // The target bitrate a hybrid file asks for follows from its bitrate accumulator: the -b of the
    // encoder, in bits per sample or, with 256 or more, in kbps.
    [Theory]
    [InlineData("external-hybrid-lossy-b4.wv", "4.00 bits/sample", "352.8 kbps")]
    [InlineData("external-hybrid-mono-b3.wv", "3.00 bits/sample", "132.3 kbps")]
    [InlineData("external-hybrid-24-b8-correction.wv", "8.00 bits/sample", "705.6 kbps")]
    [InlineData("external-hybrid-kbps-b256.wv", "2.90 bits/sample", "256.0 kbps")]
    [InlineData("external-hybrid-51-b3-correction.wv", "3.00 bits/sample", "864.0 kbps")]
    public void HybridTargetsFollowTheEncodersSetting(string name, string bits, string rate)
    {
        Report report = AnalyzeFixture(name);
        Assert.Equal(bits, Statistic(report, "Hybrid Target Bitrate"));
        Assert.Equal(rate, Statistic(report, "Hybrid Target Rate"));
    }

    [Theory]
    [InlineData("external-mode-fast.wv", "fast", "none", "2 passes")]
    [InlineData("external-lossless-16-stereo.wv", "normal", "none", "5 passes")]
    [InlineData("external-mode-high.wv", "high", "none", "10 passes")]
    [InlineData("external-mode-very-high.wv", "very high", "none", "16 passes")]
    [InlineData("external-mode-extra-1.wv", "normal", "1", "5 passes")]
    [InlineData("external-mode-extra-3.wv", "high", "3", "10 passes")]
    [InlineData("external-mode-extra-6.wv", "very high", "6", "9 to 16 passes")]
    public void TheConfigBlockRecordsTheModes(string name, string mode, string extra, string passes)
    {
        Report report = AnalyzeFixture(name);
        Assert.Equal(mode, Statistic(report, "Compression Mode"));
        Assert.Equal(extra, Statistic(report, "Extra Processing Level"));
        Assert.Equal(passes, Statistic(report, "Decorrelation Passes"));
    }

    [Theory]
    [InlineData("external-lossless-16-stereo.wv", "WAV")]
    [InlineData("external-from-aiff.wv", "AIFF")]
    [InlineData("external-from-caf.wv", "CAF")]
    [InlineData("external-from-caf-51.wv", "CAF")]
    [InlineData("external-dsd-64.wv", "DSF")]
    public void TheNewConfigBlockNamesTheSourceFormat(string name, string format) => Assert.Equal(format, Statistic(AnalyzeFixture(name), "Source Format"));

    [Theory]
    [InlineData("external-lossless-16-stereo.wv", "Joint stereo (mid/side)")]
    [InlineData("external-joint-stereo-off.wv", "Stereo")]
    [InlineData("external-lossless-16-mono.wv", "Mono")]
    [InlineData("external-lossless-51-16.wv", "Multichannel")]
    public void TheChannelModeFollowsTheBlocks(string name, string mode) => Assert.Equal(mode, AnalyzeFixture(name).Streams.Single().ChannelMode);

    [Fact]
    public void DsdCountsBitsAtTheDsdRate()
    {
        StreamInfo stream = AnalyzeFixture("external-dsd-64.wv").Streams.Single();
        Assert.Equal(2822400, stream.SampleRate);
        Assert.Equal(1, stream.BitsPerSample);
        Assert.Equal(65536 * 8, stream.Samples);
        Assert.Equal(65536 * 8 / 2822400.0, stream.Duration, 9);
        Assert.Equal(5644800, AnalyzeFixture("external-dsd-128.wv").Streams.Single().SampleRate);
    }

    [Fact]
    public void ExtraBitsMissingMakeABlockLossy()
    {
        Report report = AnalyzeFixture("built-extra-bitstream-missing.wv");
        Assert.Equal("WavPack lossy", report.Streams.Single().Codec);
        Assert.Equal("false", Statistic(report, "IsLossless"));
        Assert.Equal("true", Statistic(AnalyzeFixture("external-lossless-32-stereo.wv"), "IsLossless"));
    }

    [Fact]
    public void TheMd5DigestIsReportedNotChecked()
    {
        Report report = AnalyzeFixture("external-lossless-16-stereo-md5.wv");
        string digest = Statistic(report, "MD5 Checksum")!;
        Assert.Matches("^[0-9a-f]{32}$", digest);
        Assert.Null(Statistic(AnalyzeFixture("external-lossless-16-stereo.wv"), "MD5 Checksum"));
    }

    [Fact]
    public void ManySubBlocksAreCountedNotListed()
    {
        byte[] file = Fixture("external-lossless-16-mono.wv");
        Report whole = Analyze(file);
        Node first = Stream(whole).Children.First(c => c.Name == "Block");
        byte[] block = file[(int)first.Offset..(int)first.End];
        int dummies = 3000;
        byte[] data = [.. block[..^6], .. Enumerable.Repeat<byte>(0, dummies * 2), .. block[^6..]];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));

        Report report = Analyze(data);

        Node damaged = Stream(report).Children.First(c => c.Name == "Block");
        int listed = damaged.Children.Count(c => c.Children.Any(k => k.Name == "ID"));
        Node further = damaged.Children.Single(c => c.Name == "Further Sub-blocks");
        Assert.Equal(1024, listed);
        Assert.Equal(whole.Streams.Count == 1 ? dummies + Parts(file, first) - 1024 : 0, further.Value?.AsInteger());
        Assert.Equal(1 + 1024 + 1, damaged.Children.Count);
        Assert.Contains(report.Findings, f => f.Code == "wavpack.block-checksum-mismatch");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        TestFiles.CheckInsideParents(report.Root);
    }

    private static int Parts(byte[] file, Node block) => WavPackBuilder.Parts(file[(int)block.Offset..(int)block.End]).Count;

    // The blocks past the first 4,096 are listed with their header and without their sub-blocks.
    [Fact]
    public void BlocksPastTheDetailedOnesKeepOnlyTheirHeader()
    {
        byte[] file = Fixture("external-lossless-16-mono.wv");
        Node first = Stream(Analyze(file)).Children.First(c => c.Name == "Block");
        byte[] block = file[(int)first.Offset..(int)first.End];
        byte[] data = [.. Enumerable.Repeat(block, 4200).SelectMany(b => b)];

        Report report = Analyze(data);

        List<Node> blocks = Stream(report).Children.Where(c => c.Name == "Block").ToList();
        Assert.Equal(4200, blocks.Count);
        Assert.Contains(blocks[4095].Children, c => c.Name == "Decorrelation Terms");
        Assert.DoesNotContain(blocks[4096].Children, c => c.Name == "Decorrelation Terms");
        Node summary = blocks[4096].Children.Single(c => c.Name == "Sub-blocks");
        Assert.Equal(blocks[4095].Children.Count(c => c.Children.Any(k => k.Name == "ID")), summary.Value?.AsInteger());
        Assert.Contains(report.Findings, f => f.Code == "wavpack.block-index-overlap");
        Assert.Equal(4200, Statistics(report).Children.Single(c => c.Name == "Blocks").Value?.AsInteger());
        Assert.Equal(4200, Statistics(report).Children.Single(c => c.Name == "Verified Block Checksums").Value?.AsInteger());
    }

    // Blocks of a header and nothing else, past the ones the report details, are one entry per run.
    [Fact]
    public void EmptyBlocksPastTheDetailedOnesFormRuns()
    {
        byte[] file = Fixture("external-lossless-16-mono.wv");
        Node first = Stream(Analyze(file)).Children.First(c => c.Name == "Block");
        byte[] block = file[(int)first.Offset..(int)first.End];
        byte[] empty = (byte[])block[..32].Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(4), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(20), 0);
        byte[] data = [.. Enumerable.Repeat(block, 4090).SelectMany(b => b), .. Enumerable.Repeat(empty, 5000).SelectMany(b => b)];

        Report report = Analyze(data);

        Node stream = Stream(report);
        List<Node> runs = stream.Children.Where(c => c.Name == "Undetailed Blocks").ToList();
        Assert.Equal(4090 + 6, stream.Children.Count(c => c.Name is "Block" or "Metadata Block"));
        Node run = Assert.Single(runs);
        Assert.Equal(5000 - 6, run.Value?.AsInteger());
        Assert.Equal(32L * (5000 - 6), run.Length);
        Assert.Equal(5000, Statistics(report).Children.Single(c => c.Name == "Metadata Blocks").Value?.AsInteger());
        Assert.Contains(report.Findings, f => f.Code == "wavpack.block-checksum-missing" || f.Code == "wavpack.block-index-overlap");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        TestFiles.CheckInsideParents(report.Root);
    }

    // A truncated copy of every kind of fixture, at every 97th byte, yields a report without a parser
    // failure whose findings and streams point into the tree.
    [Theory]
    [InlineData("external-lossless-16-stereo.wv")]
    [InlineData("external-hybrid-b4-correction.wv")]
    [InlineData("external-hybrid-b4-correction-wvc.wvc")]
    [InlineData("external-lossless-51-16.wv")]
    [InlineData("external-dsd-64.wv")]
    [InlineData("external-lossless-float-stereo.wv")]
    [InlineData("external-apev2-tag.wv")]
    [InlineData("built-garbage-between-blocks.wv")]
    public void TruncatedCopiesYieldFindings(string name)
    {
        byte[] data = Fixture(name);
        for (int length = 1; length < data.Length; length += 97)
        {
            Report report = Analyze(data[..length], name);
            Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
            Assert.All(report.Findings, f => Assert.NotNull(f.Path));
            Assert.All(report.Streams, s => Assert.All(s.NodePaths, p => Assert.NotNull(p)));
            TestFiles.CheckInsideParents(report.Root);
            if (report.Root.Value?.ToString() == "WavPack" && length > 40 && !name.Contains("apev2", StringComparison.Ordinal))
            {
                Assert.Contains(report.Findings, f => f.Code.StartsWith("wavpack.", StringComparison.Ordinal) && f.Severity >= Severity.Warning);
            }
        }
    }

    // Flipping every byte of a header does not break the analysis.
    [Fact]
    public void DamagedHeadersYieldReports()
    {
        byte[] data = Fixture("external-lossless-16-stereo.wv");
        for (int at = 0; at < 32; at++)
        {
            foreach (byte value in new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFF })
            {
                byte[] damaged = (byte[])data.Clone();
                damaged[at] = value;
                Report report = Analyze(damaged);
                Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
                Assert.All(report.Findings, f => Assert.NotNull(f.Path));
                TestFiles.CheckInsideParents(report.Root);
            }
        }
    }

    [Fact]
    public void ABlockOfMaximumSizeIsRead()
    {
        byte[] file = Fixture("external-lossless-16-mono.wv");
        Node first = Stream(Analyze(file)).Children.First(c => c.Name == "Block");
        byte[] block = file[(int)first.Offset..(int)first.End];
        int fill = (1 << 20) - block.Length;
        byte[] big = [.. block[..^6], .. DummyFill(fill), .. block[^6..]];
        BinaryPrimitives.WriteUInt32LittleEndian(big.AsSpan(4), (uint)(big.Length - 8));

        Report report = Analyze(big);

        Assert.Equal(1 << 20, Stream(report).Children.First(c => c.Name == "Block").Length);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    private static byte[] DummyFill(int length)
    {
        // One large dummy sub-block takes up to 2^25 bytes.
        byte[] data = new byte[length];
        int words = (length - 4) / 2;
        data[0] = 0x80;
        data[1] = (byte)words;
        data[2] = (byte)(words >> 8);
        data[3] = (byte)(words >> 16);
        return data;
    }

    // The default parsers keep no state between analyses: the same file read twice and on several
    // threads at once gives the same report.
    [Fact]
    public void AnalysesDoNotInterfere()
    {
        byte[] data = Fixture("external-hybrid-51-b3-correction.wv");
        string expected = Fixtures.ToJson(Analyze(data));
        Parallel.For(0, 16, _ => Assert.Equal(expected, Fixtures.ToJson(Analyze(data))));
    }

    private static byte[] Syncsafe(int value) => [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    private static byte[] Frame(string id, byte[] data) => [.. System.Text.Encoding.ASCII.GetBytes(id), .. Syncsafe(data.Length), 0, 0, .. data];

    private static byte[] Id3v24(byte[] frames) => [.. "ID3"u8, 4, 0, 0, .. Syncsafe(frames.Length), .. frames];

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
