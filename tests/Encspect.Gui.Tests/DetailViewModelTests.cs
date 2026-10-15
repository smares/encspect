using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Host;

namespace Encspect.Gui.Tests;

public class DetailViewModelTests
{
    [Fact]
    public void Loading_a_file_shows_its_tree_streams_and_findings()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);

        Task load = model.LoadAsync(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"));
        Assert.True(model.IsLoading);
        ui.Until(() => load.IsCompleted);

        Assert.False(model.IsLoading);
        Assert.Equal("external-lame-cbr128-joint.mp3", model.Title);
        Assert.Same(model.Report!.Root, model.Tree!.Root.Node);
        Assert.Same(model.Tree.Root, model.SelectedNode);
        Assert.Equal("MPEG-1 Layer III · 44.1 kHz · Joint stereo · stereo (front left, front right) · 0:00.5 · 128 kbps CBR · LAME 3.100 (high)", model.StreamsText);
        Assert.NotNull(model.Document);
        Assert.True(model.HasNoFindings);
    }

    [Fact]
    public void A_file_that_two_formats_match_offers_the_other()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);

        Task load = model.LoadAsync(Path.Combine(Fixtures.Directory, "ac3", "junk-adts-in-frames.eac3"));
        ui.Until(() => load.IsCompleted);

        Assert.True(model.HasDetectionText);
        Assert.Equal("Also matches:", model.DetectionText);
        AlternativeItem alternative = Assert.Single(model.Alternatives);
        Assert.Equal("AAC", alternative.Format);
        Assert.Contains("class 50", alternative.Evidence, StringComparison.Ordinal);
        Assert.Null(model.ForcedFormat);
    }

    [Fact]
    public void A_plain_file_has_nothing_to_offer()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);

        Task load = model.LoadAsync(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"));
        ui.Until(() => load.IsCompleted);

        Assert.False(model.HasDetectionText);
        Assert.Empty(model.Alternatives);
        Assert.Contains("MPEG Audio", model.FormatNames);
    }

    [Fact]
    public void Analyzing_as_a_format_runs_that_parser_and_stays_until_another_file_is_loaded()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        string path = Fixtures.Mpeg("external-lame-cbr128-joint.mp3");
        Task load = model.LoadAsync(path);
        ui.Until(() => load.IsCompleted);

        Task forced = model.AnalyzeAsAsync("AAC");
        ui.Until(() => forced.IsCompleted);

        Assert.Equal("AAC", model.ForcedFormat);
        Assert.Equal("AAC", model.Report!.Root.Value);
        Assert.Equal("Analyzed as AAC on request; detection would take MPEG Audio.", model.DetectionText);
        Assert.Equal("MPEG Audio", Assert.Single(model.Alternatives).Format);
        Assert.Contains(model.Findings, f => f.Code == "format.forced");
        Assert.DoesNotContain(model.Findings, f => f.Code == "parse.failed");

        Task reload = model.ReloadAsync();
        ui.Until(() => reload.IsCompleted);
        Assert.Equal("AAC", model.Report!.Root.Value);

        Task back = model.AnalyzeAsAsync(null);
        ui.Until(() => back.IsCompleted);
        Assert.Null(model.ForcedFormat);
        Assert.Equal("MPEG Audio", model.Report!.Root.Value);
        Assert.False(model.HasDetectionText);

        Task again = model.AnalyzeAsAsync("ogg");
        ui.Until(() => again.IsCompleted);
        Task other = model.LoadAsync(Fixtures.Mpeg("external-lame-vbr-v2.mp3"));
        ui.Until(() => other.IsCompleted);
        Assert.Null(model.ForcedFormat);
        Assert.Equal("MPEG Audio", model.Report!.Root.Value);
    }

    [Fact]
    public void Every_load_analyzes_the_file_again()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        string path = Fixtures.Mpeg("external-lame-cbr128-joint.mp3");

        Task first = model.LoadAsync(path);
        ui.Until(() => first.IsCompleted);
        Report report = model.Report!;
        Task second = model.LoadAsync(path);
        ui.Until(() => second.IsCompleted);

        Assert.NotSame(report, model.Report);
    }

    [Fact]
    public void Selecting_a_finding_selects_and_reveals_its_node()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Mpeg("damaged-bitrate.mp3"));
        ui.Until(() => load.IsCompleted);
        FindingItem finding = model.Findings.First(f => f.Path is { Count: >= 2 });

        model.SelectedFinding = finding;

        Node expected = model.Report!.Root;
        foreach (int index in finding.Path!)
        {
            expected = expected.Children[index];
        }

        Assert.Same(expected, model.SelectedNode!.Node);
        Assert.True(model.Tree!.IndexOf(model.SelectedNode) > 0);
        Assert.Equal(expected.Name, finding.Finding.Node!.Name);
        Assert.Equal(expected.Offset, finding.Finding.Node.Offset);
        Assert.NotNull(model.Highlight);
        Assert.Equal(expected.Offset, model.Highlight!.Start);
    }

    [Fact]
    public void Severity_filters_hide_and_show_findings()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Mpeg("damaged-bitrate.mp3"));
        ui.Until(() => load.IsCompleted);
        int all = model.Findings.Count;
        int warnings = model.WarningCount;
        Assert.True(warnings > 0);

        model.ShowWarnings = false;
        Assert.Equal(all - warnings, model.Findings.Count);
        Assert.DoesNotContain(model.Findings, f => f.Severity == Severity.Warning);

        model.ShowWarnings = true;
        Assert.Equal(all, model.Findings.Count);
    }

    [Fact]
    public void Selecting_a_node_highlights_its_bytes_and_bits()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"));
        ui.Until(() => load.IsCompleted);
        StructureTree tree = model.Tree!;
        TreeRow stream = tree.Root.RowAt(0);
        tree.Expand(stream);
        TreeRow frame = Enumerable.Range(tree.IndexOf(stream) + 1, stream.VisibleCount).Select(i => tree[i]).First(r => r.Kind == NodeKind.Frame);
        tree.Expand(frame);
        TreeRow header = tree[tree.IndexOf(frame) + 1];
        tree.Expand(header);
        TreeRow version = tree[tree.IndexOf(header) + 1];

        model.SelectedNode = version;

        Assert.Equal(version.Node.Offset, model.Highlight!.Start);
        Assert.Equal(version.Node.BitOffset, model.Highlight.BitOffset);
        Assert.Equal(version.Node.BitLength, model.Highlight.BitLength);
        Assert.Contains("bits", model.SelectionText, StringComparison.Ordinal);
        Assert.Equal(version.Node.BitLength, model.BitStrip!.Marked.Count(m => m));
    }

    [Fact]
    public void A_file_that_cannot_be_read_shows_its_error()
    {
        using var temp = new TempFolder();
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);

        Task load = model.LoadAsync(temp["missing.mp3"]);
        ui.Until(() => load.IsCompleted);

        Assert.Equal(FileReports.UnreadableCode, Assert.Single(model.Findings).Code);
        Assert.Null(model.Document);
    }

    [Fact]
    public void A_folder_shows_what_it_is_and_one_that_could_not_be_listed_why()
    {
        using var temp = new TempFolder();
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);

        Task load = model.LoadAsync(temp.Path);
        ui.Until(() => load.IsCompleted);
        FindingItem finding = Assert.Single(model.Findings);
        Assert.Equal(FileReports.NotRegularCode, finding.Code);
        Assert.Equal("This is a folder, not a regular file, so it was not read.", finding.Message);

        load = model.LoadAsync(temp.Path, "Permission denied");
        ui.Until(() => load.IsCompleted);
        finding = Assert.Single(model.Findings);
        Assert.Equal(FileReports.UnreadableFolderCode, finding.Code);
        Assert.Equal("Permission denied", finding.Message);
    }

    [Fact]
    public void An_analysis_that_fails_unexpectedly_shows_why_and_ends_the_load()
    {
        using var temp = new TempFolder();
        string path = temp.Write("boom.bin", ThrowingParser.File);
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(new ThrowingParser().Analyzer(), ui.Post);

        Task load = model.LoadAsync(path);
        ui.Until(() => load.IsCompleted);

        Assert.False(model.IsLoading);
        Assert.Equal("The analysis failed: " + ThrowingParser.Message, Assert.Single(model.Findings).Message);
    }

    [Fact]
    public void A_reload_that_a_newer_load_replaced_never_shows()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Mpeg("external-lame-vbr-v2.mp3"));
        ui.Until(() => load.IsCompleted);

        Task reload = model.ReloadAsync();
        Task newer = model.LoadAsync(Fixtures.Mpeg("external-blade-stereo.mp3"));
        Thread.Sleep(200);
        ui.Until(() => reload.IsCompleted && newer.IsCompleted);
        ui.RunPending();

        Assert.Equal("external-blade-stereo.mp3", model.Title);
        Assert.Equal(Fixtures.Mpeg("external-blade-stereo.mp3"), model.Report!.Source);
        Assert.Contains("BladeEnc", model.StreamsText, StringComparison.Ordinal);
    }

    [Fact]
    public void Collapsing_a_row_above_the_selection_selects_the_row()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Mpeg("external-lame-vbr-v2.mp3"));
        ui.Until(() => load.IsCompleted);
        StructureTree tree = model.Tree!;
        TreeRow stream = tree.Root.RowAt(0);
        tree.Expand(stream);
        TreeRow frame = tree[tree.IndexOf(stream) + 3];
        tree.Expand(frame);
        TreeRow field = tree[tree.IndexOf(frame) + 1];
        model.SelectedNode = field;

        model.Toggle(stream);

        Assert.False(stream.IsExpanded);
        Assert.Same(stream, model.SelectedNode);

        // A row the selection does not lie below leaves it where it is.
        model.Toggle(stream);
        model.SelectedNode = tree.Root;
        model.Toggle(stream);
        Assert.Same(tree.Root, model.SelectedNode);
    }

    [Fact]
    public async Task Export_writes_the_report_as_the_writers_do()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Mpeg("damaged-bitrate.mp3"));
        ui.Until(() => load.IsCompleted);

        using var text = new MemoryStream();
        await model.ExportAsync(text, ReportFormat.Text);
        using var expected = new StringWriter();
        ReportTextWriter.Write(model.Report!, expected, showOffsets: true);
        Assert.Equal(expected.ToString(), Encoding.UTF8.GetString(text.ToArray()));

        using var json = new MemoryStream();
        await model.ExportAsync(json, ReportFormat.Json);
        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        JsonElement report = Assert.Single(document.RootElement.EnumerateArray().ToList());
        Assert.Equal(ReportJsonWriter.SchemaVersion, report.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(model.Report!.Findings.Count, report.GetProperty("findings").GetArrayLength());

        Assert.Equal(ReportFormat.Json, ReportExport.FormatOf("song.JSON"));
        Assert.Equal(ReportFormat.Text, ReportExport.FormatOf("song.txt"));
    }
}
