using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// One crafted file for each finding the Monkey's Audio parser emits that no fixture's manifest lists, and the finding that must come out.
// FindingCoverageTests checks that every code the parser emits is asserted somewhere.
public class MonkeysAudioFindingTests
{
    private const string Stereo = "external-level-normal-16-stereo.ape";

    private static readonly Dictionary<string, Func<byte[]>> Crafts = new()
    {
        ["monkeys-audio.no-descriptor"] = () => new byte[1000],
        ["monkeys-audio.writer-claim-differs"] = () => [.. TestFiles.Id3v23(("TSSE", "Lavf60.16.100")), .. File.ReadAllBytes(MonkeysAudioBuilder.FixturePath(Stereo)), .. TestFiles.ApeText("Encoder", "LAME 3.100")],
        ["monkeys-audio.data-after-tag"] = () =>
        {
            // A file of the format before version 3.98 states no end, so a tag inside its data ends the stream.
            // The ID3v2.4 tag in front of it points with a SEEK frame to the second one, which is what lets the host find a tag in the middle.
            byte[] file = File.ReadAllBytes(MonkeysAudioBuilder.FixturePath("built-old-version-3900.ape"));
            const int at = 20000;
            byte[] seek = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 14, (byte)'S', (byte)'E', (byte)'E', (byte)'K', 0, 0, 0, 4, 0, 0, 0, 0, at >> 8, at & 0xFF];
            byte[] inner = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 14, (byte)'T', (byte)'I', (byte)'T', (byte)'2', 0, 0, 0, 4, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c'];
            return [.. seek, .. file[..at], .. inner, .. file[at..]];
        },
        ["monkeys-audio.frames-not-listed"] = () => MonkeysAudioBuilder.Silence((1 << 20) + 8),
    };

    // Findings that decoding raises, and the crafted file that raises each.
    private static readonly Dictionary<string, Func<byte[]>> DecodedCrafts = new()
    {
        ["monkeys-audio.frame-not-decoded"] = HugeFrame,
    };

    public static TheoryData<string> Codes => [.. Crafts.Keys.Order(StringComparer.Ordinal)];

    public static TheoryData<string> DecodedCodes => [.. DecodedCrafts.Keys.Order(StringComparer.Ordinal)];

    // One frame of 65 MiB, more than the analyzer reads into memory to decode.
    private static byte[] HugeFrame()
    {
        (byte[] header, byte[] headerData, _) = MonkeysAudioBuilder.Take(Stereo);
        return MonkeysAudioBuilder.Build(header, headerData, [new byte[65 << 20]], 4096, 4096);
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void CraftedFileRaisesTheFinding(string code)
    {
        // Bytes with no descriptor are no Monkey's Audio file to the host, so the parser is handed them.
        Analyzer analyzer = code == "monkeys-audio.no-descriptor" ? new Analyzer([new ForcedMonkeysAudio()]) : Analyzer.CreateDefault();
        Report report = analyzer.Analyze("crafted.ape", new MemoryStream(Crafts[code]()));

        Finding finding = Assert.Single(report.Findings, f => f.Code == code);
        Assert.NotNull(finding.Path);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        TestFiles.CheckInsideParents(report.Root);
    }

    [Theory]
    [MemberData(nameof(DecodedCodes))]
    public void CraftedFileRaisesTheFindingWhenDecoded(string code)
    {
        Report report = Analyzer.CreateDefault().Analyze("crafted.ape", new MemoryStream(DecodedCrafts[code]()), new AnalysisOptions { Decoding = new DecodeOptions() });

        Finding finding = Assert.Single(report.Findings, f => f.Code == code);
        Assert.NotNull(finding.Path);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    [Fact]
    public void BuiltFilesOfTheFixturesFramesDecodeAsTheFixturesDo()
    {
        // The builder's own round trip: the frames of a fixture put together again are the fixture's audio, with every CRC and the MD5 right.
        (byte[] header, byte[] headerData, List<byte[]> frames) = MonkeysAudioBuilder.Take(Stereo);
        uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        byte[] file = MonkeysAudioBuilder.Build(header, headerData, frames, blocks, blocks);

        Report report = Analyzer.CreateDefault().Analyze("round.ape", new MemoryStream(file), new AnalysisOptions { Decoding = new DecodeOptions() });

        Assert.Empty(report.Findings);
        Assert.True(report.Streams.Single().Verification!.IsVerified);
    }

    [Theory]
    [InlineData("monkeys-audio")]
    [InlineData("Monkey's Audio")]
    [InlineData("monkeysaudio")]
    [InlineData("ape")]
    [InlineData("mon")]
    public void TheFormatGoesByItsNameAndItsExtension(string name)
    {
        byte[] file = File.ReadAllBytes(MonkeysAudioBuilder.FixturePath(Stereo));

        Report report = Analyzer.CreateDefault().Analyze("named.bin", new MemoryStream(file), new AnalysisOptions { ForcedFormat = name });

        Assert.Equal("Monkey's Audio", report.Root.Value?.ToString());
        Assert.Equal("format.forced", Assert.Single(report.Findings).Code);
    }

    private sealed class ForcedMonkeysAudio : IFormatParser
    {
        private readonly Encspect.Formats.MonkeysAudio.MonkeysAudioParser _parser = new();

        public string Name => _parser.Name;

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context) => _parser.Parse(context);
    }
}
