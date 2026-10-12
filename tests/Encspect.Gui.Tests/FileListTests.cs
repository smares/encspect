using System.ComponentModel;
using Avalonia.Collections;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Host;

namespace Encspect.Gui.Tests;

public class FileListTests
{
    private static readonly string[] Names = ["external-lame-cbr128-joint.mp3", "external-lame-vbr-v2.mp3", "damaged-bitrate.mp3"];

    [Fact]
    public void Rows_fill_in_as_the_background_analysis_reaches_them()
    {
        using var temp = new TempFolder();
        foreach (string name in Names)
        {
            temp.Copy(Fixtures.Mpeg(name), name);
        }

        temp.Write("notes.txt", "not audio"u8.ToArray());
        var ui = new TestDispatcher();
        var model = new MainViewModel(Analyzer.CreateDefault(), ui.Post, parallelism: 2, roots: []);

        model.Open(FileSource.ForFolder(temp.Path));
        Assert.True(model.IsRunning);
        ui.Until(() => !model.IsRunning);

        Assert.Equal(["damaged-bitrate.mp3", "external-lame-cbr128-joint.mp3", "external-lame-vbr-v2.mp3", "notes.txt"], model.Rows.Select(r => r.Name));
        Assert.All(model.Rows, r => Assert.Equal(FileState.Analyzed, r.State));
        FileRow lame = model.Rows[1];
        Assert.Equal("MPEG Audio", lame.Format);
        Assert.Equal("MPEG-1 Layer III", lame.Codec);
        Assert.Equal("LAME 3.100 (high)", lame.EncoderText);
        Assert.Equal("128 kbps CBR", lame.BitrateText);
        Assert.Equal("44.1 kHz", lame.SampleRateText);
        Assert.EndsWith("VBR", model.Rows[2].BitrateText, StringComparison.Ordinal);
        Assert.Equal("Unknown", model.Rows[3].FormatText);
        Assert.Equal(4, model.Analyzed);
        Assert.Equal("4 files", model.Status);
    }

    // The list reads streams, so MP4 and raw AAC fill in as MPEG audio does.
    [Fact]
    public void Mp4_and_aac_files_fill_in_from_their_streams()
    {
        using var temp = new TempFolder();
        temp.Copy(Fixtures.Aac("fdkaac-he-v2.m4a"), "he-v2.m4a");
        temp.Copy(Fixtures.Aac("ffmpeg-adts.aac"), "lc.aac");
        var ui = new TestDispatcher();
        var model = new MainViewModel(Analyzer.CreateDefault(), ui.Post, parallelism: 2, roots: []);

        model.Open(FileSource.ForFolder(temp.Path));
        ui.Until(() => !model.IsRunning);

        FileRow he = model.Rows.Single(r => r.Name == "he-v2.m4a");
        Assert.Equal(("MP4", "HE-AAC v2", "44.1 kHz", "Parametric stereo"), (he.Format, he.Codec, he.SampleRateText, he.ChannelMode));
        Assert.Equal("fdkaac 1.0.0 (libfdk-aac 4.0.1) (low)", he.EncoderText);
        Assert.Contains("iTunes Metadata", he.Tags, StringComparison.Ordinal);
        FileRow lc = model.Rows.Single(r => r.Name == "lc.aac");
        Assert.Equal(("AAC", "AAC LC (no SBR signaled explicitly)", "FFmpeg (Lavc60.31.102) (high)"), (lc.Format, lc.Codec, lc.EncoderText));
        Assert.EndsWith("VBR", lc.BitrateText, StringComparison.Ordinal);
    }

    [Fact]
    public void Include_subfolders_analyzes_the_whole_tree()
    {
        using var temp = new TempFolder();
        temp.Copy(Fixtures.Mpeg(Names[0]), "top.mp3");
        temp.Copy(Fixtures.Mpeg(Names[1]), "disc 1/track.mp3");
        var ui = new TestDispatcher();
        var model = new MainViewModel(Analyzer.CreateDefault(), ui.Post, roots: []);

        model.Open(FileSource.ForFolder(temp.Path));
        ui.Until(() => !model.IsRunning);
        Assert.Equal(["top.mp3"], model.Rows.Select(r => r.Name));

        model.IncludeSubfolders = true;
        ui.Until(() => !model.IsRunning);
        Assert.Equal([Path.Join("disc 1", "track.mp3"), "top.mp3"], model.Rows.Select(r => r.Name));
        Assert.All(model.Rows, r => Assert.Equal("MPEG Audio", r.Format));
    }

