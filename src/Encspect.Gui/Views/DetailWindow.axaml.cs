using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Encspect.Gui.ViewModels;
using Encspect.Host;

namespace Encspect.Gui.Views;

public partial class DetailWindow : Window
{
    internal static readonly FilePickerFileType TextReport = new("Text Report") { Patterns = ["*.txt"], MimeTypes = ["text/plain"], AppleUniformTypeIdentifiers = ["public.plain-text"] };
    internal const string ExportTitle = "Export Report";
    private const double DefaultTopicWidth = 340;
    private const double MinimumTopicWidth = 260;
    private const double MinimumPaneShare = 0.2;
    private const double MaximumPaneShare = 0.8;

    internal static readonly FilePickerFileType JsonReport = new("JSON Report") { Patterns = ["*.json"], MimeTypes = ["application/json"], AppleUniformTypeIdentifiers = ["public.json"] };

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
        GraphList.ScrollChanged += (_, _) => RequestListSnap();
        ((Control)GraphList.Parent!).SizeChanged += (_, _) => RequestListSnap();
        Loaded += (_, _) => RequestListSnap();
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
        Graph.CategoryClicked += (_, category) => Model.Graphs.ActivateCategory(category);
        Topic.LinkActivated += (_, id) => Model.ShowTopic(id);
        model.PropertyChanged += OnModelChanged;
        model.Graphs.PropertyChanged += OnGraphsChanged;
        AnalyzeAsButton.Flyout = CreateFormatMenu();

