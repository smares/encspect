using Avalonia;
using Avalonia.Controls;

namespace Encspect.Gui.Views;

// Keeps a window on the screen it opens on: its size is capped at the screen's working area and
// its position moved so that the title bar, which holds the way to move and close it, stays on
// that area. A window that is too tall for a small screen otherwise opens with its title bar
// above the top edge.
internal static class ScreenFit
{
    // What the system draws around the client area, in device-independent units, kept free of the
    // window's size because the frame is not known before the window is shown.
    public const double Chrome = 48;

    public static Size Cap(Size size, PixelRect workingArea, double scaling) =>
        new(
            Math.Min(size.Width, Math.Max(1, Math.Floor(workingArea.Width / scaling))),
            Math.Min(size.Height, Math.Max(1, Math.Floor(workingArea.Height / scaling - Chrome))));

    public static PixelPoint Clamp(PixelPoint position, Size size, PixelRect workingArea, double scaling)
    {
        int width = (int)Math.Ceiling(size.Width * scaling);
        int height = (int)Math.Ceiling((size.Height + Chrome) * scaling);
        int x = Math.Max(workingArea.X, Math.Min(position.X, workingArea.Right - width));
        int y = Math.Max(workingArea.Y, Math.Min(position.Y, workingArea.Bottom - height));
        return new PixelPoint(x, y);
    }

    // Caps the window's size, and lowers its minimum size where that is over the cap.
    public static void CapSize(Window window, PixelRect workingArea, double scaling)
    {
        Size capped = Cap(new Size(window.Width, window.Height), workingArea, scaling);
        window.Width = capped.Width;
        window.Height = capped.Height;
        window.MinWidth = Math.Min(window.MinWidth, capped.Width);
        window.MinHeight = Math.Min(window.MinHeight, capped.Height);
    }
}
