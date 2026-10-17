using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The paths in JSON (a finding's path, a stream's nodes) index the tree as written: with sections hidden
// they count the kept siblings only, and a path to a hidden node, or below one, is left out (null in a
// stream's nodes).
public class FilteredPathTests
{
    public static TheoryData<string> Fixture() => DescriptionTests.AllFixtures();

    private static readonly ReportSections[] Filters =
    [
        ReportSections.Tags,
        ReportSections.Frames,
        ReportSections.Tags | ReportSections.Frames | ReportSections.SideInfo | ReportSections.Statistics,
    ];

    [Theory]
    [MemberData(nameof(Fixture))]
    public void EveryPathInFilteredJsonNamesTheNodeItPointsTo(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        foreach (ReportSections hidden in Filters)
        {
            JsonElement written = Write(report, hidden);
            JsonElement root = written.GetProperty("root");

            JsonElement[] findings = written.GetProperty("findings").EnumerateArray().ToArray();
            Assert.Equal(report.Findings.Count, findings.Length);
            for (int i = 0; i < findings.Length; i++)
            {
                if (findings[i].TryGetProperty("path", out JsonElement path))
                {
                    Node expected = report.Findings[i].Node!;
                    Assert.True(Follow(root, path) is { } found && Same(found, expected), $"{name} --hide {hidden}: finding {report.Findings[i].Code}");
                }
                else if (report.Findings[i].Path is not null)
                {
                    Assert.True(IsHidden(report, report.Findings[i].Path!, hidden), $"{name} --hide {hidden}: finding {report.Findings[i].Code} lost its path");
                }
            }

            JsonElement[] streams = written.GetProperty("streams").EnumerateArray().ToArray();
            for (int i = 0; i < streams.Length; i++)
            {
                JsonElement[] nodes = streams[i].GetProperty("nodes").EnumerateArray().ToArray();
                Assert.Equal(report.Streams[i].NodePaths.Count, nodes.Length);
                for (int n = 0; n < nodes.Length; n++)
                {
                    if (nodes[n].ValueKind == JsonValueKind.Null)
                    {
                        Assert.True(report.Streams[i].NodePaths[n] is null || IsHidden(report, report.Streams[i].NodePaths[n]!, hidden), $"{name} --hide {hidden}: stream {i} node {n} lost its path");
                    }
                    else
                    {
                        Assert.True(Follow(root, nodes[n]) is { } found && Same(found, report.Streams[i].Nodes[n]), $"{name} --hide {hidden}: stream {i} node {n}");
                    }
                }
            }
        }
    }

    [Fact]
    public void AStreamBehindAHiddenTagKeepsItsIndex()
    {
        Report report = Fixtures.AnalyzeMpeg("mpeg/id3v22-tss-names-encoder.mp3");
        JsonElement full = Write(report, ReportSections.None);
        JsonElement filtered = Write(report, ReportSections.Tags);

        Assert.Equal(1, full.GetProperty("streams")[0].GetProperty("nodes")[0][0].GetInt32());
        Assert.Equal(0, filtered.GetProperty("streams")[0].GetProperty("nodes")[0][0].GetInt32());
    }

    [Fact]
    public void AFindingOnAHiddenNodeHasNoPath()
    {
        Report report = Fixtures.AnalyzeMpeg("tags/ape-leading-after-id3v2.mp3");
        JsonElement filtered = Write(report, ReportSections.Tags);

        Assert.Contains(report.Findings, f => f.Code == "ape.at-start");
        JsonElement apeAtStart = filtered.GetProperty("findings").EnumerateArray().First(f => f.GetProperty("code").GetString() == "ape.at-start");
        Assert.False(apeAtStart.TryGetProperty("path", out _));
        Assert.True(apeAtStart.TryGetProperty("offset", out _));
    }

    private static JsonElement Write(Report report, ReportSections hidden)
    {
        using var stream = new MemoryStream();
        ReportJsonWriter.Write([report], stream, new ReportFilter(hidden));
        using JsonDocument document = JsonDocument.Parse(stream.ToArray(), new JsonDocumentOptions { MaxDepth = 1024 });
        return document.RootElement[0].Clone();
    }

    private static JsonElement? Follow(JsonElement root, JsonElement path)
    {
        JsonElement node = root;
        foreach (JsonElement index in path.EnumerateArray())
        {
            if (!node.TryGetProperty("children", out JsonElement children) || index.GetInt32() >= children.GetArrayLength())
            {
                return null;
            }

            node = children[index.GetInt32()];
        }

        return node;
    }

    private static bool Same(JsonElement written, Node node) =>
        written.GetProperty("name").GetString() == node.Name
        && written.GetProperty("offset").GetInt64() == node.Offset
        && written.GetProperty("length").GetInt64() == node.Length;

    // Whether the path runs through a node the filter leaves out.
    private static bool IsHidden(Report report, IReadOnlyList<int> path, ReportSections hidden)
    {
        var filter = new ReportFilter(hidden);
        Node node = report.Root;
        foreach (int index in path)
        {
            node = node.Children[index];
            if (!filter.Includes(node))
            {
                return true;
            }
        }

        return false;
    }
}
