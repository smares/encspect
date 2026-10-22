using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Encspect.Gui.Views;

// THIRD-PARTY-NOTICES.md, which ships next to the executables, is also embedded in the GUI: a single-file
// publish has no folder of its own to look in, and the window cannot then show a different text than the file.
public sealed class NoticesWindow : Window
{
    public const string ResourceName = "THIRD-PARTY-NOTICES.md";

    public TextBox Box { get; }

    public NoticesWindow()
    {
        Title = "Third-Party Notices";
        Width = 760;
        Height = 560;
        MinWidth = 400;
        MinHeight = 300;
        CanMinimize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new TextBox
        {
            Name = "NoticesBox",
            Text = Load(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 12,
        };
        Box = text;
        Avalonia.Automation.AutomationProperties.SetName(text, "Third-Party Notices");
        text.Bind(FontFamilyProperty, text.GetResourceObservable("MonospaceFontFamily"));
        ScrollViewer.SetHorizontalScrollBarVisibility(text, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(text, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(20, 5) };
        close.Click += (_, _) => Close();
        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(close, Dock.Bottom);
        close.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(close);
        panel.Children.Add(text);
        Content = panel;

        Shortcuts.Bind(this, new KeyGesture(Key.Escape), Close);
        Opened += (_, _) => close.Focus();
    }

    public static string Load()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return "The third-party notices are not part of this build. They are in THIRD-PARTY-NOTICES.md in the download and in the source repository.";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