    [Fact]
    public void Cancel_stops_the_run_and_leaves_the_rest_not_analyzed()
    {
        using var temp = new TempFolder();
        temp.Copy(Fixtures.Mpeg(Names[0]), "a.mp3");
        for (int i = 0; i < 5; i++)
        {
            temp.Write($"block{i}.bin", BlockingParser.File);
        }

        var parser = new BlockingParser();
        var ui = new TestDispatcher();
        var model = new MainViewModel(parser.Analyzer(), ui.Post, parallelism: 2, roots: []);

        model.Open(FileSource.ForFolder(temp.Path));
        ui.Until(() => parser.Started == 2 && model.Rows.Count == 6);
        model.Cancel();
        ui.Until(() => !model.IsRunning);

        Assert.Equal(2, parser.Canceled);
        Assert.Equal(2, parser.Started);
        Assert.Contains(model.Rows, r => r.State == FileState.Canceled);
        Assert.DoesNotContain(model.Rows, r => r.State == FileState.Waiting);
        Assert.StartsWith("Stopped after", model.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_another_folder_cancels_the_run_before_it()
    {
        using var first = new TempFolder();
        using var second = new TempFolder();
        first.Write("block.bin", BlockingParser.File);
        second.Copy(Fixtures.Mpeg(Names[0]), "song.mp3");
        var parser = new BlockingParser();
        var ui = new TestDispatcher();
        var model = new MainViewModel(parser.Analyzer(), ui.Post, roots: []);

        model.Open(FileSource.ForFolder(first.Path));
        ui.Until(() => parser.Started == 1);
        Task firstRun = model.Completion;
        model.Open(FileSource.ForFolder(second.Path));
        ui.Until(() => !model.IsRunning && firstRun.IsCompleted);

        Assert.Equal(1, parser.Canceled);
        Assert.Equal("song.mp3", Assert.Single(model.Rows).Name);
        Assert.Equal("MPEG Audio", model.Rows[0].Format);
        Assert.Equal("1 file", model.Status);
    }

    [Fact]
    public void A_file_whose_analysis_fails_unexpectedly_says_so_and_the_run_goes_on()
    {
        using var temp = new TempFolder();
        temp.Write("a-boom.bin", ThrowingParser.File);
        temp.Copy(Fixtures.Mpeg(Names[0]), "b.mp3");
        var ui = new TestDispatcher();
        var model = new MainViewModel(new ThrowingParser().Analyzer(), ui.Post, parallelism: 1, roots: []);

        model.Open(FileSource.ForFolder(temp.Path));
        ui.Until(() => !model.IsRunning);

        Assert.Equal("Not read", model.Rows[0].FormatText);
        Assert.Equal("The analysis failed: " + ThrowingParser.Message, model.Rows[0].Problem);
        Assert.Equal("MPEG Audio", model.Rows[1].Format);
        Assert.Equal("2 files", model.Status);
        Assert.Null(model.Error);
    }

    [Fact]
    public void Opening_a_source_outside_the_folder_tree_clears_its_selection()
    {
        using var first = new TempFolder();
        using var second = new TempFolder();
        var ui = new TestDispatcher();
        var model = new MainViewModel(Analyzer.CreateDefault(), ui.Post, roots: []);
        var folder = new FolderItem("first", first.Path, FolderKind.Folder, ui.Post);
        model.SelectedFolder = folder;
        Assert.Equal(first.Path, model.Source!.Folder);

        model.Open(FileSource.ForFolder(second.Path));
        Assert.Null(model.SelectedFolder);
        Assert.Equal(second.Path, model.Source!.Folder);

        // Choosing the folder again opens it again.
        model.SelectedFolder = folder;
        Assert.Equal(first.Path, model.Source!.Folder);
        ui.Until(() => !model.IsRunning);
    }

    [Fact]
    public void Listing_the_folder_again_keeps_the_selected_file_selected()
    {
        using var temp = new TempFolder();
        foreach (string name in Names)
        {
            temp.Copy(Fixtures.Mpeg(name), name);
        }

        var ui = new TestDispatcher();
        var model = new MainViewModel(Analyzer.CreateDefault(), ui.Post, roots: []);
        model.Open(FileSource.ForFolder(temp.Path));
        ui.Until(() => !model.IsRunning);
        model.SelectedRow = model.Rows[1];
        string selected = model.Rows[1].Name;

        foreach (Action again in new Action[] { model.Refresh, () => model.IncludeSubfolders = true, model.ClearCache })
        {
            again();
            ui.Until(() => !model.IsRunning);
            Assert.Same(model.Rows[1], model.SelectedRow);
            Assert.Equal(selected, model.SelectedRow!.Name);
        }

        // Another folder starts without a selection.
        model.Open(FileSource.ForFolder(temp.Path));
        ui.Until(() => !model.IsRunning);
        Assert.Null(model.SelectedRow);
    }

    [Fact]
    public void Rows_keep_the_summary_not_the_report()
    {
        Type[] held = typeof(FileRow).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Select(f => f.FieldType).ToArray();

        Assert.DoesNotContain(typeof(Report), held);
        Assert.DoesNotContain(typeof(Node), held);
        Assert.DoesNotContain(typeof(Report), typeof(FileSummary).GetProperties().Select(p => p.PropertyType));
    }

    [Theory]
    [InlineData(nameof(FileRow.Duration))]
    [InlineData(nameof(FileRow.Bitrate))]
    public void Durations_and_bitrates_sort_as_numbers(string column)
    {
        var model = new MainViewModel(Analyzer.CreateDefault(), new TestDispatcher().Post, roots: []);
        double[] values = [9.5, 100, 10, 2, 1000];
        foreach (double value in values)
        {
            var row = new FileRow($"/{value}", $"{value}");
            row.Apply(Summary(value));
            model.Rows.Add(row);
        }

        model.RowsView.SortDescriptions.Add(DataGridSortDescription.FromPath(column, ListSortDirection.Ascending));
        Assert.Equal([2, 9.5, 10, 100, 1000], Order(model.RowsView, column));

        model.RowsView.SortDescriptions.Clear();
        model.RowsView.SortDescriptions.Add(DataGridSortDescription.FromPath(column, ListSortDirection.Descending));
        Assert.Equal([1000, 100, 10, 9.5, 2], Order(model.RowsView, column));

        // The text the cells show would sort differently: "10" before "2", "1:40" before "0:09.5".
        string text = column == nameof(FileRow.Duration) ? nameof(FileRow.DurationText) : nameof(FileRow.BitrateText);
        model.RowsView.SortDescriptions.Clear();
        model.RowsView.SortDescriptions.Add(DataGridSortDescription.FromPath(text, ListSortDirection.Ascending));
        Assert.NotEqual([2, 9.5, 10, 100, 1000], Order(model.RowsView, column));
    }

    [Fact]
    public void The_findings_column_sorts_by_errors_then_warnings_then_infos()
    {
        var model = new MainViewModel(Analyzer.CreateDefault(), new TestDispatcher().Post, roots: []);
        (int Errors, int Warnings, int Infos)[] counts = [(0, 0, 9), (1, 0, 0), (0, 2, 0), (0, 1, 5)];
        foreach ((int errors, int warnings, int infos) in counts)
        {
            var row = new FileRow($"/{errors}{warnings}{infos}", $"{errors}{warnings}{infos}");
            row.Apply(Summary(1) with { Summary = Summary(1).Summary with { Errors = errors, Warnings = warnings, Infos = infos } });
            model.Rows.Add(row);
        }

        model.RowsView.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(FileRow.FindingsRank), ListSortDirection.Descending));

        Assert.Equal(["100", "020", "015", "009"], model.RowsView.Cast<FileRow>().Select(r => r.Name));
    }

