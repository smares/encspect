using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// Exports that fail, and exceptions nothing else caught: each shows in a window, and none ends
// the app unless its state may be broken.
public sealed class ErrorTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [AvaloniaFact]
    public void A_failed_export_from_the_button_shows_in_the_window_and_leaves_no_file()
    {
        DetailWindow window = OpenDetail();
        string target = _folder["missing folder/report.txt"];
        window.PickExportTarget = () => Task.FromResult<ExportTarget?>(new ExportTarget(target, null, ReportFormat.Text));

        window.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        UiPump.Until(() => window.Model.CommandError is not null);

        Assert.StartsWith("Not exported: ", window.Model.CommandError, StringComparison.Ordinal);
        Assert.True(window.CommandErrorText.IsVisible);
        Assert.False(File.Exists(target));
        Assert.True(window.IsVisible);

        // The next export that works clears it.
        target = _folder["report.txt"];
        window.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        UiPump.Until(() => File.Exists(target));
        UiPump.Until(() => window.Model.CommandError is null);
        Assert.False(window.CommandErrorText.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void A_failed_export_leaves_the_file_it_was_to_replace_as_it_was()
    {
        DetailWindow window = OpenDetail();

        // A folder where the report was to go cannot be replaced by a file.
        string target = _folder["taken.txt"];
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "inside.txt"), "kept");
        window.PickExportTarget = () => Task.FromResult<ExportTarget?>(new ExportTarget(target, null, ReportFormat.Json));

        Task export = window.ExportAsync();
        UiPump.Until(() => export.IsCompleted);

        Assert.True(export.IsCompletedSuccessfully);
        Assert.NotNull(window.Model.CommandError);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(target, "inside.txt")));
        Assert.Equal(["inside.txt", "song.mp3", "taken.txt"], Directory.EnumerateFileSystemEntries(_folder.Path, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void An_export_replaces_the_file_as_a_whole()
    {
        DetailWindow window = OpenDetail();
        string target = _folder["report.json"];
        File.WriteAllText(target, new string('x', 1_000_000));
        window.PickExportTarget = () => Task.FromResult<ExportTarget?>(new ExportTarget(target, null, ReportFormat.Json));

        Task export = window.ExportAsync();
        UiPump.Until(() => export.IsCompleted);

        Assert.Null(window.Model.CommandError);
        Assert.StartsWith("[", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(_folder.Path).Length);
        window.Close();
    }

    // The review's case: a device that takes no more data is written as it is, and fails.
    [AvaloniaFact]
    public void An_export_to_a_full_device_shows_why()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/full"))
        {
            return;
        }

        DetailWindow window = OpenDetail();
        window.PickExportTarget = () => Task.FromResult<ExportTarget?>(new ExportTarget("/dev/full", null, ReportFormat.Text));

        Task export = window.ExportAsync();
        UiPump.Until(() => export.IsCompleted);

        Assert.Contains("No space left on device", window.Model.CommandError, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles("/dev", ".full.*"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_dialog_that_fails_shows_too()
    {
        DetailWindow window = OpenDetail();
        window.PickExportTarget = () => throw new UnauthorizedAccessException("The portal refused.");

        Task export = window.ExportAsync();
        UiPump.Until(() => export.IsCompleted);

        Assert.Equal("Not exported: The portal refused.", window.Model.CommandError);
        window.Close();
    }

    [AvaloniaFact]
    public void A_system_error_that_reached_the_ui_thread_shows_in_the_status_bar()
    {
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), new MainViewModel(Analyzer.CreateDefault(), roots: []));
        main.Show();

        Assert.True(UnhandledErrors.Handle(new AggregateException(new IOException("The clipboard is gone.")), main));
        UiPump.Until(() => true);

        Assert.Equal("The clipboard is gone.", main.Model.Error);
        Assert.True(main.ErrorText.IsVisible);
        Assert.Empty(main.OwnedWindows);
        main.Close();
    }

    [AvaloniaFact]
    public void Any_other_exception_shows_its_details_and_a_second_one_is_not_handled()
    {
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), new MainViewModel(Analyzer.CreateDefault(), roots: []));
        main.Show();

        Assert.False(UnhandledErrors.IsRecoverable(new InvalidOperationException()));
        Assert.True(UnhandledErrors.Handle(new InvalidOperationException("A bug."), main));
        UiPump.Until(() => main.OwnedWindows.Count == 1);
        ErrorWindow message = Assert.IsType<ErrorWindow>(main.OwnedWindows[0]);
        Assert.Equal("A bug.", message.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "Message").Text);
        Assert.Contains("InvalidOperationException", message.GetLogicalDescendants().OfType<TextBox>().Single().Text, StringComparison.Ordinal);

        // While the message shows, a second one ends the app.
        Assert.False(UnhandledErrors.Handle(new InvalidOperationException("Another."), main));

        message.Close();
        main.Close();
    }

    private DetailWindow OpenDetail()
    {
        string song = _folder.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "song.mp3");
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault()));
        window.Show();
        Task load = window.Model.LoadAsync(song);
        UiPump.Until(() => load.IsCompleted);
        return window;
    }
}
