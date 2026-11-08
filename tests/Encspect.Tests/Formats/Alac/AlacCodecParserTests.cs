using Encspect.Core;
using Encspect.Formats.Alac;
using Encspect.Host;

namespace Encspect.Tests;

// The "alac" codec parser, which a container that stores each frame in a packet hands packets to, with the cookie as the configuration (MP4's alac box,
// Matroska's CodecPrivate). The frames are escape elements AlacBuilder writes; the packets follow one another in a test container's file.
public class AlacCodecParserTests
{
    private sealed class Packets(int[] lengths, Func<Node, CodecConfiguration?> configure, bool callsParseStream = false, IReadOnlyList<EncoderClaim>? claims = null) : IFormatParser
    {
        public string Name => "Test packets";

        public SniffResult Sniff(SniffContext context) => context.Reader.MatchesAt(context.Reader.Start, "PKTS"u8) ? new(100, context.Reader.Start) : SniffResult.None;

        public void Parse(ParseContext context)
        {
            ByteReader reader = context.Reader;
            Node node = context.Root.Add(new Node("Packets", reader.Start, reader.Length));
            if (callsParseStream)
            {
                context.ParseStream(AlacCodecParser.CodecId, node, reader);
                return;
            }

            var list = new List<MappedBytes>();
            long at = reader.Start + 4;
            foreach (int length in lengths)
            {
                reader.Position = at;
                list.Add(new MappedBytes(reader.ReadBytes(length), at));
                at += length;
            }

            context.ParsePackets(AlacCodecParser.CodecId, node, list, configure(node), claims);
        }
    }

    private static Report Analyze(IReadOnlyList<byte[]> frames, Func<Node, CodecConfiguration?> configure, bool callsParseStream = false, IReadOnlyList<EncoderClaim>? claims = null)
    {
        byte[] file = [.. "PKTS"u8, .. frames.SelectMany(f => f)];
        return TestFiles.Analyze(file, new Packets([.. frames.Select(f => f.Length)], configure, callsParseStream, claims));
    }

    private static CodecConfiguration Cookie(Node node, byte[] cookie) => new(new MappedBytes(cookie, 1000), node);

    private static List<byte[]> Frames(int channels = 2, int bits = 16, int frameLength = 64, params int[] samples) =>
        [.. samples.Select((s, i) => AlacBuilder.EscapeFrame(channels, bits, s, frameLength, i))];

    [Fact]
    public void FramesWithTheCookieOfTheTrackAreReportedAsTheFileIs()
    {
        List<byte[]> frames = Frames(2, 16, 64, 64, 64, 30);
        byte[] cookie = AlacBuilder.Cookie();
        Report fromPackets = Analyze(frames, node => Cookie(node, cookie));
        Report fromFile = Analyzer.CreateDefault().Analyze("frames.m4a", new MemoryStream(AlacBuilder.M4a(frames, cookie)));

        StreamInfo packets = fromPackets.Streams.Single();
        StreamInfo file = fromFile.Streams.Single();
        Assert.Equal("Apple Lossless", packets.Codec);
        Assert.Equal(44100, packets.SampleRate);
        Assert.Equal(2, packets.Channels);
        Assert.Equal(16, packets.BitsPerSample);
        Assert.Equal(158, packets.Samples);
        Assert.Equal(file.Samples, packets.Samples);
        Assert.Equal(file.AverageBitrate, packets.AverageBitrate);
        Assert.Equal(file.Series.Select(s => (s.Name, s.Values.ToArray())), packets.Series.Select(s => (s.Name, s.Values.ToArray())));
        Assert.Empty(fromPackets.Findings);
        Assert.Null(packets.Verification!.IsVerified);
        Assert.Empty(packets.Verification.Checksums);
    }

