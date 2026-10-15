using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Matroska;
using Encspect.Host;

namespace Encspect.Tests;

// One crafted file for each finding the Matroska parser emits that no fixture's manifest lists, and the finding that must come
// out. FindingCoverageTests checks that every code the parser emits is asserted somewhere.
public class MatroskaFindingTests
{
    private static readonly Dictionary<string, Func<byte[]>> Crafts = new()
    {
        // A block of 70 MiB, past the 64 MiB the parser reads a block up to.
        ["matroska.block-too-large"] = () => MatroskaBuilder.File(MatroskaBuilder.OpusTracks(),
            MatroskaBuilder.Cluster(0, MatroskaBuilder.SimpleBlock(1, 0, new byte[70 << 20]))),
        // More elements than the node budget, as Void elements.
        ["matroska.too-many-elements"] = () => MatroskaBuilder.File([MatroskaBuilder.OpusTracks(), .. Enumerable.Repeat(MatroskaBuilder.Void(0), 260_000)]),
        // A DocType that is neither matroska nor webm, which only a parser handed the file reads.
        ["matroska.doctype-unknown"] = () => MatroskaBuilder.Concat(MatroskaBuilder.Header("mkvfile"), MatroskaBuilder.Segment(MatroskaBuilder.OpusTracks())),
        // A sampling frequency of 44100 Hz for AC-3 frames coded at 48000 Hz.
        ["matroska.sample-rate-differs"] = () =>
        {
            byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "matroska", "external-mkvmerge-ac3.mka"));
            // mkvmerge writes the SamplingFrequency as a 4-byte float.
            byte[] from = [0xB5, 0x84, 0, 0, 0, 0];
            BinaryPrimitives.WriteSingleBigEndian(from.AsSpan(2), 48000f);
            byte[] to = (byte[])from.Clone();
            BinaryPrimitives.WriteSingleBigEndian(to.AsSpan(2), 44100f);
            int at = data.AsSpan().IndexOf(from);
            Assert.True(at > 0, "the fixture has no SamplingFrequency of 48000 Hz");
            to.CopyTo(data, at);
            return data;
        },
    };

    public static TheoryData<string> Codes => [.. Crafts.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Codes))]
    public void CraftedFileRaisesTheFinding(string code)
    {
        Analyzer analyzer = code == "matroska.doctype-unknown" ? new Analyzer([new ForcedMatroska()]) : Analyzer.CreateDefault();
        Report report = analyzer.Analyze("crafted.mka", new MemoryStream(Crafts[code]()));

        Finding finding = Assert.Single(report.Findings, f => f.Code == code);
        Assert.NotNull(finding.Path);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        TestFiles.CheckInsideParents(report.Root);
    }

    private sealed class ForcedMatroska : IFormatParser
    {
        private readonly MatroskaParser _parser = new();

        public string Name => _parser.Name;

        public SniffResult Sniff(SniffContext context) => new(100);

        public long? DeclaredEnd(SniffContext context) => _parser.DeclaredEnd(context);

        public void Parse(ParseContext context) => _parser.Parse(context);
    }
}