    [Fact]
    public void Neighbors_follow_the_order_the_list_shows()
    {
        var model = new MainViewModel(Analyzer.CreateDefault(), new TestDispatcher().Post, roots: []);
        foreach (double value in new[] { 3.0, 1, 2 })
        {
            var row = new FileRow($"/{value}", $"{value}");
            row.Apply(Summary(value));
            model.Rows.Add(row);
        }

        model.RowsView.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(FileRow.Duration), ListSortDirection.Ascending));
        FileRow first = (FileRow)model.RowsView.GetItemAt(0)!;

        Assert.Equal("1", first.Name);
        Assert.Equal("2", model.Neighbor(first, 1)!.Name);
        Assert.Equal("3", model.Neighbor(first, 2)!.Name);
        Assert.Null(model.Neighbor(first, -1));
        Assert.Null(model.Neighbor(first, 3));
    }

    private static double[] Order(DataGridCollectionView view, string column) =>
        view.Cast<FileRow>().Select(r => column == nameof(FileRow.Duration) ? r.Duration!.Value : r.Bitrate!.Value).ToArray();

    private static FileSummary Summary(double value) => new(
        new ReportSummary("x", "MPEG Audio", 1, "MPEG-1 Layer III", value, 1152, value, 44100, "Stereo", null, null, [], 0, 0, 0),
        IsVbr: false,
        Problem: null);
}
