using Encspect.Core;

namespace Encspect.Tests;

// A category series carries the counts of the tally node it is anchored to, under the same names, and a test holds the two
// equal on every fixture from a table (category-summary.json). No parser produces a series yet, so the table is empty and the
// fixtures check that none appears unlisted; the harness itself is tested on reports built here.
public class CategorySummaryTests
{
    private const string Table = """
        {
          "series": {
            "Block Usage": { "form": "entries", "anchor": "Block Usage", "denominator": "Granules" },
            "Blocks With Coupling": { "form": "flag", "anchor": "Blocks With Coupling", "denominator": "Audio Blocks Read" },
            "Joint Stereo Modes": { "form": "entries", "anchor": "Joint Stereo Modes", "denominator": "Frames", "findings": ["mpeg.streams-limited"] }
          }
        }
        """;

    public static TheoryData<string> AllFixtures() => DescriptionTests.AllFixtures();

    [Fact]
    public void The_table_parses_and_names_known_forms()
    {
        CategorySummary table = CategorySummary.Load();

        Assert.NotNull(table.Names);
        Assert.Throws<InvalidDataException>(() => CategorySummary.Parse("""{ "series": { "X": { "form": "other", "anchor": "X", "denominator": "Y" } } }"""));
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Every_category_series_equals_its_tally(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        List<string> problems = CategorySummary.Load().Check(report);

        Assert.True(problems.Count == 0, $"{name}:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void A_series_equal_to_its_tally_passes()
    {
        (Report report, _) = Build([10, 2, 3], tally: [10, 2, 3], granules: 15);

        Assert.Empty(CategorySummary.Parse(Table).Check(report));
    }

    [Fact]
    public void A_count_that_differs_is_reported_with_both_figures()
    {
        (Report report, _) = Build([10, 2, 3], tally: [10, 3, 3], granules: 15);

        string problem = Assert.Single(CategorySummary.Parse(Table).Check(report));

        Assert.Equal("Block Usage: Start is 2 in the series and 3 in the tally", problem);
    }

    [Fact]
    public void A_total_that_differs_from_the_denominator_is_reported()
    {
        (Report report, _) = Build([10, 2, 3], tally: [10, 2, 3], granules: 16);

        Assert.Contains("Block Usage: total is 15 in the series and 16 in the tally", CategorySummary.Parse(Table).Check(report));
    }

    [Fact]
    public void A_series_missing_from_the_table_fails()
    {
        (Report report, _) = Build([10, 2, 3], tally: [10, 2, 3], granules: 15);

        string problem = Assert.Single(CategorySummary.Parse("""{ "series": {} }""").Check(report));

        Assert.Equal("Block Usage: the series is not in category-summary.json", problem);
    }

    [Fact]
    public void A_series_anchored_to_another_node_than_the_table_names_fails()
    {
        (Report report, _) = Build([10, 2, 3], tally: [10, 2, 3], granules: 15);
        string table = Table.Replace("\"anchor\": \"Block Usage\"", "\"anchor\": \"Joint Stereo Modes\"", StringComparison.Ordinal);

        Assert.Contains("not to Joint Stereo Modes", Assert.Single(CategorySummary.Parse(table).Check(report)), StringComparison.Ordinal);
    }

    [Fact]
    public void Streams_that_share_a_region_are_compared_with_the_regions_tally_once()
    {
        // One tally node, two streams: a region whose sample rate changes. The series sum to the tally.
        (Report report, Node anchor) = Build([6, 1, 1], tally: [10, 2, 3], granules: 15);
        CategorySeries second = Series("Block Usage", [4, 1, 2], anchor);
        var info = new StreamInfo { Nodes = report.Streams[0].Nodes, Codec = "Test", CategorySeries = [second] };
        Report two = new(report.Source, report.Root, [], [report.Streams[0], info]);

        Assert.Empty(CategorySummary.Parse(Table).Check(two));
    }

    [Fact]
    public void A_flag_series_holds_its_with_count_against_the_tallys_value()
    {
        var root = new Node("File", 0, 100);
        var stream = new Node("Stream", 0, 100) { Kind = NodeKind.Stream };
        var statistics = new Node("Statistics", 0, 100) { Kind = NodeKind.Statistics };
        var read = new Node("Audio Blocks Read", 0, 100, NodeValue.Integer(12, "blocks", "block"));
        var coupling = new Node("Blocks With Coupling", 0, 100, NodeValue.Integer(5, "blocks", "block"));
        root.Add(stream);
        stream.Add(statistics);
        statistics.Add(read);
        statistics.Add(coupling);
        CategorySeries series = FlagSeries("Blocks With Coupling", with: 5, without: 7, coupling);
        Report report = new("a.ac3", root, [], [new StreamInfo { Nodes = [stream], Codec = "Test", CategorySeries = [series] }]);

        Assert.Empty(CategorySummary.Parse(Table).Check(report));

        CategorySeries wrong = FlagSeries("Blocks With Coupling", with: 4, without: 8, coupling);
        Report broken = new("a.ac3", root, [], [new StreamInfo { Nodes = [stream], Codec = "Test", CategorySeries = [wrong] }]);
        Assert.Equal("Blocks With Coupling: With coupling is 4 in the series and 5 in the tally", Assert.Single(CategorySummary.Parse(Table).Check(broken)));
    }

    [Fact]
    public void A_listed_finding_allows_the_series_to_be_smaller()
    {
        (Report report, _) = Build([10, 2, 2], tally: [10, 2, 3], granules: 15, "Joint Stereo Modes", "Frames");
        Assert.NotEmpty(CategorySummary.Parse(Table).Check(report));

        Report allowed = new(report.Source, report.Root, [new Finding(Severity.Warning, "mpeg.streams-limited", "Frames belong to no stream.")], report.Streams);
        Assert.Empty(CategorySummary.Parse(Table).Check(allowed));
    }

    private static CategorySeries Series(string name, long[] counts, Node anchor)
    {
        Category[] categories = [new("Long", "l"), new("Start", "s"), new("Short", "t")];
        var builder = new CategorySeriesBuilder(name, categories, "granule", 1);
        for (int category = 0; category < counts.Length; category++)
        {
            for (long i = 0; i < counts[category]; i++)
            {
                builder.Add((byte)category);
            }
        }

        return builder.Build(new double[builder.Count], null, anchor, "Test.");
    }

    private static CategorySeries FlagSeries(string name, int with, int without, Node anchor)
    {
        var builder = new CategorySeriesBuilder(name, [new("With coupling", "w"), new("Without coupling", "o")], "block", 1);
        for (int i = 0; i < with + without; i++)
        {
            builder.Add((byte)(i < with ? 0 : 1));
        }

        return builder.Build(new double[builder.Count], null, anchor, "Test.");
    }

    private static (Report Report, Node Anchor) Build(long[] counts, long[] tally, long granules, string anchorName = "Block Usage", string denominator = "Granules")
    {
        var root = new Node("File", 0, 100);
        var stream = new Node("Stream", 0, 100) { Kind = NodeKind.Stream };
        var statistics = new Node("Statistics", 0, 100) { Kind = NodeKind.Statistics };
        var anchor = new Node(anchorName, 0, 100, NodeValue.Integer(granules, "granules", "granule"));
        string[] names = ["Long", "Start", "Short"];
        for (int i = 0; i < names.Length; i++)
        {
            anchor.Add(new Node(names[i], 0, 100, NodeValue.Integer(tally[i], "granules", "granule")));
        }

        root.Add(stream);
        stream.Add(statistics);
        statistics.Add(new Node(denominator, 0, 100, NodeValue.Integer(granules, "granules", "granule")));
        statistics.Add(anchor);
        CategorySeries series = Series(anchorName, counts, anchor);
        return (new Report("a.mp3", root, [], [new StreamInfo { Nodes = [stream], Codec = "Test", CategorySeries = [series] }]), anchor);
    }
}
