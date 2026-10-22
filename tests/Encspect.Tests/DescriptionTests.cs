using System.Reflection;
using System.Text.RegularExpressions;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Every node a parser or the host makes says what it is (Node.Description), and every topic a
// node points to exists (Node.Topic). Checked over the trees of every fixture with the default
// parsers; the test parsers of the test projects build nodes of their own and are not run here.
// A new parser's nodes meet this test as soon as its fixtures are added.
public partial class DescriptionTests
{
    // Children of a repeated node, such as the frames of a region, are visited in a sample: this
    // many spread over the list, and always those on the way to a finding's node.
    private const int SampledRepeats = 40;

    // Words that suggest a quality verdict, which the report never gives (plan, "Non-goals").
    [GeneratedRegex(@"\b(good|bad|excellent|poor|transparent)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerdictWords();

    public static TheoryData<string> AllFixtures()
    {
        var data = new TheoryData<string>();
        foreach (string name in FixtureNames())
        {
            data.Add(name);
        }

        return data;
    }

    public static IEnumerable<string> FixtureNames()
    {
        string[] extensions = [".mp1", ".mp2", ".mp3", ".m4a", ".mp4", ".mov", ".aac", ".ogg", ".spx", ".opus", ".mpc", ".ac3", ".eac3", ".wma", ".asf", ".wv", ".wvc", ".dts", ".dtshd", ".mka", ".mkv", ".webm", ".bin"];
        foreach (string path in Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*", SearchOption.AllDirectories)
            .Where(p => extensions.Contains(Path.GetExtension(p), StringComparer.Ordinal)).Order(StringComparer.Ordinal))
        {
            yield return Path.GetRelativePath(Fixtures.FixtureDirectory, path).Replace('\\', '/');
        }
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void EveryNodeIsDescribed(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        var problems = new List<string>();
        var onPath = new HashSet<(int Depth, int Index)>[Node.MaxDepth + 1];
        foreach (IReadOnlyList<int> path in report.Findings.Select(f => f.Path).OfType<IReadOnlyList<int>>())
        {
            for (int depth = 0; depth < path.Count && depth < onPath.Length; depth++)
            {
                (onPath[depth] ??= []).Add((depth, path[depth]));
            }
        }

        Walk(report.Root, "File", 0, problems);
        foreach (Series series in report.Streams.SelectMany(s => s.Series))
        {
            Check(series.Description, $"series {series.Name}", problems);
        }

        CheckCategorySeries(report, problems);

        Assert.True(problems.Count == 0, $"{name}:\n" + string.Join("\n", problems.Distinct().Take(40)));

        void Walk(Node node, string path, int depth, List<string> found)
        {
            Check(node.Description, path, found);
            if (node.Topic is { } topic && TopicLibrary.Find(topic) is null)
            {
                found.Add($"{path}: topic \"{topic}\" does not exist");
            }

            if (depth >= Node.MaxDepth)
            {
                return;
            }

            IReadOnlyList<Node> children = node.Children;
            foreach (int index in Sample(children.Count, depth))
            {
                Node child = children[index];
                Walk(child, $"{path}/{child.Name}", depth + 1, found);
            }
        }

        IEnumerable<int> Sample(int count, int depth)
        {
            if (count <= SampledRepeats * 2)
            {
                return Enumerable.Range(0, count);
            }

            IEnumerable<int> spread = Enumerable.Range(0, SampledRepeats).Select(i => (int)((long)i * (count - 1) / (SampledRepeats - 1)));
            IEnumerable<int> findings = onPath[depth]?.Select(p => p.Index).Where(i => i < count) ?? [];
            return spread.Concat(Enumerable.Range(0, Math.Min(count, 8))).Concat(findings).Distinct().Order();
        }
    }

    [Fact]
    public void ErrorNodeOfAFailedBuilderIsDescribed()
    {
        var node = new Node("Frame", 0, 4, null, () => throw new InvalidOperationException("broken"));
        Node error = Assert.Single(node.Children);
        Assert.Equal("Error", error.Name);
        var problems = new List<string>();
        Check(error.Description, "Error", problems);
        Assert.Empty(problems);
    }

    [Fact]
    public void FileReportsWithoutAnalysisAreDescribed()
    {
        Report report = FileReports.Unreadable("gone.mp3", "No such file.");
        Assert.NotNull(report.Root.Description);
    }

    [Fact]
    public void TopicsLoadWithValidFrontMatter()
    {
        IReadOnlyList<Topic> topics = TopicLibrary.All;
        Assert.True(topics.Count >= 40, $"Only {topics.Count} topics loaded.");
        Assert.Equal(topics.Count, topics.Select(t => t.Id).Distinct().Count());
        foreach (Topic topic in topics)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Title), topic.Id);
            Assert.NotEmpty(topic.References);
            Assert.NotEmpty(topic.Blocks);
        }
    }

