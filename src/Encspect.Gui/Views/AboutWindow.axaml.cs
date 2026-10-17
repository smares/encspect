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

    // A visible window to own the dialog: the active one, else any other. None is a valid answer: the application menu
    // on macOS stays usable when only a closed main window is left behind a detail window, or none is open.
    public static Window? PickOwner(IEnumerable<Window> windows)
    {
        Window[] open = [.. windows.Where(w => w.IsVisible && w is not AboutWindow)];
        return open.FirstOrDefault(w => w.IsActive) ?? open.FirstOrDefault();
    }

    // Current is set only once the window has been shown, so a failed show leaves the next try a fresh start.
    public static void ShowFor(Window? owner)
    {
        if (Current is { } open)
        {
            open.Activate();
            return;
        }

        var window = new AboutWindow();
        if (owner is { IsVisible: true })
        {
            _ = window.ShowDialog(owner);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }

        Current = window;
        window.Closed += (_, _) =>
        {
            if (Current == window)
            {
                Current = null;
            }
        };
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
