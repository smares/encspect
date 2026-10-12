using System.ComponentModel;
using Avalonia;
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
    private const double DefaultTopicWidth = 340;
    private const double MinimumTopicWidth = 260;

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
        Topic.LinkActivated += (_, id) => Model.ShowTopic(id);
        model.PropertyChanged += OnModelChanged;

        // The menu shows in macOS's menu bar; elsewhere the window has no menu bar, which would
        // only repeat its buttons, and the menu's shortcuts still work.
        Shortcuts.Bind(this, new KeyGesture(Key.Escape), Close);
        Shortcuts.Bind(this, new KeyGesture(Key.F1), Model.ToggleTopics);
        NativeMenu.SetMenu(this, CreateMenu());

        string modifier = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
        ToolTip.SetTip(PreviousButton, $"Previous file ({modifier}Up)");
        ToolTip.SetTip(NextButton, $"Next file ({modifier}Down)");
        ToolTip.SetTip(TopicsButton, "Background articles on what the report shows (F1)");
        Closing += (_, _) => RememberTopicPane();
        Closed += (_, _) => Model.Dispose();
    }

    // Where the window keeps whether the pane of background articles is open, and its width.
    public Settings? Settings
    {
        get;
        set
        {
            field = value;
            _topicWidth = value?.TopicPaneWidth is { } width and >= MinimumTopicWidth ? width : DefaultTopicWidth;
            Model.IsTopicOpen = value?.IsTopicPaneOpen == true;
            ShowTopicPane();
        }
    }

    private double _topicWidth = DefaultTopicWidth;

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
        Shortcuts.Submenu("Help", Shortcuts.Item(this, "Topics", null, () => Model.ShowTopic(null))),
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

    private void OnTopics(object? sender, RoutedEventArgs e) => Model.ToggleTopics();

    private void OnCloseTopic(object? sender, RoutedEventArgs e) => Model.IsTopicOpen = false;

    private void OnLearnMore(object? sender, TappedEventArgs e) => Model.ShowTopic(Model.SelectionTopic?.Id);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetailViewModel.IsTopicOpen))
        {
            ShowTopicPane();
            RememberTopicPane();
        }
        else if (e.PropertyName == nameof(DetailViewModel.ShownTopic))
        {
            TopicScroll.Offset = default;
        }
    }

    // The pane takes its own column at the right of the window, which the splitter resizes.
    private void ShowTopicPane()
    {
        ColumnDefinition column = Body.ColumnDefinitions[2];
        if (!Model.IsTopicOpen && column.ActualWidth >= MinimumTopicWidth)
        {
            _topicWidth = column.ActualWidth;
        }

        column.MinWidth = Model.IsTopicOpen ? MinimumTopicWidth : 0;
        column.Width = new GridLength(Model.IsTopicOpen ? _topicWidth : 0);
    }

    private void RememberTopicPane()
    {
        if (Settings is not { } settings)
        {
            return;
        }

        if (Model.IsTopicOpen && Body.ColumnDefinitions[2].ActualWidth >= MinimumTopicWidth)
        {
            _topicWidth = Body.ColumnDefinitions[2].ActualWidth;
        }

        settings.IsTopicPaneOpen = Model.IsTopicOpen;
        settings.TopicPaneWidth = _topicWidth;
        settings.Save();
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
