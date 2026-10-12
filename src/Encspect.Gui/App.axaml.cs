using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Encspect.Gui.Cache;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // $Default is Skia's default typeface, which on macOS is a fixed family (Helvetica) rather
        // than the system UI font; CoreText knows that font by the family name .AppleSystemUIFont,
        // and the names after it stand in should it not match. Windows (Segoe UI) and Linux
        // (fontconfig's default) get theirs from $Default.
        if (OperatingSystem.IsMacOS())
        {
            Resources["ContentControlThemeFontFamily"] = new FontFamily(".AppleSystemUIFont, Helvetica Neue, $Default");
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            UnhandledErrors.InstallForUiThread();
            var cache = new SummaryCache(SummaryCache.DefaultPath, AnalyzerVersion.Current);
            var window = new MainWindow(Analyzer.CreateDefault(), Settings.Load(Settings.DefaultPath), cache: cache);
            desktop.MainWindow = window;
            if (desktop.Args is { Length: > 0 } args)
            {
                window.OpenItems(args.Select(Path.GetFullPath).ToList());
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnAbout(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            _ = new AboutWindow().ShowDialog(owner);
        }
    }
}
