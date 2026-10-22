using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Encspect.Host;

namespace Encspect.Gui.Views;

// A background article, rendered from the host's Markdown subset (TopicMarkdown) without a Markdown
// library: paragraphs, list items, emphasis, strong text, inline code and links to other topics,
// then the related topics and the specifications. Without a topic it lists all topics. A link
// raises LinkActivated with its topic ID, or null for the list.
public sealed class TopicView : ContentControl
{
    public static readonly StyledProperty<Topic?> TopicProperty = AvaloniaProperty.Register<TopicView, Topic?>(nameof(Topic));

    public static readonly StyledProperty<IReadOnlyList<Topic>?> TopicsProperty = AvaloniaProperty.Register<TopicView, IReadOnlyList<Topic>?>(nameof(Topics));

    static TopicView()
    {
        TopicProperty.Changed.AddClassHandler<TopicView>((view, _) => view.Render());
        TopicsProperty.Changed.AddClassHandler<TopicView>((view, _) => view.Render());
    }

    private readonly List<TextBlock> _links = [];

    public event EventHandler<string?>? LinkActivated;

    public Topic? Topic
    {
        get => GetValue(TopicProperty);
        set => SetValue(TopicProperty, value);
    }

    public IReadOnlyList<Topic>? Topics
    {
        get => GetValue(TopicsProperty);
        set => SetValue(TopicsProperty, value);
    }

    // The links shown, by their text, for tests and keyboard users.
    internal IReadOnlyList<TextBlock> Links => _links;

    // Follows a link as a click would.
    internal void Activate(TextBlock link) => LinkActivated?.Invoke(this, link.Tag as string);

    private void Render()
    {
        _links.Clear();
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(16, 12, 16, 16) };
        if (Topic is { } topic)
        {
            panel.Children.Add(new TextBlock { Text = topic.Title, Classes = { "title" }, FontSize = 17, TextWrapping = TextWrapping.Wrap });
            foreach (TopicBlock block in topic.Blocks)
            {
                panel.Children.Add(block.IsListItem ? ListItem(block) : Paragraph(block.Inlines));
            }

            if (topic.Related.Count > 0)
            {
                panel.Children.Add(Caption("Related"));
                var related = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (string id in topic.Related)
                {
                    TextBlock link = Link(TopicLibrary.Find(id)?.Title ?? id, id);
                    link.Margin = new Thickness(0, 0, 14, 4);
                    related.Children.Add(link);
                }

                panel.Children.Add(related);
            }

            if (topic.References.Count > 0)
            {
                panel.Children.Add(Caption("Specified in"));
                panel.Children.Add(new SelectableTextBlock { Text = string.Join("; ", topic.References), TextWrapping = TextWrapping.Wrap, Classes = { "secondary" } });
            }

            TextBlock all = Link("All Topics", null);
            all.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(all);
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = "Topics", Classes = { "title" }, FontSize = 17 });
            panel.Children.Add(new TextBlock
            {
                Text = "Background on what the report shows: why each coding tool exists, how it works and what seeing it in a file means.",
                TextWrapping = TextWrapping.Wrap,
                Classes = { "secondary" },
            });
            foreach (Topic each in Topics ?? [])
            {
                panel.Children.Add(Link(each.Title, each.Id));
            }
        }

        Content = panel;
    }

    private SelectableTextBlock Paragraph(IReadOnlyList<TopicInline> inlines)
    {
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, LineSpacing = 2, Inlines = [] };
        foreach (TopicInline inline in inlines)
        {
            text.Inlines.Add(Inline(inline));
        }

        return text;
    }

    private Grid ListItem(TopicBlock block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*"), Margin = new Thickness(6, -4, 0, 0) };
        grid.Children.Add(new TextBlock { Text = "•" });
        SelectableTextBlock text = Paragraph(block.Inlines);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private Inline Inline(TopicInline inline)
    {
        if (inline.Link is { } id)
        {
            return new InlineUIContainer(Link(inline.Text, id)) { BaselineAlignment = BaselineAlignment.TextBottom };
        }

        var run = new Run(inline.Text);
        if (inline.IsStrong)
        {
            run.FontWeight = FontWeight.SemiBold;
        }

        if (inline.IsEmphasis)
        {
            run.FontStyle = FontStyle.Italic;
        }

        if (inline.IsCode)
        {
            run.FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace");
        }

        return run;
    }

    private TextBlock Link(string text, string? id)
    {
        var link = new TextBlock { Text = text, Tag = id, Classes = { "link" }, Cursor = new Cursor(StandardCursorType.Hand), TextWrapping = TextWrapping.Wrap };
        _links.Add(link);
        link.Tapped += (_, e) =>
        {
            e.Handled = true;
            Activate(link);
        };
        return link;
    }

    private static TextBlock Caption(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, -4) };
}
