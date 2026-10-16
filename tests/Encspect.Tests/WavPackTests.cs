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

    private static int Sniff(byte[] data) => Parser.Sniff(new SniffContext(new ByteReader(new MemoryStream(data)))).Class;

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
            Assert.True(Parser.Sniff(new SniffContext(reader)).Class == 0, path);
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

    // Where the data does not start with a block, the stream declares no end, and the tag still
    // breaks the chain: the stream is read up to it, so no node covers the tag, and the bytes after
    // it are reported as data.
    [Fact]
    public void ATagBetweenBlocksEndsTheStreamWhereNoEndIsDeclared()
    {
        List<byte[]> blocks = Blocks(Fixture("external-lossless-16-stereo.wv"));
        byte[] junk = new byte[100];
        Array.Fill(junk, (byte)0x90);
        byte[] data = TaggedBetween(blocks, junk.Length, junk);

        Report report = Analyze(data);

        Assert.Equal(["ID3v2", "WavPack", "ID3v2", "Data"], report.Root.Children.Select(c => c.Name));
        Node stream = Stream(report);
        Assert.Equal(1, stream.Children.Count(c => c.Name == "Block"));
        Assert.Equal(1, stream.Children.Count(c => c.Name == "Skipped Data"));
        Assert.Contains(report.Findings, f => f.Code == "wavpack.data-after-tag");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.Equal(blocks[0].Length + junk.Length, stream.Length);
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
        Assert.Equal("true", Statistic(alone, "IsCorrectionFileCreated"));
        Assert.Equal("false", Statistic(alone, "IsLossless"));
        Assert.Equal(["wavpack.correction-file"], alone.Findings.Select(f => f.Code));

        Report hybrid = Analyze(main);
        Assert.Equal("WavPack hybrid", hybrid.Streams.Single().Codec);
        Assert.Equal("true", Statistic(hybrid, "IsHybrid"));
        Assert.Equal("false", Statistic(hybrid, "IsLossless"));
        Assert.Equal("true", Statistic(hybrid, "IsCorrectionFileCreated"));
        Assert.Equal("false", Statistic(Analyze(Fixture("external-hybrid-lossy-b4.wv")), "IsCorrectionFileCreated"));
        Assert.Equal("false", Statistic(Analyze(Fixture("external-lossless-16-stereo.wv")), "IsCorrectionFileCreated"));
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

    private static IEnumerable<Node> Descendants(Node node) => node.Children.SelectMany(c => new[] { c }.Concat(Descendants(c)));

    private static Node FirstBlock(Report report) => Stream(report).Children.First(c => c.Name == "Block");

    // The encoder codes the first channel of a stereo block as left, or as side with joint stereo, and
    // the second as right or mid (pack.c), and the weights, medians and hybrid profile follow that
    // order. The noise shaping is applied before the mid/side conversion, so it stays left and right.
    [Theory]
    [InlineData("external-joint-stereo-on.wv", "Side", "Mid")]
    [InlineData("external-joint-stereo-off.wv", "Left", "Right")]
    [InlineData("external-hybrid-lossy-b4.wv", "Side", "Mid")]
    [InlineData("external-hybrid-b4-left-right.wv", "Left", "Right")]
    public void TheChannelsOfAStereoBlockAreNamedByHowTheyAreCoded(string name, string first, string second)
    {
        string[] names = [.. Descendants(FirstBlock(AnalyzeFixture(name))).Select(n => n.Name)];
        Assert.Contains($"{first} Weight", names);
        Assert.Contains($"{second} Weight", names);
        Assert.Contains($"{first} Medians", names);
        Assert.Contains($"{second} Medians", names);
        foreach (string other in first == "Side" ? (string[])["Left", "Right"] : ["Side", "Mid"])
        {
            Assert.DoesNotContain($"{other} Weight", names);
            Assert.DoesNotContain($"{other} Medians", names);
        }

        if (name.Contains("hybrid", StringComparison.Ordinal))
        {
            Assert.Contains($"{first} Bitrate Accumulator", names);
            Assert.Contains($"{second} Slow Level", names);
        }
    }

    [Fact]
    public void NoiseShapingStaysLeftAndRightWithJointStereoAndMonoBlocksHaveNoSecondChannel()
    {
        // Sub-blocks of 12 bytes (an error, an accumulator and a delta of each channel), of 2 (a weight of each) and of 6 for mono.
        List<byte[]> stereo = WavPackBuilder.Blocks("external-hybrid-lossy-b4.wv");
        stereo[0] = WavPackBuilder.Join(stereo[0], WavPackBuilder.Parts(stereo[0]).Prepend(WavPackBuilder.Sub(0x07, new byte[12])));
        string[] joint = [.. Descendants(FirstBlock(Analyze(WavPackBuilder.Concat(stereo)))).Select(n => n.Name)];
        Assert.Contains("Left Error", joint);
        Assert.Contains("Right Shaping Delta", joint);
        Assert.DoesNotContain("Side Error", joint);

        stereo[0] = WavPackBuilder.Replace(stereo[0], 0x07, WavPackBuilder.Sub(0x07, [0x10, 0x20]));
        string[] weights = [.. Descendants(FirstBlock(Analyze(WavPackBuilder.Concat(stereo)))).Select(n => n.Name)];
        Assert.Contains("Left Weight", weights);
        Assert.Contains("Right Weight", weights);

        List<byte[]> mono = WavPackBuilder.Blocks("external-hybrid-mono-b3.wv");
        mono[0] = WavPackBuilder.Join(mono[0], WavPackBuilder.Parts(mono[0]).Prepend(WavPackBuilder.Sub(0x07, [0x10, 0x20])));
        string[] unused = [.. Descendants(FirstBlock(Analyze(WavPackBuilder.Concat(mono)))).Select(n => n.Name)];
        Assert.Contains("Weight", unused);
        Assert.Contains("Unused Weight", unused);
        Assert.DoesNotContain("Right Weight", unused);

        mono[0] = WavPackBuilder.Replace(mono[0], 0x07, WavPackBuilder.Sub(0x07, new byte[6]));
        string[] longForm = [.. Descendants(FirstBlock(Analyze(WavPackBuilder.Concat(mono)))).Select(n => n.Name)];
        Assert.Contains("Error", longForm);
        Assert.Contains("Shaping Delta", longForm);
        Assert.DoesNotContain(longForm, n => n is "Left Error" or "Right Error" or "Left Shaping Delta" or "Right Shaping Delta");
    }

    // The samples of the decorrelation passes are listed pass by pass, which needs the terms
    // sub-block of the same block.
    [Fact]
    public void TheDecorrelationSamplesListTheirPasses()
    {
        Node samples = Descendants(FirstBlock(AnalyzeFixture("external-mode-high.wv"))).First(n => n.Name == "Decorrelation Samples");
        Node data = samples.Children.Single(c => c.Name == "Data");
        Assert.Equal("Pass 1", data.Children.First().Name);
        Assert.All(data.Children, pass => Assert.StartsWith("Pass ", pass.Name, StringComparison.Ordinal));
    }

    // A hybrid target below 568/256 bits per sample is stored as an accumulator of 0 (write_words.c
    // word_set_bitrate), so only a limit is known.
    [Theory]
    [InlineData("external-hybrid-b2.wv")]
    [InlineData("external-hybrid-b22.wv")]
    public void AHybridTargetBelowTheLowestTheEncoderStoresIsALimit(string name)
    {
        Report report = AnalyzeFixture(name);
        Assert.Equal("2.22 bits/sample or less", Statistic(report, "Hybrid Target Bitrate"));
        Assert.Equal("195.7 kbps or less", Statistic(report, "Hybrid Target Rate"));
        Node profile = Descendants(FirstBlock(report)).First(n => n.Name == "Hybrid Profile");
        Assert.Equal("2.22 bits/sample or less", profile.Children.Single(c => c.Name == "Data").Children.Single(c => c.Name == "Target Bitrate").Value?.ToString());
        Assert.Equal("3.00 bits/sample", Statistic(AnalyzeFixture("external-hybrid-mono-b3.wv"), "Hybrid Target Bitrate"));
    }

    [Fact]
    public void AChannelInfoOfNoChannelsAndNoMaskIsNoChannelInfo()
    {
        Report report = AnalyzeFixture("built-channel-info-no-count.wv");
        Assert.DoesNotContain(report.Findings, f => f.Code == "wavpack.channel-info-invalid");
        StreamInfo stream = report.Streams.Single();
        Assert.Equal(2, stream.Channels);
        Assert.Equal(["front left", "front right"], stream.ChannelLayout!.Select(ChannelLayouts.Name));

        // With speakers in the mask the count of 0 is refused.
        List<byte[]> blocks = WavPackBuilder.Blocks("external-lossless-16-stereo.wv");
        blocks[0] = WavPackBuilder.Join(blocks[0], WavPackBuilder.Parts(blocks[0]).Prepend(WavPackBuilder.Sub(0x0D, [0, 3])));
        Assert.Contains(Analyze(WavPackBuilder.Concat(blocks)).Findings, f => f.Code == "wavpack.channel-info-invalid");
    }

    [Fact]
    public void ASampleRateOfZeroIsIgnoredAsLibwavpackDoes()
    {
        Report flags = AnalyzeFixture("built-sample-rate-zero.wv");
        Assert.Equal(44100, flags.Streams.Single().SampleRate);
        Assert.NotEqual(0.0, flags.Streams.Single().Duration);
        Assert.NotNull(flags.Streams.Single().AverageBitrate);
        Assert.DoesNotContain(flags.Findings, f => f.Code == "wavpack.sample-rate-missing");

        Report custom = AnalyzeFixture("built-sample-rate-zero-custom.wv");
        Assert.Equal(44100, custom.Streams.Single().SampleRate);
        Assert.Contains(custom.Findings, f => f.Code == "wavpack.sample-rate-missing");
        Assert.Contains(custom.Findings, f => f.Code == "wavpack.sample-rate-zero");
    }

    [Fact]
    public void ADsdRateThatOverflowsIsHeldAndReported()
    {
        Report report = AnalyzeFixture("built-dsd-rate-overflow.wv");
        StreamInfo stream = report.Streams.Single();
        Assert.Equal(int.MaxValue, stream.SampleRate);
        Assert.True(stream.Duration > 0);
        Assert.True(stream.AverageBitrate > 0);
        Assert.Equal("2147483647 Hz", Statistic(report, "Sample Rate"));
        Finding finding = Assert.Single(report.Findings, f => f.Code == "wavpack.sample-rate-implausible");
        Assert.NotNull(finding.Path);
        Assert.DoesNotContain(AnalyzeFixture("external-dsd-64.wv").Findings, f => f.Code == "wavpack.sample-rate-implausible");
    }

    // libwavpack reads at most 8 streams into a set without the long channel info, so a file of blocks
    // that never end a set has the channels of one set, not of the file.
    [Fact]
    public void ASetWithoutAFinalBlockEndsAtTheStreamLimit()
    {
        Report report = AnalyzeFixture("built-set-without-final-limit.wv");
        Assert.Equal(16, report.Streams.Single().Channels);
        Assert.Equal("3 sets", Statistic(report, "Block Sets"));
        Assert.Contains(report.Findings, f => f.Code == "wavpack.block-set-broken" && f.Message.Contains("stream limit", StringComparison.Ordinal));

        // Mono blocks with no initial or final flag, as an encoder that never ends a set writes them.
        byte[] mono = WavPackBuilder.Blocks("external-lossless-16-mono.wv")[0];
        byte[] open = WavPackBuilder.SetFlags(mono, 0, 0x1800);
        Report many = Analyze(WavPackBuilder.Concat(Enumerable.Repeat(open, 300)));
        Assert.True(many.Streams.Single().Channels <= 8, $"{many.Streams.Single().Channels} channels");
        Assert.Contains(many.Findings, f => f.Code == "wavpack.block-set-broken");
        Assert.DoesNotContain(many.Findings, f => f.Code == "parse.failed");
    }

    [Fact]
    public void TheDsdModesAreCheckedAsLibwavpackChecksThem()
    {
        Assert.DoesNotContain(AnalyzeFixture("built-dsd-stored-mode.wv").Findings, f => f.Code == "wavpack.dsd-invalid");
        Assert.Contains(AnalyzeFixture("built-dsd-stored-size.wv").Findings, f => f.Code == "wavpack.dsd-invalid" && f.Message.Contains("stored mode", StringComparison.Ordinal));
        Assert.Contains(AnalyzeFixture("built-dsd-fast-history-only.wv").Findings, f => f.Code == "wavpack.dsd-invalid" && f.Message.Contains("history bits", StringComparison.Ordinal));
    }

    // The audio's bitrate leaves out the wrapper the first block holds: the bits are the blocks' less
    // the RIFF header sub-block.
    [Fact]
    public void TheAverageBitrateLeavesOutTheWrapper()
    {
        byte[] file = Fixture("external-lossless-16-stereo.wv");
        Report report = Analyze(file);
        long wrapper = Descendants(Stream(report)).Where(n => n.Name is "RIFF Header" or "RIFF Trailer").Sum(n => n.Length);
        Assert.True(wrapper > 0);
        long audio = Stream(report).Children.Where(c => c.Name == "Block").Sum(c => c.Length) - wrapper;
        StreamInfo stream = report.Streams.Single();
        Assert.Equal(audio * 8 * 44100.0 / 24000 / 1000, stream.AverageBitrate!.Value, 6);

        // The same blocks without the wrapper, and with it in a block of its own that counts no sample, give the same bitrate.
        List<byte[]> blocks = WavPackBuilder.Blocks("external-lossless-16-stereo.wv");
        byte[] riff = WavPackBuilder.Parts(blocks[0]).Single(p => WavPackBuilder.Function(p) == 0x21);
        blocks[0] = WavPackBuilder.Replace(blocks[0], 0x21, null);
        Report stripped = Analyze(WavPackBuilder.Concat(blocks));
        Assert.Equal(stream.AverageBitrate!.Value, stripped.Streams.Single().AverageBitrate!.Value, 9);

        byte[] metadata = [.. blocks[0][..WavPackBuilder.HeaderLength]];
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(20), 0);
        metadata = WavPackBuilder.Join(metadata, [riff, WavPackBuilder.Sub(0x2F, new byte[4])]);
        Report separate = Analyze([.. metadata, .. WavPackBuilder.Concat(blocks)]);
        Assert.Equal(stream.AverageBitrate!.Value, separate.Streams.Single().AverageBitrate!.Value, 9);
        Assert.Equal(1, Stream(separate).Children.Count(c => c.Name == "Metadata Block"));
    }

    // Libwavpack 5.0 and later write a new config block and block checksums; a WavPack 4 stream
    // without them also comes from other encoders, FFmpeg's among them.
    [Fact]
    public void AWavPackFourStreamNamesNoEncoder()
    {
        Report ffmpeg = AnalyzeFixture("external-ffmpeg-apev2-tag.wv");
        StreamInfo stream = ffmpeg.Streams.Single();
        Assert.Equal("WavPack 4 stream, no WavPack 5 features", stream.Encoder);
        Assert.Equal(Confidence.Low, stream.EncoderConfidence);
        Node encoder = Stream(ffmpeg).Children.Single(c => c.Name == "Encoder");
        Assert.Equal("Low", encoder.Children.Single(c => c.Name == "Confidence").Value?.ToString());
        Assert.Contains(encoder.Children, c => c.Name == "Evidence" && c.Value!.ToString().Contains("another encoder, such as FFmpeg, may have written it", StringComparison.Ordinal));
        // FFmpeg's APE item names its muxer: the writer, apart from the WavPack 4 stream's encoder.
        Assert.StartsWith("FFmpeg (Lavf", stream.Writer, StringComparison.Ordinal);
        Node writer = Stream(ffmpeg).Children.Single(c => c.Name == "Writer");
        Assert.Contains(writer.Children, c => c.Name == "Evidence" && c.Value!.ToString().Contains("APE Encoder item", StringComparison.Ordinal) && c.Value!.ToString().Contains("Lavf", StringComparison.Ordinal));
        Assert.DoesNotContain(ffmpeg.Findings, f => f.Code is "wavpack.encoder-claim-differs" or "wavpack.writer-claim-differs");

        StreamInfo five = AnalyzeFixture("external-lossless-16-stereo.wv").Streams.Single();
        Assert.Equal("WavPack 5.0 or later", five.Encoder);
        Assert.Equal(Confidence.Medium, five.EncoderConfidence);
    }

    // The APE tag's Encoder item, spelled in any case, reaches the parser as a claim of the writer role: it names the writer, whatever
    // the blocks say of the encoder, and is no conflict with it.
    [Fact]
    public void AnApeEncoderItemIsAWriterClaim()
    {
        Report differs = AnalyzeFixture("built-ape-encoder-claim.wv");
        StreamInfo other = differs.Streams.Single();
        Assert.Equal(("LAME 3.100", Confidence.Low), (other.Writer, other.WriterConfidence));
        Assert.DoesNotContain(differs.Findings, f => f.Code is "wavpack.encoder-claim-differs" or "wavpack.writer-claim-differs");

        Report agrees = AnalyzeFixture("built-ape-encoder-wavpack.wv");
        Assert.DoesNotContain(agrees.Findings, f => f.Code is "wavpack.encoder-claim-differs" or "wavpack.writer-claim-differs");
        Node writer = Stream(agrees).Children.Single(c => c.Name == "Writer");
        Assert.Contains(writer.Children, c => c.Name == "Evidence" && c.Value!.ToString().Contains("APE Encoder item", StringComparison.Ordinal) && c.Value!.ToString().Contains("WavPack 5.6.0", StringComparison.Ordinal));
    }

    // An ID3v2 TSSE frame is the encoder's, as the specification defines it; one that differs from the blocks is a conflict within the role.
    [Fact]
    public void AnId3TsseFrameIsAnEncoderClaim()
    {
        byte[] file = [.. TestFiles.Id3v23(("TSSE", "LAME 3.100")), .. File.ReadAllBytes(WavPackBuilder.FixturePath("external-lossless-16-stereo.wv"))];
        Report report = Analyze(file, "tsse.wv");
        Assert.Contains(report.Findings, f => f.Code == "wavpack.encoder-claim-differs");
        Assert.Null(report.Streams.Single().Writer);
    }

    private static byte[] Syncsafe(int value) => [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    private static byte[] Frame(string id, byte[] data) => [.. System.Text.Encoding.ASCII.GetBytes(id), .. Syncsafe(data.Length), 0, 0, .. data];

    private static byte[] Id3v24(byte[] frames) => [.. "ID3"u8, 4, 0, 0, .. Syncsafe(frames.Length), .. frames];

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
