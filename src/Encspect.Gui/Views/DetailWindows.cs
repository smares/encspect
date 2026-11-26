using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Encspect.Gui.ViewModels;
using Encspect.Host;

namespace Encspect.Gui.Views;

// The detail windows of the main window. One shared window shows the file opened last and, while
// it is open, follows the list's selection; it first opens centered over the main window and then
// where it was last, also in later sessions. "Open in New Window" adds independent windows, which
// stay on their file, for comparing two files. None is owned by the main window, so each moves on
// its own. Where the windowing system does not let a program place its windows (Wayland), only
// the size is restored and the compositor chooses the position; the saved position is kept as it
// was, so that another backend on the same account still finds it.
//
// Previous and Next follow the list as it fills in, at a cost per added row that does not grow
// with the list (ListNeighbors). When the list is listed again, a window whose row left the list
// moves to the new row of the same file once that is listed; after Refresh or Clear Summary Cache
// it also analyzes the file again, keeping its selected node where the new tree has it.
public sealed class DetailWindows
{
    private const int CascadeStep = 32;

    private readonly Window _main;
    private readonly MainViewModel _model;
    private readonly Analyzer _analyzer;
    private readonly Settings _settings;
    private readonly List<DetailWindow> _independent = [];
    private readonly Dictionary<DetailWindow, ListNeighbors> _neighbors = [];

    // Where the shared window was when it was last neither maximized nor minimized, which is what
    // its placement keeps, with whether it is maximized.
    private WindowPlacement? _normalPlacement;

    public DetailWindows(Window main, MainViewModel model, Analyzer analyzer, Settings settings)
    {
        _main = main;
        _model = model;
        _analyzer = analyzer;
        _settings = settings;
        _model.SelectedRowChanged += (_, _) =>
        {
            if (Shared is { } window && _model.SelectedRow is { } row && row != window.Row)
            {
                Load(window, row);
            }
        };
        _model.RowsView.CollectionChanged += OnRowsChanged;
    }

    public DetailWindow? Shared { get; private set; }

    public IReadOnlyList<DetailWindow> Independent => _independent;

    private IEnumerable<DetailWindow> All => Shared is null ? _independent : _independent.Prepend(Shared);

