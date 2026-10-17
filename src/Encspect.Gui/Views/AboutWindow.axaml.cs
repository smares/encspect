using Avalonia.Controls;
using Avalonia.Input;
using Encspect.Host;

namespace Encspect.Gui.Views;

public partial class AboutWindow : Window
{
    // The string that `encspect --version` prints after the program name and the JSON reports give as tool.version.
    public static string VersionLine => "Version " + ReportJsonWriter.ToolVersion;

    // The open About window, so that choosing About again brings it forward instead of opening a second one.
    public static AboutWindow? Current { get; private set; }

    public static void ShowFor(Window owner)
    {
        if (Current is { } open)
        {
            open.Activate();
            return;
        }

        Current = new AboutWindow();
        Current.Closed += (_, _) => Current = null;
        _ = Current.ShowDialog(owner);
    }

    public NoticesWindow? Notices { get; private set; }

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = VersionLine;
        DescriptionText.Text = "Reports what is knowable about an audio file's structure.";
        CopyrightText.Text = "Copyright (c) 2026 Sebastian Mares. Released under the MIT License.";
        NoticesText.Text = "Encspect carries the libraries named in the third-party notices.";
        CloseButton.Click += (_, _) => Close();
        NoticesButton.Click += (_, _) =>
        {
            Notices = new NoticesWindow();
            _ = Notices.ShowDialog(this);
        };

        // Esc and Enter close the window; a focused button takes Enter first.
        Shortcuts.Bind(this, new KeyGesture(Key.Escape), Close);
        Shortcuts.Bind(this, new KeyGesture(Key.Enter), Close);
        Opened += (_, _) => CloseButton.Focus();
    }
}
