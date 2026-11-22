using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Nero's tags box (plan 0.4, item 9) in the four files neroAacTag 1.5.4.0 tagged: the parsed fields against neroAacTag's own listings, which the provenance
// records, and the findings for a copy that differs from the iTunes metadata or the chapter list beside it.
public class NeroTagsTests
{
    private static readonly string[] Names = ["cover", "unicode", "userfield", "chapters"];

    public static TheoryData<string> Files => new(Names);

    private static string PathOf(string name) => Path.Combine(Fixtures.FixtureDirectory, "aac", "sample-kit", $"external-neroaactag-{name}");

    private static Report Analyze(byte[] data) => Analyzer.CreateDefault().Analyze("nero.m4a", new MemoryStream(data));

    private static Report AnalyzeFile(string name) => Analyze(File.ReadAllBytes(PathOf(name) + ".m4a"));

    private static JsonElement Listings(string name) => JsonDocument.Parse(File.ReadAllText(PathOf(name) + ".provenance.json")).RootElement.GetProperty("listings");

    private static Node Find(Node node, string name) => node.Name == name ? node : node.Children.Select(c => Find(c, name)).FirstOrDefault(n => n is not null)!;

    private static IEnumerable<Node> FindAll(Node node, string name) => (node.Name == name ? [node] : Enumerable.Empty<Node>()).Concat(node.Children.SelectMany(c => FindAll(c, name)));

    private static Node Tag(Report report) => FindAll(report.Root, "Nero Tags").Single();

    private static string ChildText(Node node, string name) => node.Children.Single(c => c.Name == name).Value!.AsText()!;

    // The fields of one field list, by key.
    private static Dictionary<string, string> Fields(Node list) => list.Children.Where(c => c.Children.Any(k => k.Name == "Key")).ToDictionary(c => ChildText(c, "Key"), c => ChildText(c, "Value"));

    // neroAacTag's console output replaces letters its code page lacks with "?" and shows others in the console's code page: the listing agrees where the
    // text is ASCII, and has a character for each other one.
    private static bool ListedAs(string listed, string actual) =>
        listed.Length == actual.Length && listed.Zip(actual, (l, a) => l == a || a > 127).All(b => b);

