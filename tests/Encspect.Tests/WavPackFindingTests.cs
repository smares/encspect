using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// One damaged or unusual file for each finding the WavPack parser emits that no fixture's manifest
// lists, built from an encoder-written fixture with its blocks changed, and the finding that must come
// out. FindingCoverageTests checks that every code the parser emits is asserted somewhere.
public class WavPackFindingTests
{
    private static readonly Dictionary<string, Func<byte[]>> Crafts = new()
    {
        ["wavpack.subblock-size"] = () =>
        {
            List<byte[]> blocks = WavPackBuilder.Blocks("external-lossless-16-stereo.wv");
            blocks[1] = WavPackBuilder.Replace(blocks[1], 0x05, WavPackBuilder.Sub(0x05, new byte[8]));
            return WavPackBuilder.Concat(blocks);
        },
        ["wavpack.dsd-flag-mismatch"] = () =>
        {
            List<byte[]> blocks = WavPackBuilder.Blocks("external-lossless-16-stereo.wv");
            blocks[1] = WavPackBuilder.SetFlags(blocks[1], 0x80000000);
            return WavPackBuilder.Concat(blocks);
        },
        ["wavpack.format-changes"] = () =>
        {
            List<byte[]> blocks = WavPackBuilder.Blocks("external-lossless-16-stereo.wv");
            blocks[1] = WavPackBuilder.SetFlags(blocks[1], 10u << 23, 0xFu << 23);
            return WavPackBuilder.Concat(blocks);
        },
        ["wavpack.mixed-streams"] = () => WavPackBuilder.Concat([.. WavPackBuilder.Blocks("external-hybrid-b4-correction.wv"), .. WavPackBuilder.Blocks("external-hybrid-b4-correction-wvc.wvc")]),
        ["wavpack.no-blocks"] = () => new byte[1000],
        ["wavpack.encoder-claim-differs"] = () => [.. TestFiles.Id3v23(("TSSE", "LAME 3.100")), .. File.ReadAllBytes(WavPackBuilder.FixturePath("external-lossless-16-stereo.wv"))],
    };

    public static TheoryData<string> Codes => [.. Crafts.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Codes))]
    public void CraftedFileRaisesTheFinding(string code)
    {
        // Bytes with no block in them are no WavPack file to the host, so the parser is handed them.
        Analyzer analyzer = code == "wavpack.no-blocks" ? new Analyzer([new ForcedWavPack()]) : Analyzer.CreateDefault();
        Report report = analyzer.Analyze("crafted.wv", new MemoryStream(Crafts[code]()));

        Finding finding = Assert.Single(report.Findings, f => f.Code == code);
        Assert.NotNull(finding.Path);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        TestFiles.CheckInsideParents(report.Root);
    }

    [Fact]
    public void ABlockOfTheWrongSizeFindsTheSizeOfEveryKind()
    {
        // The sub-blocks libwavpack reads at a fixed size or a few: each is told apart by what it holds.
        (int Function, byte[] Data)[] wrong =
        [
            (0x05, new byte[4]), (0x06, new byte[3]), (0x07, new byte[3]), (0x08, new byte[3]), (0x09, new byte[5]), (0x0A, []), (0x0C, new byte[4]),
            (0x26, new byte[8]), (0x27, new byte[5]),
        ];
        foreach ((int function, byte[] data) in wrong)
        {
            List<byte[]> blocks = WavPackBuilder.Blocks("external-lossless-16-stereo.wv");
            List<byte[]> parts = WavPackBuilder.Parts(blocks[1]);
            parts.Insert(parts.Count - 1, WavPackBuilder.Sub(function, data));
            blocks[1] = WavPackBuilder.Join(blocks[1], parts);
            Report report = Analyzer.CreateDefault().Analyze("crafted.wv", new MemoryStream(WavPackBuilder.Concat(blocks)));
            Assert.True(report.Findings.Any(f => f.Code == "wavpack.subblock-size"), $"function 0x{function:X2}");
        }
    }

    // A block of a correction file may hold the extra bits alone; in a main file it cannot be decoded.
    [Fact]
    public void ExtraBitsAloneAreAFaultOnlyOutsideACorrectionFile()
    {
        List<byte[]> blocks = WavPackBuilder.Blocks("external-lossless-32-stereo.wv");
        blocks[0] = WavPackBuilder.Replace(blocks[0], 0x0A, null);
        Report report = Analyzer.CreateDefault().Analyze("extra.wv", new MemoryStream(WavPackBuilder.Concat(blocks)));
        Assert.Contains(report.Findings, f => f.Code == "wavpack.audio-without-bitstream" && f.Message.Contains("only extra bits", StringComparison.Ordinal));
        Assert.Equal("WavPack lossless", report.Streams.Single().Codec);
    }

    [Fact]
    public void ACorrectionFilesBlocksNeedNoMainBitstream()
    {
        Report report = Analyzer.CreateDefault().Analyze("correction.wvc", new MemoryStream(File.ReadAllBytes(WavPackBuilder.FixturePath("external-hybrid-32-b12-correction-wvc.wvc"))));
        Assert.DoesNotContain(report.Findings, f => f.Code == "wavpack.audio-without-bitstream");
    }

    private sealed class ForcedWavPack : IFormatParser
    {
        private readonly Encspect.Formats.WavPack.WavPackParser _parser = new();

        public string Name => _parser.Name;

        public int Sniff(SniffContext context) => 100;

        public void Parse(ParseContext context) => _parser.Parse(context);
    }
}
