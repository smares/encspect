using Encspect.Core;
using Encspect.Formats.Tta;
using Encspect.Host;

namespace Encspect.Tests;

// The "tta" codec parser, which a container that stores each frame in a packet, without the header and the seek table, hands packets to
// (Matroska's A_TTA1, whose codec mapping keeps the frame whole with its CRC-32 and puts the header in the CodecPrivate; the route is a later
// change). The packets are the frames of a fixture; its header is the configuration. The test container's packets follow one another in the file;
// FramesInPagesAreReadFromTheirRanges is the test of packets that span "pages".
public class TtaCodecParserTests
{
    private const string Fixture = "external-16-stereo.tta";

    // A test container: "PKTS", then the packets one after another, handed to the codec with the configuration the test gives.
    private sealed class Packets(int[] lengths, Func<Node, CodecConfiguration?> configure, bool callsParseStream = false, int gapAt = -1) : IFormatParser
    {
        public string Name => "Test packets";

        public SniffResult Sniff(SniffContext context) => context.Reader.MatchesAt(context.Reader.Start, "PKTS"u8) ? new(100, context.Reader.Start) : SniffResult.None;

        public void Parse(ParseContext context)
        {
            ByteReader reader = context.Reader;
            Node node = context.Root.Add(new Node("Packets", reader.Start, reader.Length));
            if (callsParseStream)
            {
                context.ParseStream(TtaCodecParser.CodecId, node, reader);
                return;
            }

            var list = new List<MappedBytes>();
            long at = reader.Start + 4;
            foreach (int length in lengths)
            {
                reader.Position = at;
                list.Add(new MappedBytes(reader.ReadBytes(length), at) { FollowsGap = list.Count == gapAt });
                at += length;
            }

            context.ParsePackets(TtaCodecParser.CodecId, node, list, configure(node));
        }
    }

    private static Report Analyze(IReadOnlyList<byte[]> frames, Func<Node, CodecConfiguration?> configure, bool callsParseStream = false, int gapAt = -1)
    {
        byte[] file = [.. "PKTS"u8, .. frames.SelectMany(f => f)];
        var container = new Packets([.. frames.Select(f => f.Length)], configure, callsParseStream, gapAt);
        return TestFiles.Analyze(file, container);
    }

    private static CodecConfiguration Header(Node node, byte[] header) => new(new MappedBytes(header, 1000), node);

    private static (byte[] Header, uint[] Sizes, List<byte[]> Frames) Parts() => TtaBuilder.Parts(File.ReadAllBytes(TtaBuilder.FixturePath(Fixture)));