        // The menu shows in macOS's menu bar; elsewhere the window has no menu bar, which would
        // only repeat its buttons, and the menu's shortcuts still work.
        Shortcuts.Bind(this, new KeyGesture(Key.Escape), Close);
        Shortcuts.Bind(this, new KeyGesture(Key.F1), OnTopicsKey);
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
            if (value?.GraphPaneShare is { } share and >= MinimumPaneShare and <= MaximumPaneShare)
            {
                Panes.RowDefinitions[0].Height = new GridLength(1 - share, GridUnitType.Star);
                Panes.RowDefinitions[2].Height = new GridLength(share, GridUnitType.Star);
            }
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
            Title = ExportTitle,
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
            Shortcuts.Item(this, "Decode Audio", null, () => _ = Model.DecodeAudioAsync()),
            Shortcuts.Submenu("Analyze as", [Shortcuts.Item(this, "Detect Automatically", null, () => _ = Model.AnalyzeAsAsync(null)), new NativeMenuItemSeparator(), .. Model.FormatNames.Select(name => Shortcuts.Item(this, name, null, () => _ = Model.AnalyzeAsAsync(name)))]),
            new NativeMenuItemSeparator(),
            Shortcuts.Item(this, "Close", Shortcuts.WithCommand(Key.W), Close)),
        Shortcuts.Submenu(
            "Go",
            Shortcuts.Item(this, "Previous File", Shortcuts.WithCommand(Key.Up), () => StepRequested?.Invoke(this, -1)),
            Shortcuts.Item(this, "Next File", Shortcuts.WithCommand(Key.Down), () => StepRequested?.Invoke(this, 1))),
        Shortcuts.WindowMenu(this),
        Shortcuts.Submenu("Help", Shortcuts.Item(this, "Topics", null, () => Model.ShowTopic(null))),
    ];

    // The "Analyze as…" menu: detection, then every format, with the one in use checked.
    private MenuFlyout CreateFormatMenu()
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            flyout.Items.Add(FormatItem("Detect Automatically", null, Model.ForcedFormat is null));
            flyout.Items.Add(new Separator());
            foreach (string name in Model.FormatNames)
            {
                flyout.Items.Add(FormatItem(name, name, Model.ForcedFormat == name));
            }
        };
        return flyout;

        MenuItem FormatItem(string header, string? format, bool isChecked)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = isChecked, GroupName = "format" };
            item.Click += (_, _) => _ = Model.AnalyzeAsAsync(format);
            return item;
        }
    }

    private void OnAlternative(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AlternativeItem item })
        {
            _ = Model.AnalyzeAsAsync(item.Format);
        }
    }

    private void OnFindingsTab(object? sender, RoutedEventArgs e) => Model.IsGraphsShown = false;

    private void OnGraphsTab(object? sender, RoutedEventArgs e) => Model.IsGraphsShown = true;

    private void OnGraphForm(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GraphFormChoice choice })
        {
            Model.Graphs.SelectForm(choice.Kind);
        }
    }

    private void OnGraphChoice(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GraphChoice choice })
        {
            Model.Graphs.Selected = choice;
        }
    }

    // F1 opens the article of the selected node, or of the selected graph while the focus is in the graph area.
    private void OnTopicsKey()
    {
        bool inGraphs = Model.IsGraphsShown && GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focus && (ReferenceEquals(focus, GraphArea) || focus.GetVisualAncestors().Contains(GraphArea));
        Model.ToggleTopics(inGraphs ? Model.Graphs.Topic : null);
    }

    private void OnTopics(object? sender, RoutedEventArgs e) => Model.ToggleTopics();

    private void OnDecodeAudio(object? sender, RoutedEventArgs e) => _ = Model.DecodeAudioAsync();

    private void OnGraphDecode(object? sender, RoutedEventArgs e) => Model.Graphs.RequestDecode();

    private void OnStopDecoding(object? sender, RoutedEventArgs e) => Model.StopDecoding();

    private void OnCloseTopic(object? sender, RoutedEventArgs e) => Model.IsTopicOpen = false;

    private void OnLearnMore(object? sender, TappedEventArgs e) => Model.ShowTopic(Model.SelectionTopic?.Id);

    private void OnGraphLearnMore(object? sender, RoutedEventArgs e) => Model.ShowTopic(Model.Graphs.Topic?.Id);

    private void OnFigureLearnMore(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MeasurementLine line })
        {
            Model.ShowTopic(line.Topic);
        }
    }

    // The selected entry of the list comes into view, whatever selected it (a click, the remembered title of the last file, a key).
    private void OnGraphsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GraphsViewModel.Selected))
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Model.Graphs.Selected is { } selected && GraphChoices.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => ReferenceEquals(b.DataContext, selected)) is { } button)
            {
                button.BringIntoView();
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

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

        double above = Panes.RowDefinitions[0].ActualHeight;
        double below = Panes.RowDefinitions[2].ActualHeight;
        if (above + below > 0 && below / (above + below) is >= MinimumPaneShare and <= MaximumPaneShare)
        {
            settings.GraphPaneShare = below / (above + below);
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

    // The list of graphs shows whole items only: headings, entries, and the caption and command under the heading "Audio" are never cut by its edges. Its viewport is as high as the
    // whole items that fit in the room it has (the rest stays empty below it), and it scrolls from the top of one item to the top
    // of another, down to the last position whose items all fit. Where all of them fit it has no scrollbar.
    private bool _snapping;
    private bool _snapPending;
    private double _snappedOffset;

    private void RequestListSnap()
    {
        if (_snapping || _snapPending)
        {
            return;
        }

        _snapPending = true;
        Dispatcher.UIThread.Post(SnapList);
    }

    // The room of an item: from its top margin to its bottom margin, in the coordinates of the list's content.
    private List<(double Top, double Bottom)> ListItems() =>
    [
        .. GraphChoices.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && ((c is TextBlock t && (t.Classes.Contains("heading") || t.Classes.Contains("caption"))) || (c is Button b && (b.Classes.Contains("entry") || b.Classes.Contains("decode")))))
            .Select(c => (Top: c.TranslatePoint(default, GraphChoices)!.Value.Y - c.Margin.Top + GraphChoices.Margin.Top, Bottom: c.TranslatePoint(new Point(0, c.Bounds.Height), GraphChoices)!.Value.Y + c.Margin.Bottom + GraphChoices.Margin.Top))
            .OrderBy(i => i.Top),
    ];

    private void SnapList()
    {
        _snapPending = false;
        if (GraphList.Parent is not Control host || host.Bounds.Height <= 0)
        {
            return;
        }

        _snapping = true;
        try
        {
            List<(double Top, double Bottom)> items = ListItems();
            double room = host.Bounds.Height;
            double end = GraphList.Extent.Height;
            if (items.Count == 0 || end <= room + 0.5)
            {
                GraphList.Height = double.NaN;
                _snappedOffset = 0;
                return;
            }

            // The first item reaches up to the top of the content (the list's own margin above the first heading is none).
            double offset = GraphList.Offset.Y;
            double[] tops = [0, .. items.Skip(1).Select(i => i.Top)];
            double lastTop = tops.First(t => end - t <= room + 0.5);
            double top = offset > _snappedOffset + 0.5
                ? tops.FirstOrDefault(t => t >= offset - 0.5, lastTop)
                : offset < _snappedOffset - 0.5
                    ? tops.LastOrDefault(t => t <= offset + 0.5, 0)
                    : tops.OrderBy(t => Math.Abs(t - offset)).First();
            top = Math.Min(top, lastTop);
            double bottom = top >= lastTop ? end : items.Last(i => i.Bottom - top <= room + 0.5).Bottom;
            GraphList.Height = bottom - top;
            GraphList.UpdateLayout();
            GraphList.Offset = new Vector(0, top);
            _snappedOffset = top;
        }
        finally
        {
            _snapping = false;
        }
    }

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
