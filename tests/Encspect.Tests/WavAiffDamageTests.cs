using System.Buffers.Binary;
using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Damaged and unusual WAVE and AIFF files that the committed fixtures do not hold, built here: chunks of the name data in other lists, size fields
// that disagree with the ds64 chunk, bursts out of their period, and fields at the edge of their ranges.
public class WavAiffDamageTests
{
    private static Report Analyze(byte[] data, string name = "damage.wav") => Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));

    private static Finding? Find(Report report, string code) => report.Findings.FirstOrDefault(f => f.Code == code);

    private static byte[] U32(uint value, bool isBigEndian = false)
    {
        byte[] bytes = new byte[4];
        if (isBigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }

        return bytes;
    }

    private static byte[] Chunk(string id, byte[] body, bool isBigEndian = false) =>
        [.. Encoding.ASCII.GetBytes(id), .. U32((uint)body.Length, isBigEndian), .. body, .. (body.Length % 2 == 1 ? new byte[] { 0 } : [])];

    private static byte[] Fmt(int tag = 1, int channels = 2, uint rate = 48000, int bits = 16, int? blockAlign = null, uint? average = null, byte[]? extra = null)
    {
        int align = blockAlign ?? channels * ((bits + 7) / 8);
        byte[] body =
        [
            (byte)tag, (byte)(tag >> 8), (byte)channels, (byte)(channels >> 8), .. U32(rate), .. U32(average ?? (uint)(rate * (uint)align)),
            (byte)align, (byte)(align >> 8), (byte)bits, (byte)(bits >> 8),
            .. (extra is null ? [] : (byte[])[(byte)extra.Length, (byte)(extra.Length >> 8), .. extra]),
        ];
        return Chunk("fmt ", body);
    }

    private static byte[] Riff(string magic, uint size, params byte[][] chunks) => [.. Encoding.ASCII.GetBytes(magic), .. U32(size), .. "WAVE"u8, .. chunks.SelectMany(c => c)];

    private static byte[] Riff(params byte[][] chunks) => Riff("RIFF", (uint)(4 + chunks.Sum(c => c.Length)), chunks);

    private static byte[] Ds64(ulong riff, ulong data, uint count = 0, byte[]? table = null) =>
        Chunk("ds64", [.. BitConverter.GetBytes(riff), .. BitConverter.GetBytes(data), .. new byte[8], .. U32(count), .. (table ?? [])]);

    private static byte[] Pcm() => new byte[1024];

    [Fact]
    public void ADataChunkInsideAnInfoListIsNotTheAudio()
    {
        byte[] info = [.. "INFO"u8, .. Chunk("data", [1, 2, 3, 4]), .. Chunk("INAM", [(byte)'x', 0])];
        Report report = Analyze(Riff(Fmt(), Chunk("LIST", info), Chunk("data", Pcm())));
        Node inner = Fixtures.Resolve(report.Root, "RIFF/List Chunk").Single().Children.First(n => n.Children.Any(c => c.Name == "Chunk ID" && c.Value?.ToString() == "data"));
        Assert.NotEqual(NodeKind.Stream, inner.Kind);
        Assert.Equal(NodeKind.Stream, Fixtures.Resolve(report.Root, "RIFF/Data Chunk").Single().Kind);
        Assert.Null(Find(report, "wav.data-size-placeholder"));
    }

    [Fact]
    public void AnEmptyDataChunkInAnAdtlListIsNotAPlaceholder()
    {
        byte[] adtl = [.. "adtl"u8, .. "data"u8, .. U32(0), 5, 6, 7, 8];
        Report report = Analyze(Riff(Fmt(), Chunk("LIST", adtl), Chunk("data", Pcm())));
        Assert.Null(Find(report, "wav.data-size-placeholder"));
        Assert.Equal(NodeKind.Stream, Fixtures.Resolve(report.Root, "RIFF/Data Chunk").Single().Kind);
    }

    [Fact]
    public void ASizeOfZeroInAnRf64FileNamesWhatIsWrong()
    {
        Finding finding = Find(Analyze(Riff("RF64", 0, Ds64(0, 0), Fmt(), Chunk("data", Pcm()))), "wav.riff-size-placeholder")!;
        Assert.DoesNotContain("does not give a size", finding.Message);
        Assert.Contains("is 0", finding.Message);
        Assert.Contains("0xFFFFFFFF stands for the size in the ds64 chunk", finding.Message);
    }

    [Fact]
    public void AMisplacedDs64ChunkIsNamedAndItsSizeCompared()
    {
        Finding finding = Find(Analyze(Riff("RF64", uint.MaxValue, Fmt(), Ds64(1234, 1024), Chunk("data", Pcm()))), "wav.riff-size-placeholder")!;
        Assert.Contains("does not come first", finding.Message);
        Assert.Contains("1234", finding.Message);
        Assert.Contains("not the", finding.Message);
    }

    [Fact]
    public void AMissingDs64ChunkIsNamed()
    {
        Finding finding = Find(Analyze(Riff("BW64", uint.MaxValue, Fmt(), Chunk("data", Pcm()))), "wav.riff-size-placeholder")!;
        Assert.Contains("no ds64 chunk", finding.Message);
    }

    [Fact]
    public void ACompressionNameCutByTheChunkKeepsTheStream()
    {
        byte[] comm = [0, 2, 0, 0, 0, 100, 0, 16, 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0, .. "sowt"u8, 40, .. "abc"u8];
        byte[] body = [.. "AIFC"u8, .. Chunk("FVER", U32(0xA2805140, true), true), .. Chunk("COMM", comm, true), .. Chunk("SSND", [.. new byte[8], .. new byte[400]], true)];
        byte[] file = [.. "FORM"u8, .. U32((uint)body.Length, true), .. body];
        Report report = Analyze(file, "name.aifc");
        Assert.NotNull(Find(report, "aiff.chunk-too-short"));
        Assert.Single(report.Streams);
        Assert.Null(Find(report, "aiff.no-common-chunk"));
    }

    [Fact]
    public void ASampleRateBeyondADoubleIsNamedSo()
    {
        byte[] comm = [0, 2, 0, 0, 0, 100, 0, 16, 0x7F, 0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0];
        byte[] body = [.. "AIFF"u8, .. Chunk("COMM", comm, true), .. Chunk("SSND", [.. new byte[8], .. new byte[400]], true)];
        Report report = Analyze([.. "FORM"u8, .. U32((uint)body.Length, true), .. body], "rate.aiff");
        Assert.Equal("too large for a double (not a possible sample rate)", Fixtures.Resolve(report.Root, "FORM/Common Chunk/Sample Rate").Single().Value?.ToString());
        Assert.NotNull(Find(report, "aiff.sample-rate-invalid"));
    }

    [Fact]
    public void TheExpectedAverageRateDoesNotOverflow()
    {
        Report report = Analyze(Riff(Fmt(channels: 65535, rate: uint.MaxValue, bits: 65528, blockAlign: 0, average: 0), Chunk("data", new byte[16])));
        Finding finding = Find(report, "wav.average-bytes-mismatch")!;
        Assert.DoesNotContain("give -", finding.Message);
    }

    [Fact]
    public void TheSpeakerAllBitIsLeftOutOfTheMaskCount()
    {
        byte[] ext = [16, 0, 7, 0, 0, 0x80, .. U32(1), 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71];
        Report report = Analyze(Riff(Fmt(tag: 0xFFFE, extra: ext), Chunk("data", new byte[64])));
        Finding finding = Find(report, "wav.channel-mask-mismatch")!;
        Assert.Contains("3 speakers", finding.Message);
    }

    [Fact]
    public void ALongDs64TableIsSkippedPastTheListedEntries()
    {
        byte[] table = [.. Enumerable.Range(0, 3000).SelectMany(_ => (byte[])[.. "abcd"u8, .. new byte[8]])];
        byte[] data = Chunk("data", Pcm());
        byte[] ds64 = Ds64(0, 1024, 3000, table);
        ulong riff = (ulong)(4 + ds64.Length + Fmt().Length + data.Length);
        Report report = Analyze(Riff("RF64", uint.MaxValue, Ds64(riff, 1024, 3000, table), Fmt(), data));
        Node chunk = Fixtures.Resolve(report.Root, "RF64/Data Size 64 Chunk").Single();
        Assert.Equal(1024, chunk.Children.Single(c => c.Name == "Table").Children.Count);
        Assert.DoesNotContain(chunk.Children, c => c.Name == "Extra Data");
    }

    private static byte[] Preamble(int dataType, int lengthBits) => [0x72, 0xF8, 0x1F, 0x4E, (byte)dataType, 0, (byte)lengthBits, (byte)(lengthBits >> 8)];

    private static byte[] Bursts(int dataType, int period, int payload, int count, int? extraAt = null, int? lastBurstOnly = null)
    {
        int bytes = period * 4;
        byte[] buffer = new byte[bytes * count];
        for (int k = 0; k < count; k++)
        {
            Preamble(dataType, payload * 8).CopyTo(buffer, k * bytes);
            for (int i = 0; i < payload; i++)
            {
                buffer[(k * bytes) + 8 + i] = (byte)(i == 0 ? 0x0B : i == 1 ? 0x77 : 0);
            }
        }

        if (extraAt is { } at)
        {
            Preamble(dataType, payload * 8).CopyTo(buffer, at);
        }

        return lastBurstOnly is { } keep ? buffer[..keep] : buffer;
    }

    [Fact]
    public void ABurstInsideTheRepetitionPeriodIsReportedAsEarly()
    {
        int period = 1536 * 4;
        Report report = Analyze(Riff(Fmt(), Chunk("data", Bursts(1, 1536, 100, 5, period + (period / 2)))));
        Assert.NotNull(Find(report, "wav.iec61937-burst-spacing"));
    }

    [Fact]
    public void AnOddPayloadThatTheDataEndsInIsCut()
    {
        int period = 1536 * 4;
        byte[] data = Bursts(1, 1536, 101, 4, null, (3 * period) + 8 + 101);
        Report report = Analyze(Riff(Fmt(), Chunk("data", data)));
        Assert.NotNull(Find(report, "wav.iec61937-length-invalid"));
    }

    [Fact]
    public void ABurstStreamWithNoParserIsAStreamNode()
    {
        Report report = Analyze(Riff(Fmt(), Chunk("data", Bursts(5, 1152, 200, 4))));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(NodeKind.Stream, stream.Nodes[0].Kind);
    }

    // The search for an early burst asks the window for no more than it needs: a read per burst, as a refill of the window at every burst makes, would
    // be 2,000 or more.
    [Fact]
    public void ReadsOfTheSearchForEarlyBurstsFollowTheWindow()
    {
        byte[] file = Riff(Fmt(), Chunk("data", Bursts(1, 1536, 100, 2000)));
        var stream = new CountingStream(file);
        Report report = Analyzer.CreateDefault().Analyze("bursts.wav", stream);
        Assert.Null(Find(report, "wav.iec61937-burst-spacing"));
        Assert.True(stream.Reads < 1400, $"{stream.Reads} reads for {file.Length} bytes");
    }

    // A search that stops at the limit steps over each frame of the data about once, whatever the number of bursts; one that runs on past it (which asks
    // the window for nothing, so no read shows it) steps over the rest of the data for every burst.
    [Theory]
    [InlineData(300)]
    [InlineData(1500)]
    public void TheSearchForEarlyBurstsStopsAtTheNextPeriod(int bursts)
    {
        byte[] file = Riff(Fmt(), Chunk("data", Bursts(1, 1536, 100, bursts)));
        Encspect.Formats.Wav.BurstRun.FramesScanned = 0;
        Report report = Analyzer.CreateDefault().Analyze("bursts.wav", new MemoryStream(file));
        long scanned = Encspect.Formats.Wav.BurstRun.FramesScanned;
        Assert.Null(Find(report, "wav.iec61937-burst-spacing"));
        Assert.InRange(scanned, 1, 2L * bursts * 1536);
    }

    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            return base.Read(buffer);
        }
    }
}
