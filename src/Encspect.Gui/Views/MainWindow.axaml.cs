using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Encspect.Gui.Cache;
using Encspect.Gui.ViewModels;
using Encspect.Host;

namespace Encspect.Gui.Views;

public partial class MainWindow : Window
{
    // For the XAML previewer and loader only.
    public MainWindow()
        : this(Analyzer.CreateDefault(), new Settings())
    {
    }

    // Without a cache, as in tests, every folder is analyzed in full.
    public MainWindow(Analyzer analyzer, Settings settings, MainViewModel? model = null, SummaryCache? cache = null)
    {
        InitializeComponent();
        Model = model ?? new MainViewModel(analyzer, cache: cache);
        DataContext = Model;
        DetailWindows = new DetailWindows(this, Model, analyzer, settings);
        WindowList.Register(this);

        // The grid does not scroll to a row selected from elsewhere, such as Previous and Next.
        Model.SelectedRowChanged += (_, _) =>
        {
            if (Model.SelectedRow is { } row)
            {
                Files.ScrollIntoView(row, null);
            }
        };

        AddColumns(Files);
        Files.DoubleTapped += (_, e) =>
        {
            if (e.Source is Avalonia.Visual visual && visual.FindAncestorOfType<DataGridRow>() is { DataContext: FileRow row })
            {
                DetailWindows.Open(row);
            }
        };

        // The grid moves to the next row on Enter; here Enter opens the file.
        Files.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && Model.SelectedRow is { } row)
            {
                DetailWindows.Open(row);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        NativeMenu.SetMenu(this, CreateMenu());
        Closed += (_, _) =>
        {
            DetailWindows.CloseAll();
            Model.Dispose();
            Model.Cache?.Flush();
        };
    }

    public MainViewModel Model { get; }

    public DetailWindows DetailWindows { get; }