    [Theory]
    [MemberData(nameof(Files))]
    public void TheFieldsAreWhatNeroAacTagListsAsMetadata(string name)
    {
        Node tag = Tag(AnalyzeFile(name));
        Dictionary<string, string> parsed = Fields(tag.Children.First(c => c.Name == "Nero Tag Fields"));
        JsonElement listed = Listings(name).GetProperty("meta");

        Assert.Equal(listed.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).Order(), parsed.Keys.Select(k => k.ToLowerInvariant()).Order());
        foreach (JsonProperty field in listed.EnumerateObject())
        {
            string actual = parsed.Single(p => string.Equals(p.Key, field.Name, StringComparison.OrdinalIgnoreCase)).Value;
            Assert.True(ListedAs(field.Value.GetString()!, actual), $"{field.Name}: listed \"{field.Value.GetString()}\", parsed \"{actual}\"");
        }
    }

    [Fact]
    public void TheUnicodeFileHoldsUtf8TextInBothTagsWhereNeroAacTagPrintsQuestionMarks()
    {
        Report report = AnalyzeFile("unicode");
        Dictionary<string, string> fields = Fields(Tag(report).Children.First(c => c.Name == "Nero Tag Fields"));

        Assert.Equal("Encspect Äöüß test", fields["title"]);
        Assert.Equal("Тест", fields["artist"]);
        Assert.Equal("テスト", fields["album"]);
        Node items = FindAll(report.Root, "iTunes Metadata").Single();
        Assert.Equal(fields["artist"], items.Children.Single(c => c.Name == "Artist").Value!.AsText());
        Assert.Equal(fields["album"], items.Children.Single(c => c.Name == "Album").Value!.AsText());
        Assert.Equal(fields["title"], items.Children.Single(c => c.Name == "Title").Value!.AsText());
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TheCoversAreWhatNeroAacTagListsAsCoverEntries(string name)
    {
        Node tag = Tag(AnalyzeFile(name));
        string[] parsed = [.. tag.Children.Where(c => c.Name == "Nero Cover").Select((c, i) =>
            $"{ChildText(c, "Cover Type")} #{i + 1},{(ChildText(c, "Image").StartsWith("JPEG", StringComparison.Ordinal) ? "image/jpeg" : "other")}")];

        Assert.Equal(Listings(name).GetProperty("covers").EnumerateArray().Select(e => e.GetString()), parsed);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TheChaptersAreWhatNeroAacTagListsAsChapters(string name)
    {
        Report report = AnalyzeFile(name);
        JsonElement[] listed = [.. Listings(name).GetProperty("chapters").EnumerateArray()];
        Node[] chapters = [.. Find(report.Root, "chpl").Children.Where(c => c.Name == "Chapter")];
        double delay = Convert.ToInt32(Listings(name).GetProperty("meta").GetProperty("itunsmpb").GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], 16) / 44100.0;

        Assert.Equal(listed.Length, chapters.Length);
        for (int i = 0; i < listed.Length; i++)
        {
            // The listing is the start the box states less the encoder delay (the second word of iTunSMPB, 2,624 samples at 44,100 Hz), cut to milliseconds.
            double start = chapters[i].Children.Single(c => c.Name == "Start").Value!.AsDouble()!.Value;
            double seconds = Math.Floor(((start - delay) * 1000) + 0.01) / 1000;
            Assert.Equal(listed[i].GetProperty("start").GetString(), $"{(int)(seconds / 60)}:{seconds % 60:00.000}");
            if (listed[i].TryGetProperty("title", out JsonElement title))
            {
                Assert.True(ListedAs(title.GetString()!, ChildText(chapters[i], "Title")));
            }
        }

        if (name == "chapters")
        {
            // Each segment of the tag holds the title and track of its chapter, as -list-meta lists them chapter by chapter.
            Node[] segments = [.. Tag(report).Children.Where(c => c.Name == "Nero Tag Segment")];
            JsonElement[] meta = [.. Listings(name).GetProperty("meta_per_chapter").EnumerateArray()];
            Assert.Equal(meta.Length, segments.Length);
            for (int i = 0; i < segments.Length; i++)
            {
                Assert.Equal(i, (int)Find(segments[i], "Segment Index").Value!.AsDouble()!.Value);
                Dictionary<string, string> fields = Fields(Find(segments[i], "Nero Tag Fields"));
                Assert.Equal(meta[i].GetProperty("title").GetString(), fields["title"]);
                Assert.Equal(meta[i].GetProperty("track").GetString(), fields["track"]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TheCopiesAgreeWithTheItunesMetadataAndTheTagIsListedAsATag(string name)
    {
        Report report = AnalyzeFile(name);

        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("mp4.nero-", StringComparison.Ordinal));
        Assert.Contains("Nero Tags", ReportSummary.From(report).Tags);
        Assert.Equal(NodeKind.Tag, Tag(report).Kind);
    }

    // The tags box follows the iTunes metadata and the chapter list in the files, so its copy of a text is the last one.
    private static byte[] Patched(string name, string from, string to)
    {
        byte[] data = File.ReadAllBytes(PathOf(name) + ".m4a");
        byte[] old = Encoding.UTF8.GetBytes(from);
        int at = data.AsSpan().LastIndexOf(old);
        Assert.True(at > 0);
        Encoding.UTF8.GetBytes(to).CopyTo(data, at);
        return data;
    }

    [Fact]
    public void AChapterTitleThatDiffersFromTheChapterListIsAFinding()
    {
        Report report = Analyze(Patched("chapters", "Chapter 2", "Chapter X"));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "mp4.nero-chapter-differs");
        Assert.Equal("Title", finding.Node!.Name);
        Assert.Contains("Chapter X", finding.Message, StringComparison.Ordinal);
        Assert.Contains("Chapter 2", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFieldThatDiffersFromTheItunesMetadataIsAFinding()
    {
        Report report = Analyze(Patched("userfield", "Encspect comment", "Encspect Comment"));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "mp4.nero-field-differs");
        Assert.Equal("Comment", finding.Node!.Name);
        Assert.DoesNotContain(report.Findings, f => f.Code is "mp4.nero-chapter-differs" or "mp4.nero-cover-differs");
    }

    [Fact]
    public void AFreeformFieldThatDiffersFromItsItunesItemIsAFinding()
    {
        Report report = Analyze(Patched("userfield", "user field test", "user field TEST"));

        Assert.Equal("encspect", Assert.Single(report.Findings, f => f.Code == "mp4.nero-field-differs").Node!.Name);
    }

    [Fact]
    public void ACoverThatDiffersFromTheItunesCoverIsAFinding()
    {
        byte[] data = File.ReadAllBytes(PathOf("cover") + ".m4a");
        int at = data.AsSpan().LastIndexOf(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        data[at + 40] ^= 0x55;

        Report report = Analyze(data);

        Assert.Single(report.Findings, f => f.Code == "mp4.nero-cover-differs");
    }

    [Fact]
    public void AValueLongerThanItsListIsCutAndReported()
    {
        byte[] data = File.ReadAllBytes(PathOf("unicode") + ".m4a");
        // The album field: 80 00 05 "album", the type 00 01 and the 32-bit length, which is 9 bytes.
        byte[] key = [0x80, 0x00, 0x05, (byte)'a', (byte)'l', (byte)'b', (byte)'u', (byte)'m', 0x00, 0x01, 0x00, 0x00, 0x00];
        int at = data.AsSpan().LastIndexOf(key);
        Assert.True(at > 0);
        data[at + 10] = 0x10;

        Report report = Analyze(data);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "mp4.nero-field-size-invalid");
        Assert.Equal("Album", finding.Node!.Name);
    }

    [Fact]
    public void AFieldCountThatDiffersFromTheFieldsIsReported()
    {
        byte[] data = File.ReadAllBytes(PathOf("userfield") + ".m4a");
        byte[] header = [(byte)'m', (byte)'e', (byte)'t', (byte)'a', 0x00, 0x00, 0x00, 0x05, 0x80, 0x00, 0x04, (byte)'c', (byte)'d', (byte)'e', (byte)'c'];
        int at = data.AsSpan().LastIndexOf(header);
        Assert.True(at > 0);

        data[at + 7] = 9;
        Assert.Single(Analyze(data).Findings, f => f.Code == "mp4.nero-field-count-invalid");

        data[at + 7] = 2;
        Assert.Single(Analyze(data).Findings, f => f.Code == "mp4.nero-field-count-invalid");
    }

    [Fact]
    public void ATagsBoxThatHoldsNoneOfNeroBoxesStaysUnread()
    {
        byte[] box = [0, 0, 0, 12, (byte)'t', (byte)'a', (byte)'g', (byte)'s', 1, 2, 3, 4];
        byte[] data = Mp4Builder.Join(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd(), Mp4Builder.Box("udta", box)));

        Report report = Analyze(data);

        Assert.Empty(FindAll(report.Root, "Nero Tags"));
        Assert.NotNull(Find(report.Root, "tags"));
    }
}