    [Fact]
    public void TheCookieAndTheFramesAreNodesAtTheContainersBytes()
    {
        List<byte[]> frames = Frames(2, 16, 64, 64, 64);
        Report report = Analyze(frames, node => Cookie(node, AlacBuilder.Cookie(maxFrameBytes: 300)));

        Node packets = report.Root.Children.Single(c => c.Name == "Packets");
        Node cookie = packets.Children.Single(c => c.Name == "Magic Cookie");
        Assert.Equal(1000, cookie.Offset);
        Assert.Equal(24, cookie.Length);
        Node config = cookie.Children.Single(c => c.Name == "Specific Config");
        Assert.Equal(64, config.Children.Single(c => c.Name == "Frame Length").Value?.AsInteger());
        Assert.Equal(300, config.Children.Single(c => c.Name == "Maximum Frame Size").Value?.AsInteger());
        Node stream = packets.Children.Single(c => c.Kind == NodeKind.Stream);
        Node[] nodes = [.. stream.Children.Where(c => c.Name == "Frame")];
        Assert.Equal(2, nodes.Length);
        Node element = nodes[0].Children.Single(c => c.Name == "Channel Pair Element");
        Assert.True(element.Children.Single(c => c.Name == "IsUncompressed").Value?.AsBoolean());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TheOlderFormsOfTheCookieGiveTheSameStream(bool atom, bool older)
    {
        List<byte[]> frames = Frames(1, 16, 64, 64, 20);
        byte[] plain = AlacBuilder.Cookie(channels: 1);
        byte[] cookie = atom || older ? AlacBuilder.AtomCookie(plain, older) : plain;
        Report report = Analyze(frames, node => Cookie(node, cookie));

        StreamInfo stream = report.Streams.Single();
        Assert.Equal(84, stream.Samples);
        Assert.Equal(1, stream.Channels);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void WithoutAnyCookieTheFramesAreCountedAndNotRead()
    {
        Report report = Analyze(Frames(2, 16, 64, 64, 64), _ => null);

        Assert.Contains(report.Findings, f => f.Code == "alac.cookie-missing");
        Assert.Empty(report.Streams);
        Assert.Single(report.Findings);
    }

    [Fact]
    public void ACookieShorterThanTheConfigurationIsAFindingAndTheFramesAreNotRead()
    {
        Report report = Analyze(Frames(2, 16, 64, 64), node => Cookie(node, AlacBuilder.Cookie()[..20]));

        Assert.Contains(report.Findings, f => f.Code == "alac.cookie-too-short");
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("alac.frame-", StringComparison.Ordinal));
    }

    [Fact]
    public void APayloadHandedInOnePieceIsNotAnalyzed()
    {
        Report report = Analyze(Frames(2, 16, 64, 64), _ => null, callsParseStream: true);

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal("alac.payload-not-analyzed", finding.Code);
        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void AFrameWithBytesAfterItsEndTagAndOneThatIsCutShort()
    {
        byte[] whole = AlacBuilder.EscapeFrame(2, 16, 64, 64);
        List<byte[]> frames = [[.. whole, 0, 0, 0], whole[..40]];
        Report report = Analyze(frames, node => Cookie(node, AlacBuilder.Cookie()));

        Assert.Contains(report.Findings, f => f.Code == "alac.frame-underrun");
        Assert.Contains(report.Findings, f => f.Code == "alac.frame-overrun");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    [Fact]
    public void AFrameWithoutAnEndTagAfterItsLastChannelIsAFinding()
    {
        List<byte[]> frames = [AlacBuilder.EscapeFrame(2, 16, 64, 64, endTag: false)];
        Report report = Analyze(frames, node => Cookie(node, AlacBuilder.Cookie()));

        Assert.Contains(report.Findings, f => f.Code == "alac.end-missing");
    }

    [Fact]
    public void ZeroPaddingAfterTheLastChannelIsAMissingEndTagAndNotAnOverrun()
    {
        // 75 bits: a partial mono element of one 20-bit sample, then 5 zero bits, which the reference decoder never reads as a tag.
        byte[] frame = AlacBuilder.Frame((0, 3), (0, 4), (0, 12), (1, 1), (0, 2), (1, 1), (1, 32), (5, 20));
        Report report = Analyze([frame], node => Cookie(node, AlacBuilder.Cookie(bits: 20, channels: 1)));

        Assert.Contains(report.Findings, f => f.Code == "alac.end-missing");
        Assert.DoesNotContain(report.Findings, f => f.Code == "alac.frame-overrun");
    }

    [Fact]
    public void AChannelPairBeyondTheCookiesChannelsIsNotReadForItsReservedBits()
    {
        List<(long, int)> fields = [(0, 3), (0, 4), (0, 12), (1, 4)];
        fields.AddRange(Enumerable.Repeat((0L, 16), 64));
        fields.AddRange([(1, 3), (0, 4), (1, 12), (1, 4)]);
        fields.AddRange(Enumerable.Repeat((0L, 16), 128));
        fields.Add((7, 3));
        Report report = Analyze([AlacBuilder.Frame([.. fields])], node => Cookie(node, AlacBuilder.Cookie(channels: 2)));

        // The pair is not read by the reference decoder, so its reserved bits are no fault that ends the frame's walk.
        Finding reserved = Assert.Single(report.Findings, f => f.Code == "alac.element-reserved-bits");
        Assert.Contains("does not read past", reserved.Message);
        Assert.DoesNotContain("refuses", reserved.Message);
        Assert.Contains(report.Findings, f => f.Code == "alac.channels-exceed");
    }

    [Fact]
    public void AFindingOnAnElementPastTheKeptOnesPointsToTheFurtherElementsNode()
    {
        List<(long, int)> fields = [];
        for (int i = 0; i < 9; i++)
        {
            fields.AddRange([(6, 3), (0, 4)]);
        }

        fields.Add((5, 3));
        Report report = Analyze([AlacBuilder.Frame([.. fields])], node => Cookie(node, AlacBuilder.Cookie()));

        Node frameNode = Find(report.Root, n => n.Name == "Frame") ?? throw new InvalidOperationException("No frame node.");
        Node further = frameNode.Children.Single(c => c.Name == "Further Elements");
        Finding finding = Assert.Single(report.Findings, f => f.Code == "alac.element-unknown");
        Assert.Equal(further.Offset, finding.Node?.Offset);
        Assert.Equal(further.Length, finding.Node?.Length);
    }

    [Fact]
    public void AClaimOfAnotherFormatsWriterAndOfADifferentWriterAreNamed()
    {
        List<byte[]> frames = Frames(2, 16, 64, 64);
        EncoderClaim[] claims =
        [
            new("LAME 3.100", new Node("Claim", 0, 1), "test claim", ClaimRole.Writer),
            new("Foo Muxer 1.0", new Node("Claim", 0, 1), "test claim", ClaimRole.Writer),
        ];
        Report report = Analyze(frames, node => Cookie(node, AlacBuilder.Cookie()), claims: claims);

        Assert.Contains(report.Findings, f => f.Code == "alac.writer-other-format");
    }

    [Fact]
    public void ClaimsOfTwoWritersThatDisagreeAreReported()
    {
        List<byte[]> frames = Frames(2, 16, 64, 64);
        EncoderClaim[] claims =
        [
            new("Lavf60.16.100", new Node("Claim", 0, 1), "test claim", ClaimRole.Writer),
            new("Foo Muxer 1.0", new Node("Claim", 0, 1), "test claim", ClaimRole.Writer),
        ];
        Report report = Analyze(frames, node => Cookie(node, AlacBuilder.Cookie()), claims: claims);

        Assert.Contains(report.Findings, f => f.Code == "alac.writer-claim-differs");
    }

    [Fact]
    public void FillElementsPastTheEighthOfAFrameAreOneNodeAndStillCounted()
    {
        byte[] frame = AlacBuilder.FillFrame(40, 64, 64);
        Report report = Analyze([frame], node => Cookie(node, AlacBuilder.Cookie()));

        Node frameNode = Find(report.Root, n => n.Name == "Frame") ?? throw new InvalidOperationException("No frame node.");
        Assert.Equal(8, frameNode.Children.Count(c => c.Name == "Fill Element"));
        Node further = frameNode.Children.Single(c => c.Name == "Further Elements");
        Assert.Equal(34, further.Value?.AsInteger());
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("alac.", StringComparison.Ordinal) && f.Severity != Severity.Info);
        Assert.Equal(64, report.Streams.Single().Samples);
    }

    private static Node? Find(Node start, Func<Node, bool> match)
    {
        if (match(start))
        {
            return start;
        }

        foreach (Node child in start.Children)
        {
            if (Find(child, match) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