    private static (Report Report, PcmHashingSink Sink) AnalyzeDecoding(IReadOnlyList<byte[]> frames, Func<Node, CodecConfiguration?> configure)
    {
        var sink = new PcmHashingSink();
        byte[] file = [.. "PKTS"u8, .. frames.SelectMany(f => f)];
        var container = new Packets([.. frames.Select(f => f.Length)], configure);
        Report report = TestFiles.Analyze(file, container, options: new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare } }, decoders: DefaultParsers.Decoders);
        return (report, sink);
    }

    [Fact]
    public void FramesWithTheHeaderOfTheTrackAreDecodedAsTheFileIs()
    {
        (byte[] header, _, List<byte[]> frames) = Parts();
        using System.Text.Json.JsonDocument manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, "tta", "external-16-stereo.expected.json")));

        (Report report, PcmHashingSink sink) = AnalyzeDecoding(frames, node => Header(node, header));

        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal) || f.Code is "parse.failed");
        Assert.Equal(manifest.RootElement.GetProperty("decoded_samples").GetInt64(), sink.Samples);
        Assert.Equal(manifest.RootElement.GetProperty("decoded_pcm_md5").GetString(), sink.Hash());
        Assert.Equal(DecodeStatus.Decoded, report.Streams.Single().Verification!.Decoding);
    }

    // Without the header the container's figures and length stand in: the frames decode to the file's samples, the last one's count chosen by its codes.
    // 100,000 samples at 44,100 Hz are frames of 46,080, 46,080 and 7,840.
    [Theory]
    [InlineData(100_000L)]
    [InlineData(100_003L)]
    [InlineData(140_000L)]
    public void FramesWithoutAHeaderAreDecodedWithTheContainersFiguresAndLength(long length)
    {
        (_, _, List<byte[]> frames) = Parts();
        using System.Text.Json.JsonDocument manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, "tta", "external-16-stereo.expected.json")));

        (Report report, PcmHashingSink sink) = AnalyzeDecoding(frames, node => new CodecConfiguration(new MappedBytes(Array.Empty<byte>(), 1000), node)
        {
            Format = new AudioFormat(0, 2, 44100, 0, 0, 16),
            DurationSamples = length,
        });

        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal) || f.Code is "parse.failed");
        Assert.Equal(manifest.RootElement.GetProperty("decoded_pcm_md5").GetString(), sink.Hash());
        Assert.Equal(100_000, report.Streams.Single().Verification!.DecodedSamples);
    }

    // Without a length the codes alone give the last frame's samples, and the stream counts them, decoded or not.
    [Fact]
    public void WithoutAHeaderOrALengthTheLastFramesCodesGiveItsSamples()
    {
        (_, _, List<byte[]> frames) = Parts();

        (Report report, PcmHashingSink sink) = AnalyzeDecoding(frames, node => new CodecConfiguration(new MappedBytes(Array.Empty<byte>(), 1000), node) { Format = new AudioFormat(0, 2, 44100, 0, 0, 16) });
        Report plain = Analyze(frames, node => new CodecConfiguration(new MappedBytes(Array.Empty<byte>(), 1000), node) { Format = new AudioFormat(0, 2, 44100, 0, 0, 16) });

        Assert.Equal(100_000, sink.Samples);
        Assert.Equal(100_000, report.Streams.Single().Samples);
        Assert.Equal(100_000, plain.Streams.Single().Samples);
        Assert.Equal(0, plain.Streams.Single().LostSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal) || f.Code is "parse.failed" or "tta.last-frame-samples-ambiguous");
    }

    // Silence codes in a bit each once its parameters fall, so several counts end in the last byte: the fewest without a length, the nearest with one
    // (with a length past the true count, the largest, which the padding bits put at the true count or up to 7 past it).
    [Theory]
    [InlineData(null, -1)]
    [InlineData(8359L + 5000, 0)]
    [InlineData(8359L + 5100, 1)]
    public void ASilentLastFrameTakesTheFewestCountOrTheOneTheLengthPicks(long? length, int expected)
    {
        (_, List<byte[]> frames) = TtaEncoder.Encode(8000, 16, [new int[8359 + 5000]]);

        (Report report, PcmHashingSink sink) = AnalyzeDecoding(frames, node => new CodecConfiguration(new MappedBytes(Array.Empty<byte>(), 1000), node)
        {
            Format = new AudioFormat(0, 1, 8000, 0, 0, 16),
            DurationSamples = length,
        });

        Finding finding = Assert.Single(report.Findings, f => f.Code == "tta.last-frame-samples-ambiguous");
        Assert.NotNull(finding.Path);
        long samples = report.Streams.Single().Samples;
        Assert.Equal(samples, sink.Samples);
        switch (expected)
        {
            case -1:
                Assert.InRange(samples, 8359 + 5000 - 7, 8359 + 5000);
                break;
            case 0:
                Assert.Equal(8359 + 5000, samples);
                break;
            default:
                Assert.InRange(samples, 8359 + 5000, 8359 + 5000 + 7);
                break;
        }
    }

    [Fact]
    public void WithoutAHeaderOrAFormatTheTrackIsNotDecoded()
    {
        (_, _, List<byte[]> frames) = Parts();

        (Report report, PcmHashingSink sink) = AnalyzeDecoding(frames, node => new CodecConfiguration(new MappedBytes(Array.Empty<byte>(), 1000), node));

        Assert.Contains(report.Findings, f => f.Code == "decode.not-possible");
        Assert.Equal(0, sink.Declared);
    }

    [Fact]
    public void FramesWithTheHeaderOfTheTrackAreReportedAsTheFileIs()
    {
        (byte[] header, _, List<byte[]> frames) = Parts();
        Report fromPackets = Analyze(frames, node => Header(node, header));
        Report fromFile = Analyzer.CreateDefault().Analyze(Fixture, File.OpenRead(TtaBuilder.FixturePath(Fixture)));

        StreamInfo packets = fromPackets.Streams.Single();
        StreamInfo file = fromFile.Streams.Single();
        Assert.Equal("TTA", packets.Codec);
        Assert.Equal(file.SampleRate, packets.SampleRate);
        Assert.Equal(file.Channels, packets.Channels);
        Assert.Equal(file.BitsPerSample, packets.BitsPerSample);
        Assert.Equal(file.Samples, packets.Samples);
        Assert.Equal(file.Duration, packets.Duration);
        Assert.Equal(file.AverageBitrate, packets.AverageBitrate);
        Assert.Equal(file.IsVbr, packets.IsVbr);
        Assert.Equal(file.Series.Select(s => (s.Name, s.Values.ToArray())), packets.Series.Select(s => (s.Name, s.Values.ToArray())));
        Assert.Empty(fromPackets.Findings);
        Assert.Equal(true, packets.Verification!.IsVerified);
        Assert.Equal(["header CRC-32", "frame CRC-32"], packets.Verification.Checksums.Select(c => c.Name));
        Assert.Equal([1L, 3L], packets.Verification.Checksums.Select(c => c.Checked));
    }

    [Fact]
    public void TheFramesAreNodesWithTheirCrcsAndTheHeaderIsNodesAtTheContainersBytes()
    {
        (byte[] header, _, List<byte[]> frames) = Parts();
        Report report = Analyze(frames, node => Header(node, header));

        Node packets = report.Root.Children.Single(c => c.Name == "Packets");
        Node headerNode = packets.Children.Single(c => c.Name == "Header");
        Assert.Equal(1000, headerNode.Offset);
        Assert.Equal(22, headerNode.Length);
        Assert.Equal("TTA1", headerNode.Children.Single(c => c.Name == "Signature").Value?.ToString());
        Node stream = packets.Children.Single(c => c.Name == "Frames");
        Node[] nodes = [.. stream.Children.Where(c => c.Name == "Frame")];
        Assert.Equal(3, nodes.Length);
        Assert.All(nodes, n => Assert.True(n.Children.Single(c => c.Name == "IsCrc32Valid").Value?.AsBoolean()));
        Assert.Equal(46080, nodes[0].Value?.AsInteger());
        Assert.Equal(7840, nodes[2].Value?.AsInteger());
        Assert.Equal(frames[1].Length - 4, nodes[1].Children.Single(c => c.Name == "Frame Data").Length);
    }

    [Fact]
    public void WithoutAnyConfigurationTheFramesAreCheckedAndNothingIsKnownOfTheAudio()
    {
        (_, _, List<byte[]> frames) = Parts();
        Report report = Analyze(frames, _ => null);

        StreamInfo stream = report.Streams.Single();
        Assert.Equal(0, stream.SampleRate);
        Assert.Equal(0, stream.Samples);
        Assert.Empty(stream.Series);
        Assert.Null(stream.Verification!.IsVerified);
        Assert.Equal("frame CRC-32", Assert.Single(stream.Verification.Checksums).Name);
        Assert.Equal(3, stream.Verification.Checksums[0].Checked);
        Assert.False(stream.Verification.Checksums[0].CoversAllAudio);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void TheContainersOwnFiguresStandInForAMissingHeader()
    {
        (_, _, List<byte[]> frames) = Parts();
        Report report = Analyze(frames, node => new CodecConfiguration(new MappedBytes(Array.Empty<byte>(), 1000), node) { Format = new AudioFormat(0, 2, 44100, 0, 0, 16) });

        StreamInfo stream = report.Streams.Single();
        Assert.Equal(44100, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
        Assert.Equal(16, stream.BitsPerSample);
        Assert.Null(stream.Verification!.IsVerified);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void ConfigurationThatIsNoHeaderIsAFindingAndTheFiguresComeFromTheContainer()
    {
        (_, _, List<byte[]> frames) = Parts();
        Report report = Analyze(frames, node => new CodecConfiguration(new MappedBytes(new byte[22], 1000), node) { Format = new AudioFormat(0, 2, 48000, 0, 0, 24) });

        Assert.Contains(report.Findings, f => f.Code == "tta.header-invalid");
        Assert.Equal(48000, report.Streams.Single().SampleRate);
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    [Fact]
    public void BytesAfterTheHeaderInTheConfigurationAreShownAndNotRead()
    {
        (byte[] header, uint[] sizes, List<byte[]> frames) = Parts();
        byte[] withTable = [.. header, .. sizes.SelectMany(BitConverter.GetBytes), 1, 2, 3, 4];
        Report report = Analyze(frames, node => Header(node, withTable));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "tta.configuration-extra");
        Assert.Equal(Severity.Info, finding.Severity);
        Node extra = report.Root.Children.Single(c => c.Name == "Packets").Children.Single(c => c.Name == "Extra Data");
        Assert.Equal(withTable.Length - 22, extra.Length);
        Assert.Equal(1022, extra.Offset);
        Assert.Equal(true, report.Streams.Single().Verification!.IsVerified);
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    [Fact]
    public void FiguresOfTheTrackThatDifferFromTheHeadersAreAFinding()
    {
        (byte[] header, _, List<byte[]> frames) = Parts();
        Report report = Analyze(frames, node => new CodecConfiguration(new MappedBytes(header, 1000), node) { Format = new AudioFormat(0, 1, 48000, 0, 0, 24) });

        Finding finding = Assert.Single(report.Findings, f => f.Code == "tta.configuration-differs");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("48000 Hz, 1 channel and 24 bits", finding.Message);
        Assert.Contains("44100 Hz, 2 channels and 16 bits", finding.Message);
        Assert.Equal(44100, report.Streams.Single().SampleRate);
        Assert.NotNull(finding.Path);
    }

    [Fact]
    public void FiguresOfTheTrackThatAgreeWithTheHeaderAreNoFinding()
    {
        (byte[] header, _, List<byte[]> frames) = Parts();
        Report report = Analyze(frames, node => new CodecConfiguration(new MappedBytes(header, 1000), node) { Format = new AudioFormat(0, 2, 44100, 0, 0, 16) });

        Assert.Empty(report.Findings);
    }

    [Fact]
    public void FewerFramesThanTheHeaderCallsForIsAFindingAndTheStreamIsNotVerified()
    {
        (byte[] header, _, List<byte[]> frames) = Parts();
        Report report = Analyze(frames[..2], node => Header(node, header));

        Assert.Contains(report.Findings, f => f.Code == "tta.frame-count-differs");
        Assert.Null(report.Streams.Single().Verification!.IsVerified);
        Assert.False(report.Streams.Single().Verification!.Checksums.Single(c => c.Name == "frame CRC-32").CoversAllAudio);
    }

    [Fact]
    public void AFrameWhoseCrcFailsIsAFindingAtItsCrcField()
    {
        (byte[] header, _, List<byte[]> frames) = Parts();
        frames[1][10] ^= 1;
        Report report = Analyze(frames, node => Header(node, header));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "tta.frame-crc-mismatch");
        Assert.Equal("CRC-32", TestFiles.Follow(report.Root, finding.Path!).Name);
        Assert.Equal(false, report.Streams.Single().Verification!.IsVerified);
    }

    [Theory]
    [InlineData("matroska/external-ffmpeg-tta.mka")]
    [InlineData("matroska/external-mkvmerge-tta.mka")]
    public void TtaInMatroskaWithoutAHeaderTakesItsTimingFromTheContainer(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        StreamInfo stream = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), new MemoryStream(data)).Streams.Single();

        Assert.Equal("TTA", stream.Codec);
        Assert.True(stream.Duration > 0.5);
        Assert.Equal((long)Math.Round(stream.Duration * stream.SampleRate), stream.Samples);
        Assert.True(stream.AverageBitrate > 10);
    }

    // A headerless track's Channels element is the container's figure, up to int.MaxValue: the parser allocates nothing for it (fuzz/ has the same
    // files, whose peak memory MemoryTests measures).
    [Theory]
    [InlineData("matroska-tta-track-channels-2-pow-26.bin", 1 << 26)]
    [InlineData("matroska-tta-track-channels-2-pow-31-minus-1.bin", int.MaxValue)]
    public void ATrackWithMoreChannelsThanTtaHoldsIsRefusedAsAFinding(string name, int channels)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", name));
        Report report = Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "tta.channels-beyond-limit");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains(channels.ToString(System.Globalization.CultureInfo.InvariantCulture), finding.Message, StringComparison.Ordinal);
        Assert.NotNull(finding.Path);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }

    [Fact]
    public void APacketShorterThanACrcIsAFinding()
    {
        (byte[] header, _, List<byte[]> frames) = Parts();
        frames[1] = frames[1][..3];
        Report report = Analyze(frames, node => Header(node, header));

        Assert.Contains(report.Findings, f => f.Code == "tta.frame-too-small");
        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.NotEqual(true, verification.IsVerified);
        ChecksumTally frameCrc = verification.Checksums.Single(c => c.Name == "frame CRC-32");
        Assert.Equal(2, frameCrc.Stored);
        Assert.Equal(0, frameCrc.Failed);
    }

    [Fact]
    public void APacketAfterALostOneStopsTheCrcsFromVouchingForAllTheAudio()
    {
        (byte[] header, _, List<byte[]> frames) = Parts();
        Report report = Analyze(frames, node => Header(node, header), gapAt: 1);

        Assert.False(report.Streams.Single().Verification!.Checksums.Single(c => c.Name == "frame CRC-32").CoversAllAudio);
        Assert.Null(report.Streams.Single().Verification!.IsVerified);
    }

    [Fact]
    public void NoPacketsAtAllGiveTheStreamOfTheHeader()
    {
        (byte[] header, _, _) = Parts();
        Report report = Analyze([], node => Header(node, header));

        StreamInfo stream = report.Streams.Single();
        Assert.Equal(44100, stream.SampleRate);
        Assert.Equal(0, stream.Samples);
        Assert.Contains(report.Findings, f => f.Code == "tta.frame-count-differs");
    }

    [Fact]
    public void APayloadInOnePieceIsNotAnalyzed()
    {
        (_, _, List<byte[]> frames) = Parts();
        Report report = Analyze(frames, _ => null, callsParseStream: true);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "tta.payload-not-analyzed");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Empty(report.Streams);
    }

    [Fact]
    public void FramesInPagesAreReadFromTheirRanges()
    {
        (byte[] header, _, List<byte[]> frames) = Parts();
        (byte[] file, List<List<ByteRange>> ranges) = PagedContainer.Build(200, [header, .. frames]);
        var analyzer = new Analyzer(DefaultParsers.Tags, [new PagedContainer(TtaCodecParser.CodecId, configured: true), .. DefaultParsers.Formats], DefaultParsers.Codecs);

        Report report = analyzer.Analyze("pages.bin", new MemoryStream(file));

        Assert.Empty(report.Findings);
        Assert.Equal(true, report.Streams.Single().Verification!.IsVerified);
        Node stream = report.Root.Children.Single().Children.Single(c => c.Name == "Frames");
        Node spanning = stream.Children[1];
        Assert.True(spanning.Ranges?.Count > 1, "The second frame spans pages, so its bytes are not contiguous.");
        Assert.Equal(ranges[2][0].Offset, spanning.Offset);
        TestFiles.CheckInsideParents(report.Root);
    }
}
