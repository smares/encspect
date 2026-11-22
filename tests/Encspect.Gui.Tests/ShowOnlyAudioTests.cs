using Encspect.Gui.Cache;
using Encspect.Gui.ViewModels;
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
        Assert.True(model.Rows.Single(r => r.Name == "audio-named.txt").HasAudio);
        Assert.False(model.Rows.Single(r => r.Name == "text-named.mp3").HasAudio);
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
}
