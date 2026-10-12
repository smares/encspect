using Avalonia.Controls;

namespace Encspect.Gui.Views;

// The app's open windows in the order they opened, which the Window menu lists.
internal static class WindowList
{
    private static readonly List<Window> Open = [];

    public static event EventHandler? Changed;

    public static IReadOnlyList<Window> Windows => Open;

    public static void Register(Window window)
    {
        window.Opened += (_, _) =>
        {
            Open.Add(window);
            Changed?.Invoke(null, EventArgs.Empty);
        };
        window.Closed += (_, _) =>
        {
            Open.Remove(window);
            Changed?.Invoke(null, EventArgs.Empty);
        };
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.TitleProperty && Open.Contains(window))
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        };
    }

    public static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    // Every window that is not minimized, the given one last, so it stays in front.
    public static void BringAllToFront(Window front)
    {
        foreach (Window window in Open.Where(w => w != front && w.WindowState != WindowState.Minimized).ToList())
        {
            window.Activate();
        }

        BringToFront(front);
    }
}
