using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Encspect.Gui.Views;

// The application's icon from the embedded assets (Assets/Icon; docs/development.md, "The app icon").
public static class AppIcon
{
    private static readonly Uri Window256 = new("avares://encspect-gui/Assets/Icon/encspect-256.png");
    private static readonly Uri Picture128 = new("avares://encspect-gui/Assets/Icon/encspect-128.png");

    // Windows takes the closest entry of an .ico for the title bar (16 pixels at 100%) and the taskbar (24), where a PNG
    // would become one large icon that Windows shrinks, so the small form would never be used.
    private static readonly Uri WindowIco = new("avares://encspect-gui/Assets/Icon/encspect.ico");

    private static WindowIcon? _window;

    // One icon shared by every window.
    public static WindowIcon Window => _window ??= new WindowIcon(AssetLoader.Open(OperatingSystem.IsWindows() ? WindowIco : Window256));

    // The large form at 128 pixels, for the About window to draw at 64 device-independent pixels.
    public static Bitmap Picture() => new(AssetLoader.Open(Picture128));
}
