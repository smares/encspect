using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// Screenshots for comparing the over-time forms of a category chart; does nothing unless ENCSPECT_SCREENSHOTS is set.
// ENCSPECT_AREA_LONG and ENCSPECT_AREA_SHORT name scratch files that are never committed.
public class AreaFormShots
{
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersTheOverTimeForms(string variant)
    {
        if (Environment.GetEnvironmentVariable("ENCSPECT_SCREENSHOTS") is not { Length: > 0 })
        {
            return;
        }

        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var files = new List<(string Name, string Path, string[] Graphs)>
        {
            ("joint", Fixtures.Mpeg("external-lame-joint-music.mp3"), ["Stereo Coding"]),
            ("dual", Fixtures.Mpeg("external-lame-dual-music.mp3"), ["Stereo Coding"]),
        };
        if (Environment.GetEnvironmentVariable("ENCSPECT_AREA_LONG") is { Length: > 0 } longFile)
        {
            files.Add(("long", longFile, ["Stereo Coding", "Block Sizes"]));
        }

        if (Environment.GetEnvironmentVariable("ENCSPECT_AREA_SHORT") is { Length: > 0 } shortFile)
        {
            files.Add(("short", shortFile, ["Stereo Coding"]));
        }

        foreach ((string name, string path, string[] graphs) in files)
        {
            var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1180, Height = 800 };
            window.Show();
            Task load = window.Model.LoadAsync(path);
            UiPump.Until(() => load.IsCompleted);
            window.Model.IsGraphsShown = true;
            foreach (string graph in graphs)
            {
                window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == graph);
                foreach ((string form, GraphForm kind) in Forms())
                {
                    if (window.Model.Graphs.Selected!.Forms.All(f => f.Kind != kind))
                    {
                        continue;
                    }

                    window.Model.Graphs.SelectForm(kind);
                    ScreenshotTests.Save(window, $"{name}-{graph.ToLowerInvariant().Replace(' ', '-')}-{form}-{variant}.png");
                }
            }

            window.Close();
        }

        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    private static (string, GraphForm)[] Forms() => [("columns", GraphForm.Columns), ("area", GraphForm.OverTime), ("area-smoothed", GraphForm.Smoothed), ("lines", GraphForm.Lines)];
}
