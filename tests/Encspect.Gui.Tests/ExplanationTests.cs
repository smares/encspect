using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// What the GUI says about the fields it shows: the descriptions parsers give their nodes, in the
// tree's tooltips and under the hex view, the background articles in a side pane, and the
// tooltips of the file list's columns and of the graphs.
public sealed class ExplanationTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [AvaloniaFact]
    public void Tree_rows_show_their_description_in_a_tooltip()
    {
        DetailWindow window = OpenDetail();
        TreeRow field = ModeExtension(window);

        window.TreeList.ScrollIntoView(field);
        UiPump.Until(() => window.TreeList.ContainerFromItem(field) is not null);
        Control container = window.TreeList.ContainerFromItem(field)!;
        StackPanel owner = container.GetVisualDescendants().OfType<StackPanel>().First(p => ToolTip.GetTip(p) is not null);
        ToolTip.SetIsOpen(owner, true);
        UiPump.Until(() => true);
        Control tip = Assert.IsAssignableFrom<Control>(ToolTip.GetTip(owner));
        string[] texts = [.. tip.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];
        ToolTip.SetIsOpen(owner, false);

        Assert.Contains("Mode Extension: Mid/side stereo", texts);
        Assert.Contains(field.Node.Description, texts);
        Assert.StartsWith("Which joint stereo tools", field.Node.Description);
        window.Close();
    }

    [AvaloniaFact]
    public void The_selection_panel_shows_the_description_and_a_link_to_the_topic()
    {
        DetailWindow window = OpenDetail();
        Assert.False(window.LearnMore.IsVisible);

        TreeRow field = ModeExtension(window);
        window.Model.SelectedNode = field;
        UiPump.Until(() => true);

        Assert.True(window.SelectionDescription.IsVisible);
        Assert.Equal(field.Node.Description, window.SelectionDescription.Text);
        Assert.True(window.LearnMore.IsVisible);
        Assert.Equal("Learn more: Mid/side stereo", window.LearnMore.Text);

        // A node without a topic of its own leads to its nearest ancestor's.
        TreeRow emphasis = Below(window, field.Parent!).First(r => r.Name == "IsPrivate");
        window.Model.SelectedNode = emphasis;
        Assert.Null(emphasis.Node.Topic);
        Assert.Equal("Learn more: The MPEG audio frame header", window.Model.LearnMoreText);
        window.Close();
    }

    [AvaloniaFact]
    public void Learn_more_opens_the_article_and_its_links_navigate_in_the_pane()
    {
        DetailWindow window = OpenDetail();
        window.Model.SelectedNode = ModeExtension(window);
        Assert.False(window.TopicPane.IsVisible);

        window.LearnMore.RaiseEvent(new TappedEventArgs(InputElement.TappedEvent, null!));
        UiPump.Until(() => true);
        Assert.True(window.TopicPane.IsVisible);
        Assert.Equal("mid-side-stereo", window.Model.ShownTopic!.Id);
        Assert.True(window.Body.ColumnDefinitions[2].ActualWidth >= 260);

        TextBlock link = window.Topic.Links.First(l => (string?)l.Tag == "intensity-stereo");
        window.Topic.Activate(link);
        Assert.Equal("intensity-stereo", window.Model.ShownTopic!.Id);

        window.Topic.Activate(window.Topic.Links.Single(l => l.Tag is null));
        Assert.Null(window.Model.ShownTopic);
        Assert.Equal(TopicLibrary.All.Count, window.Topic.Links.Count);

        window.CloseTopicButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        UiPump.Until(() => true);
        Assert.False(window.TopicPane.IsVisible);
        Assert.Equal(0, window.Body.ColumnDefinitions[2].ActualWidth);
        window.Close();
    }

    [AvaloniaFact]
    public void The_pane_remembers_whether_it_was_open()
    {
        string path = _folder["settings.json"];
        Settings settings = Settings.Load(path);
        DetailWindow first = OpenDetail(settings);
        Assert.False(first.Model.IsTopicOpen);
        first.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        Assert.True(first.Model.IsTopicOpen);
        first.Close();

        Assert.True(Settings.Load(path).IsTopicPaneOpen);
        DetailWindow second = OpenDetail(Settings.Load(path));
        Assert.True(second.Model.IsTopicOpen);
        Assert.True(second.TopicPane.IsVisible);
        second.Close();
    }

    [AvaloniaFact]
    public void Column_headers_of_the_file_list_explain_their_columns()
    {
        _folder.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "a.mp3");
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), new MainViewModel(Analyzer.CreateDefault(), roots: []));
        main.Show();
        main.Model.Open(FileSource.ForFolder(_folder.Path));
        UiPump.Until(() => !main.Model.IsRunning);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        DataGridColumnHeader[] headers = [.. main.Files.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.Content is string)];
        Assert.StartsWith("Which program encoded the file", (string?)ToolTip.GetTip(headers.Single(h => (string)h.Content! == "Encoder")));
        Assert.StartsWith("Which software wrote the file around the audio", (string?)ToolTip.GetTip(headers.Single(h => (string)h.Content! == "Written by")));
        Assert.StartsWith("Problems and notes found", (string?)ToolTip.GetTip(headers.Single(h => (string)h.Content! == "Findings")));
        Assert.All(headers, h => Assert.NotNull(ToolTip.GetTip(h)));
        main.Close();
    }

    // The channel layout column shows the layout's name, its cells' tooltips the speakers in the
    // stream's order, and the detail window's streams line both.
    [AvaloniaFact]
    public void The_channel_layout_column_names_the_layout_and_lists_its_speakers()
    {
        _folder.Copy(Path.Combine(Fixtures.Directory, "layer2", "mc-3-2-lfe.mp2"), "surround.mp2");
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), new MainViewModel(Analyzer.CreateDefault(), roots: []));
        main.Show();
        main.Model.Open(FileSource.ForFolder(_folder.Path));
        UiPump.Until(() => !main.Model.IsRunning);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        DataGridColumnHeader header = main.Files.GetVisualDescendants().OfType<DataGridColumnHeader>().Single(h => h.Content as string == "Channel Layout");
        Assert.StartsWith("Which speakers the first stream's channels feed", (string?)ToolTip.GetTip(header));
        string speakers = "front left, front right, front center, back left, back right, LFE";
        TextBlock cell = main.Files.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "5.1");
        Assert.Equal(speakers, ToolTip.GetTip(cell));
        FileRow row = main.Model.Rows.Single();
        Assert.Equal(("Stereo", "5.1", speakers), (row.ChannelMode, row.ChannelLayout, row.ChannelLayoutTip));

        DetailWindow detail = main.DetailWindows.Open(row);
        UiPump.Until(() => !detail.Model.IsLoading);
        Assert.Contains($"Stereo · 5.1 ({speakers})", detail.Model.StreamsText, StringComparison.Ordinal);
        main.Close();
    }

    [AvaloniaFact]
    public void Graph_chips_and_the_streams_line_have_tooltips()
    {
        DetailWindow window = OpenDetail();
        window.Model.IsGraphsShown = true;
        UiPump.Until(() => window.Model.Graphs.Choices.Count > 0);

        Assert.All(window.Model.Graphs.Choices, c => Assert.False(string.IsNullOrEmpty(c.Description)));
        Assert.All(window.Model.Graphs.Choices.SelectMany(c => c.Forms), f => Assert.False(string.IsNullOrEmpty(f.Description)));
        Assert.StartsWith("How many frames have each value. The bitrate", window.Model.Graphs.Choices.SelectMany(c => c.Forms).Select(f => f.Graph).OfType<DistributionGraph>().Single().Description);
        Assert.NotNull(ToolTip.GetTip(window.StreamsText));
        window.Close();
    }

    // The selection panel with a field and its description, then the article on mid/side stereo
    // open beside the tree, in light and dark mode.
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersDescriptionsAndArticles(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        DetailWindow window = OpenDetail();
        window.Width = 1180;
        window.Height = 800;
        TreeRow field = ModeExtension(window);
        window.Model.SelectedNode = field;
        window.TreeList.ScrollIntoView(field);
        Save(window, $"detail-description-{variant}.png");

        window.Model.ShowTopic(window.Model.SelectionTopic!.Id);
        window.Width = 1400;
        Save(window, $"detail-article-{variant}.png");

        // A long description in the selection panel stays within three lines.
        TreeRow confidence = Below(window, Below(window, window.Model.Tree!.Root).First(r => r.Kind == NodeKind.Stream)).First(r => r.Name == "Encoder");
        window.Model.Tree!.Expand(confidence);
        window.Model.SelectedNode = Below(window, confidence).First(r => r.Name == "Confidence");
        window.Model.ShowTopic(null);
        Save(window, $"detail-long-description-{variant}.png");
        Assert.True(window.SelectionDescription.Bounds.Height < 60);

        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    private static DetailWindow OpenDetail(Settings? settings = null)
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1120, Height = 780 };
        if (settings is not null)
        {
            window.Settings = settings;
        }

        window.Show();
        Task load = window.Model.LoadAsync(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"));
        UiPump.Until(() => load.IsCompleted);
        return window;
    }

    // The Mode Extension field of the stream's first frame, which codes mid/side stereo.
    private static TreeRow ModeExtension(DetailWindow window)
    {
        StructureTree tree = window.Model.Tree!;
        TreeRow stream = Below(window, tree.Root).First(r => r.Kind == NodeKind.Stream);
        tree.Expand(stream);
        TreeRow frame = Below(window, stream).First(r => r.Kind == NodeKind.Frame);
        tree.Expand(frame);
        TreeRow header = Below(window, frame).First(r => r.Name == "Header");
        tree.Expand(header);
        return Below(window, header).First(r => r.Name == "Mode Extension");
    }

    private static IEnumerable<TreeRow> Below(DetailWindow window, TreeRow row)
    {
        StructureTree tree = window.Model.Tree!;
        if (!row.IsExpanded)
        {
            tree.Expand(row);
        }

        int index = tree.IndexOf(row);
        return Enumerable.Range(index + 1, row.VisibleCount).Select(i => tree[i]).Where(r => r.Parent == row);
    }

    private static void Save(TopLevel window, string name)
    {
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("ENCSPECT_SCREENSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, name), new PngBitmapEncoderOptions());
        }
    }
}
