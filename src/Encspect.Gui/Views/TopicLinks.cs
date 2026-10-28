using Avalonia.Data.Converters;
using Encspect.Host;

namespace Encspect.Gui.Views;

// What the links to background articles show for an article ID: its title as hover text, and "Learn More: <title>" as the
// automation text. An ID the library does not know has no link.
public static class TopicLinks
{
    public static readonly IValueConverter IsKnown = new FuncValueConverter<string?, bool>(id => TopicLibrary.Find(id) is not null);

    public static readonly IValueConverter IsUnknown = new FuncValueConverter<string?, bool>(id => TopicLibrary.Find(id) is null);

    public static readonly IValueConverter Title = new FuncValueConverter<string?, string?>(id => TopicLibrary.Find(id)?.Title);

    public static readonly IValueConverter LearnMore = new FuncValueConverter<string?, string?>(id => TopicLibrary.Find(id) is { } topic ? $"Learn More: {topic.Title}" : null);
}
