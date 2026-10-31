using System.Buffers.Binary;
using System.Security.Cryptography;
using Encspect.Core;
using Encspect.Formats.WavPack;
using Encspect.Host;

namespace Encspect.Tests;

// The "wavpack" codec parser, which a container that stores a block without its header hands packets to (Matroska's A_WAVPACK4;
// the route is a later change). The packets are made from the blocks of a fixture as matroskadec.c's matroska_parse_wavpack takes them
// apart: the samples, then each block's flags and CRC and, unless the block is the only one of its set, its size, then its sub-blocks.
public class WavPackCodecParserTests
{
    private const string Magic = "PKTS";

    // A test-only container: "PKTS", the CodecPrivate (a 16-bit length and its bytes), then each packet as a 32-bit length and its bytes.
    internal sealed class PacketContainer : IFormatParser
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
            int privateLength = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(4));
            if (privateLength == 0xFFFF)
            {
                var whole = new Node("Payload", 0, all.Length);
                context.Root.Add(whole);
                context.ParseStream(WavPackCodecParser.CodecId, whole, reader);
                return;
            }

            var packets = new List<MappedBytes>();
            int at = 6 + privateLength;
            while (at + 4 <= all.Length)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(at));
                at += 4;
                packets.Add(new MappedBytes(all.AsMemory(at, length), at));
                at += length;
            }

            var node = new Node("Packets", 0, all.Length);
            context.Root.Add(node);
            CodecConfiguration? configuration = privateLength > 0 ? new CodecConfiguration(new MappedBytes(all.AsMemory(6, privateLength), 6), node) : null;
            context.ParsePackets(WavPackCodecParser.CodecId, node, packets, configuration);
        }
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

    // The packets of a file: one for each set of blocks (from the initial block to the final), without the 32-byte headers.
    internal static List<byte[]> Packets(string name, bool withChecksum = true) => Packets(WavPackBuilder.Blocks(name));

    internal static List<byte[]> Packets(IEnumerable<byte[]> blocks)
    {
        var packets = new List<byte[]>();
        var current = new List<byte[]>();
        foreach (byte[] block in blocks)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(24));
            uint samples = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(20));
            if (samples == 0)
            {
                continue;
            }

            current.Add(block);
            if ((flags & 0x1000) != 0)
            {
                packets.Add(Packet(current));
                current.Clear();
            }
        }

        return packets;
    }

    internal static byte[] Packet(IReadOnlyList<byte[]> set)
    {
        var packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(BinaryPrimitives.ReadUInt32LittleEndian(set[0].AsSpan(20))));
        foreach (byte[] block in set)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(24));
            packet.AddRange(block.AsSpan(24, 8).ToArray());
            if ((flags & 0x1800) != 0x1800)
            {
                packet.AddRange(BitConverter.GetBytes(block.Length - 32));
            }

            packet.AddRange(block.AsSpan(32).ToArray());
        }

        return [.. packet];
    }

    internal static byte[] Container(IEnumerable<byte[]> packets, ushort? version = 0x410)
    {
        var file = new List<byte>("PKTS"u8.ToArray());
        byte[] version2 = version is { } v ? BitConverter.GetBytes(v) : [];
        file.AddRange(BitConverter.GetBytes((ushort)version2.Length));
        file.AddRange(version2);
        foreach (byte[] packet in packets)
        {
            file.AddRange(BitConverter.GetBytes(packet.Length));
            file.AddRange(packet);
        }

        return [.. file];
    }

    private static Analyzer Analyzer() => new([], [new PacketContainer()], [new WavPackCodecParser()], DefaultParsers.Decoders);

    private static (Report Report, HashingSink Sink) Decode(byte[] file)
    {
        var sink = new HashingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = stream => { sink.Format(stream.Format); return sink; } } };
        Report report = Analyzer().Analyze("packets.bin", new MemoryStream(file), options);
        return (report, sink);
    }

    private static (string Md5, long Samples) DecodeFile(string name)
    {
        var sink = new HashingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = stream => { sink.Format(stream.Format); return sink; } } };
        Report report = Encspect.Host.Analyzer.CreateDefault().Analyze(name, File.OpenRead(WavPackBuilder.FixturePath(name)), options);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        return (sink.Hash(), sink.Samples);
    }

    public static TheoryData<string> Files =>
    [
        "external-lossless-16-stereo.wv", "external-lossless-16-mono.wv", "external-lossless-24-stereo.wv", "external-lossless-32-stereo.wv",
        "external-lossless-float-stereo.wv", "external-lossless-8-stereo.wv", "external-lossless-51-16.wv", "external-hybrid-b4-correction.wv",
        "external-hybrid-32-b12.wv", "external-lossless-16-stereo-small-blocks.wv", "external-hybrid-false-stereo-b4.wv",
    ];

    [Theory]
    [MemberData(nameof(Files))]
    public void PacketsDecodeToTheSamplesOfTheFile(string name)
    {
        (string md5, long samples) = DecodeFile(name);
        (Report report, HashingSink sink) = Decode(Container(Packets(name)));

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed" or "decode.frame-invalid" or "wavpack.audio-crc-mismatch");
        Assert.Equal(md5, sink.Hash());
        Assert.Equal(samples, sink.Samples);
        StreamInfo stream = report.Streams.Single();
        StreamVerification verification = stream.Verification!;
        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.Equal(samples, verification.DecodedSamples);
        ChecksumTally crc = verification.Checksums.Single(c => c.Name == "block header CRC");
        Assert.Equal(crc.Stored, crc.Checked);
        Assert.Equal(0, crc.Failed);
        Assert.True(crc.Stored > 0);
        Assert.Equal(verification.IsLossy, name.Contains("hybrid", StringComparison.Ordinal));
        Assert.True(verification.IsVerified);
        Assert.Equal(samples, stream.Samples);
        TestFiles.CheckInsideParents(report.Root);
    }

    [Fact]
    public void ThePacketsAreListedWithTheirBlocks()
    {
        (Report report, _) = Decode(Container(Packets("external-lossless-16-stereo.wv")));
        Node packets = report.Root.Children.Single();
        Assert.Equal(["Packet", "Packet", "Packet", "Statistics"], packets.Children.Select(c => c.Name));
        Node block = packets.Children[0].Children.Single(c => c.Name == "Block");
        Assert.Equal(["Flags", "CRC", "Sub-blocks"], block.Children.Select(c => c.Name));
        Assert.Equal(11025, packets.Children[0].Children.Single(c => c.Name == "Samples").Value?.AsInteger());
        Assert.Equal(11025 + 6487 + 6488, packets.Children.Single(c => c.Name == "Statistics").Children.Single(c => c.Name == "Samples").Value?.AsInteger());
    }

    [Fact]
    public void ADamagedBlockFailsItsCrc()
    {
        List<byte[]> packets = Packets("external-lossless-16-stereo.wv");
        packets[1][200] ^= 0x10;
        (Report report, HashingSink sink) = Decode(Container(packets));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "wavpack.audio-crc-mismatch");
        Assert.Equal("CRC", finding.Node!.Name);
        Assert.NotNull(finding.Path);
        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.False(verification.IsVerified);
        Assert.Equal(1, verification.Checksums.Single(c => c.Name == "block header CRC").Failed);
        Assert.Equal(24000, sink.Samples);
    }

    [Fact]
    public void WithoutACodecPrivateTheVersionIsTheOneFFmpegAssumes()
    {
        (Report report, HashingSink sink) = Decode(Container(Packets("external-lossless-16-stereo.wv"), version: null));
        Assert.Equal(DecodeStatus.Decoded, report.Streams.Single().Verification!.Decoding);
        Assert.Equal(24000, sink.Samples);
        Node statistics = report.Root.Children.Single().Children.Single(c => c.Name == "Statistics");
        Assert.Equal(0x410, statistics.Children.Single(c => c.Name == "Stream Version").Value?.AsInteger());
    }

    [Fact]
    public void AVersionLibwavpackDoesNotDecodeIsNotDecoded()
    {
        (Report report, HashingSink sink) = Decode(Container(Packets("external-lossless-16-stereo.wv"), version: 0x411));
        Assert.Equal(DecodeStatus.NotPossible, report.Streams.Single().Verification!.Decoding);
        Assert.Contains(report.Findings, f => f.Code == "decode.not-possible");
        Assert.Equal(0, sink.Samples);
    }

    [Fact]
    public void ADefaultAnalysisComparesNothingOverTheAudio()
    {
        Report report = Analyzer().Analyze("packets.bin", new MemoryStream(Container(Packets("external-lossless-16-stereo.wv"))));
        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Equal(DecodeStatus.NotRequested, verification.Decoding);
        Assert.Null(verification.IsVerified);
        Assert.All(verification.Checksums, c => Assert.Equal(0, c.Checked));
    }

    [Fact]
    public void APacketThatTheBlockSizesDoNotFitIsReported()
    {
        List<byte[]> packets = Packets("external-lossless-16-stereo.wv");
        packets.Add([1, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        packets.Add(Packets("external-lossless-16-mono.wv")[0][..6]);
        packets[0] = packets[0][..^3];
        (Report report, _) = Decode(Container(packets));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "wavpack.matroska-packet-invalid");
        Assert.NotNull(finding.Path);
        Assert.True(finding.Count >= 2);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        Assert.False(report.Streams.Single().Verification!.Checksums.Single(c => c.Name == "block header CRC").CoversAllAudio);
    }

    [Fact]
    public void ABlockWhoseHeaderLibwavpackWouldNotTakeIsReported()
    {
        List<byte[]> packets = Packets("external-lossless-16-mono.wv");

        // An odd size: the block's data is one byte longer.
        byte[] odd = [.. packets[0], 0];
        (Report report, _) = Decode(Container([odd, .. packets.Skip(1)]));
        Assert.Contains(report.Findings, f => f.Code == "wavpack.matroska-block-invalid" && f.Message.Contains("odd", StringComparison.Ordinal));

        // More samples than a block holds.
        byte[] many = [.. packets[1]];
        BinaryPrimitives.WriteUInt32LittleEndian(many, 0x30000);
        (report, _) = Decode(Container([packets[0], many]));
        Assert.Contains(report.Findings, f => f.Code == "wavpack.matroska-block-invalid");
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
    }

    // A block the parser cannot take is handed on as unreadable, so the host counts it as not decoded: the samples it would have given are
    // missing from the decoding, which goes on with the packets after it, and the stream is not verified.
    [Fact]
    public void ABlockThatCannotBeRebuiltIsNotDecodedAndTheStreamGoesOn()
    {
        List<byte[]> packets = Packets("external-lossless-16-mono.wv");
        (Report whole, _) = Decode(Container(packets));
        long total = whole.Streams.Single().Verification!.DecodedSamples;

        byte[] odd = [.. packets[1], 0];
        (Report report, HashingSink sink) = Decode(Container([packets[0], odd, .. packets.Skip(2)]));
        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Contains(report.Findings, f => f.Code == "wavpack.matroska-block-invalid");
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        Assert.Equal(total - BinaryPrimitives.ReadUInt32LittleEndian(packets[1]), verification.DecodedSamples);
        Assert.Equal(verification.DecodedSamples, sink.Samples);
        Assert.NotEqual(true, verification.IsVerified);
    }

    [Theory]
    [InlineData("built-md5-digest-changed.wv", "wavpack.md5-mismatch", 0)]
    [InlineData("built-extra-crc-changed.wv", "wavpack.audio-crc-mismatch", 0)]
    [InlineData("built-md5-digest-changed.wv", "wavpack.md5-mismatch", 70)]
    [InlineData("built-extra-crc-changed.wv", "wavpack.audio-crc-mismatch", 70)]
    public void TheFindingsOfTheDecoderPointAtANodeOfThePacketsTree(string name, string code, int unlistedBefore)
    {
        List<byte[]> blocks = WavPackBuilder.Blocks(name);

        // Matroska carries no block without samples, so the MD5 sub-block of the last block goes into the last block with audio.
        byte[] md5 = WavPackBuilder.Parts(blocks[^1]).SingleOrDefault(p => WavPackBuilder.Function(p) == 0x26) ?? [];
        if (md5.Length > 0)
        {
            byte[] audio = blocks[^2];
            List<byte[]> parts = WavPackBuilder.Parts(audio);
            parts.Insert(parts.Count - 1, md5);
            blocks[^2] = WavPackBuilder.Join(audio, parts);
        }

        // Optional sub-blocks libwavpack skips, ahead of the ones the findings point at, past what the parser lists.
        for (int i = 0; i < blocks.Count && unlistedBefore > 0; i++)
        {
            List<byte[]> parts = WavPackBuilder.Parts(blocks[i]);
            int at = parts.FindIndex(p => WavPackBuilder.Function(p) is 0x26 or 0x0C);
            if (at >= 0)
            {
                parts.InsertRange(at, Enumerable.Repeat(WavPackBuilder.Sub(0x3E, new byte[2]), unlistedBefore));
                blocks[i] = WavPackBuilder.Join(blocks[i], parts);
            }
        }

        (Report report, _) = Decode(Container(Packets(blocks)));
        Finding finding = Assert.Single(report.Findings, f => f.Code == code);
        Assert.NotNull(finding.Path);
        TestFiles.CheckInsideParents(report.Root);
    }

    [Fact]
    public void ABlockOfAnotherMajorVersionIsRefusedForThatReason()
    {
        (Report report, _) = Decode(Container(Packets("external-lossless-16-mono.wv"), version: 0x510));
        Finding finding = report.Findings.First(f => f.Code == "wavpack.matroska-block-invalid");
        Assert.Contains("major version 5", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("196,607", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnythingElseThanPacketsIsNotAnalyzed()
    {
        byte[] file = [.. "PKTS"u8, 0xFF, 0xFF, .. new byte[100]];
        Report report = Analyzer().Analyze("payload.bin", new MemoryStream(file));
        Finding finding = Assert.Single(report.Findings, f => f.Code == "wavpack.payload-not-analyzed");
        Assert.NotNull(finding.Path);
        Assert.Empty(report.Streams);
    }
}
