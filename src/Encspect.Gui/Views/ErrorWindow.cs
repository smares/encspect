using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Encspect.Gui.Views;

// An error the app cannot carry on from: what happened, the exception's message, and its details
// to copy into a report.
public sealed class ErrorWindow : Window
{
    public ErrorWindow(string summary, Exception exception, string button)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Title = "Encspect";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var close = new Button { Content = button, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(20, 5) };
        close.Click += (_, _) => Close();
        var details = new TextBox
        {
            Name = "Details",
            Text = exception.ToString(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 180,
            FontSize = 12,
        };
        details.Bind(FontFamilyProperty, details.GetResourceObservable("MonospaceFontFamily"));
        Content = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = summary, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Name = "Message", Text = exception.Message, TextWrapping = TextWrapping.Wrap },
                details,
                close,
            },
        };
    }
}
