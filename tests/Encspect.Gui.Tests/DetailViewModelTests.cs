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
        Assert.Equal("MPEG-1 Layer III · 44.1 kHz · Joint stereo · 0:00.5 · 128 kbps CBR · LAME 3.100 (high)", model.StreamsText);
        Assert.NotNull(model.Document);
        Assert.True(model.HasNoFindings);
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
