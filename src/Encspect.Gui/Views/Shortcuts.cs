using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Encspect.Gui.Views;

// Menu items and their shortcuts, with Cmd on macOS and Ctrl elsewhere. Where the menu is in the
// system's menu bar (macOS), the menu bar handles the shortcuts of its items; elsewhere the
// window's key bindings do, and a window that shows its menu shows it in the window
// (NativeMenuBar).
internal static class Shortcuts
{
    public static KeyModifiers Command =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    public static KeyGesture WithCommand(Key key, KeyModifiers more = KeyModifiers.None) => new(key, Command | more);

    public static NativeMenuItem Item(Window window, string header, KeyGesture? gesture, Action action)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) => action();
        if (gesture is not null)
        {
            window.KeyBindings.Add(new KeyBinding
            {
                Gesture = gesture,
                Command = new Relay(() =>
                {
                    if (!NativeMenu.GetIsNativeMenuExported(window))
                    {
                        action();
                    }
                }),
            });
        }

        return item;
    }

    public static NativeMenuItem Submenu(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (NativeMenuItemBase item in items)
        {
            menu.Add(item);
        }

        return new NativeMenuItem(header) { Menu = menu };
    }

    // The Window menu macOS apps have: minimize and zoom the key window.
    public static NativeMenuItem WindowMenu(Window window) => Submenu(
        "Window",
        Item(window, "Minimize", OperatingSystem.IsMacOS() ? new KeyGesture(Key.M, KeyModifiers.Meta) : null, () => window.WindowState = WindowState.Minimized),
        Item(window, "Zoom", null, () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));

    // Shortcuts without a menu item, such as Esc to close a window.
    public static void Bind(Window window, KeyGesture gesture, Action action) =>
        window.KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = new Relay(action) });

    private sealed class Relay(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
