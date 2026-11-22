using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Encspect.Gui.Cache;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

public class ShowOnlyAudioTests
{
    private static (MainViewModel Model, TestDispatcher Ui) Open(string folder, SummaryCache? cache = null)
    {
        var ui = new TestDispatcher();
        var model = new MainViewModel(Analyzer.CreateDefault(), ui.Post, parallelism: 2, roots: [], cache: cache);
        model.Open(FileSource.ForFolder(folder));
        ui.Until(() => !model.IsRunning);
        return (model, ui);
    }

    private static string[] Shown(MainViewModel model) => [.. model.RowsView.Cast<FileRow>().Select(r => r.Name)];

    [Fact]
    public void The_switch_hides_the_files_without_audio_and_shows_them_again_and_the_list_counts_them()
    {
        using var temp = new TempFolder();
        temp.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "song.mp3");
        temp.Copy(Fixtures.Flac("built-blocks-repeated.flac"), "lossless.flac");
        temp.Write("notes.txt", "not audio"u8.ToArray());
        temp.Write("empty.bin", []);
        (MainViewModel model, _) = Open(temp.Path);

        Assert.False(model.ShowOnlyAudio);
        Assert.Equal(["empty.bin", "lossless.flac", "notes.txt", "song.mp3"], Shown(model));
        Assert.Equal(0, model.HiddenCount);
        Assert.Equal(string.Empty, model.HiddenText);

        model.ShowOnlyAudio = true;
        Assert.Equal(["lossless.flac", "song.mp3"], Shown(model));
        Assert.Equal(2, model.HiddenCount);
        Assert.True(model.HasHidden);
        Assert.Equal("2 files without audio hidden", model.HiddenText);

