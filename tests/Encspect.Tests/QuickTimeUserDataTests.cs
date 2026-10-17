using System.Diagnostics;
using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

[Collection(nameof(ComplexityTests))]
public class QuickTimeUserDataTests
{
    private const string Fixture = "mp4/quicktime-udta-text.mov";

    private static Report Analyze(byte[] data) => Analyzer.CreateDefault().Analyze("udta.mov", new MemoryStream(data));

    // Mp4Builder writes types in ASCII; the © of a user data atom is the byte 0xA9.
    private static byte[] TextAtom(string type, byte[] payload)
    {
        byte[] atom = Mp4Builder.Box("?" + type, payload);
        atom[4] = 0xA9;
        return atom;
    }

    private static Node Child(Node node, params string[] names) => names.Aggregate(node, (n, name) => n.Children.Single(c => c.Name == name));

    // The three places a front end finds tags: the summary, the CSV's Tags column and the report that --hide tags filters.
    [Fact]
    public void SummaryCsvAndHideSeeTheTextAtoms()
    {
        Report report = Fixtures.AnalyzeMpeg(Fixture);
        string[] names = ["QuickTime Title", "QuickTime Artist", "QuickTime Album", "QuickTime Creation Date", "QuickTime Software", "QuickTime Description", "QuickTime Comment", "QuickTime Copyright"];

        IReadOnlyList<string> tags = ReportSummary.From(report).Tags;
        Assert.All(names, name => Assert.Contains(name, tags));
        Assert.Equal(names.Length + 1, tags.Count);

        var csv = new StringWriter();
        ReportCsvWriter.Write([report], csv);
        string row = csv.ToString().Split("\r\n")[1];
        Assert.All(names, name => Assert.Contains(name, row, StringComparison.Ordinal));

        string all = Fixtures.ToJson(report);
        Assert.Contains("\"name\": \"QuickTime Title\"", all, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"WLOC\"", all, StringComparison.Ordinal);
        using var hidden = new MemoryStream();
        ReportJsonWriter.Write([report], hidden, new ReportFilter(ReportSections.Tags));
        string filtered = Encoding.UTF8.GetString(hidden.ToArray());
        Assert.DoesNotContain("\"name\": \"QuickTime ", filtered, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"WLOC\"", filtered, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"udta\"", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryItemIsDecodedWithItsLanguage()
    {
        Node udta = Child(Fixtures.AnalyzeMpeg(Fixture).Root, "MP4", "moov", "udta");

        Node title = Child(udta, "QuickTime Title");
        Assert.Equal(NodeKind.Tag, title.Kind);
        Assert.Equal("Café title", title.Value?.ToString());
        Node[] items = [.. title.Children.Where(c => c.Name == "Text Item")];
        Assert.Equal(2, items.Length);
        Assert.Equal("0 (English, Macintosh language code)", Child(items[0], "Language").Value?.ToString());
        Assert.Equal("Café title", Child(items[0], "Text").Value?.ToString());
        Assert.Equal("ger", Child(items[1], "Language").Value?.ToString());
        Assert.Equal("Handgefertigter Titel über Café", Child(items[1], "Text").Value?.ToString());
        Assert.Equal("Utf sixteen", Child(udta, "QuickTime Description").Value?.ToString());
        Assert.Equal("32767 (unspecified Macintosh language)", Child(udta, "QuickTime Album", "Text Item", "Language").Value?.ToString());
    }

    [Fact]
    public void OnlyListedAtomsAreTagsAndStructureIsNever()
    {
        Node udta = Child(Fixtures.AnalyzeMpeg(Fixture).Root, "MP4", "moov", "udta");

        // An unlisted © atom that fills itself with items is read as text, and one that does not is an unread box.
        Node unlisted = Child(udta, "©xyz");
        Assert.NotEqual(NodeKind.Tag, unlisted.Kind);
        Assert.Equal("Unlisted text atom", unlisted.Value?.ToString());
        Assert.DoesNotContain(Child(udta, "©qqq").Children, c => c.Name == "Text Item");
        Assert.NotEqual(NodeKind.Tag, Child(udta, "WLOC").Kind);
        Assert.DoesNotContain(udta.Children, c => c.Kind == NodeKind.Tag && !c.Name.StartsWith("QuickTime ", StringComparison.Ordinal));

        Node trackTitle = Child(Fixtures.AnalyzeMpeg(Fixture).Root, "MP4", "moov", "trak", "udta", "QuickTime Title");
        Assert.Equal(NodeKind.Tag, trackTitle.Kind);
        Assert.Equal("Track title", trackTitle.Value?.ToString());
    }

    [Fact]
    public void MalformedSizesAreFindingsAndTheTextThatIsThereIsShown()
    {
        Report report = Fixtures.AnalyzeMpeg(Fixture);
        Node udta = Child(report.Root, "MP4", "moov", "udta");

        Finding size = Assert.Single(report.Findings, f => f.Code == "mp4.text-size-invalid");
        Assert.Contains("states 40 bytes, but 5 follow", size.Message, StringComparison.Ordinal);
        Node cut = Child(udta, "QuickTime Comment").Children.Last(c => c.Name == "Text Item");
        Assert.Equal("short", cut.Value?.ToString());
        Assert.Equal(cut.Offset, size.Node!.Offset);
        Finding tail = Assert.Single(report.Findings, f => f.Code == "mp4.text-item-truncated");
        Assert.Equal(2, tail.Node!.Length);
        Assert.Equal("Unused Bytes", Child(udta, "QuickTime Copyright").Children.Last().Name);
    }

    [Fact]
    public void SoftwareAtomIsTheWriter()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg(Fixture).Streams);

        Assert.Equal("hand-built 1.0", stream.Writer);
        Assert.Null(stream.Encoder);
    }

    // 1.5 million items of no text in one atom (6 MB): the work is linear, and only the first 1,000 items are nodes.
    [Fact]
    public void ManyItemsInOneAtomAreCappedAndFast()
    {
        const int items = 1_500_000;
        byte[] payload = new byte[items * 4];
        byte[] atom = TextAtom("nam", payload);
        byte[] data = Mp4Builder.Join(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd(), Mp4Builder.Box("udta", atom)));

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        Report report = Analyze(data);
        timer.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Node title = Child(report.Root, "MP4", "moov", "udta", "QuickTime Title");
        Assert.Equal(1000, title.Children.Count(c => c.Name == "Text Item"));
        Assert.Equal(NodeValue.Integer(items - 1000, "items", "item"), Child(title, "More Text Items").Value);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"Took {timer.Elapsed}.");
        Assert.True(allocated < 200_000_000, $"Allocated {allocated / 1_000_000} MB.");
    }

    [Fact]
    public void AtomsCutAtEveryByteGiveFindingsNotExceptions()
    {
        byte[] text = Mp4Builder.Join([0, 4, 0x15, 0xC7], "abcd"u8.ToArray());
        foreach (int cut in Enumerable.Range(0, text.Length + 1))
        {
            byte[] data = Mp4Builder.Join(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd(), Mp4Builder.Box("udta", TextAtom("cmt", text[..cut]))));
            Report report = Analyze(data);
            Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        }
    }
}
