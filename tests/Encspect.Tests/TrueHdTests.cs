using System.Buffers.Binary;
using System.Security.Cryptography;
using Encspect.Core;
using Encspect.Formats.TrueHd;
using Encspect.Host;

namespace Encspect.Tests;

// The checks and the example unit of docs/design/truehd-format.md, the sniffer, and the "truehd" and "mlp" codec parsers on the units of a fixture
// handed over as packets and as one payload (the routes of MP4 and Matroska are a later change).
public class TrueHdTests
{
    private static readonly byte[] Digits = "123456789"u8.ToArray();

    // The first access unit of 440hz.mlp (docs/design/truehd-format.md, "Example unit").
    private static readonly byte[] ExampleUnit = Convert.FromHexString(
        "9033ffd8f8726fbb0f8f0001b752400000008d9a11054c0300008080001b538c3022" +
        "f1ea0000011000000453bdc0000000023044417922b341380d2009ffd7eafe19ddde" +
        "07663ec49100d7601981a53c86d7848ffdda86656f52aa2d9bca9762cb635800e1ff");

    [Fact]
    public void TheCheckValuesOfTheDescriptionHold()
    {
        Assert.Equal(0x4FF7, Checks.Standard16Public(Digits));
        Assert.Equal(0xC59E, Checks.MajorSync([.. Digits, 0, 0]));
        Assert.Equal(0x37, Checks.Standard8Restart(Digits));
        Assert.Equal(0xA4, Checks.Substream(Digits));

        // The restart header's form, bit by bit, is the standard CRC over all but the last 8 bits XORed with those bits.
        Assert.Equal((byte)(Checks.Standard8Restart(Digits[..^1]) ^ Digits[^1]), Checks.RestartHeader(Digits, 0, Digits.Length * 8));
    }

    [Theory]
    [InlineData("f8726fba0007800fb75200000000841b203c003ec947e307e3003602", 0x3602)]
    [InlineData("f8726fbb0f8f0001b752400000008d9a11054c0300008080001b538c", 0x538C)]
    public void TheMajorSyncsOfTheRealFilesCheckOut(string hex, int crc)
    {
        byte[] sync = Convert.FromHexString(hex);

        Assert.Equal(crc, Checks.MajorSync(sync));
        Assert.Equal(crc, (sync[^2] << 8) | sync[^1]);
    }

    [Fact]
    public void TheExampleUnitReadsAsTheDescriptionSays()
    {
        Assert.Equal(102, ExampleUnit.Length);
        var layout = new UnitLayout();
        layout.Read(ExampleUnit, 1, true);

        Assert.Equal(102, layout.Length);
        Assert.Equal(9, layout.CheckNibble);
        Assert.Equal(0xFFD8, layout.InputTiming);
        Assert.True(layout.HasSync);
        Assert.Equal(UnitProblems.None, layout.Problems);
        DirectoryEntry entry = layout.Entries[0];
        Assert.Equal(34, entry.End);
        Assert.True(entry.HasCheckData);
        Assert.True(entry.Reserved);
        Assert.False(entry.IsNonRestart);

        ReadOnlySpan<byte> substream = ExampleUnit.AsSpan(entry.Start, entry.Stop - entry.Start);
        Assert.True(Checks.SubstreamParityHolds(substream[..^2], substream[^2]));
        Assert.Equal(0xE1, substream[^2]);
        Assert.Equal(0xFF, substream[^1]);
        Assert.Equal(substream[^1], Checks.Substream(substream[..^2]));

        // The restart header in bits 2 to 126, its CRC byte the 0x18 the description says to expect.
        Assert.Equal(0x18, Checks.RestartHeader(substream, 2, 125));
    }

