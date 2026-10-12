using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Encspect.Gui.ViewModels;
using Encspect.Host;

namespace Encspect.Gui.Views;

public partial class DetailWindow : Window
{
    private static readonly FilePickerFileType TextReport = new("Text report") { Patterns = ["*.txt"], MimeTypes = ["text/plain"], AppleUniformTypeIdentifiers = ["public.plain-text"] };
    private static readonly FilePickerFileType JsonReport = new("JSON report") { Patterns = ["*.json"], MimeTypes = ["application/json"], AppleUniformTypeIdentifiers = ["public.json"] };

    // For the XAML previewer and loader only.
    public DetailWindow()
        : this(new DetailViewModel(Analyzer.CreateDefault()))
    {
    }

    public DetailWindow(DetailViewModel model)
    {
        InitializeComponent();
        Model = model;
        DataContext = model;
        PickExportTarget = PickExportTargetAsync;
        WindowList.Register(this);
        TreeList.AddHandler(TappedEvent, OnTreeTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        TreeList.DoubleTapped += (_, e) =>
        {
            if (!IsOnExpander(e.Source) && Model.SelectedNode is { } row)
            {
                Model.Toggle(row);
            }
        };
        TreeList.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        Graph.PointClicked += (_, index) => Model.Graphs.Activate(index);

        // The menu shows in macOS's menu bar; elsewhere the window has no menu bar, which would
        // only repeat its buttons, and the menu's shortcuts still work.
        Shortcuts.Bind(this, new KeyGesture(Key.Escape), Close);
        NativeMenu.SetMenu(this, CreateMenu());

        string modifier = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
        ToolTip.SetTip(PreviousButton, $"Previous file ({modifier}Up)");
        ToolTip.SetTip(NextButton, $"Next file ({modifier}Down)");
        Closed += (_, _) => Model.Dispose();
    }

    // Previous (-1) or Next (1), which the window that opened this one carries out.
    public event EventHandler<int>? StepRequested;

    public DetailViewModel Model { get; }

    // The row of the file list this window shows.
    public FileRow? Row { get; set; }

    // Where an export goes: the save dialog's choice, or null when it was canceled. Tests replace
    // the dialog, which the headless platform does not have.
    internal Func<Task<ExportTarget?>> PickExportTarget { get; set; }

    // Asks where to export the report and writes it there. Never throws: what goes wrong, from the
    // dialog to the last byte written, shows in the window.
    public async Task ExportAsync()
    {
        if (Model.Report is null)
        {
            return;
        }

        await Model.TryExportAsync(async () =>
        {
            if (await PickExportTarget() is not { } chosen)
            {
                return;
            }

            if (chosen.Path is { } path)
            {
                await Model.ExportAsync(path, chosen.Format);
            }
            else if (chosen.File is { } file)
            {
                await using Stream stream = await file.OpenWriteAsync();
                await Model.ExportAsync(stream, chosen.Format);
            }
        });
    }

    private async Task<ExportTarget?> PickExportTargetAsync()
    {
        if (!StorageProvider.CanSave)
        {
            return null;
        }

        string name = Path.GetFileNameWithoutExtension(Model.Path) ?? "report";
        SaveFilePickerResult result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
        {
            Title = "Export Report",
            SuggestedFileName = name + ".txt",
            DefaultExtension = "txt",
            FileTypeChoices = [TextReport, JsonReport],
            ShowOverwritePrompt = true,
        });
        if (result.File is not { } file)
        {
            return null;
        }

        ReportFormat format = result.SelectedFileType == JsonReport ? ReportFormat.Json : ReportExport.FormatOf(file.Name);
        return new ExportTarget(file.TryGetLocalPath(), file, format);
    }

    private NativeMenu CreateMenu() =>
    [
        Shortcuts.Submenu(
            "File",
            Shortcuts.Item(this, "Export Report…", Shortcuts.WithCommand(Key.S), () => _ = ExportAsync()),
            new NativeMenuItemSeparator(),
            Shortcuts.Item(this, "Close", Shortcuts.WithCommand(Key.W), Close)),
        Shortcuts.Submenu(
            "Go",
            Shortcuts.Item(this, "Previous File", Shortcuts.WithCommand(Key.Up), () => StepRequested?.Invoke(this, -1)),
            Shortcuts.Item(this, "Next File", Shortcuts.WithCommand(Key.Down), () => StepRequested?.Invoke(this, 1))),
        Shortcuts.WindowMenu(this),
    ];

    private void OnFindingsTab(object? sender, RoutedEventArgs e) => Model.IsGraphsShown = false;

    private void OnGraphsTab(object? sender, RoutedEventArgs e) => Model.IsGraphsShown = true;

    private void OnGraphChoice(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GraphChoice choice })
        {
            Model.Graphs.Selected = choice;
        }
    }

    private void OnPrevious(object? sender, RoutedEventArgs e) => StepRequested?.Invoke(this, -1);

    private void OnNext(object? sender, RoutedEventArgs e) => StepRequested?.Invoke(this, 1);

    private void OnExport(object? sender, RoutedEventArgs e) => _ = ExportAsync();

    private static bool IsOnExpander(object? source) =>
        source is Avalonia.Visual visual && visual.GetSelfAndVisualAncestors().Any(v => v is Border { Name: "Expander" });

    private void OnTreeTapped(object? sender, TappedEventArgs e)
    {
        if (IsOnExpander(e.Source) && e.Source is Avalonia.StyledElement { DataContext: TreeRow row })
        {
            Model.Toggle(row);
        }
    }

    // Right expands a row or moves to its first child, Left collapses it or moves to its parent,
    // as in a tree view; Enter and Space expand or collapse.
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model.Tree is not { } tree || Model.SelectedNode is not { } row || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Right when row.HasChildren && !row.IsExpanded:
            case Key.Left when row.IsExpanded:
            case Key.Enter or Key.Space when row.HasChildren:
                Model.Toggle(row);
                e.Handled = true;
                break;
            case Key.Right when row.IsExpanded:
                Model.SelectedNode = tree[tree.IndexOf(row) + 1];
                e.Handled = true;
                break;
            case Key.Left when row.Parent is { } parent:
                Model.SelectedNode = parent;
                e.Handled = true;
                break;
        }
    }
}

// A file to export to: its local path when it has one, which is written through a temporary file,
// otherwise the storage file the dialog gave, written as a stream.
internal readonly record struct ExportTarget(string? Path, IStorageFile? File, ReportFormat Format);