    public void OpenItems(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0)
        {
            Model.Open(FileSource.ForItems(paths));
        }
    }

    private static void AddColumns(DataGrid grid)
    {
        grid.Columns.Add(Column("Name", CompiledBinding.Create<FileRow, string>(r => r.Name), nameof(FileRow.Name), new DataGridLength(1, DataGridLengthUnitType.Star), 180));
        grid.Columns.Add(Column("Format", CompiledBinding.Create<FileRow, string>(r => r.FormatText), nameof(FileRow.FormatText), new DataGridLength(104)));
        grid.Columns.Add(Column("Codec", CompiledBinding.Create<FileRow, string?>(r => r.Codec), nameof(FileRow.Codec), new DataGridLength(128)));
        grid.Columns.Add(Column("Duration", CompiledBinding.Create<FileRow, string?>(r => r.DurationText), nameof(FileRow.Duration), new DataGridLength(82), 0, "numeric"));
        grid.Columns.Add(Column("Bitrate", CompiledBinding.Create<FileRow, string?>(r => r.BitrateText), nameof(FileRow.Bitrate), new DataGridLength(112), 0, "numeric"));
        grid.Columns.Add(Column("Sample Rate", CompiledBinding.Create<FileRow, string?>(r => r.SampleRateText), nameof(FileRow.SampleRate), new DataGridLength(98), 0, "numeric"));
        grid.Columns.Add(Column("Channel Mode", CompiledBinding.Create<FileRow, string?>(r => r.ChannelMode), nameof(FileRow.ChannelMode), new DataGridLength(110)));
        grid.Columns.Add(Column("Encoder", CompiledBinding.Create<FileRow, string?>(r => r.EncoderText), nameof(FileRow.EncoderText), new DataGridLength(150)));
        grid.Columns.Add(Column("Tags", CompiledBinding.Create<FileRow, string?>(r => r.Tags), nameof(FileRow.Tags), new DataGridLength(110)));
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Findings",
            SortMemberPath = nameof(FileRow.FindingsRank),
            Width = new DataGridLength(112),
            CellTemplate = new FuncDataTemplate<FileRow>((_, _) => FindingsCell()),
        });
    }

    // Each column shows text and sorts by the typed figure behind it (SortMemberPath), so durations
    // and bitrates sort as numbers.
    private static DataGridTextColumn Column(string header, CompiledBinding binding, string sortBy, DataGridLength width, double minWidth = 0, string? cellClass = null)
    {
        var column = new DataGridTextColumn { Header = header, Binding = binding, SortMemberPath = sortBy, Width = width };
        if (minWidth > 0)
        {
            column.MinWidth = minWidth;
        }

        if (cellClass is not null)
        {
            column.CellStyleClasses.Add(cellClass);
        }

        return column;
    }

    // The counts of errors, warnings and infos, each after a dot of its severity's color; zero
    // counts are left out.
    private static StackPanel FindingsCell()
    {
        var cell = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Margin = new Avalonia.Thickness(10, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        cell.Children.Add(Count("error", CompiledBinding.Create<FileRow, string?>(r => r.ErrorsText)));
        cell.Children.Add(Count("warning", CompiledBinding.Create<FileRow, string?>(r => r.WarningsText)));
        cell.Children.Add(Count("info", CompiledBinding.Create<FileRow, string?>(r => r.InfosText)));
        return cell;

        static StackPanel Count(string severity, CompiledBinding text)
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            dot.Classes.Add("severity");
            dot.Classes.Add(severity);
            var label = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            label.Bind(TextBlock.TextProperty, text);
            var count = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            count.Children.Add(dot);
            count.Children.Add(label);
            count.Bind(IsVisibleProperty, new CompiledBinding(text.Path!) { Converter = ObjectConverters.IsNotNull });
            return count;
        }
    }

    private NativeMenu CreateMenu()
    {
        var file = Shortcuts.Submenu(
            "File",
            Shortcuts.Item(this, "Open Folder…", Shortcuts.WithCommand(Key.O), () => _ = OpenFolderAsync()),
            Shortcuts.Item(this, "Open Files…", Shortcuts.WithCommand(Key.O, KeyModifiers.Shift), () => _ = OpenFilesAsync()),
            new NativeMenuItemSeparator(),
            Shortcuts.Item(this, "Open Selected File", null, () => OpenSelected(newWindow: false)),
            Shortcuts.Item(this, "Open Selected File in New Window", Shortcuts.WithCommand(Key.Enter), () => OpenSelected(newWindow: true)));
        file.Menu!.Add(new NativeMenuItemSeparator());
        file.Menu.Add(OperatingSystem.IsMacOS()
            ? Shortcuts.Item(this, "Close Window", Shortcuts.WithCommand(Key.W), Close)
            : Shortcuts.Item(this, "Exit", null, Close));

        var view = Shortcuts.Submenu(
            "View",
            Shortcuts.Item(this, "Include Subfolders", Shortcuts.WithCommand(Key.I), () => Model.IncludeSubfolders = !Model.IncludeSubfolders),
            Shortcuts.Item(this, "Refresh", OperatingSystem.IsMacOS() ? Shortcuts.WithCommand(Key.R) : new KeyGesture(Key.F5), Model.Refresh),
            Shortcuts.Item(this, "Stop Analysis", Shortcuts.WithCommand(Key.OemPeriod), Model.Cancel));
        if (Model.Cache is not null)
        {
            view.Menu!.Add(new NativeMenuItemSeparator());
            view.Menu.Add(Shortcuts.Item(this, "Clear Summary Cache", null, Model.ClearCache));
        }
        var menu = new NativeMenu { file, view };
        if (OperatingSystem.IsMacOS())
        {
            menu.Add(Shortcuts.WindowMenu(this));
        }
        else
        {
            menu.Add(Shortcuts.Submenu("Help", Shortcuts.Item(this, "About Encspect", null, () => _ = new AboutWindow().ShowDialog(this))));
        }

        return menu;
    }

    private void OpenSelected(bool newWindow)
    {
        if (Model.SelectedRow is not { } row)
        {
            return;
        }

        if (newWindow)
        {
            DetailWindows.OpenNew(row);
        }
        else
        {
            DetailWindows.Open(row);
        }
    }

    private async Task OpenFolderAsync()
    {
        try
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open Folder", AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                Model.Open(FileSource.ForFolder(path));
            }
        }
        catch (Exception ex) when (UnhandledErrors.IsRecoverable(ex))
        {
            ShowError("The folder dialog failed", ex);
        }
    }

    private async Task OpenFilesAsync()
    {
        try
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open Files", AllowMultiple = true });
            OpenItems(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
        }
        catch (Exception ex) when (UnhandledErrors.IsRecoverable(ex))
        {
            ShowError("The file dialog failed", ex);
        }
    }

    private void ShowError(string what, Exception exception)
    {
        ErrorLog.Write(what, exception);
        Model.ShowError($"{what}: {exception.Message}");
    }

    private void OnStop(object? sender, RoutedEventArgs e) => Model.Cancel();

    private void OnOpenSelected(object? sender, RoutedEventArgs e) => OpenSelected(newWindow: false);

    private void OnOpenSelectedInNewWindow(object? sender, RoutedEventArgs e) => OpenSelected(newWindow: true);

    private async void OnCopyPath(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Model.SelectedRow is { } row && Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(row.Path);
            }
        }
        catch (Exception ex) when (UnhandledErrors.IsRecoverable(ex))
        {
            ShowError("The path was not copied", ex);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { } items)
        {
            OpenItems(items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList());
            e.Handled = true;
        }
    }
}
