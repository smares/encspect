using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

public class MatroskaClusterHeaderTests
{
    // A Cluster of unknown size has an Element Data Size of five octets (all ones), more than an EBMLMaxSizeLength of 4 allows (RFC 8794, section 11.2.5), and so
    // has a SimpleBlock inside it. The finding keeps the first case in file order: the Cluster's own header, whose node ends where the Cluster does.
    [Fact]
    public void TheClustersOwnHeaderIsTheFirstCaseOfAGroupedFinding()
    {
        byte[] header = MatroskaBuilder.Element(0x1A45DFA3, MatroskaBuilder.Unsigned(0x4286, 1), MatroskaBuilder.Unsigned(0x42F7, 1), MatroskaBuilder.Unsigned(0x42F2, 4),
            MatroskaBuilder.Unsigned(0x42F3, 4), MatroskaBuilder.Text(0x4282, "matroska"), MatroskaBuilder.Unsigned(0x4287, 4), MatroskaBuilder.Unsigned(0x4285, 2));
        byte[] block = MatroskaBuilder.Concat([0xA3, 0x08, 0x00, 0x00, 0x00, 0x07], [0x81, 0x00, 0x00, 0x80], [0xFC, 0xFF, 0xFE]);
        byte[] cluster = MatroskaBuilder.Concat([0x1F, 0x43, 0xB6, 0x75, 0x0F, 0xFF, 0xFF, 0xFF, 0xFF], MatroskaBuilder.Unsigned(0xE7, 0), block);
        byte[] file = MatroskaBuilder.Concat(header, MatroskaBuilder.Segment(MatroskaBuilder.OpusTracks()), cluster);

        Report report = Analyzer.CreateDefault().Analyze("cluster.mka", new MemoryStream(file));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "matroska.size-too-long");
        Assert.Equal(2, finding.Count);
        Assert.Equal("Cluster", finding.Node?.Name);
        Assert.Equal(file.Length - finding.Node!.Offset, finding.Node.Length);
        TestFiles.CheckInsideParents(report.Root);
    }
}