    [Fact]
    public void TheExampleUnitAloneIsAnMlpStreamThatDecodesToTheDescriptionsSamples()
    {
        var sink = new FirstSamplesSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = stream => sink } };

        Report report = Analyzer.CreateDefault().Analyze("example.mlp", new MemoryStream(ExampleUnit), options);

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        StreamInfo stream = report.Streams.Single();
        Assert.Equal("MLP", stream.Codec);
        Assert.Equal(44100, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
        Assert.Equal(16, stream.BitsPerSample);
        Assert.Equal(40, stream.Samples);

        // "Output: the first four sample periods are 131,072, 261,888, 391,680 and 520,192 (24-bit values) on both channels."
        Assert.Equal(40, sink.Length);
        Assert.Equal([131_072, 261_888, 391_680, 520_192], sink.Channels[0][..4]);
        Assert.Equal([131_072, 261_888, 391_680, 520_192], sink.Channels[1][..4]);
    }

    // Takes the samples of every channel as the 24-bit values of the description (the sink gets them shifted down to the stream's 16 bits).
    private sealed class FirstSamplesSink : IPcmSink
    {
        public List<int>[] Channels { get; } = [[], []];

        public int Length => Channels[0].Count;

        public void Write(PcmBlock block)
        {
            for (int c = 0; c < 2; c++)
            {
                Channels[c].AddRange(block.IntegerChannel(c).ToArray().Select(v => v << 8));
            }
        }

        public void End(long samples)
        {
        }
    }

    [Fact]
    public void ADamagedBitOfTheExampleUnitIsFoundByItsChecks()
    {
        byte[] data = [.. ExampleUnit];
        data[60] ^= 0x10;

        Report report = Analyzer.CreateDefault().Analyze("example.mlp", new MemoryStream(data));

        Assert.Contains(report.Findings, f => f.Code == "truehd.substream-crc-mismatch");
        Assert.Contains(report.Findings, f => f.Code == "truehd.substream-parity-mismatch");
    }

    [Fact]
    public void TheSnifferNeedsTheChainOfUnitsAndTheMajorSyncCrc()
    {
        var parser = new TrueHdParser();
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "truehd", "external-truehd-stereo-48k-24.thd"));

        Assert.True(Sniff(parser, file).Class >= 90);

        // Cut after the first unit and a half: the chain is broken, and one unit alone is not a stream unless the data ends with it.
        int first = (((file[0] & 0xF) << 8) | file[1]) * 2;
        Assert.Equal(0, Sniff(parser, file[..(first + 20)]).Class);
        Assert.True(Sniff(parser, file[..first]).Class >= 90);

        // The major sync's CRC is wrong.
        byte[] damaged = [.. file];
        damaged[4 + 30 - 1] ^= 1;
        Assert.Equal(0, Sniff(parser, damaged).Class);

        Assert.Equal(0, Sniff(parser, "RIFF....WAVEfmt "u8.ToArray()).Class);
        Assert.Equal(0, Sniff(parser, []).Class);
        Assert.Equal(0, Sniff(parser, new byte[200]).Class);
    }

    // docs/design/truehd-format.md, "MLP channel arrangements": the channels of each code and how many of them are in group 1.
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 2, 2)]
    [InlineData(2, 3, 2)]
    [InlineData(3, 4, 2)]
    [InlineData(4, 3, 2)]
    [InlineData(5, 4, 2)]
    [InlineData(6, 5, 2)]
    [InlineData(7, 3, 2)]
    [InlineData(8, 4, 2)]
    [InlineData(9, 5, 2)]
    [InlineData(10, 4, 2)]
    [InlineData(11, 5, 2)]
    [InlineData(12, 6, 2)]
    [InlineData(13, 4, 3)]
    [InlineData(14, 5, 3)]
    [InlineData(15, 4, 3)]
    [InlineData(16, 5, 3)]
    [InlineData(17, 6, 3)]
    [InlineData(18, 5, 4)]
    [InlineData(19, 5, 4)]
    [InlineData(20, 6, 4)]
    public void TheMlpArrangementsHaveTheChannelsOfTheTable(int code, int channels, int group1)
    {
        Speaker[]? speakers = Tables.ArrangementSpeakers(code);

        Assert.NotNull(speakers);
        Assert.Equal(channels, speakers.Length);
        Assert.Equal(group1, Tables.ArrangementGroup1(code));
        Assert.Null(Tables.ArrangementSpeakers(21));
    }

    [Fact]
    public void TheTrueHdChannelFlagsAddTheChannelsOfTheTableInItsOrder()
    {
        // 5.1 is 0x000F: left, right, center, LFE and the surround pair (the back pair, as the layout has no rear pair).
        Assert.Equal([Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight], Tables.SpeakersOf(0x0F));
        Assert.Equal([Speaker.FrontLeft, Speaker.FrontRight], Tables.SpeakersOf(0x01));

        // With a rear pair the surround pair is the side pair: 7.1 is 0x004F.
        Assert.Equal([Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.SideLeft, Speaker.SideRight, Speaker.BackLeft, Speaker.BackRight],
            Tables.SpeakersOf(0x4F));

        // Every flag adds as many channels as the table gives, and the list has as many speakers as ChannelsOf counts.
        int[] channels = [2, 1, 1, 2, 2, 2, 2, 1, 1, 2, 2, 1, 1];
        int total = 0;
        for (int flag = 0; flag < channels.Length; flag++)
        {
            Assert.Equal(channels[flag], Tables.ChannelsOf(1 << flag));
            Assert.Equal(channels[flag], Tables.SpeakersOf(1 << flag).Length);
            total += channels[flag];
        }

        Assert.Equal(total, Tables.ChannelsOf(0x1FFF));
        Assert.Equal(total, Tables.SpeakersOf(0x1FFF).Length);
    }

    [Theory]
    [InlineData(0, 48000, 40)]
    [InlineData(1, 96000, 80)]
    [InlineData(2, 192000, 160)]
    [InlineData(8, 44100, 40)]
    [InlineData(9, 88200, 80)]
    [InlineData(10, 176400, 160)]
    public void TheRateCodesGiveTheRatesAndTheSamplesOfAUnit(int code, int rate, int samples)
    {
        Assert.Equal(rate, Tables.RateOf(code));
        Assert.Equal(samples, Tables.UnitSamplesOf(code));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(15)]
    public void TheOtherRateCodesGiveNoRate(int code) => Assert.Equal(0, Tables.RateOf(code));

    private static SniffResult Sniff(TrueHdParser parser, byte[] data) => parser.Sniff(new SniffContext(new ByteReader(new MemoryStream(data))));

    // A test-only container: "PKTS", then each packet as a 32-bit length and its bytes, with the codec ID in the first line.
    private sealed class PacketContainer(string codecId) : IFormatParser
    {
        public string Name => "Packets";

        public SniffResult Sniff(SniffContext context)
        {
            ByteReader reader = context.Reader;
            Span<byte> start = stackalloc byte[4];
            reader.Position = reader.Start;
            reader.Read(start);
            return start.SequenceEqual("PKTS"u8) ? new SniffResult(100) : SniffResult.None;
        }

        public void Parse(ParseContext context)
        {
            ByteReader reader = context.Reader;
            byte[] all = new byte[reader.Length];
            reader.Position = reader.Start;
            reader.Read(all);
            var packets = new List<MappedBytes>();
            int at = 4;
            while (at + 4 <= all.Length)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(at));
                at += 4;
                packets.Add(new MappedBytes(all.AsMemory(at, length), at));
                at += length;
            }

            var node = new Node("Packets", 0, all.Length);
            context.Root.Add(node);
            context.ParsePackets(codecId, node, packets, null);
        }
    }

    private static List<byte[]> Units(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "truehd", name));
        var units = new List<byte[]>();
        for (int at = 0; at < file.Length;)
        {
            int length = (((file[at] & 0xF) << 8) | file[at + 1]) * 2;
            units.Add(file[at..(at + length)]);
            at += length;
        }

        return units;
    }

    private static byte[] Container(IEnumerable<byte[]> packets)
    {
        var file = new List<byte>("PKTS"u8.ToArray());
        foreach (byte[] packet in packets)
        {
            file.AddRange(BitConverter.GetBytes(packet.Length));
            file.AddRange(packet);
        }

        return [.. file];
    }

    private sealed class HashingSink : IPcmSink
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        private int _bits;

        public long Samples { get; private set; }

        public void Format(PcmFormat format) => _bits = format.BitsPerSample;

        public void Write(PcmBlock block)
        {
            var buffer = new byte[CanonicalPcm.ByteCount(block, _bits)];
            CanonicalPcm.Write(block, _bits, buffer);
            _hash.AppendData(buffer);
            Samples += block.Length;
        }

        public void End(long samples)
        {
        }

        public string Hash() => Convert.ToHexStringLower(_hash.GetHashAndReset());
    }

    [Theory]
    [InlineData("external-truehd-51-48k-24.thd", "truehd", "Dolby TrueHD")]
    [InlineData("external-mlp-51-48k-24.mlp", "mlp", "MLP")]
    public void PacketsOfUnitsDecodeToTheAudioOfTheFile(string name, string codecId, string codec)
    {
        var fileSink = new HashingSink();
        Report whole = Analyzer.CreateDefault().Analyze(name, File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, "truehd", name)),
            new AnalysisOptions { Decoding = new DecodeOptions { Output = s => { fileSink.Format(s.Format); return fileSink; } } });
        var packetSink = new HashingSink();
        var analyzer = new Analyzer([], [new PacketContainer(codecId)], DefaultParsers.Codecs, DefaultParsers.Decoders);

        Report report = analyzer.Analyze("packets.bin", new MemoryStream(Container(Units(name))),
            new AnalysisOptions { Decoding = new DecodeOptions { Output = s => { packetSink.Format(s.Format); return packetSink; } } });

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        StreamInfo stream = report.Streams.Single();
        Assert.Equal(codec, stream.Codec);
        Assert.Equal(whole.Streams.Single().Samples, stream.Samples);
        Assert.Equal(whole.Streams.Single().ChannelLayout, stream.ChannelLayout);
        Assert.True(stream.Verification!.IsVerified);
        Assert.Equal(fileSink.Hash(), packetSink.Hash());
        Assert.Equal(fileSink.Samples, packetSink.Samples);
    }

    [Fact]
    public void APacketThatIsNoUnitIsSkippedWithAFinding()
    {
        List<byte[]> units = Units("external-truehd-stereo-48k-24.thd");
        units.Insert(5, new byte[3]);
        var analyzer = new Analyzer([], [new PacketContainer("truehd")], DefaultParsers.Codecs, DefaultParsers.Decoders);

        Report report = analyzer.Analyze("packets.bin", new MemoryStream(Container(units)));

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed");
        Assert.NotEmpty(report.Findings);
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    [Fact]
    public void AMajorSyncOfOtherKindThanTheCodecIdIsStillReadAsWhatItIs()
    {
        // An MLP stream handed over as "truehd": the major sync decides, as it does for a raw file.
        var analyzer = new Analyzer([], [new PacketContainer("truehd")], DefaultParsers.Codecs, DefaultParsers.Decoders);

        Report report = analyzer.Analyze("packets.bin", new MemoryStream(Container(Units("external-mlp-stereo-48k-24.mlp"))));

        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.Single(report.Streams);
    }
}
