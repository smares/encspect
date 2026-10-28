using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The "Learn More" links of the graph area: one under the chosen graph, one beside each line of the Figures panel.
public class GraphTopicTests
{
    private const string Mpeg = "external-lame-vbr-v2.mp3";
    private const string Steps = "synthetic-loudness-steps-8000.flac";

    private static Button GraphLink(DetailWindow window) => window.FindControl<Button>("GraphLearnMore")!;

    private static Button[] FigureLinks(DetailWindow window) =>
        [.. window.FindControl<ItemsControl>("FigureLines")!.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "FigureLearnMore")];

    [AvaloniaFact]
    public void A_category_graph_links_its_series_topic_in_every_form()
    {
        DetailWindow window = AudioPanelTests.Open(Fixtures.Mpeg(Mpeg), decode: false);
        GraphChoice[] categories = [.. window.Model.Graphs.Choices.Where(c => c.Forms[0].Graph is CategoryGraph)];
        Assert.NotEmpty(categories);
        bool anyLinked = false;
        foreach (GraphChoice choice in categories)
        {
            string? id = ((CategoryGraph)choice.Forms[0].Graph).Series.Topic;
            foreach (GraphFormChoice form in choice.Forms)
            {
                AudioPanelTests.Choose(window, choice.Title, form.Kind);
                Assert.Equal(id, form.Graph.Topic);
                if (TopicLibrary.Find(id) is { } topic)
                {
                    anyLinked = true;
                    Assert.True(GraphLink(window).IsEffectivelyVisible, $"{choice.Title} as {form.Kind}");
                    Assert.Equal($"Learn More: {topic.Title}", ((TextBlock)GraphLink(window).Content!).Text);
                    Assert.Equal($"Learn More: {topic.Title}", AutomationProperties.GetName(GraphLink(window)));
                }
                else
                {
                    Assert.False(GraphLink(window).IsEffectivelyVisible);
                }
            }
        }

        Assert.True(anyLinked);
        window.Close();
    }

    [AvaloniaFact]
    public void A_numeric_series_and_the_coding_summary_have_no_link()
    {
        DetailWindow window = AudioPanelTests.Open(Fixtures.Mpeg(Mpeg), decode: false);
        GraphChoice[] numeric = [.. window.Model.Graphs.Choices.Where(c => c.Forms.All(f => f.Graph is not CategoryGraph and not CodingSummaryGraph))];
        Assert.NotEmpty(numeric);
        foreach (GraphChoice choice in numeric)
        {
            foreach (GraphFormChoice form in choice.Forms)
            {
                AudioPanelTests.Choose(window, choice.Title, form.Kind);
                Assert.Null(form.Graph.Topic);
                Assert.Null(window.Model.Graphs.LearnMoreText);
                Assert.False(GraphLink(window).IsEffectivelyVisible, $"{choice.Title} as {form.Kind}");
            }
        }

        AudioPanelTests.Choose(window, CodingSummaryGraph.TitleText);
        Assert.False(GraphLink(window).IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("Waveform", "measurements")]
    [InlineData("Spectrogram", "spectrogram")]
    [InlineData("Spectrum", "spectrogram")]
    [InlineData("Loudness", "loudness")]
    [InlineData("Figures", "measurements")]
    public void Each_audio_panel_links_its_article(string title, string id)
    {
        DetailWindow window = AudioPanelTests.Open(Fixtures.Measurement(Steps));
        AudioPanelTests.Choose(window, title);
        Topic topic = TopicLibrary.Find(id)!;
        Assert.Equal(id, window.Model.Graphs.Graph!.Topic);
        Assert.True(GraphLink(window).IsEffectivelyVisible);
        Assert.Equal($"Learn More: {topic.Title}", ((TextBlock)GraphLink(window).Content!).Text);

        GraphLink(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.Model.IsTopicOpen);
        Assert.Same(topic, window.Model.ShownTopic);
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_on_the_focused_link_opens_the_article_and_the_link_is_reachable_by_tab()
    {
        DetailWindow window = AudioPanelTests.Open(Fixtures.Measurement(Steps));
        AudioPanelTests.Choose(window, "Loudness");
        Button link = GraphLink(window);
        Assert.True(link.Focusable);
        Assert.True(link.IsTabStop);
        Assert.True(link.Focus());
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        AudioPanelTests.Settle();
        Assert.True(window.Model.IsTopicOpen);
        Assert.Equal("loudness", window.Model.ShownTopic!.Id);
        window.Close();
    }

    [AvaloniaFact]
    public void Each_figures_line_links_its_own_article()
    {
        DetailWindow window = AudioPanelTests.Open(Fixtures.Measurement(Steps));
        AudioPanelTests.Choose(window, "Figures");
        IReadOnlyList<MeasurementLine> lines = window.Model.Graphs.Figures!;
        Button[] links = FigureLinks(window);
        Assert.Equal(lines.Count, links.Length);
        for (int i = 0; i < lines.Count; i++)
        {
            Topic topic = TopicLibrary.Find(lines[i].Topic)!;
            Assert.Same(lines[i], links[i].DataContext);
            Assert.True(links[i].IsEffectivelyVisible, lines[i].Name);
            Assert.Equal(lines[i].Name, ((TextBlock)links[i].Content!).Text);
            Assert.Equal($"Learn More: {topic.Title}", ToolTip.GetTip(links[i]));
            Assert.Equal($"Learn More: {topic.Title}", AutomationProperties.GetName(links[i]));
        }

        Button loudness = links[lines.ToList().FindIndex(l => l.Name == "Integrated Loudness")];
        loudness.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("loudness", window.Model.ShownTopic!.Id);

        Button peak = links[lines.ToList().FindIndex(l => l.Name == "Sample Peak")];
        Assert.True(peak.Focus());
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        AudioPanelTests.Settle();
        Assert.Equal("levels", window.Model.ShownTopic!.Id);
        window.Close();
    }

    [AvaloniaFact]
    public void F1_opens_the_graph_article_while_the_graph_area_has_focus_and_the_node_article_elsewhere()
    {
        DetailWindow window = AudioPanelTests.Open(Fixtures.Measurement(Steps));
        AudioPanelTests.Choose(window, "Loudness");
        Assert.True(GraphLink(window).Focus());
        window.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        Assert.True(window.Model.IsTopicOpen);
        Assert.Equal("loudness", window.Model.ShownTopic!.Id);

        window.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        Assert.False(window.Model.IsTopicOpen);

        // Outside the graph area F1 is what it was: the selected node's article, or the list.
        Assert.True(window.FindControl<Button>("GraphsTab")!.Focus());
        window.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        Assert.True(window.Model.IsTopicOpen);
        Assert.Equal(window.Model.SelectionTopic, window.Model.ShownTopic);
        window.Close();
    }

    [AvaloniaFact]
    public void Every_article_the_graph_area_can_link_is_in_the_library()
    {
        string[] fixedIds = [.. typeof(MeasurementText).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Topic", StringComparison.Ordinal)).Select(f => (string)f.GetRawConstantValue()!)];
        Assert.NotEmpty(fixedIds);
        Assert.All(fixedIds, id => Assert.NotNull(TopicLibrary.Find(id)));

        foreach (string fixture in new[] { Fixtures.Mpeg(Mpeg), Fixtures.Measurement(Steps) })
        {
            DetailWindow window = AudioPanelTests.Open(fixture);
            foreach (GraphChoice choice in window.Model.Graphs.Choices)
            {
                foreach (GraphFormChoice form in choice.Forms)
                {
                    Assert.True(form.Graph.Topic is null || TopicLibrary.Find(form.Graph.Topic) is not null, $"{choice.Title}: {form.Graph.Topic}");
                }

                if (choice.Graph is FiguresGraph figures)
                {
                    Assert.All(figures.Lines, l => Assert.NotNull(TopicLibrary.Find(l.Topic)));
                }
            }

            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersTheLinks(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        DetailWindow window = AudioPanelTests.Open(Fixtures.Measurement(Steps));
        AudioPanelTests.Choose(window, "Loudness");
        ScreenshotTests.Save(window, $"graph-link-{variant}.png");
        AudioPanelTests.Choose(window, "Figures");
        ScreenshotTests.Save(window, $"figures-links-{variant}.png");
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }
}