    [Fact]
    public void TopicLinksResolve()
    {
        foreach (Topic topic in TopicLibrary.All)
        {
            foreach (string related in topic.Related)
            {
                Assert.True(TopicLibrary.Find(related) is not null, $"{topic.Id} lists the related topic {related}, which does not exist.");
                Assert.NotEqual(topic.Id, related);
            }

            foreach (TopicInline link in topic.Blocks.SelectMany(b => b.Inlines).Where(i => i.Link is not null))
            {
                Assert.True(TopicLibrary.Find(link.Link) is not null, $"{topic.Id} links to {link.Link}, which does not exist.");
            }
        }
    }

    // Each article aims for 100 to 300 words of plain prose (the bound is 400, a guard against bloat), without verdicts, and uses only the
    // Markdown subset the front ends render: no stray markup survives the parse.
    [Fact]
    public void TopicsFollowTheWritingRules()
    {
        foreach (Topic topic in TopicLibrary.All)
        {
            int words = Regex.Matches(topic.Body, @"[\p{L}\p{N}][\p{L}\p{N}'/.-]*").Count;
            Assert.True(words is >= 100 and <= 400, $"{topic.Id} has {words} words.");
            Assert.False(VerdictWords().IsMatch(topic.Body), $"{topic.Id}: \"{VerdictWords().Match(topic.Body).Value}\" reads as a verdict.");
            foreach (TopicInline inline in topic.Blocks.SelectMany(b => b.Inlines).Where(i => !i.IsCode))
            {
                Assert.DoesNotContain('*', inline.Text);
                Assert.DoesNotContain('`', inline.Text);
                Assert.False(inline.Text.Contains("](", StringComparison.Ordinal), $"{topic.Id} has a link that did not parse.");
            }

            foreach (string term in (string[])["MS stereo", "M/S", " IS ", " LR ", "colour", "behaviour", "optimise", "recognise"])
            {
                Assert.False(topic.Body.Contains(term, StringComparison.Ordinal), $"{topic.Id} writes \"{term.Trim()}\".");
            }
        }
    }

