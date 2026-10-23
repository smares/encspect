using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The GUI's rule: a label that names something (menus, buttons, checkboxes, tabs, column and section headings, list
// entries) is in Title Case, with short function words lowercase unless they come first. Values, messages, tooltips and
// captions are sentence case and not checked here.
public sealed class CapitalizationTests
{
    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "in", "on", "for", "to", "as", "by", "at", "with", "from",
    };

    // Words that keep their own spelling where a spec or product name starts with a lowercase letter.
    private static readonly HashSet<string> OwnSpelling = new(StringComparer.Ordinal) { "mid/side" };

    // Whole labels that legitimately break the rule, with the reason.
    private static readonly HashSet<string> Exceptions = new(StringComparer.Ordinal);

    [Theory]
    [InlineData("Include Subfolders", true)]
    [InlineData("Include subfolders", false)]
    [InlineData("Analyze as…", true)]
    [InlineData("Analyze As…", false)]
    [InlineData("Written by", true)]
    [InlineData("Written By", false)]
    [InlineData("Bring All to Front", true)]
    [InlineData("The Frame Header", true)]
    [InlineData("the Frame Header", false)]
    [InlineData("Open in New Window", true)]
    [InlineData("MPEG Audio", true)]
    [InlineData("Third-Party Notices", true)]
    [InlineData("Third-party Notices", false)]
    [InlineData("Open/close Window", false)]
    [InlineData("Open/Close Window", true)]
    [InlineData("Stereo mid/side Coding", true)]
    [InlineData("Layout 5.1 and ID3v2", true)]
    [InlineData("MPEG-1 Audio in the Header", true)]
    [InlineData("Include Subfolders…", true)]
    [InlineData("Include subfolders…", false)]
    [InlineData("Detect Automatically", true)]
    [InlineData("Detect automatically", false)]
    [InlineData("Learn More: the Frame", false)]
    [InlineData("", false)]
    public void The_rule_itself_behaves(string label, bool expected) =>
        Assert.Equal(expected, IsTitleCase(label));

    [AvaloniaFact]
    public void Labels_in_the_main_window_are_in_title_case()
    {
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), new MainViewModel(Analyzer.CreateDefault(), roots: []));
        main.Show();
        List<string> labels = [.. MenuLabels(main), .. ControlLabels(main)];
        foreach (DataGridColumn column in main.FindControl<DataGrid>("Files")!.Columns)
        {
            labels.Add((string)column.Header!);
        }

        foreach (MenuItem item in main.FindControl<DataGrid>("Files")!.ContextMenu!.Items.OfType<MenuItem>())
        {
            labels.Add((string)item.Header!);
        }

        Assert.Contains("Include Subfolders", labels);
        Assert.Contains("Written by", labels);
        AssertTitleCase(labels);
        main.Close();
    }

    [AvaloniaFact]
    public void Labels_in_the_detail_window_are_in_title_case()
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1100, Height = 760 };
        window.Show();
        Task load = window.Model.LoadAsync(Fixtures.Mpeg("external-lame-vbr-v2.mp3"));
        UiPump.Until(() => load.IsCompleted);
        UiPump.Until(() => true);

        List<string> labels = [.. MenuLabels(window), .. ControlLabels(window)];
        window.Model.IsGraphsShown = true;
        UiPump.Until(() => true);
        labels.AddRange(ControlLabels(window));
        labels.AddRange(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("heading")).Select(t => t.Text!));

        // The view buttons of each entry show only while it is selected, so every entry is selected in turn and every one of its forms.
        foreach (GraphChoice choice in window.Model.Graphs.Choices)
        {
            window.Model.Graphs.Selected = choice;
            foreach (GraphFormChoice form in choice.Forms)
            {
                window.Model.Graphs.SelectForm(form.Kind);
                UiPump.Until(() => true);
                labels.AddRange(ControlLabels(window));
            }
        }

        Assert.Contains("First and Second", labels);
        Assert.Contains("Distribution", labels);
        Assert.Contains("Over Time", labels);

        var menu = Assert.IsType<MenuFlyout>(window.AnalyzeAsButton.Flyout);
        menu.ShowAt(window.AnalyzeAsButton);
        labels.AddRange(menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!));
        menu.Hide();

        Assert.Contains("Analyze as…", labels);
        Assert.Contains("Findings", labels);
        Assert.Contains("Graphs", labels);
        AssertTitleCase(labels);
        window.Close();
    }

    // The entries and forms of an AAC file's graphs (Window Sequences, Block Sizes, Stereo Coding, Coded Bandwidth), each selected in turn.
    [AvaloniaFact]
    public void Labels_of_the_graphs_of_an_aac_file_are_in_title_case()
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1100, Height = 760 };
        window.Show();
        Task load = window.Model.LoadAsync(Fixtures.Aac("ffmpeg-adts.aac"));
        UiPump.Until(() => load.IsCompleted);
        window.Model.IsGraphsShown = true;
        UiPump.Until(() => true);

        List<string> labels = [.. ControlLabels(window)];
        labels.AddRange(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("heading")).Select(t => t.Text!));
        foreach (GraphChoice choice in window.Model.Graphs.Choices)
        {
            window.Model.Graphs.Selected = choice;
            foreach (GraphFormChoice form in choice.Forms)
            {
                window.Model.Graphs.SelectForm(form.Kind);
                UiPump.Until(() => true);
                labels.AddRange(ControlLabels(window));
            }
        }

        Assert.Contains("Window Sequences", labels);
        Assert.Contains("Coded Bandwidth", labels);
        Assert.Contains("Stereo Coding", labels);
        AssertTitleCase(labels);
        window.Close();
    }

    [AvaloniaFact]
    public void Labels_in_the_main_window_with_a_cache_and_in_the_application_menu_are_in_title_case()
    {
        string store = Path.Combine(Path.GetTempPath(), "encspect-case-" + Guid.NewGuid().ToString("N") + ".json");
        using var cache = new Cache.SummaryCache(store, "test");
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), new MainViewModel(Analyzer.CreateDefault(), roots: [], cache: cache), cache);
        main.Show();
        List<string> labels = [.. MenuLabels(main), .. MenuLabels(NativeMenu.GetMenu(Avalonia.Application.Current!)!)];
        Assert.Contains("Clear Summary Cache", labels);
        Assert.Contains("About Encspect", labels);
        labels.AddRange([MainWindow.OpenFolderTitle, MainWindow.OpenFilesTitle, DetailWindow.ExportTitle, DetailWindow.TextReport.Name, DetailWindow.JsonReport.Name]);
        AssertTitleCase(labels);
        main.Close();
    }

    [AvaloniaFact]
    public void Labels_in_the_about_and_notices_windows_are_in_title_case()
    {
        var about = new AboutWindow();
        about.Show();
        List<string> labels = [about.Title!, .. ControlLabels(about)];
        about.FindControl<Button>("NoticesButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        NoticesWindow notices = about.Notices!;
        labels.Add(notices.Title!);
        labels.AddRange(ControlLabels(notices));
        labels.Add(Avalonia.Automation.AutomationProperties.GetName(notices.Box)!);
        Assert.Contains("Third-Party Notices", labels);
        AssertTitleCase(labels);
        notices.Close();
        about.Close();
    }

    [AvaloniaFact]
    public void Labels_in_the_topic_pane_are_in_title_case()
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1100, Height = 760 };
        window.Show();
        Task load = window.Model.LoadAsync(Fixtures.Mpeg("external-lame-vbr-v2.mp3"));
        UiPump.Until(() => load.IsCompleted);
        List<string> labels = [];
        foreach (string? id in new string?[] { null, "mid-side-stereo" })
        {
            window.Model.ShowTopic(id);
            UiPump.Until(() => true);
            TopicView view = window.Topic;
            labels.AddRange(view.Links.Where(l => l.Tag is null).Select(l => l.Text!));
            labels.AddRange(view.GetVisualDescendants().OfType<TextBlock>().Where(t => (t.Classes.Contains("title") && id is null) || t.Classes.Contains("caption")).Select(t => t.Text!));
        }

        Assert.Contains("All Topics", labels);
        Assert.Contains("Topics", labels);
        Assert.Contains("Related", labels);
        Assert.Contains("Specified in", labels);
        AssertTitleCase(labels);
        window.Close();
    }

    private static IEnumerable<string> ControlLabels(Visual root)
    {
        foreach (Visual visual in root.GetVisualDescendants())
        {
            switch (visual)
            {
                case ContentControl { Content: string text } and (Button or CheckBox or RadioButton or ToggleButton):
                    yield return text;
                    break;
                case MenuItem { Header: string header }:
                    yield return header;
                    break;
            }
        }
    }

    private static IEnumerable<string> MenuLabels(Window window)
    {
        NativeMenu? menu = NativeMenu.GetMenu(window);
        return menu is null ? [] : MenuLabels(menu);
    }

    // The Window menu lists the open windows by their titles, which are file names, not labels.
    private static IEnumerable<string> MenuLabels(NativeMenu menu)
    {
        string?[] titles = [.. WindowList.Windows.Select(w => w.Title)];
        return Walk(menu);

        IEnumerable<string> Walk(NativeMenu menu)
        {
            foreach (NativeMenuItem item in menu.Items.OfType<NativeMenuItem>())
            {
                if (item.Header is { } header && !titles.Contains(header))
                {
                    yield return header;
                }

                if (item.Menu is { } sub)
                {
                    foreach (string label in Walk(sub))
                    {
                        yield return label;
                    }
                }
            }
        }
    }

    private static void AssertTitleCase(IEnumerable<string> labels)
    {
        string[] wrong = [.. labels.Distinct().Where(l => !Exceptions.Contains(l) && !IsTitleCase(l))];
        Assert.NotEmpty(labels);
        Assert.True(wrong.Length == 0, "Not in Title Case: " + string.Join(", ", wrong.Select(l => $"\"{l}\"")));
    }

    private static bool IsTitleCase(string label)
    {
        string[] words = label.TrimEnd('…').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }

        // A word after a colon starts a new phrase, so it counts as first.
        bool first = true;
        foreach (string raw in words)
        {
            string word = raw.Trim('(', ')', ',');
            bool endsPhrase = word.EndsWith(':');
            word = word.TrimEnd(':');
            if (word.Length > 0 && !OwnSpelling.Contains(word))
            {
                string[] parts = word.Split('-', '/');
                for (int p = 0; p < parts.Length; p++)
                {
                    string part = parts[p];
                    if (part.Length == 0 || !char.IsLetter(part[0]))
                    {
                        continue;
                    }

                    bool shouldBeLower = !(first && p == 0) && FunctionWords.Contains(part.ToLowerInvariant());
                    if (char.IsUpper(part[0]) == shouldBeLower)
                    {
                        return false;
                    }
                }
            }

            first = endsPhrase;
        }

        return true;
    }
}
