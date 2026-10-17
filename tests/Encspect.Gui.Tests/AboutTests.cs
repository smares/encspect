using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Encspect.Gui;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

public sealed class AboutTests
{
    // The CLI's --version prints "encspect " and the same ReportJsonWriter.ToolVersion (CommandLineTests checks it).
    private static readonly string Version = ReportJsonWriter.ToolVersion;

    [AvaloniaFact]
    public void The_help_menu_opens_the_about_window_with_the_version_of_the_command_line()
    {
        if (OperatingSystem.IsMacOS())
        {
            // The About item is in the application menu there (App.axaml); the window is the same.
            return;
        }

        MainWindow main = OpenMain();
        NativeMenuItem about = NativeMenu.GetMenu(main)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Help")
            .Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "About Encspect");

        Click(about);

        AboutWindow window = Assert.IsType<AboutWindow>(AboutWindow.Current);
        Assert.True(window.IsVisible);
        Assert.Equal("About Encspect", window.Title);
        Assert.Equal("Version " + Version, window.FindControl<TextBlock>("VersionText")!.Text);
        Assert.StartsWith("Version 0.1.0", window.FindControl<TextBlock>("VersionText")!.Text);
        Assert.Contains("MIT License", window.FindControl<TextBlock>("CopyrightText")!.Text);

        // Choosing it again brings the same window forward.
        Click(about);
        Assert.Same(window, AboutWindow.Current);

        window.Close();
        Assert.False(window.IsVisible);
        Assert.Null(AboutWindow.Current);
        main.Close();
    }

    // The item of the application menu (App.axaml), where macOS has About; the menu exists on every platform.
    [AvaloniaFact]
    public void The_application_menu_opens_the_about_window_without_an_owner_when_no_window_is_open()
    {
        NativeMenuItem about = NativeMenu.GetMenu(Avalonia.Application.Current!)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "About Encspect");

        Click(about);

        AboutWindow window = Assert.IsType<AboutWindow>(AboutWindow.Current);
        Assert.True(window.IsVisible);
        Assert.Equal("Version " + Version, window.FindControl<TextBlock>("VersionText")!.Text);
        window.Close();
        Assert.Null(AboutWindow.Current);
    }

    [AvaloniaFact]
    public void The_owner_is_a_visible_window_and_a_closed_one_is_not_chosen()
    {
        var closed = new Window();
        closed.Show();
        closed.Close();
        var shown = new Window();
        shown.Show();
        var other = new Window();
        other.Show();

        Assert.Same(shown, AboutWindow.PickOwner([closed, shown]));
        Assert.Null(AboutWindow.PickOwner([closed]));
        Assert.Null(AboutWindow.PickOwner([]));

        // With a closed owner, About still opens.
        AboutWindow.ShowFor(AboutWindow.PickOwner([closed]));
        Assert.NotNull(AboutWindow.Current);
        AboutWindow.Current!.Close();
        shown.Close();
        other.Close();
    }

    [AvaloniaTheory]
    [InlineData(Key.Escape, PhysicalKey.Escape)]
    [InlineData(Key.Enter, PhysicalKey.Enter)]
    public void Escape_and_Enter_close_the_window(Key key, PhysicalKey physical)
    {
        var owner = new Window();
        owner.Show();
        AboutWindow.ShowFor(owner);
        AboutWindow window = AboutWindow.Current!;
        Assert.True(window.IsVisible);

        // The headless platform sends keys to the active window.
        window.Activate();
        window.KeyPress(key, RawInputModifiers.None, physical, null);

        Assert.False(window.IsVisible);
        Assert.Null(AboutWindow.Current);
        owner.Close();
    }

    [AvaloniaFact]
    public void The_notices_are_the_file_that_ships_with_the_build()
    {
        var owner = new Window();
        owner.Show();
        AboutWindow.ShowFor(owner);
        AboutWindow window = AboutWindow.Current!;

        window.FindControl<Button>("NoticesButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        NoticesWindow notices = window.Notices!;
        string text = notices.Box.Text!;
        Assert.StartsWith("# Third-party notices", text);
        Assert.Contains("HarfBuzz", text);
        Assert.Equal("Third-party notices", Avalonia.Automation.AutomationProperties.GetName(notices.Box));
        Assert.Equal(File.ReadAllText(Path.Combine(RepositoryRoot(), "THIRD-PARTY-NOTICES.md")).ReplaceLineEndings("\n"), text.ReplaceLineEndings("\n"));

        notices.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(notices.IsVisible);
        window.Close();
        owner.Close();
    }

    // What the platform's menu does when the user chooses an item; Avalonia keeps the method off its public surface.
    private static void Click(NativeMenuItem item) =>
        typeof(NativeMenuItem).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .First(m => m.Name.EndsWith("RaiseClick", StringComparison.Ordinal) || m.Name.EndsWith("RaiseClicked", StringComparison.Ordinal))
            .Invoke(item, null);

    private static string RepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "THIRD-PARTY-NOTICES.md")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new FileNotFoundException("THIRD-PARTY-NOTICES.md");
    }

    private static MainWindow OpenMain()
    {
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), new MainViewModel(Analyzer.CreateDefault(), roots: []));
        main.Show();
        return main;
    }
}
