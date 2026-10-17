using System.Diagnostics;
using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

[Collection(nameof(ComplexityTests))]
public class QuickTimeUserDataTests
{
    private const string Fixture = "mp4/quicktime-udta-text.mov";

    private const string UserData = "QuickTime User Data";

    private static Report Analyze(byte[] data) => Analyzer.CreateDefault().Analyze("udta.mov", new MemoryStream(data));

    // Mp4Builder writes types in ASCII; the © of a user data atom is the byte 0xA9.
    private static byte[] TextAtom(string type, byte[] payload)
    {
        byte[] atom = Mp4Builder.Box("?" + type, payload);
        atom[4] = 0xA9;
        return atom;
    }

    private static byte[] MovieWithUserData(params byte[][] atoms) =>
        Mp4Builder.Join(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd(), Mp4Builder.Box("udta", atoms)));

    private static Node Child(Node node, params string[] names) => names.Aggregate(node, (n, name) => n.Children.Single(c => c.Name == name));

    private static int Count(Node node, string name) => (node.Name == name ? 1 : 0) + node.Children.Sum(c => Count(c, name));

    // The three places a front end finds tags: the summary, the CSV's Tags column and the report that --hide tags filters. The user data box
    // is the tag, like an item list, and a tag it holds is listed too.
    [Fact]
    public void SummaryCsvAndHideSeeTheUserDataTag()
    {
        Report report = Fixtures.AnalyzeMpeg(Fixture);

        Assert.Equal([UserData, UserData, "iTunes Metadata"], ReportSummary.From(report).Tags);

        var csv = new StringWriter();
        ReportCsvWriter.Write([report], csv);
        Assert.Contains($"\"{UserData}, {UserData}, iTunes Metadata\"", csv.ToString().Split("\r\n")[1], StringComparison.Ordinal);

        string all = Fixtures.ToJson(report);
        Assert.Contains($"\"name\": \"{UserData}\"", all, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"WLOC\"", all, StringComparison.Ordinal);
        using var hidden = new MemoryStream();
        ReportJsonWriter.Write([report], hidden, new ReportFilter(ReportSections.Tags));
        string filtered = Encoding.UTF8.GetString(hidden.ToArray());
        Assert.DoesNotContain($"\"name\": \"{UserData}\"", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\": \"WLOC\"", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("iTunes Metadata", filtered, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"moov\"", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoxWithoutTextAtomsIsNoTag()
    {
        Report report = Analyze(MovieWithUserData(Mp4Builder.Box("WLOC", [0, 10, 0, 20])));

        Node udta = Child(report.Root, "MP4", "moov", "udta");
        Assert.NotEqual(NodeKind.Tag, udta.Kind);
        Assert.Empty(ReportSummary.From(report).Tags);
    }

    [Fact]
    public void EveryItemIsDecodedWithItsLanguage()
    {
        Node udta = Child(Fixtures.AnalyzeMpeg(Fixture).Root, "MP4", "moov", UserData);
        Assert.Equal(NodeKind.Tag, udta.Kind);

        Node title = Child(udta, "Title");
        Assert.NotEqual(NodeKind.Tag, title.Kind);
        Assert.Equal("Café title", title.Value?.ToString());
        Node[] items = [.. title.Children.Where(c => c.Name == "Text Item")];
        Assert.Equal(2, items.Length);
        Assert.Equal("0 (English, Macintosh language code)", Child(items[0], "Language").Value?.ToString());
        Assert.Equal("Café title", Child(items[0], "Text").Value?.ToString());
        Assert.Equal("deu", Child(items[1], "Language").Value?.ToString());
        Assert.Equal("Handgefertigter Titel über Café", Child(items[1], "Text").Value?.ToString());
        Assert.Equal("Utf sixteen", Child(udta, "Description").Value?.ToString());
        Assert.Equal("32767 (unspecified Macintosh language)", Child(udta, "Album", "Text Item", "Language").Value?.ToString());
    }

    [Fact]
    public void OnlyListedAtomsAreNamedForTheirFieldAndStructureIsNeverText()
    {
        Report report = Fixtures.AnalyzeMpeg(Fixture);
        Node udta = Child(report.Root, "MP4", "moov", UserData);

        // An unlisted © atom that fills itself with items is read as text, and one that does not is an unread box.
        Node unlisted = Child(udta, "©xyz");
        Assert.Equal("Unlisted text atom", unlisted.Value?.ToString());
        Assert.DoesNotContain(Child(udta, "©qqq").Children, c => c.Name == "Text Item");
        Assert.DoesNotContain(Child(udta, "WLOC").Children, c => c.Name == "Text Item");
        Assert.Equal("Hand-built genre", Child(udta, "meta", "iTunes Metadata", "Genre").Value?.ToString());

        Node trackTitle = Child(report.Root, "MP4", "moov", "trak", UserData, "Title");
        Assert.Equal("Track title", trackTitle.Value?.ToString());
    }

    [Fact]
    public void MalformedSizesAreFindingsAndTheTextThatIsThereIsShown()
    {
        Report report = Fixtures.AnalyzeMpeg(Fixture);
        Node udta = Child(report.Root, "MP4", "moov", UserData);

        Finding size = Assert.Single(report.Findings, f => f.Code == "mp4.text-size-invalid");
        Assert.Contains("states 40 bytes, but 5 follow", size.Message, StringComparison.Ordinal);
        Node cut = Child(udta, "Comment").Children.Last(c => c.Name == "Text Item");
        Assert.Equal("short", cut.Value?.ToString());
        Assert.Equal(cut.Offset, size.Node!.Offset);
        Finding tail = Assert.Single(report.Findings, f => f.Code == "mp4.text-item-truncated");
        Assert.Equal(2, tail.Node!.Length);
        Assert.Equal("Unused Bytes", Child(udta, "Copyright").Children.Last().Name);
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
        byte[] data = MovieWithUserData(TextAtom("nam", new byte[items * 4]));

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        Report report = Analyze(data);
        timer.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Node title = Child(report.Root, "MP4", "moov", UserData, "Title");
        Assert.Equal(1000, title.Children.Count(c => c.Name == "Text Item"));
        Assert.Equal(NodeValue.Integer(items - 1000, "items", "item"), Child(title, "More Text Items").Value);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"Took {timer.Elapsed}.");
        Assert.True(allocated < 30_000_000, $"Allocated {allocated / 1_000_000} MB.");
    }

    // 3,000 atoms of 1,000 items each (12 MB): the items are listed against one budget for the file, and the atoms past the tree's node
    // budget build no item nodes at all. Listing every item once took 2.9 GB of allocations and 7 s for 10 MB.
    [Fact]
    public void ManyAtomsOfManyItemsStayWithinOneBudget()
    {
        const int atoms = 3000;
        byte[][] all = [.. Enumerable.Range(0, atoms).Select(_ => TextAtom("nam", new byte[1000 * 4]))];
        byte[] data = MovieWithUserData(all);

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        Report report = Analyze(data);
        timer.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.InRange(Count(report.Root, "Text Item"), 1, 10_000);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"Took {timer.Elapsed}.");
        Assert.True(allocated < 50_000_000, $"Allocated {allocated / 1_000_000} MB.");
    }

    // 100,000 atoms of 4 items (2.4 MB) take the tree past its node budget: the atoms there build no item nodes, and the items are still walked.
    [Fact]
    public void AtomsPastTheNodeBudgetBuildNoItems()
    {
        byte[][] all = [.. Enumerable.Range(0, 100_000).Select(_ => TextAtom("nam", [0, 0, 0, 0, 0, 1, 0, 0, 65, 0, 0, 0, 0, 0, 0, 0]))];

        Report report = Analyze(MovieWithUserData(all));

        Assert.Contains(report.Findings, f => f.Code == "mp4.node-limit");
        Assert.InRange(Count(report.Root, "Text Item"), 1, 10_000);
    }

    // A udta box that its parent cuts short is a tag for the atoms inside the bytes that are left, not for the top-level atom that follows.
    [Fact]
    public void AUserDataBoxCutByItsParentIsNamedFromWhatIsLeft()
    {
        byte[] udta = Mp4Builder.Box("udta", Mp4Builder.Box("WLOC", [0, 10, 0, 20]));
        Mp4Builder.U32(udta.Length + 19).CopyTo(udta, 0);
        byte[] after = TextAtom("nam", [0, 7, 0, 0, .. "outside"u8.ToArray()]);
        Assert.Equal(19, after.Length);
        byte[] data = Mp4Builder.Join(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd(), udta), after);

        Report report = Analyze(data);

        Assert.NotEqual(NodeKind.Tag, Child(report.Root, "MP4", "moov", "udta").Kind);
        Assert.Empty(ReportSummary.From(report).Tags);
        Assert.Contains(report.Findings, f => f.Code == "mp4.box-overruns-parent");
    }

    // The node budget is spent before the udta box, so its 2,000 atoms of 1,000 items build no nodes and take none of the item budget: it
    // allocates no more than the same file with atoms of no items (building the nodes the budget allows costs 14 MB more).
    [Fact]
    public void AUserDataBoxPastTheNodeBudgetBuildsNothing()
    {
        byte[][] free = [.. Enumerable.Range(0, 90_000).Select(_ => Mp4Builder.Box("free", []))];
        (Report report, long many) = Measure(free, 1000);
        (_, long none) = Measure(free, 1);

        Assert.Contains(report.Findings, f => f.Code == "mp4.node-limit");
        Assert.Equal(0, Count(report.Root, "Text Item"));
        Assert.True(many < none + 5_000_000, $"Allocated {many / 1_000_000} MB, against {none / 1_000_000} MB for atoms of one item.");
    }

    private static (Report Report, long Allocated) Measure(byte[][] free, int items)
    {
        byte[][] atoms = [.. Enumerable.Range(0, 2000).Select(_ => TextAtom("nam", new byte[items * 4]))];
        byte[] data = Mp4Builder.Join(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd(), Mp4Builder.Join(free), Mp4Builder.Box("udta", atoms)));
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Report report = Analyze(data);
        return (report, GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    // A tag whose children are built on demand is not opened by the summary; a tag held in memory is searched for the tags in it.
    [Fact]
    public void TheSummaryLeavesChildrenBuiltOnDemandAlone()
    {
        var root = new Node("File", 0, 10);
        root.Add(new Node("Lazy", 0, 5, null, () => throw new InvalidOperationException("built")) { Kind = NodeKind.Tag });
        Node held = root.Add(new Node("Held", 5, 5) { Kind = NodeKind.Tag });
        held.Add(new Node("Container", 5, 5)).Add(new Node("Nested", 5, 5) { Kind = NodeKind.Tag });
        Node inLazy = root.Add(new Node("Detail", 0, 1));
        inLazy.Add(new Node("Other", 0, 1, null, () => throw new InvalidOperationException("built")) { Kind = NodeKind.Tag });

        Assert.Equal(["Lazy", "Held", "Nested", "Other"], ReportSummary.From(new Report("x", root, [])).Tags);
        Assert.True(root.Children[0].AreChildrenBuiltOnDemand);
        Assert.False(held.AreChildrenBuiltOnDemand);
    }

    [Fact]
    public void ABlockedTagAndAHostWrappedTagAreBuiltOnDemand()
    {
        Node blocked = Fixtures.AnalyzeMpeg("ogg/packet-spanning-pages.spx").Root.Children.Single().Children.Single(c => c.Name == "Logical Stream").Children.Single(c => c.Kind == NodeKind.Tag);
        Node wrapped = Fixtures.AnalyzeMpeg("mpeg/external-lame-tagged.mp3").Root.Children.First(c => c.Kind == NodeKind.Tag);

        Assert.True(blocked.AreChildrenBuiltOnDemand);
        Assert.True(wrapped.AreChildrenBuiltOnDemand);
    }

    // An unlisted © atom is text only where every one of its items fits: past the first thousand as well.
    [Fact]
    public void AnUnlistedAtomIsCheckedToItsEnd()
    {
        byte[] items = new byte[1001 * 4];
        byte[] junk = [.. items, 0xFF, 0xFF, 0, 0, 1, 2, 3, 4];
        Report report = Analyze(MovieWithUserData(TextAtom("xyz", junk), TextAtom("nam", [0, 1, 0, 0, 65])));

        Node udta = Child(report.Root, "MP4", "moov", UserData);
        Assert.DoesNotContain(Child(udta, "©xyz").Children, c => c.Name == "Text Item");
        Assert.DoesNotContain(report.Findings, f => f.Code is "mp4.text-size-invalid" or "mp4.text-item-truncated");
        Node fitting = Child(Analyze(MovieWithUserData(TextAtom("xyz", items))).Root, "MP4", "moov", "udta", "©xyz");
        Assert.Equal(1000, fitting.Children.Count(c => c.Name == "Text Item"));
        Assert.Contains(fitting.Children, c => c.Name == "More Text Items");
    }

    [Fact]
    public void AFileCutAtEveryByteGivesFindingsNotExceptions()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, Fixture));
        for (int cut = 0; cut <= file.Length; cut++)
        {
            Report report = Analyze(file[..cut]);
            Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        }
    }

    [Fact]
    public void AtomsCutAtEveryByteGiveFindingsNotExceptions()
    {
        byte[] text = Mp4Builder.Join([0, 4, 0x15, 0xC7], "abcd"u8.ToArray());
        foreach (int cut in Enumerable.Range(0, text.Length + 1))
        {
            Assert.DoesNotContain(Analyze(MovieWithUserData(TextAtom("cmt", text[..cut]))).Findings, f => f.Code == "parse.failed");
        }
    }
}