        model.ShowOnlyAudio = false;
        Assert.Equal(4, Shown(model).Length);
        Assert.Equal(string.Empty, model.HiddenText);
    }

    [Fact]
    public void The_hidden_files_stay_analyzed_and_listed()
    {
        using var temp = new TempFolder();
        temp.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "song.mp3");
        temp.Write("notes.txt", "not audio"u8.ToArray());
        using var cacheFolder = new TempFolder();
        using var cache = new SummaryCache(cacheFolder["cache.bin"], "test", flushDelay: TimeSpan.FromHours(1));
        var ui = new TestDispatcher();
        var model = new MainViewModel(Analyzer.CreateDefault(), ui.Post, parallelism: 2, roots: [], cache: cache) { ShowOnlyAudio = true };

        model.Open(FileSource.ForFolder(temp.Path));
        ui.Until(() => !model.IsRunning);

        Assert.Equal(["song.mp3"], Shown(model));
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(2, model.Total);
        Assert.Equal(2, model.Analyzed);
        Assert.All(model.Rows, r => Assert.Equal(FileState.Analyzed, r.State));
        Assert.Equal(2, cache.Count);
        Assert.Equal("2 files", model.Status);
        Assert.Equal("1 file without audio hidden", model.HiddenText);
    }

    // The detected format decides, never the name: audio under a .txt name shows, and a text file under an .mp3 name is hidden.
    [Fact]
    public void A_mislabeled_file_is_shown_or_hidden_by_its_content()
    {
        using var temp = new TempFolder();
        temp.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "audio-named.txt");
        temp.Write("text-named.mp3", "just some words, no audio in here"u8.ToArray());
        (MainViewModel model, _) = Open(temp.Path);

        model.ShowOnlyAudio = true;

        Assert.Equal(["audio-named.txt"], Shown(model));
        Assert.False(model.Rows.Single(r => r.Name == "audio-named.txt").HasNoAudio);
        Assert.True(model.Rows.Single(r => r.Name == "text-named.mp3").HasNoAudio);
        Assert.Equal(1, model.HiddenCount);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_not_hidden_as_one_without_audio()
    {
        var unread = new FileRow("x", "x", "The folder cannot be listed.");
        unread.Apply(FileSummary.From(FileReports.UnreadableFolder("x", "The folder cannot be listed.")));

        Assert.False(unread.HasNoAudio);
    }

    [Fact]
    public void A_file_that_turns_out_to_have_no_audio_leaves_the_list_as_its_analysis_arrives()
    {
        using var temp = new TempFolder();
        temp.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "song.mp3");
        temp.Write("notes.txt", "not audio"u8.ToArray());
        var ui = new TestDispatcher();
        var model = new MainViewModel(Analyzer.CreateDefault(), ui.Post, parallelism: 1, roots: []) { ShowOnlyAudio = true };

        model.Open(FileSource.ForFolder(temp.Path));
        ui.Until(() => !model.IsRunning);

        Assert.Equal(["song.mp3"], Shown(model));
        Assert.Equal(1, model.HiddenCount);
    }

    // A detected format without a stream (a damaged header, a parser that keeps its audio out of the streams) says nothing about the audio.
    [Fact]
    public void A_file_with_a_format_and_no_stream_is_not_hidden()
    {
        using var temp = new TempFolder();
        temp.Copy(Path.Combine(Fixtures.Directory, "asf", "broadcast.asf"), "broadcast.asf");
        temp.Copy(Fixtures.Wav("built-format-too-short.wav"), "short.wav");
        temp.Write("notes.txt", "not audio"u8.ToArray());
        (MainViewModel model, _) = Open(temp.Path);

        model.ShowOnlyAudio = true;

        Assert.Equal(["broadcast.asf", "short.wav"], Shown(model));
        Assert.Equal(1, model.HiddenCount);
    }

    // The count is what the list does not show, at every step of a run.
    [Fact]
    public void The_hidden_count_agrees_with_the_rows_shown_during_the_run()
    {
        using var temp = new TempFolder();
        for (int i = 0; i < 40; i++)
        {
            temp.Write($"n{i:D2}.txt", "not audio"u8.ToArray());
            temp.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), $"s{i:D2}.mp3");
        }

        var ui = new TestDispatcher();
        var model = new MainViewModel(Analyzer.CreateDefault(), ui.Post, parallelism: 1, roots: []) { ShowOnlyAudio = true };
        model.Open(FileSource.ForFolder(temp.Path));
        do
        {
            ui.RunPending();
            Assert.Equal(model.Rows.Count - Shown(model).Length, model.HiddenCount);
            Assert.Equal(model.HiddenCount == 0 ? string.Empty : $"{model.HiddenCount} {(model.HiddenCount == 1 ? "file" : "files")} without audio hidden", model.HiddenText);
            Thread.Sleep(2);
        }
        while (model.IsRunning);

        ui.RunPending();
        Assert.Equal(40, model.HiddenCount);
        Assert.Equal(40, Shown(model).Length);
    }

    [Fact]
    public void A_row_filled_from_the_cache_is_hidden_like_an_analyzed_one()
    {
        using var temp = new TempFolder();
        temp.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "song.mp3");
        temp.Write("notes.txt", "not audio"u8.ToArray());
        using var cacheFolder = new TempFolder();
        using var cache = new SummaryCache(cacheFolder["cache.bin"], "test", flushDelay: TimeSpan.FromHours(1));
        Open(temp.Path, cache);

        (MainViewModel model, _) = Open(temp.Path, cache);
        model.ShowOnlyAudio = true;

        Assert.All(model.Rows, r => Assert.True(r.IsCached));
        Assert.Equal(["song.mp3"], Shown(model));
        Assert.Equal(1, model.HiddenCount);
    }

    [Fact]
    public void Refresh_does_not_select_a_hidden_row_again()
    {
        using var temp = new TempFolder();
        temp.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "song.mp3");
        temp.Write("notes.txt", "not audio"u8.ToArray());
        (MainViewModel model, TestDispatcher ui) = Open(temp.Path);
        model.ShowOnlyAudio = true;
        model.SelectedRow = model.Rows.Single(r => r.Name == "notes.txt");

        model.Refresh();
        ui.Until(() => !model.IsRunning);

        Assert.Null(model.SelectedRow);
    }

    [AvaloniaFact]
    public void A_detail_window_on_a_row_that_becomes_hidden_still_steps_to_its_neighbors()
    {
        using var temp = new TempFolder();
        temp.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "a.mp3");
        temp.Write("b.txt", "not audio"u8.ToArray());
        temp.Copy(Fixtures.Mpeg("external-lame-vbr-v2.mp3"), "c.mp3");
        (MainViewModel model, _) = Open(temp.Path);
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), model);
        main.Show();
        FileRow hidden = model.Rows.Single(r => r.Name == "b.txt");
        DetailWindow window = main.DetailWindows.Open(hidden);

        model.ShowOnlyAudio = true;

        Assert.DoesNotContain(hidden, model.RowsView.Cast<FileRow>());
        Assert.True(window.Model.CanGoPrevious);
        Assert.True(window.Model.CanGoNext);
        Assert.Equal("c.mp3", model.Neighbor(hidden, 1)!.Name);
        Assert.Equal("a.mp3", model.Neighbor(hidden, -1)!.Name);
        main.Close();
    }

    [Fact]
    public void A_hidden_row_steps_by_the_lists_sort()
    {
        using var temp = new TempFolder();
        temp.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "a.mp3");
        temp.Write("b.txt", "not audio"u8.ToArray());
        temp.Copy(Fixtures.Mpeg("external-lame-vbr-v2.mp3"), "c.mp3");
        (MainViewModel model, _) = Open(temp.Path);
        model.ShowOnlyAudio = true;
        FileRow hidden = model.Rows.Single(r => r.Name == "b.txt");

        // Sorted by name, descending, the order is c, b, a.
        model.RowsView.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(FileRow.Name), ListSortDirection.Descending));
        Assert.Equal("a.mp3", model.Neighbor(hidden, 1)!.Name);
        Assert.Equal("c.mp3", model.Neighbor(hidden, -1)!.Name);

        model.RowsView.SortDescriptions.Clear();
        model.RowsView.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(FileRow.Name), ListSortDirection.Ascending));
        Assert.Equal("c.mp3", model.Neighbor(hidden, 1)!.Name);
        Assert.Equal("a.mp3", model.Neighbor(hidden, -1)!.Name);
    }

    [AvaloniaFact]
    public void The_checkbox_and_the_menu_item_toggle_the_switch()
    {
        var model = new MainViewModel(Analyzer.CreateDefault(), roots: []);
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), model);
        main.Show();
        CheckBox box = main.FindControl<CheckBox>("OnlyAudioToggle")!;
        Assert.Equal("Show Only Audio", box.Content);

        box.IsChecked = true;
        Assert.True(model.ShowOnlyAudio);
        box.IsChecked = false;
        Assert.False(model.ShowOnlyAudio);

        NativeMenuItem item = NativeMenu.GetMenu(main)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "View")
            .Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Show Only Audio");
        Click(item);
        Assert.True(model.ShowOnlyAudio);
        Assert.True(box.IsChecked);
        Click(item);
        Assert.False(model.ShowOnlyAudio);
        main.Close();
    }

    // What the platform's menu does when the user chooses an item; Avalonia keeps the method off its public surface.
    private static void Click(NativeMenuItem item) =>
        typeof(NativeMenuItem).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .First(m => m.Name.EndsWith("RaiseClick", StringComparison.Ordinal) || m.Name.EndsWith("RaiseClicked", StringComparison.Ordinal))
            .Invoke(item, null);
}