    // Shows the row in the shared window, opening it first if needed.
    public DetailWindow Open(FileRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (Shared is null)
        {
            DetailWindow window = Create();
            window.StepRequested += (_, delta) =>
            {
                if (window.Row is { } current && _model.Neighbor(current, delta) is { } next)
                {
                    _model.SelectedRow = next;
                }
            };
            window.PositionChanged += (_, _) => RememberNormalPlacement(window);
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == TopLevel.ClientSizeProperty)
                {
                    RememberNormalPlacement(window);
                }
            };
            window.Closing += (_, _) =>
            {
                _settings.DetailWindow = PlacementOf(window);
                _settings.Save();
            };
            window.Closed += (_, _) =>
            {
                Shared = null;
                _neighbors.Remove(window);
            };
            _normalPlacement = Place(window, _settings.DetailWindow);
            Shared = window;
            Load(window, row);
            window.Show();
            RememberNormalPlacement(window);
        }
        else
        {
            Load(Shared, row);
            Shared.Activate();
        }

        if (_model.SelectedRow != row)
        {
            _model.SelectedRow = row;
        }

        return Shared;
    }

    // An independent window on the row, cascaded from the shared one or the main window.
    public DetailWindow OpenNew(FileRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        DetailWindow window = Create();
        window.StepRequested += (_, delta) =>
        {
            if (window.Row is { } current && _model.Neighbor(current, delta) is { } next)
            {
                Load(window, next);
            }
        };
        window.Closed += (_, _) =>
        {
            _independent.Remove(window);
            _neighbors.Remove(window);
        };
        Place(window, null);
        int step = (_independent.Count + 1) * CascadeStep;
        if (CanPlace)
        {
            (PixelRect area, double scaling) = ScreenOf(_main.Position);
            window.Position = ScreenFit.Clamp(new PixelPoint(window.Position.X + step, window.Position.Y + step), new Size(window.Width, window.Height), area, scaling);
        }

        _independent.Add(window);
        Load(window, row);
        window.Show();
        return window;
    }

    public void CloseAll()
    {
        foreach (DetailWindow window in All.ToList())
        {
            window.Close();
        }
    }

    // How often a window's row was looked up in the list to find its neighbors.
    internal int NeighborLookups(DetailWindow window) => _neighbors.TryGetValue(window, out ListNeighbors? neighbors) ? neighbors.Lookups : 0;

    // Whether the windowing system lets a program place its windows; tests set it to exercise both.
    internal bool? CanPlaceOverride { get; set; }

    private bool CanPlace => CanPlaceOverride ?? PlatformBackend.CanPlaceWindows(_main);

    // The screen's working area and scaling the window is placed on, in tests set by hand: the
    // screen under the point, or the main window's, or the primary one.
    internal (PixelRect WorkingArea, double Scaling)? ScreenOverride { get; set; }

    private (PixelRect WorkingArea, double Scaling) ScreenOf(PixelPoint? point)
    {
        if (ScreenOverride is { } screenOverride)
        {
            return screenOverride;
        }

        Avalonia.Platform.Screen? screen = point is { } p ? _main.Screens.ScreenFromPoint(p) : null;
        screen ??= _main.Screens.ScreenFromWindow(_main) ?? _main.Screens.Primary;
        return screen is null ? (new PixelRect(0, 0, 100000, 100000), 1) : (screen.WorkingArea, screen.Scaling);
    }

    private DetailWindow Create()
    {
        var window = new DetailWindow(new DetailViewModel(_analyzer)) { Settings = _settings };
        _neighbors[window] = new ListNeighbors(_model.RowsView);
        return window;
    }

    private void Load(DetailWindow window, FileRow row)
    {
        window.Row = row;
        _ = window.Model.LoadAsync(row.Path, row.FolderError);
        _neighbors[window].Track(row);
        ShowNeighbors(window);
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        FileRow? added = e is { Action: NotifyCollectionChangedAction.Add, NewItems: [FileRow row] } ? row : null;
        foreach ((DetailWindow window, ListNeighbors neighbors) in _neighbors)
        {
            if (added is not null && !neighbors.IsListed && window.Row is { } stale && stale.Path == added.Path)
            {
                window.Row = added;
                neighbors.Track(added);
                ShowNeighbors(window);
                if (_model.IsRereading)
                {
                    _ = window.Model.ReloadAsync();
                }
            }
            else if (neighbors.Update(e))
            {
                ShowNeighbors(window);
            }
        }
    }

    private void ShowNeighbors(DetailWindow window)
    {
        ListNeighbors neighbors = _neighbors[window];
        if (!neighbors.IsListed && window.Row is { } row)
        {
            // A row the list hides (Show Only Audio) is not in the list but has neighbors all the same.
            window.Model.CanGoPrevious = _model.Neighbor(row, -1) is not null;
            window.Model.CanGoNext = _model.Neighbor(row, 1) is not null;
            return;
        }

        window.Model.CanGoPrevious = neighbors.HasPrevious;
        window.Model.CanGoNext = neighbors.HasNext;
    }

    private void RememberNormalPlacement(Window window)
    {
        if (window.WindowState == WindowState.Normal && window.IsVisible)
        {
            _normalPlacement = PlacementOf(window, false);
        }
    }

    // Where the window was last, when that is still on a screen; otherwise centered over the main
    // window. The size is capped at the screen's working area and the position moved so that the
    // title bar is on it. Returns the placement used, or null when centered.
    private WindowPlacement? Place(Window window, WindowPlacement? placement)
    {
        PixelPoint? saved = placement is { X: { } x, Y: { } y } ? new PixelPoint(x, y) : null;
        bool onScreen = saved is { } at && (ScreenOverride is not null || _main.Screens.ScreenFromPoint(new PixelPoint(at.X + 40, at.Y + 20)) is not null);
        if (placement is { Width: > 0, Height: > 0 } && (!CanPlace || onScreen))
        {
            (PixelRect area, double scaling) = ScreenOf(saved is { } p ? new PixelPoint(p.X + 40, p.Y + 20) : null);
            window.Width = placement.Width;
            window.Height = placement.Height;
            ScreenFit.CapSize(window, area, scaling);
            if (CanPlace)
            {
                window.Position = ScreenFit.Clamp(saved!.Value, new Size(window.Width, window.Height), area, scaling);
            }

            if (placement.IsMaximized)
            {
                window.WindowState = WindowState.Maximized;
            }

            return placement with { IsMaximized = false };
        }

        (PixelRect mainArea, double mainScaling) = ScreenOf(null);
        ScreenFit.CapSize(window, mainArea, mainScaling);
        if (CanPlace)
        {
            double scaling = _main.DesktopScaling;
            Size main = _main.ClientSize;
            window.Position = ScreenFit.Clamp(
                new PixelPoint(
                    _main.Position.X + (int)((main.Width - window.Width) * scaling / 2),
                    _main.Position.Y + (int)((main.Height - window.Height) * scaling / 2)),
                new Size(window.Width, window.Height),
                mainArea,
                mainScaling);
        }

        return null;
    }

    private WindowPlacement PlacementOf(Window window)
    {
        bool isMaximized = window.WindowState == WindowState.Maximized;
        if (window.WindowState == WindowState.Normal || _normalPlacement is null)
        {
            return PlacementOf(window, isMaximized);
        }

        return _normalPlacement with { IsMaximized = isMaximized };
    }

    private WindowPlacement PlacementOf(Window window, bool isMaximized) =>
        CanPlace
            ? new(window.Position.X, window.Position.Y, window.ClientSize.Width, window.ClientSize.Height, isMaximized)
            : new(_settings.DetailWindow?.X, _settings.DetailWindow?.Y, window.ClientSize.Width, window.ClientSize.Height, isMaximized);
}
