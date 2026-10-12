using Avalonia;
using Avalonia.Controls;
using Encspect.Gui.ViewModels;
using Encspect.Host;

namespace Encspect.Gui.Views;

// The detail windows of the main window. One shared window shows the file opened last and, while
// it is open, follows the list's selection; it first opens centered over the main window and then
// where it was last, also in later sessions. "Open in New Window" adds independent windows, which
// stay on their file, for comparing two files. None is owned by the main window, so each moves on
// its own.
public sealed class DetailWindows
{
    private const int CascadeStep = 32;

    private readonly Window _main;
    private readonly MainViewModel _model;
    private readonly Analyzer _analyzer;
    private readonly Settings _settings;
    private readonly List<DetailWindow> _independent = [];

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
        _model.RowsView.CollectionChanged += (_, _) =>
        {
            foreach (DetailWindow window in All)
            {
                UpdateNavigation(window);
            }
        };
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
            window.Closing += (_, _) =>
            {
                _settings.DetailWindow = PlacementOf(window);
                _settings.Save();
            };
            window.Closed += (_, _) => Shared = null;
            Place(window, _settings.DetailWindow);
            Shared = window;
            Load(window, row);
            window.Show();
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
        window.Closed += (_, _) => _independent.Remove(window);
        Place(window, null);
        int step = (_independent.Count + 1) * CascadeStep;
        window.Position = new PixelPoint(window.Position.X + step, window.Position.Y + step);
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

    private DetailWindow Create() => new(new DetailViewModel(_analyzer));

    private void Load(DetailWindow window, FileRow row)
    {
        window.Row = row;
        _ = window.Model.LoadAsync(row.Path);
        UpdateNavigation(window);
    }

    private void UpdateNavigation(DetailWindow window)
    {
        window.Model.CanGoPrevious = window.Row is { } row && _model.Neighbor(row, -1) is not null;
        window.Model.CanGoNext = window.Row is { } current && _model.Neighbor(current, 1) is not null;
    }

    // Where the window was last, when that is still on a screen; otherwise centered over the main
    // window.
    private void Place(Window window, WindowPlacement? placement)
    {
        if (placement is { Width: > 0, Height: > 0 } && _main.Screens.ScreenFromPoint(new PixelPoint(placement.X + 40, placement.Y + 20)) is not null)
        {
            window.Width = placement.Width;
            window.Height = placement.Height;
            window.Position = new PixelPoint(placement.X, placement.Y);
            if (placement.IsMaximized)
            {
                window.WindowState = WindowState.Maximized;
            }

            return;
        }

        double scaling = _main.DesktopScaling;
        Size main = _main.ClientSize;
        window.Position = new PixelPoint(
            _main.Position.X + (int)((main.Width - window.Width) * scaling / 2),
            _main.Position.Y + (int)((main.Height - window.Height) * scaling / 2));
    }

    private static WindowPlacement PlacementOf(Window window) =>
        new(window.Position.X, window.Position.Y, window.ClientSize.Width, window.ClientSize.Height, window.WindowState == WindowState.Maximized);
}
