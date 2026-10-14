using Encspect.Gui.Views;

namespace Encspect.Gui.Tests;

public class SettingsFileTests
{
    // Two instances closing at once write the file from a temporary one and move it over, so the file is
    // always one instance's whole settings, and no temporary file is left.
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
}
