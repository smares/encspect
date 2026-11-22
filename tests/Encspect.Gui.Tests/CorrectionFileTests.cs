using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Encspect.Core;
using Encspect.Gui.Cache;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The GUI's side of correction files (docs/design/correction-files.md, "The GUI"): a pair is one row in the file list, one detail window with a
// second top-level row in the structure tree and the companion's own bytes in the hex view, and one summary cache key that also holds the companion.
public sealed class CorrectionFileTests : IDisposable
{
    private const string Version = "1.0.0+test";

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    // A pair of the fixtures copied under the names the host looks for: x.wv and x.wvc, x.ofs and x.ofc.
    private (string Main, string Companion) WavPack(string main, string companion, string name = "song")
        => (_folder.Copy(Fixtures.WavPack(main), name + ".wv"), _folder.Copy(Fixtures.WavPack(companion), name + ".wvc"));

    private (string Main, string Companion) OptimFrog(string main, string companion, string name = "song")
        => (_folder.Copy(Path.Combine(Fixtures.Directory, "optimfrog", main), name + ".ofs"), _folder.Copy(Path.Combine(Fixtures.Directory, "optimfrog", companion), name + ".ofc"));

    private (string Main, string Companion) Consistent(string name = "song") => WavPack("external-hybrid-b4-correction.wv", "external-hybrid-b4-correction-wvc.wvc", name);

    private (string Main, string Companion) Broken(string name = "song") => WavPack("built-pair-checksum-broken.wv", "built-pair-checksum-broken-wvc.wvc", name);

    private static FileRow Row(string path)
    {
        var ui = new TestDispatcher();
        var model = new MainViewModel(Analyzer.CreateDefault(), ui.Post, parallelism: 1, roots: []);
        model.Open(FileSource.ForItems([path]));
        ui.Until(() => !model.IsRunning);
        return Assert.Single(model.Rows);
    }

    private static DetailViewModel Load(string path, out TestDispatcher ui)
    {
        ui = new TestDispatcher();
        var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(path);
        TestDispatcher dispatcher = ui;
        dispatcher.Until(() => load.IsCompleted);
        return model;
    }

    [Fact]
    public void The_file_list_keeps_a_row_per_file_and_the_main_rows_codec_tooltip_gives_the_pairing()
    {
        (string main, string companion) = Consistent();
        var ui = new TestDispatcher();
        var model = new MainViewModel(Analyzer.CreateDefault(), ui.Post, parallelism: 1, roots: []);
        model.Open(FileSource.ForFolder(_folder.Path));
        ui.Until(() => !model.IsRunning);

        // The correction file keeps a row of its own, as a folder walk analyzes every file.
        Assert.Equal(["song.wv", "song.wvc"], model.Rows.Select(r => r.Name));
        FileRow row = model.Rows[0];
        Assert.Equal("Correction file: consistent (song.wvc), lossless with its correction file", row.CodecTip);
        Assert.Null(model.Rows[1].CodecTip);
        Assert.True(File.Exists(companion) && File.Exists(main));
    }

