using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// What the parser does with the boxes past its node budget (Mp4Parser.MaxNodes): they are read and not shown, and everything the report says about them
// still points to a node of the tree.
public class Mp4NodeLimitTests
{
    public static TheoryData<string> Names => Fixtures.Mp4Fixtures();

    // Every fixture that starts with ftyp, with 90,000 free boxes inserted after it: all its other boxes lie past the budget, so its codec findings, tag
    // and claims come from boxes in no tree. The offsets in the file's tables then point to the wrong bytes, which only adds findings.
    [Theory]
    [MemberData(nameof(Names))]
    public void FindingsOfBoxesNotShownHavePaths(string fileName)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fileName));
        if (data.Length < 12 || !"ftyp"u8.SequenceEqual(data.AsSpan(4, 4)))
        {
            return;
        }

        int ftyp = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        byte[] padded = Mp4Builder.Join(data[..ftyp], Mp4Builder.Join([.. Enumerable.Repeat(Mp4Builder.Box("free"), 90_000)]), data[ftyp..]);

        Report report = Analyzer.CreateDefault().Analyze(fileName, new MemoryStream(padded));

        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.True(f.Path is not null, $"{f.Code}: {f.Message}"));
    }

    // A moov box whose last trak holds the boxes past the budget: its own children are all shown, so a missing or a repeated mvhd box is still found.
    [Theory]
    [InlineData(false, "mp4.missing-mvhd")]
    [InlineData(true, "mp4.duplicate-box")]
    public void ChecksOfShownChildrenStillRunWhenALaterChildHoldsHiddenBoxes(bool isRepeated, string code)
    {
        byte[] trak = Mp4Builder.Trak(1, Mp4Builder.AlacEntry(), Mp4Builder.EmptyTables());
        byte[] padded = Mp4Builder.Box("trak", trak[8..], Mp4Builder.Join([.. Enumerable.Repeat(Mp4Builder.Box("free"), 90_000)]));
        byte[] moov = Mp4Builder.Box("moov", isRepeated ? Mp4Builder.Join(Mp4Builder.Mvhd(), Mp4Builder.Mvhd(), padded) : padded);

        Report report = Analyzer.CreateDefault().Analyze("hidden.m4a", new MemoryStream(Mp4Builder.Join(Mp4Builder.Ftyp(), moov)));

        Assert.Contains(report.Findings, f => f.Code == "mp4.node-limit");
        Assert.Contains(report.Findings, f => f.Code == code);
        Assert.DoesNotContain(report.Findings, f => f.Code is "mp4.missing-tkhd" or "mp4.missing-mdia");
    }

    // A fragmented file cut near its end: the finding about its last box points to the "Boxes Not Shown" node of the file, where the box was hidden, and not to
    // the first such node, which lies in a track fragment in the middle of the file.
    [Fact]
    public void FindingsPointToTheNodeOfTheParentWhereTheBoxWasHidden()
    {
        byte[] unit = Mp4Builder.AacUnit();
        byte[] moov = Mp4Builder.Box("moov", Mp4Builder.Mvhd(), Mp4Builder.Trak(1, Mp4Builder.AacEntry(), Mp4Builder.EmptyTables()),
            Mp4Builder.Box("mvex", Mp4Builder.FullBox("trex", Mp4Builder.U32(1), Mp4Builder.U32(1), Mp4Builder.U32(1024), Mp4Builder.U32(unit.Length), Mp4Builder.U32(0))));
        byte[] Moof(int sequence, long dataOffset) => Mp4Builder.Box("moof", Mp4Builder.FullBox("mfhd", Mp4Builder.U32(sequence)),
            Mp4Builder.Box("traf", Mp4Builder.Box("tfhd", [0, 2, 0, 0], Mp4Builder.U32(1)), Mp4Builder.FullBox("tfdt", Mp4Builder.U32(sequence * 1024L)),
                Mp4Builder.Box("trun", [0, 0, 2, 1], Mp4Builder.U32(1), Mp4Builder.U32(dataOffset), Mp4Builder.U32(unit.Length))));
        long moofLength = Moof(0, 0).Length;
        byte[] data = Mp4Builder.Join(Mp4Builder.Ftyp(), moov, Mp4Builder.Join([.. Enumerable.Range(0, 20_000).Select(i => Mp4Builder.Join(Moof(i + 1, moofLength + 8), Mp4Builder.Box("mdat", unit)))]));

        Report report = Analyzer.CreateDefault().Analyze("cut.mp4", new MemoryStream(data[..^30]));

        Finding truncated = Assert.Single(report.Findings, f => f.Code == "mp4.truncated");
        Assert.Equal("Boxes Not Shown", truncated.Node!.Name);
        Assert.Equal(2, truncated.Path!.Count);
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }
}
