using Encspect.Gui.Views;

namespace Encspect.Gui.Tests;

public class SettingsFileTests
{
    // A smoke test: two instances closing at once write the file from a temporary one and move it over,
    // so the file is always one instance's whole settings, and no temporary file is left. It also
    // passes on the code that wrote the file in place, since a torn file shows only when a read meets
    // a write, which one run in a thousand does; what it guards is the move and the cleanup.
    [Fact]
    public void Saving_on_several_threads_never_leaves_a_torn_file()
    {
        using var temp = new TempFolder();
        string path = temp["settings.json"];
        Settings.Load(path).Save();

        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            Settings settings = Settings.Load(path);
            settings.IsTopicPaneOpen = i % 2 == 0;
            settings.TopicPaneWidth = 100 + i;
            settings.DetailWindow = new WindowPlacement(i, i, 800 + i, 600, false);
            settings.Save();
        });

        Settings last = Settings.Load(path);
        Assert.NotNull(last.DetailWindow);
        Assert.Equal(last.DetailWindow.X, last.DetailWindow.Y);
        Assert.Equal([path], Directory.EnumerateFiles(temp.Path));
    }

    // A temporary file left by a process that ended between writing and moving it is swept on the
    // next save, unless it is recent enough to be another instance's write in progress.
    [Fact]
    public void Saving_sweeps_stale_temporary_files_only()
    {
        using var temp = new TempFolder();
        string path = temp["settings.json"];
        string stale = temp.Write("settings.json.0123456789abcdef.tmp", "{"u8.ToArray());
        string fresh = temp.Write("settings.json.fedcba9876543210.tmp", "{"u8.ToArray());
        string other = temp.Write("cache.json.0123456789abcdef.tmp", "{"u8.ToArray());
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-2));

        Settings.Load(path).Save();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(path));
    }
}