    // A topic is reached from a node or from another topic. The IDs the parsers and the host
    // declare are read from their Topics classes, so an ID that no fixture's tree happens to show
    // is checked too.
    [Fact]
    public void EveryTopicIsReachableAndEveryDeclaredTopicExists()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<Assembly> assemblies = DefaultParsers.Formats.Cast<object>().Concat(DefaultParsers.Tags).Concat(DefaultParsers.Codecs)
            .Select(p => p.GetType().Assembly).Append(typeof(Analyzer).Assembly).Distinct();
        foreach (Type type in assemblies.SelectMany(a => a.GetTypes()).Where(t => t.Name == "Topics"))
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Where(f => f.IsLiteral))
            {
                string id = (string)field.GetRawConstantValue()!;
                Assert.True(TopicLibrary.Find(id) is not null, $"{type.FullName}.{field.Name} names the topic {id}, which does not exist.");
                declared.Add(id);
            }
        }

        var linked = TopicLibrary.All.SelectMany(t => t.Related.Concat(t.Blocks.SelectMany(b => b.Inlines).Select(i => i.Link).OfType<string>())).ToHashSet();
        foreach (Topic topic in TopicLibrary.All)
        {
            Assert.True(declared.Contains(topic.Id) || linked.Contains(topic.Id), $"No node and no topic leads to {topic.Id}.");
        }
    }

    [Fact]
    public void FrontMatterIsChecked()
    {
        Assert.Throws<InvalidDataException>(() => TopicLibrary.Parse("no front matter"));
        Assert.Throws<InvalidDataException>(() => TopicLibrary.Parse("---\nid: x\n---\n\nText."));
        Assert.Throws<InvalidDataException>(() => TopicLibrary.Parse("---\nid: Bad Id\ntitle: T\n---\n\nText."));
        Assert.Throws<InvalidDataException>(() => TopicLibrary.Parse("---\nid: x\ntitle: T\nauthor: me\n---\n\nText."));
        Assert.Throws<InvalidDataException>(() => TopicLibrary.Parse("---\nid: x\ntitle: T\n---\n"));
        Topic topic = TopicLibrary.Parse("---\nid: x\ntitle: T\nrelated: a, b\nreferences: ISO/IEC 11172-3, 2.4; LAME\n---\n\nOne *two* **three** `four` [five](a).\n\n- six\n  seven");
        Assert.Equal(["a", "b"], topic.Related);
        Assert.Equal(["ISO/IEC 11172-3, 2.4", "LAME"], topic.References);
        Assert.Equal(2, topic.Blocks.Count);
        Assert.Equal(
            [new TopicInline("One "), new TopicInline("two", IsEmphasis: true), new TopicInline(" "), new TopicInline("three", IsStrong: true), new TopicInline(" "),
                new TopicInline("four", IsCode: true), new TopicInline(" "), new TopicInline("five", Link: "a"), new TopicInline(".")],
            topic.Blocks[0].Inlines);
        Assert.True(topic.Blocks[1].IsListItem);
        Assert.Equal("six seven", Assert.Single(topic.Blocks[1].Inlines).Text);
    }

    private static void CheckCategorySeries(Report report, List<string> problems)
    {
        foreach (CategorySeries series in report.Streams.SelectMany(s => s.CategorySeries))
        {
            Check(series.Description, $"category series {series.Name}", problems);
            CheckTopic(series.Topic, $"category series {series.Name}", problems);
            foreach (Category category in series.Categories)
            {
                Check(category.Description, $"category {category.Name} of {series.Name}", problems);
                CheckTopic(category.Topic, $"category {category.Name} of {series.Name}", problems);
            }
        }
    }

    [Fact]
    public void CategorySeriesAndTheirCategoriesAreDescribed()
    {
        var builder = new CategorySeriesBuilder("Modes", [new("A", "Described."), new("B", "") { Topic = "no-such-topic" }], "frame", 1);
        builder.Add(0);
        CategorySeries bad = builder.Build(new double[1], null, null, "no period");
        Report report = new("a.bin", new Node("File", 0, 1), [], [new StreamInfo { Nodes = [], Codec = "Test", CategorySeries = [bad] }]);
        var problems = new List<string>();

        CheckCategorySeries(report, problems);

        Assert.Equal(
            [
                "category series Modes: description ends with neither a period nor a closing parenthesis: \"no period\"",
                "category B of Modes: no description",
                "category B of Modes: topic \"no-such-topic\" does not exist",
            ],
            problems);
    }

    private static void CheckTopic(string? topic, string where, List<string> problems)
    {
        if (topic is not null && TopicLibrary.Find(topic) is null)
        {
            problems.Add($"{where}: topic \"{topic}\" does not exist");
        }
    }

    private static void Check(string? description, string where, List<string> problems)
    {
        if (string.IsNullOrEmpty(description))
        {
            problems.Add($"{where}: no description");
            return;
        }

        if (description != description.Trim())
        {
            problems.Add($"{where}: description not trimmed");
        }

        if (!description.EndsWith('.') && !description.EndsWith(')'))
        {
            problems.Add($"{where}: description ends with neither a period nor a closing parenthesis: \"{description}\"");
        }

        if (VerdictWords().Match(description) is { Success: true } verdict)
        {
            problems.Add($"{where}: \"{verdict.Value}\" reads as a verdict: \"{description}\"");
        }
    }
}