    [Fact]
    public void The_hex_header_is_announced_visible_when_a_pair_is_opened_and_again_after_a_refresh()
    {
        (string main, _) = Consistent();
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        var last = new Dictionary<string, object?>();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DetailViewModel.HasHexTitle) or nameof(DetailViewModel.HexTitle))
            {
                last[e.PropertyName] = e.PropertyName == nameof(DetailViewModel.HasHexTitle) ? model.HasHexTitle : model.HexTitle;
            }
        };

        Task load = model.LoadAsync(main);
        ui.Until(() => load.IsCompleted);
        Assert.Equal((true, "song.wv"), ((bool)last[nameof(DetailViewModel.HasHexTitle)]!, (string?)last[nameof(DetailViewModel.HexTitle)]));

        last.Clear();
        Task reload = model.ReloadAsync();
        ui.Until(() => reload.IsCompleted);
        Assert.True(last.TryGetValue(nameof(DetailViewModel.HasHexTitle), out object? shown) && (bool)shown!);
    }

    [AvaloniaFact]
    public void The_detail_window_shows_the_hex_header_naming_the_file_on_open_and_the_companion_after_selection()
    {
        (string main, _) = Consistent();
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1100, Height = 760 };
        window.Show();
        Task load = window.Model.LoadAsync(main);
        UiPump.Until(() => load.IsCompleted);

        TextBlock title = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "HexTitle");
        Assert.True(title.IsVisible);
        Assert.Equal("song.wv", title.Text);

        window.Model.Tree!.Expand(window.Model.Tree.Roots[1]);
        window.Model.SelectedNode = window.Model.Tree[window.Model.Tree.Count - 1];
        UiPump.Until(() => true);
        Assert.Equal("song.wvc", title.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void A_file_without_a_companion_shows_no_hex_header()
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1100, Height = 760 };
        window.Show();
        Task load = window.Model.LoadAsync(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"));
        UiPump.Until(() => load.IsCompleted);

        Assert.False(window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "HexTitle").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void The_codec_cell_of_the_file_list_carries_the_pairing_as_its_tooltip()
    {
        Consistent();
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), new MainViewModel(Analyzer.CreateDefault(), roots: []));
        main.Show();
        main.Model.Open(FileSource.ForFolder(_folder.Path));
        UiPump.Until(() => !main.Model.IsRunning);
        UiPump.Until(() => true);

        TextBlock cell = main.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == main.Model.Rows[0].Codec && ToolTip.GetTip(t) is string);
        Assert.Equal("Correction file: consistent (song.wvc), lossless with its correction file", ToolTip.GetTip(cell));
        main.Close();
    }

    [Fact]
    public void A_pair_whose_companion_is_damaged_counts_the_companions_findings_with_the_files()
    {
        (string main, _) = Broken();
        Report report = Analyzer.CreateDefault().AnalyzeFile(main);
        List<Finding> companions = [.. report.Companions.SelectMany(c => c.Report.Findings)];
        Assert.NotEmpty(companions);

        FileRow row = Row(main);

        int errors = report.Findings.Count(f => f.Severity == Severity.Error) + companions.Count(f => f.Severity == Severity.Error);
        int warnings = report.Findings.Count(f => f.Severity == Severity.Warning) + companions.Count(f => f.Severity == Severity.Warning);
        int infos = report.Findings.Count(f => f.Severity == Severity.Info) + companions.Count(f => f.Severity == Severity.Info);
        Assert.Equal((errors, warnings, infos), (row.Errors, row.Warnings, row.Infos));
        Assert.Contains("inconsistent (song.wvc:", row.CodecTip, StringComparison.Ordinal);
    }

    [Fact]
    public void An_optimfrog_pair_reads_the_same()
    {
        (string ofs, _) = OptimFrog("external-ofs-mono-quality3.ofs", "external-ofs-mono-quality3-correction-data.ofc");

        Assert.Equal("Correction file: consistent (song.ofc), lossless with its correction file", Row(ofs).CodecTip);
    }

    [Fact]
    public void A_file_without_a_correction_concept_has_no_tooltip()
    {
        string file = _folder.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "a.mp3");

        Assert.Null(Row(file).CodecTip);
    }

    [Fact]
    public void The_detail_windows_streams_line_shows_the_pairing()
    {
        (string main, _) = Consistent();
        using DetailViewModel model = Load(main, out _);

        Assert.EndsWith("Correction file: consistent (song.wvc), lossless with its correction file", model.StreamsText, StringComparison.Ordinal);
        Assert.Contains(" · hybrid coding (lossless with its correction file) · ", model.StreamsText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_structure_tree_gets_a_second_top_level_row_named_for_the_correction_file()
    {
        (string main, _) = Consistent();
        using DetailViewModel model = Load(main, out _);
        StructureTree tree = model.Tree!;

        Assert.Equal(2, tree.Roots.Count);
        TreeRow second = tree.Roots[1];
        Assert.Equal(("Correction File", "song.wvc", 1, 0), (second.Name, second.Value, second.RootIndex, second.Depth));
        Assert.Same(model.Report!.Companions[0].Report.Root, second.Node);
        Assert.Equal(tree.Count - 1, tree.IndexOf(second));
        Assert.Same(second, tree[tree.Count - 1]);
        Assert.False(second.IsExpanded);

        // Opening it lists its own rows below it, and the main file's rows stay where they were.
        int before = tree.Count;
        int mainRows = tree.IndexOf(second);
        tree.Expand(second);
        Assert.True(tree.Count > before);
        Assert.Equal(mainRows, tree.IndexOf(second));
        TreeRow child = tree[mainRows + 1];
        Assert.Equal(1, child.RootIndex);
        Assert.Equal(mainRows + 1, tree.IndexOf(child));
        Assert.Same(tree.Root, tree[0]);
    }

    [Fact]
    public void The_hex_view_shows_the_file_of_the_selected_nodes_tree_and_names_it()
    {
        (string main, string companion) = Consistent();
        using DetailViewModel model = Load(main, out _);
        StructureTree tree = model.Tree!;
        HexDocument mainDocument = model.Document!;
        Assert.Equal(new FileInfo(main).Length, mainDocument.Length);
        Assert.Equal("song.wv", model.HexTitle);

        tree.Expand(tree.Roots[1]);
        TreeRow inside = tree[tree.Count - 1];
        Assert.Equal(1, inside.RootIndex);
        model.SelectedNode = inside;

        Assert.NotSame(mainDocument, model.Document);
        Assert.Equal(new FileInfo(companion).Length, model.Document!.Length);
        Assert.Equal("song.wvc", model.HexTitle);
        Assert.True(model.HasHexTitle);
        byte[] bytes = new byte[4];
        Assert.Equal(4, model.Document.Read(0, bytes));
        Assert.Equal(File.ReadAllBytes(companion)[..4], bytes);

        model.SelectedNode = tree.Root;
        Assert.Same(mainDocument, model.Document);
        Assert.Equal("song.wv", model.HexTitle);
    }

    [Fact]
    public void A_file_without_companions_has_no_hex_header()
    {
        using DetailViewModel model = Load(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), out _);

        Assert.Null(model.HexTitle);
        Assert.False(model.HasHexTitle);
        Assert.Single(model.Tree!.Roots);
    }

    [Fact]
    public void The_companions_findings_are_listed_and_select_their_node_in_the_companions_tree()
    {
        (string main, _) = Broken();
        using DetailViewModel model = Load(main, out _);
        FindingItem[] fromCompanion = [.. model.Findings.Where(f => f.RootIndex == 1)];
        Assert.NotEmpty(fromCompanion);
        Assert.All(fromCompanion, f => Assert.EndsWith("· song.wvc", f.Detail, StringComparison.Ordinal));
        Assert.Equal(model.Report!.Findings.Count + model.Report.Companions[0].Report.Findings.Count, model.ErrorCount + model.WarningCount + model.InfoCount);

        FindingItem withPath = fromCompanion.First(f => f.Path is not null);
        model.SelectedFinding = withPath;

        Assert.Equal(1, model.SelectedNode!.RootIndex);
        Assert.Equal("song.wvc", model.HexTitle);
        Assert.Equal(withPath.Finding.Node!.Offset, model.SelectedNode.Node.Offset);
    }

    [Fact]
    public void A_reload_selects_the_same_place_in_the_companions_tree()
    {
        (string main, _) = Consistent();
        using DetailViewModel model = Load(main, out TestDispatcher ui);
        model.Tree!.Expand(model.Tree.Roots[1]);
        TreeRow inside = model.Tree[model.Tree.Count - 1];
        model.SelectedNode = inside;

        Task reload = model.ReloadAsync();
        ui.Until(() => reload.IsCompleted);

        Assert.Equal((1, inside.Name), (model.SelectedNode!.RootIndex, model.SelectedNode.Name));
        Assert.Equal("song.wvc", model.HexTitle);
    }

    [Fact]
    public void Decode_Audio_decodes_the_pair_and_keeps_its_companion_and_the_selection()
    {
        (string main, _) = Consistent();
        using DetailViewModel model = Load(main, out TestDispatcher ui);
        model.Tree!.Expand(model.Tree.Roots[1]);
        model.SelectedNode = model.Tree[model.Tree.Count - 1];
        Assert.True(model.CanDecodeAudio);

        Task decode = model.DecodeAudioAsync();
        ui.Until(() => decode.IsCompleted);

        Assert.Null(model.CommandError);
        Assert.NotEqual(DecodeStatus.NotRequested, model.Report!.Streams[0].Verification!.Decoding);
        Assert.Single(model.Report.Companions);
        Assert.Equal(2, model.Tree.Roots.Count);
        Assert.Equal(1, model.SelectedNode!.RootIndex);
        Assert.Equal("song.wvc", model.HexTitle);
        Assert.Contains("Correction file: consistent (song.wvc)", model.StreamsText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cache_key_holds_the_companion_so_a_changed_added_or_removed_correction_file_analyzes_the_pair_again()
    {
        (string main, string companion) = Consistent();
        using SummaryCache cache = new(_folder["cache.bin"], Version);

        Assert.False(Run(cache).IsCached);
        Assert.True(Run(cache).IsCached);

        // Changed in place, with the size and the time kept, so only the first and last 64 KiB tell.
        DateTime time = File.GetLastWriteTimeUtc(companion);
        byte[] bytes = File.ReadAllBytes(companion);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(companion, bytes);
        File.SetLastWriteTimeUtc(companion, time);
        FileRow changed = Run(cache);
        Assert.False(changed.IsCached);
        Assert.True(Run(cache).IsCached);

        // Removed: the pair is now missing its correction file, which the row says.
        File.Delete(companion);
        FileRow removed = Run(cache);
        Assert.False(removed.IsCached);
        Assert.Contains("missing (song.wvc)", removed.CodecTip, StringComparison.Ordinal);
        FileRow missing = Run(cache);
        Assert.True(missing.IsCached);
        Assert.Contains("missing (song.wvc)", missing.CodecTip, StringComparison.Ordinal);

        // Added again beside a file whose cached summary says it was absent.
        File.WriteAllBytes(companion, bytes);
        FileRow added = Run(cache);
        Assert.False(added.IsCached);
        Assert.True(File.Exists(main));

        FileRow Run(SummaryCache c)
        {
            var ui = new TestDispatcher();
            var model = new MainViewModel(Analyzer.CreateDefault(), ui.Post, parallelism: 1, roots: [], cache: c);
            model.Open(FileSource.ForItems([main]));
            ui.Until(() => !model.IsRunning);
            return Assert.Single(model.Rows);
        }
    }

    [Fact]
    public void A_file_with_no_correction_file_beside_it_is_keyed_by_its_absence_and_the_store_keeps_it()
    {
        (string main, string companion) = Consistent();
        File.Delete(companion);
        FileSummary summary = FileSummary.From(Analyzer.CreateDefault().AnalyzeFile(main));
        Assert.Equal([companion], summary.CompanionPaths);
        Assert.Contains("missing (song.wvc)", summary.Correction, StringComparison.Ordinal);

        FileFingerprint fingerprint = FileFingerprint.Read(main)!;
        using (var cache = new SummaryCache(_folder["cache.bin"], Version))
        {
            cache.Put(main, fingerprint, summary);
        }

        using var later = new SummaryCache(_folder["cache.bin"], Version);
        FileSummary? read = later.TryGet(main, fingerprint);
        Assert.NotNull(read);
        Assert.Equal(summary.Correction, read.Correction);
        Assert.Equal(summary.CompanionPaths, read.CompanionPaths);

        File.WriteAllBytes(companion, [0]);
        Assert.Null(later.TryGet(main, fingerprint));
    }

    [Fact]
    public void An_optimfrog_pair_is_keyed_by_its_correction_file_too()
    {
        (string ofs, string ofc) = OptimFrog("external-ofs-mono-quality3.ofs", "external-ofs-mono-quality3-correction-data.ofc");
        FileSummary summary = FileSummary.From(Analyzer.CreateDefault().AnalyzeFile(ofs));
        Assert.Equal([ofc], summary.CompanionPaths);

        FileFingerprint fingerprint = FileFingerprint.Read(ofs)!;
        using var cache = new SummaryCache(_folder["cache.bin"], Version);
        cache.Put(ofs, fingerprint, summary);
        Assert.NotNull(cache.TryGet(ofs, fingerprint));

        File.Delete(ofc);
        Assert.Null(cache.TryGet(ofs, fingerprint));
    }

    [Fact]
    public void A_summary_that_a_companion_contradicts_is_not_kept()
    {
        (string main, string companion) = Consistent();
        FileSummary consistent = FileSummary.From(Analyzer.CreateDefault().AnalyzeFile(main));
        FileFingerprint fingerprint = FileFingerprint.Read(main)!;
        using var cache = new SummaryCache(_folder["cache.bin"], Version);

        // Written to after the analysis began.
        DateTime written = File.GetLastWriteTimeUtc(companion);
        cache.Put(main, fingerprint, consistent, written - TimeSpan.FromSeconds(1));
        Assert.Equal(0, cache.Count);
        cache.Put(main, fingerprint, consistent, written + TimeSpan.FromMinutes(1));
        Assert.Equal(1, cache.Count);

        // Longer than the report read it.
        using var other = new SummaryCache(_folder["other.bin"], Version);
        File.AppendAllText(companion, "x");
        other.Put(main, fingerprint, consistent);
        Assert.Equal(0, other.Count);

        // Present now, where the report found none.
        File.Delete(companion);
        FileSummary missing = FileSummary.From(Analyzer.CreateDefault().AnalyzeFile(main));
        File.WriteAllBytes(companion, [1]);
        other.Put(main, fingerprint, missing);
        Assert.Equal(0, other.Count);
    }

    [Fact]
    public void The_changed_file_notice_names_the_file_that_changed()
    {
        (string main, string companion) = Consistent();
        using DetailViewModel model = Load(main, out _);
        model.Tree!.Expand(model.Tree.Roots[1]);
        File.AppendAllText(companion, "more");
        model.SelectedNode = model.Tree[model.Tree.Count - 1];
        model.Document!.Read(0, new byte[4]);

        Assert.True(model.HasFileChanged);
        Assert.StartsWith("Changed: song.wvc.", model.FileChangedTip, StringComparison.Ordinal);
    }
}
