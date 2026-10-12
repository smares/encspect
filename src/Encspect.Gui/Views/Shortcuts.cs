using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Encspect.Gui.Views;

// Menu items and their shortcuts, with Cmd on macOS and Ctrl elsewhere. On macOS the menu is in the
// system's menu bar, which handles the shortcuts of its items. Elsewhere the window's key bindings
// do, also where a Linux desktop shows the menu in a global menu bar over D-Bus, which does not
// deliver shortcuts; a window that shows its menu itself does so with NativeMenuBar.
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
                    if (!OperatingSystem.IsMacOS() || !NativeMenu.GetIsNativeMenuExported(window))
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

    // The Window menu macOS apps have: minimize and zoom the window, bring every window to the
    // front, and the open windows, this one checked, to bring one to the front.
    public static NativeMenuItem WindowMenu(Window window)
    {
        NativeMenuItem menu = Submenu(
            "Window",
            Item(window, "Minimize", OperatingSystem.IsMacOS() ? new KeyGesture(Key.M, KeyModifiers.Meta) : null, () => window.WindowState = WindowState.Minimized),
            Item(window, "Zoom", null, () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized),
            new NativeMenuItemSeparator(),
            Item(window, "Bring All to Front", null, () => WindowList.BringAllToFront(window)),
            new NativeMenuItemSeparator());
        int fixedCount = menu.Menu!.Items.Count;
        void List(object? sender, EventArgs e)
        {
            while (menu.Menu.Items.Count > fixedCount)
            {
                menu.Menu.Items.RemoveAt(menu.Menu.Items.Count - 1);
            }

            foreach (Window open in WindowList.Windows)
            {
                var item = new NativeMenuItem(open.Title ?? string.Empty) { ToggleType = MenuItemToggleType.CheckBox, IsChecked = open == window };
                item.Click += (_, _) => WindowList.BringToFront(open);
                menu.Menu.Items.Add(item);
            }
        }

        List(null, EventArgs.Empty);
        WindowList.Changed += List;
        window.Closed += (_, _) => WindowList.Changed -= List;
        return menu;
    }

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
