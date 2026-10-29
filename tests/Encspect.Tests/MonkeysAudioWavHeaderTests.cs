using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The WAV header data a Monkey's Audio file stores: the fmt chunk is read for the channels and the channel mask, and a header data that is cut or malformed
// gives findings and nothing else (the frames do not depend on it, so decoding goes on). The fixtures hold the intact cases (RIFF, BW64 and Wave64 headers)
// and the one cut inside the fmt chunk; the crafted header data here covers the paths of the chunk walk the fixtures do not.
public class MonkeysAudioWavHeaderTests
{
    private const string Stereo = "external-level-normal-16-stereo.ape";

    private static byte[] Chunk(string id, int declared, int present) =>
        [.. System.Text.Encoding.ASCII.GetBytes(id), .. BitConverter.GetBytes(declared), .. new byte[present]];

    private static byte[] Riff(params byte[][] chunks) => [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, .. chunks.SelectMany(c => c)];

    private static byte[] Fmt(int channels, int tag = 1, int declared = 16, int present = 16)
    {
        byte[] body = new byte[present];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), (ushort)tag);
        if (present >= 4)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)channels);
        }

        return [.. "fmt "u8, .. BitConverter.GetBytes(declared), .. body];
    }

    public static TheoryData<string, string> Damaged => new()
    {
        { "chunk-header", "ends inside a chunk header" },
        { "chunk", "ends inside the LIST chunk" },
        { "no-format-before-data", "holds no fmt chunk before its data chunk" },
        { "no-format-before-end", "holds no fmt chunk before its end" },
        { "format-short", "fewer than the 16 of the common fields" },
        { "many-chunks", "more than 64 chunks" },
    };

    private static byte[] HeaderData(string kind) => kind switch
    {
        "chunk-header" => Riff("fmt "u8.ToArray()[..3]),
        "chunk" => Riff(Chunk("LIST", 100, 10)),
        "no-format-before-data" => Riff(Chunk("data", 1000, 8)),
        "no-format-before-end" => Riff(),
        "format-short" => Riff(Fmt(2, declared: 8, present: 8)),
        "many-chunks" => Riff([.. Enumerable.Range(0, 70).Select(_ => Chunk("JUNK", 0, 0))]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // A WAVEFORMATEXTENSIBLE fmt chunk of `size` bytes: the format tag 0xFFFE, the channels, 16 bits, the cbSize, a valid bits field and the mask (where they fit).
    private static byte[] Extensible(int channels, uint mask, int cbSize = 22, int size = 40)
    {
        byte[] body = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 44100);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(16), (ushort)cbSize);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(18), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), mask);
        return [.. "fmt "u8, .. BitConverter.GetBytes(size), .. body];
    }

    // The same chunk as Wave64 writes it: the GUID of fmt, a size that counts the 24 bytes of header, and padding to a multiple of 8 bytes.
    private static byte[] W64Chunk(byte[] guid, byte[] body, ulong? size = null) =>
        [.. guid, .. BitConverter.GetBytes(size ?? (ulong)(24 + body.Length)), .. body, .. new byte[-body.Length & 7]];

    private static byte[] W64Id(string id) => [.. System.Text.Encoding.ASCII.GetBytes(id), .. Convert.FromHexString("F3ACD3118CD100C04F8EDB8A")];

    private static byte[] Wave64(params byte[][] chunks) =>
        [.. Convert.FromHexString("726966662E91CF11A5D628DB04C10000"), .. new byte[8], .. Convert.FromHexString("77617665F3ACD3118CD100C04F8EDB8A"), .. chunks.SelectMany(c => c)];

    private static byte[] W64Fmt(int channels, uint mask) => W64Chunk(W64Id("fmt "), Extensible(channels, mask)[8..]);

    private static List<Node> Find(Node node, string name) =>
        [.. node.Children.Where(c => c.Name == name), .. node.Children.SelectMany(c => Find(c, name))];

    internal static byte[] CraftedFile(byte[] headerData, ushort extraFlags = 0)
    {
        (byte[] header, _, List<byte[]> frames) = MonkeysAudioBuilder.Take(Stereo);
        byte[] flagged = [.. header];
        BinaryPrimitives.WriteUInt16LittleEndian(flagged.AsSpan(2), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(flagged.AsSpan(2)) | extraFlags));
        return MonkeysAudioBuilder.Build(flagged, headerData, frames, MonkeysAudioBuilder.BlocksOf(Stereo, 0), MonkeysAudioBuilder.BlocksOf(Stereo, frames.Count - 1));
    }

    // For MonkeysAudioFindingTests: the codes of the findings that only these files raise.
    internal static byte[] IncompleteExtensibleFile() => CraftedFile(Riff(Extensible(2, 0x600, cbSize: 2), Chunk("data", 0, 0)));

    internal static byte[] UnusedHeaderDataFile() => CraftedFile(Riff(Extensible(2, 0x600), Chunk("data", 0, 0)), extraFlags: CreatesWavHeader);

    private const ushort CreatesWavHeader = 32;

    private static Report Analyze(byte[] headerData, bool isDecoded = false, ushort extraFlags = 0)
    {
        byte[] file = CraftedFile(headerData, extraFlags);
        return Analyzer.CreateDefault().Analyze("crafted.ape", new MemoryStream(file), isDecoded ? new AnalysisOptions { Decoding = new DecodeOptions() } : AnalysisOptions.Default);
    }

    [Theory]
    [MemberData(nameof(Damaged))]
    public void DamagedHeaderDataGivesAWarningAndTheFileIsStillRead(string kind, string text)
    {
        Report report = Analyze(HeaderData(kind), isDecoded: true);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "monkeys-audio.wav-header-damaged");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains(text, finding.Message, StringComparison.Ordinal);
        Assert.NotNull(finding.Path);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Equal(true, report.Streams.Single().Verification?.IsVerified);
        TestFiles.CheckInsideParents(report.Root);
    }

    [Fact]
    public void TheChannelsOfTheFmtChunkAreComparedWithTheHeader()
    {
        Report report = Analyze(Riff(Fmt(6), Chunk("data", 0, 0)));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "monkeys-audio.wav-channels-differ");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("states 6 channels", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Code == "monkeys-audio.wav-header-damaged");
    }

    [Fact]
    public void ANonzeroMaskSetsTheLayoutOfAStereoFile()
    {
        // Front center and low frequency in place of left and right.
        byte[] fmt = Fmt(2, tag: 0xFFFE, declared: 40, present: 40);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(8 + 16), 22);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8 + 20), 0xC);

        Report report = Analyze(Riff(fmt, Chunk("data", 0, 0)));

        Assert.Equal([Speaker.FrontCenter, Speaker.LowFrequency], report.Streams.Single().ChannelLayout);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("monkeys-audio.wav-", StringComparison.Ordinal));
    }

    [Fact]
    public void AMaskInAHeaderThatIsNotARiffWaveHeaderIsNotRead()
    {
        byte[] aiff = [.. "FORM"u8, 0, 0, 0, 0, .. "AIFF"u8, .. new byte[40]];

        Report report = Analyze(aiff);

        Assert.Equal([Speaker.FrontLeft, Speaker.FrontRight], report.Streams.Single().ChannelLayout);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("monkeys-audio.wav-", StringComparison.Ordinal));
    }

    private static readonly Speaker[] LeftRight = [Speaker.FrontLeft, Speaker.FrontRight];

    private static readonly Speaker[] Sides = [Speaker.SideLeft, Speaker.SideRight];

    // The pad byte after a chunk of odd size is skipped, or the fmt chunk after it would be looked for one byte too early.
    [Fact]
    public void AnOddSizedChunkBeforeTheFmtChunkIsSkippedWithItsPadByte()
    {
        Report report = Analyze(Riff(Chunk("LIST", 3, 4), Extensible(2, 0x600), Chunk("data", 0, 0)));

        Assert.Equal(Sides, report.Streams.Single().ChannelLayout);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("monkeys-audio.wav-", StringComparison.Ordinal));
    }

    // The ds64 chunk of RF64 and BW64 is a chunk like any other before the fmt chunk; Wave64 has GUIDs and 64-bit sizes that count the chunk's header.
    public static TheoryData<string, string> Containers => new()
    {
        { "riff", "a RIFF WAVE header" },
        { "rf64", "an RF64 WAVE header" },
        { "bw64", "a BW64 WAVE header" },
        { "wave64", "a Wave64 header" },
    };

    private static byte[] Container(string kind) => kind switch
    {
        "riff" => Riff(Extensible(2, 0x600), Chunk("data", 0, 0)),
        "rf64" => [.. "RF64"u8, .. BitConverter.GetBytes(uint.MaxValue), .. "WAVE"u8, .. Chunk("ds64", 28, 28), .. Extensible(2, 0x600), .. Chunk("data", -1, 0)],
        "bw64" => [.. "BW64"u8, .. BitConverter.GetBytes(uint.MaxValue), .. "WAVE"u8, .. Chunk("ds64", 28, 28), .. Extensible(2, 0x600), .. Chunk("data", -1, 0)],
        "wave64" => Wave64(W64Fmt(2, 0x600), W64Chunk(W64Id("data"), [])),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(Containers))]
    public void TheMaskOfEachKindOfWaveHeaderIsRead(string kind, string name)
    {
        Report report = Analyze(Container(kind));

        Assert.Equal(Sides, report.Streams.Single().ChannelLayout);
        Node data = Find(report.Root, "WAV Header Data").Single();
        Assert.EndsWith(name, data.Value?.ToString(), StringComparison.Ordinal);
        Assert.Single(Find(data, "Channel Mask"));
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("monkeys-audio.wav-", StringComparison.Ordinal));
    }

    // A Wave64 chunk of a size that is no multiple of 8 is padded to one, and a chunk of a GUID that is no four characters is skipped by its size.
    [Fact]
    public void Wave64ChunksAreAlignedToEightBytes()
    {
        byte[] other = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        Report report = Analyze(Wave64(W64Chunk(W64Id("junk"), new byte[5]), W64Chunk(other, new byte[1]), W64Fmt(2, 0x600), W64Chunk(W64Id("data"), [])));

        Assert.Equal(Sides, report.Streams.Single().ChannelLayout);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("monkeys-audio.wav-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(23UL, "under 24")]
    [InlineData(0UL, "under 24")]
    public void AWave64ChunkSmallerThanItsHeaderIsDamage(ulong size, string text)
    {
        Report report = Analyze(Wave64(W64Chunk(W64Id("junk"), new byte[8], size), W64Fmt(2, 0x600)));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "monkeys-audio.wav-header-damaged");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains(text, finding.Message, StringComparison.Ordinal);
        Assert.Equal(LeftRight, report.Streams.Single().ChannelLayout);
    }

    [Fact]
    public void AWave64ChunkBeyondTheHeaderDataIsDamage()
    {
        Report report = Analyze(Wave64(W64Chunk(W64Id("junk"), new byte[8], 1000)));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "monkeys-audio.wav-header-damaged");
        Assert.Contains("ends inside the junk chunk", finding.Message, StringComparison.Ordinal);
    }

    // Like the WAV parser's wav.extensible-incomplete, at the same severity: a cbSize under the 22 bytes of valid bits, mask and sub-format leaves no mask to read.
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(21)]
    public void AnExtensibleFormatWithACbSizeUnder22HasNoMask(int cbSize)
    {
        Report report = Analyze(Riff(Extensible(2, 0x600, cbSize), Chunk("data", 0, 0)));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "monkeys-audio.wav-extensible-incomplete");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains($"states {cbSize} byte", finding.Message, StringComparison.Ordinal);
        Assert.Equal(LeftRight, report.Streams.Single().ChannelLayout);
        Assert.Empty(Find(report.Root, "Channel Mask"));
        Assert.NotNull(finding.Path);
    }

    // The cbSize counts bytes the chunk may not hold: what is there is what counts, so a chunk of 30 bytes has 12 after its cbSize and no mask, and a chunk that
    // holds all 22 has it whatever the cbSize says.
    [Fact]
    public void ACbSizeBeyondTheChunkIsCutToWhatTheChunkHolds()
    {
        Report cut = Analyze(Riff(Extensible(2, 0x600, cbSize: 200, size: 30), Chunk("data", 0, 0)));

        Finding finding = Assert.Single(cut.Findings, f => f.Code == "monkeys-audio.wav-extensible-incomplete");
        Assert.Contains("states 200 bytes and the fmt chunk holds only 12 after it", finding.Message, StringComparison.Ordinal);
        Assert.Equal(LeftRight, cut.Streams.Single().ChannelLayout);

        Report whole = Analyze(Riff(Extensible(2, 0x600, cbSize: 200), Chunk("data", 0, 0)));

        Assert.Equal(Sides, whole.Streams.Single().ChannelLayout);
        Assert.DoesNotContain(whole.Findings, f => f.Code.StartsWith("monkeys-audio.wav-", StringComparison.Ordinal));
    }

    // SPEAKER_ALL (bit 31) is not a count of speakers, so it is no mismatch with the channels.
    [Fact]
    public void ASpeakerAllMaskIsNoMismatch()
    {
        Report report = Analyze(Riff(Extensible(2, 0x80000000), Chunk("data", 0, 0)));

        Assert.Equal([Speaker.Discrete, Speaker.Discrete], report.Streams.Single().ChannelLayout);
        Assert.DoesNotContain(report.Findings, f => f.Code == "monkeys-audio.channel-mask-mismatch");
    }

    // One speaker more than the channels is one extra high bit, which is ignored (Microsoft, "Details about dwChannelMask").
    [Fact]
    public void AMaskWithOneBitMoreThanTheChannelsIsTrimmed()
    {
        Report report = Analyze(Riff(Extensible(2, 0x7), Chunk("data", 0, 0)));

        Assert.Equal(LeftRight, report.Streams.Single().ChannelLayout);
        Finding finding = Assert.Single(report.Findings, f => f.Code == "monkeys-audio.channel-mask-mismatch");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("the extra bits, the most significant, are ignored", finding.Message, StringComparison.Ordinal);
    }

    // With the flag for a header created on decompression the SDK reads the stored bytes as nothing (APEHeader.cpp), so a mask in them sets no layout.
    [Fact]
    public void StoredHeaderDataIsUnusedWhereTheFlagCreatesTheHeader()
    {
        Report report = Analyze(Riff(Extensible(2, 0x600), Chunk("data", 0, 0)), extraFlags: CreatesWavHeader);

        Assert.Equal(LeftRight, report.Streams.Single().ChannelLayout);
        Finding finding = Assert.Single(report.Findings, f => f.Code == "monkeys-audio.wav-header-unused");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.NotNull(finding.Path);
        Assert.Single(Find(report.Root, "WAV Header Data"));
        Assert.Empty(Find(report.Root, "Channel Mask"));
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("monkeys-audio.wav-", StringComparison.Ordinal) && f.Code != "monkeys-audio.wav-header-unused");
    }
}
