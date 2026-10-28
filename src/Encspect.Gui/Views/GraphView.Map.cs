using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Encspect.Core;
using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Views;

// The Frame Map: one square for each unit of a series, 10 by 10 device-independent pixels with a gap of 2, in rows like
// text, as many columns as fit in steps of 10. Only the rows on screen are drawn and a square's shade is read from the series
// when it is drawn, so a stream of two million units costs what a screen of squares does. A scrollbar, and beside it a lane of
// marks for the findings and the selected frame, move through the rows.
public sealed partial class GraphView
{
    public static readonly StyledProperty<SeverityMask> ShownSeveritiesProperty =
        AvaloniaProperty.Register<GraphView, SeverityMask>(nameof(ShownSeverities), SeverityMask.All);

    private const double Square = 10;
    private const double SquareGap = 2;
    private const double MapPitch = Square + SquareGap;
    private const double MapPad = 4;
    private const double LaneWidth = 8;
    private const double BarWidth = 14;
    private const string NarrowCaption = "Widen the pane to see 10 columns";
    private const double MarkHeight = 3;
    private const double SwatchWidth = 14;

    private readonly ScrollBar _mapBar = new() { Orientation = Orientation.Vertical, AllowAutoHide = false, IsVisible = false, Width = BarWidth };
    private double _mapScroll;
    private bool _mapSyncing;
    private bool _mapRevealPending;
    private bool _mapKeyboard;
    private int _mapRects;
    private int _mapHelpColumns = -1;
    private (FrameMapGraph Graph, Size Size, double Scale, SeverityMask Shown, MapLayout Layout)? _mapCache;
    private Rect _mapHoverItem;
    private Point? _mapPointer;
    private (int Columns, double Pitch, double ViewHeight)? _mapPrevious;

    // The severities whose findings the map rings and marks.
    public SeverityMask ShownSeverities
    {
        get => GetValue(ShownSeveritiesProperty);
        set => SetValue(ShownSeveritiesProperty, value);
    }

    private enum MapItemKind
    {
        Text,
        Swatch,
        Legend,
        NoValue,
        OutsideRange,
        Finding,
    }

    // A part of the line under the squares: the key's ends and swatches, or an entry of the legend.
    private readonly record struct MapItem(Rect Rect, MapItemKind Kind, int Entry, string Label);

    private readonly record struct MapLayout(
        double Scale,
        int PitchPx,
        int GapPx,
        int Columns,
        int Rows,
        double Left,
        double ViewTop,
        double ViewHeight,
        double GridRight,
        Rect Lane,
        double BarX,
        double ContentHeight,
        bool IsNarrow,
        IReadOnlyList<MapItem> Items)
    {
        public double Pitch => PitchPx / Scale;

        public double MaxScroll => Math.Max(0, ContentHeight - ViewHeight);

        // The right edge of the squares: they are clipped here, so that a pane too narrow for 10 columns never reaches the lane.
        public double ClipRight => GridRight + 2;
    }

    // The answer to a position in the map: a square, an item of the line below, or the lane.
    private readonly record struct MapHit(int Square, MapItem? Item, bool IsLane, double LaneFraction);

    private void InitializeMap()
    {
        VisualChildren.Add(_mapBar);
        LogicalChildren.Add(_mapBar);
        _mapBar.ValueChanged += (_, e) =>
        {
            if (!_mapSyncing)
            {
                _mapScroll = e.NewValue;
                InvalidateVisual();
                RefreshMapHover();
            }
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _mapBar.Measure(new Size(BarWidth, Math.Max(0, availableSize.Height)));
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Graph is FrameMapGraph map && finalSize.Width >= 80 && finalSize.Height >= 60)
        {
            MapLayout layout = MapLayoutFor(map, finalSize);
            AnchorMapScroll(map, layout);
            if (_mapRevealPending)
            {
                RevealMarked(map, layout);
            }

            SyncMapBar(layout);
            _mapBar.Arrange(new Rect(layout.BarX, layout.ViewTop, BarWidth, layout.ViewHeight));
            if (_mapHelpColumns != layout.Columns)
            {
                AutomationProperties.SetHelpText(this, MapHelp(map, finalSize));
            }
        }
        else
        {
            _mapBar.IsVisible = false;
            _mapBar.Arrange(default);
            _mapPrevious = null;
        }

        return finalSize;
    }

    // The columns the map has now, for tests and the help text; null for anything but a map.
    public int? MapColumns => Graph is FrameMapGraph map && Bounds.Width >= 80 && Bounds.Height >= 60 ? MapLayoutFor(map, Bounds.Size).Columns : null;

    // How far the map is scrolled, in device-independent pixels, and how far it can be.
    public (double Offset, double Maximum, double ViewHeight)? MapScroll => Graph is FrameMapGraph map && Bounds.Width >= 80 && Bounds.Height >= 60
        ? (_mapScroll, MapLayoutFor(map, Bounds.Size).MaxScroll, MapLayoutFor(map, Bounds.Size).ViewHeight)
        : null;

    // The rectangles the last render drew; a render of rows off screen would draw as many as the units, which a test forbids.
    public int MapRectanglesDrawn => _mapRects;

    // The rectangle of a square in the view's coordinates, or null when its row is off screen or the view is not a map.
    public Rect? MapSquareRect(int square)
    {
        if (Graph is not FrameMapGraph map || Bounds.Width < 80 || Bounds.Height < 60 || (uint)square >= (uint)map.Count)
        {
            return null;
        }

        MapLayout layout = MapLayoutFor(map, Bounds.Size);
        Rect rect = SquareRect(layout, square, (int)Math.Round(_mapScroll * layout.Scale));
        return rect.Bottom > layout.ViewTop && rect.Y < layout.ViewTop + layout.ViewHeight ? rect : null;
    }

    // The marks of the lane beside the scrollbar: where each is from the top of the lane and in what color, for tests. A finding
    // is its severity, the selected frame null.
    public IReadOnlyList<(double Y, Severity? Severity)>? MapLaneMarks()
    {
        if (Graph is not FrameMapGraph map || Bounds.Width < 80 || Bounds.Height < 60)
        {
            return null;
        }

        MapLayout layout = MapLayoutFor(map, Bounds.Size);
        var marks = new List<(double, Severity?)>();
        foreach (int square in map.FindingSquares(ShownSeverities))
        {
            marks.Add((LaneY(layout, square), map.SeverityAt(square, ShownSeverities)));
        }

        if (MarkedIndex is { } marked && marked < map.Count)
        {
            marks.Add((LaneY(layout, marked), null));
        }

        return marks;
    }

    // The lane in the view's coordinates, for tests.
    public Rect? MapLane => Graph is FrameMapGraph map && Bounds.Width >= 80 && Bounds.Height >= 60 ? MapLayoutFor(map, Bounds.Size).Lane : null;

    // The items under the squares (key swatches, legend entries) with their rectangles, for tests.
    internal IReadOnlyList<(Rect Rect, string Label, int Entry, bool IsSwatch)>? MapItems() =>
        Graph is FrameMapGraph map && Bounds.Width >= 80 && Bounds.Height >= 60
            ? [.. MapLayoutFor(map, Bounds.Size).Items.Select(i => (i.Rect, i.Label, i.Entry, i.Kind is MapItemKind.Swatch or MapItemKind.Legend))]
            : null;

    // ---- Layout

    // Avalonia 12's visual root is a presentation source, not the top level, so the top level is asked for.
    private double RenderScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private MapLayout MapLayoutFor(FrameMapGraph map, Size size)
    {
        double scale = RenderScale;
        if (_mapCache is { } cached && ReferenceEquals(cached.Graph, map) && cached.Size == size && cached.Scale == scale && cached.Shown == ShownSeverities)
        {
            return cached.Layout;
        }

        MapLayout layout = MeasureMap(map, size, scale);
        _mapCache = (map, size, scale, ShownSeverities, layout);
        return layout;
    }

    private MapLayout MeasureMap(FrameMapGraph map, Size size, double scale)
    {
        int count = map.Count;
        int pitchPx = Math.Max(2, (int)Math.Round(MapPitch * scale, MidpointRounding.AwayFromZero));
        int gapPx = Math.Max(1, (int)Math.Round(SquareGap * scale, MidpointRounding.AwayFromZero));
        double labelWidth = Text(count.ToString("N0", CultureInfo.InvariantCulture), AxisSize, Brushes.Black).Width;
        double left = 12 + labelWidth + 8;
        double barX = size.Width - 8 - BarWidth;
        double laneX = barX - 4 - LaneWidth;
        double gridRight = laneX - 8;
        int available = (int)Math.Floor(Math.Max(0, gridRight - left) * scale);
        int columns = Math.Max(10, (available + gapPx) / pitchPx / 10 * 10);
        int rows = (count + columns - 1) / columns;

        (List<MapItem> items, int lines) = MapItems(map, size.Width - 12);
        double bottom = (lines * LegendLine) + 8;
        double viewTop = CaptionHeight;
        double viewHeight = Math.Max(32, size.Height - viewTop - bottom);
        double lineTop = viewTop + viewHeight + 6;
        var placed = items.Select(i => i with { Rect = new Rect(i.Rect.X, lineTop + i.Rect.Y, i.Rect.Width, i.Rect.Height) }).ToList();
        double content = MapPad + (rows * (pitchPx / scale)) + MapPad;
        bool isNarrow = (columns * pitchPx) - gapPx > available;
        return new MapLayout(scale, pitchPx, gapPx, columns, rows, left, viewTop, viewHeight, gridRight, new Rect(laneX, viewTop, LaneWidth, viewHeight), barX, content, isNarrow, placed);
    }

    // The key or legend and its extras as items, in lines of LegendLine from y 0, at most three; each line starts at x 12.
    private (List<MapItem> Items, int Lines) MapItems(FrameMapGraph map, double right)
    {
        var items = new List<MapItem>();
        int line = 0;
        double x = 12;

        void Flow(double width, Action<double, double> place)
        {
            if (x + width > right && x > 12 && line + 1 < MaxLegendLines)
            {
                line++;
                x = 12;
            }

            if (line < MaxLegendLines)
            {
                place(x, line * LegendLine);
            }

            x += width;
        }

        void Entry(MapItemKind kind, int entry, string label)
        {
            double width = 16 + Text(label, TextSize, Brushes.Black).Width + 14;
            Flow(width, (px, py) => items.Add(new MapItem(new Rect(px, py, width, LegendLine), kind, entry, label)));
        }

        if (map.IsCategory)
        {
            CategorySeries series = map.CategorySeries!;
            for (int c = 0; c < series.Categories.Count; c++)
            {
                if (series.Counts[c] > 0)
                {
                    Entry(MapItemKind.Legend, c, $"{series.Categories[c].Name} {CategoryGraph.SharePercent(series.Counts[c], series.Total)}");
                }
            }

            long empty = map.EmptyCount;
            if (empty > 0)
            {
                Entry(MapItemKind.NoValue, -1, $"No value · {Format.Count(empty, "frame", "frames")}");
            }
        }
        else
        {
            AddKey(map, items, Flow, right);
            if (map.NoValueCount > 0)
            {
                Entry(MapItemKind.NoValue, -1, $"No value · {Format.Count(map.NoValueCount, "frame", "frames")}");
            }

            if (map.ClampedCount > 0)
            {
                Entry(MapItemKind.OutsideRange, -1, $"Outside the declared range · {Format.Count(map.ClampedCount, "frame", "frames")}");
            }
        }

        if (map.FindingSquares(ShownSeverities).Length > 0)
        {
            Entry(MapItemKind.Finding, FrameMapGraph.FindingEntry, "Finding (first case)");
        }

        return (items, Math.Min(MaxLegendLines, line + 1));
    }

    // The thin key line: the smallest value, a swatch for each class from low to high, the largest value.
    private void AddKey(FrameMapGraph map, List<MapItem> items, Action<double, Action<double, double>> flow, double right)
    {
        IReadOnlyList<MapKeyEntry> key = map.Key;
        if (key.Count == 0)
        {
            return;
        }

        if (map.IsSingleValue)
        {
            string text = map.NoValueCount == 0 ? $"Every frame {map.KeyLow}" : $"{map.KeyLow} · {Format.Count(key[0].Count, "frame", "frames")}";
            double width = 16 + Text(text, TextSize, Brushes.Black).Width + 14;
            flow(width, (px, py) =>
            {
                items.Add(new MapItem(new Rect(px, py, 14, LegendLine), MapItemKind.Swatch, 0, text));
                items.Add(new MapItem(new Rect(px + 20, py, width - 20, LegendLine), MapItemKind.Text, -1, text));
            });
            return;
        }

        string low = map.KeyLow;
        string high = map.KeyHigh;
        double lowWidth = Text(low, TextSize, Brushes.Black).Width;
        double highWidth = Text(high, TextSize, Brushes.Black).Width;
        double swatch = Math.Clamp((right - 12 - lowWidth - highWidth - 24) / key.Count - 2, 5, SwatchWidth);
        double total = lowWidth + 6 + (key.Count * (swatch + 2)) + 4 + highWidth + 14;
        flow(total, (px, py) =>
        {
            items.Add(new MapItem(new Rect(px, py, lowWidth, LegendLine), MapItemKind.Text, -1, low));
            double sx = px + lowWidth + 6;
            foreach (MapKeyEntry entry in key)
            {
                items.Add(new MapItem(new Rect(sx, py, swatch, LegendLine), MapItemKind.Swatch, entry.Class, string.Empty));
                sx += swatch + 2;
            }

            items.Add(new MapItem(new Rect(sx + 4, py, highWidth, LegendLine), MapItemKind.Text, -1, high));
        });
    }

    private Rect SquareRect(MapLayout layout, int square, int scrollPx)
    {
        double s = layout.Scale;
        int row = square / layout.Columns;
        int column = square % layout.Columns;
        int leftPx = (int)Math.Round(layout.Left * s);
        int topPx = (int)Math.Round(layout.ViewTop * s) + (int)Math.Round(MapPad * s);
        int size = layout.PitchPx - layout.GapPx;
        return new Rect((leftPx + (column * layout.PitchPx)) / s, (topPx + (row * layout.PitchPx) - scrollPx) / s, size / s, size / s);
    }

    private double LaneY(MapLayout layout, int square)
    {
        // The lane is as high as the pane, so a file that fits in it is marked where its rows are.
        double top = MapPad + ((square / layout.Columns) * layout.Pitch);
        double extent = Math.Max(layout.ContentHeight, layout.ViewHeight);
        return Math.Clamp(top / extent * layout.Lane.Height, 0, Math.Max(0, layout.Lane.Height - MarkHeight));
    }

    // ---- Scrolling

    private void SyncMapBar(MapLayout layout)
    {
        _mapSyncing = true;
        try
        {
            _mapScroll = Math.Clamp(_mapScroll, 0, layout.MaxScroll);
            _mapBar.IsVisible = layout.MaxScroll > 0;
            _mapBar.Minimum = 0;
            _mapBar.Maximum = layout.MaxScroll;
            _mapBar.ViewportSize = layout.ViewHeight;
            _mapBar.SmallChange = layout.Pitch;
            _mapBar.LargeChange = Math.Max(layout.Pitch, layout.ViewHeight - layout.Pitch);
            _mapBar.Value = _mapScroll;
        }
        finally
        {
            _mapSyncing = false;
        }
    }

    private void SetMapScroll(MapLayout layout, double value)
    {
        double next = Math.Clamp(value, 0, layout.MaxScroll);
        if (next != _mapScroll)
        {
            _mapScroll = next;
            SyncMapBar(layout);
            InvalidateVisual();
            RefreshMapHover();
        }
    }

    // Scrolls the row of a square into view: when it is not wholly on screen, by the least; a row off screen altogether goes to
    // the middle if centered. A row partly on screen never recenters the map, as a click on a half-visible square would.
    private void EnsureMapVisible(MapLayout layout, int square, bool centered)
    {
        double top = MapPad + ((square / layout.Columns) * layout.Pitch);
        double bottom = top + layout.Pitch;
        if (top - MapPad >= _mapScroll && bottom + MapPad <= _mapScroll + layout.ViewHeight)
        {
            return;
        }

        bool isPartlyOnScreen = bottom > _mapScroll && top < _mapScroll + layout.ViewHeight;
        if (centered && !isPartlyOnScreen)
        {
            SetMapScroll(layout, top - ((layout.ViewHeight - layout.Pitch) / 2));
        }
        else
        {
            SetMapScroll(layout, top - MapPad < _mapScroll ? top - MapPad : bottom + MapPad - layout.ViewHeight);
        }
    }

    private void RevealMarked(FrameMapGraph map, MapLayout layout)
    {
        _mapRevealPending = false;
        if (MarkedIndex is { } marked && marked < map.Count)
        {
            EnsureMapVisible(layout, marked, centered: true);
        }
    }

    private void MapPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == GraphProperty)
        {
            _mapScroll = 0;
            _mapKeyboard = false;
            _mapCache = null;
            _mapHelpColumns = -1;
            _mapRevealPending = Graph is FrameMapGraph;
            _mapPointer = null;
            _mapPrevious = null;
            if (Graph is FrameMapGraph map && Bounds.Width >= 80 && Bounds.Height >= 60)
            {
                MapLayout layout = MapLayoutFor(map, Bounds.Size);
                RevealMarked(map, layout);
                SyncMapBar(layout);
                _mapPrevious = (layout.Columns, layout.Pitch, layout.ViewHeight);
            }
            else
            {
                _mapBar.IsVisible = false;
            }
        }
        else if (change.Property == MarkedIndexProperty && Graph is FrameMapGraph marked)
        {
            _mapRevealPending = true;
            if (Bounds.Width >= 80 && Bounds.Height >= 60)
            {
                RevealMarked(marked, MapLayoutFor(marked, Bounds.Size));
            }

            UpdateAutomation(marked);
        }
        else if (change.Property == ShownSeveritiesProperty && Graph is FrameMapGraph shown)
        {
            UpdateAutomation(shown);
        }
    }

    // A resize keeps the top row's first frame at the top, and a selected frame that was on screen on screen.
    private void AnchorMapScroll(FrameMapGraph map, MapLayout layout)
    {
        if (_mapPrevious is { } old && (old.Columns != layout.Columns || old.Pitch != layout.Pitch || old.ViewHeight != layout.ViewHeight))
        {
            int topFrame = Math.Max(0, (int)Math.Round(_mapScroll / old.Pitch)) * old.Columns;
            int? selected = null;
            if (MarkedIndex is { } marked && marked < map.Count)
            {
                double top = MapPad + ((marked / old.Columns) * old.Pitch);
                if (top + old.Pitch > _mapScroll && top < _mapScroll + old.ViewHeight)
                {
                    selected = marked;
                }
            }

            _mapScroll = Math.Clamp(topFrame / layout.Columns * layout.Pitch, 0, layout.MaxScroll);
            SyncMapBar(layout);
            if (selected is { } keep)
            {
                EnsureMapVisible(layout, keep, centered: false);
            }
        }

        _mapPrevious = (layout.Columns, layout.Pitch, layout.ViewHeight);
    }

    // The square under a pointer that has not moved changes when the rows move under it.
    private void RefreshMapHover()
    {
        if (Graph is FrameMapGraph { Count: > 0 } map && !_mapKeyboard && _mapPointer is { } point)
        {
            UpdateMapHover(map, point);
        }
    }

    // ---- Pointer and keys

    private MapHit? MapHitAt(FrameMapGraph map, Point point)
    {
        if (Bounds.Width < 80 || Bounds.Height < 60)
        {
            return null;
        }

        MapLayout layout = MapLayoutFor(map, Bounds.Size);
        foreach (MapItem item in layout.Items)
        {
            if (item.Kind != MapItemKind.Text && item.Rect.Contains(point))
            {
                return new MapHit(-1, item, false, 0);
            }
        }

        if (new Rect(layout.Lane.X - 2, layout.Lane.Y, layout.Lane.Width + 4, layout.Lane.Height).Contains(point))
        {
            return new MapHit(-1, null, true, (point.Y - layout.Lane.Y) / layout.Lane.Height);
        }

        if (point.Y < layout.ViewTop || point.Y >= layout.ViewTop + layout.ViewHeight || point.X < layout.Left || point.X >= Math.Min(layout.Left + (layout.Columns * layout.Pitch), layout.ClipRight))
        {
            return null;
        }

        int column = (int)Math.Floor((point.X - layout.Left) / layout.Pitch);
        int row = (int)Math.Floor((point.Y - layout.ViewTop - MapPad + _mapScroll) / layout.Pitch);
        int square = (row * layout.Columns) + column;
        return row >= 0 && column < layout.Columns && square < map.Count ? new MapHit(square, null, false, 0) : null;
    }

    private int? MapSquareAt(FrameMapGraph map, Point point) => MapHitAt(map, point) is { Square: >= 0 } hit ? hit.Square : null;

    private string MapItemText(FrameMapGraph map, MapItem item) => item.Kind switch
    {
        MapItemKind.Swatch => map.DescribeKeyEntry(item.Entry),
        MapItemKind.Legend => CategoryGraph.DescribeCategory(map.CategorySeries!, item.Entry),
        MapItemKind.NoValue => item.Label,
        MapItemKind.OutsideRange => item.Label + ". A value outside the declared range goes to the end class, one between declared steps to the nearest step",
        MapItemKind.Finding => "A ring marks the square of the first case of a finding; clicking steps through the rings. The Findings tab lists them all",
        _ => string.Empty,
    };

    private void SetMapHover(FrameMapGraph map, int? square, string? text = null)
    {
        string? shown = text ?? (square is { } s ? map.Describe(s, ShownSeverities) : null);
        if (_hover != square || _hoverText != shown)
        {
            _hover = square;
            _hoverText = shown;
            UpdateAutomation(map);
            InvalidateVisual();
        }
    }

    private void MapPointerMoved(FrameMapGraph map, Point point)
    {
        _mapKeyboard = false;
        _mapPointer = point;
        UpdateMapHover(map, point);
    }

    private void UpdateMapHover(FrameMapGraph map, Point point)
    {
        MapHit? hit = MapHitAt(map, point);
        if (hit is { Square: >= 0 } square)
        {
            SetMapHover(map, square.Square);
        }
        else if (hit is { Item: { } item } && item.Kind != MapItemKind.Text)
        {
            _mapHoverItem = item.Rect;
            SetMapHover(map, null, MapItemText(map, item));
        }
        else
        {
            SetMapHover(map, null);
        }
    }

    private bool MapPointerPressed(FrameMapGraph map, PointerPressedEventArgs e)
    {
        if (_mapBar.IsVisible && e.Source is Visual source && (ReferenceEquals(source, _mapBar) || _mapBar.IsVisualAncestorOf(source)))
        {
            return false;
        }

        Point point = e.GetPosition(this);
        MapHit? hit = MapHitAt(map, point);
        if (hit is null)
        {
            return false;
        }

        if (hit.Value.IsLane)
        {
            MapLayout layout = MapLayoutFor(map, Bounds.Size);
            double y = hit.Value.LaneFraction * Math.Max(layout.ContentHeight, layout.ViewHeight);
            SetMapScroll(layout, y - (layout.ViewHeight / 2));
            return true;
        }

        if (hit.Value.Item is { } item)
        {
            if (item.Kind is MapItemKind.Swatch or MapItemKind.Legend or MapItemKind.Finding)
            {
                CategoryClicked?.Invoke(this, item.Entry);
            }

            return true;
        }

        _mapKeyboard = false;
        SetMapHover(map, hit.Value.Square);
        PointClicked?.Invoke(this, hit.Value.Square);
        return true;
    }

    private bool MapKey(FrameMapGraph map, KeyEventArgs e)
    {
        if (Bounds.Width < 80 || Bounds.Height < 60)
        {
            return false;
        }

        MapLayout layout = MapLayoutFor(map, Bounds.Size);
        int count = map.Count;
        if (e.Key is Key.Enter or Key.Space)
        {
            if (_hover is { } chosen)
            {
                PointClicked?.Invoke(this, chosen);
                return true;
            }

            return false;
        }

        int rowsOnScreen = Math.Max(1, (int)(layout.ViewHeight / layout.Pitch));
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End))
        {
            return false;
        }

        int? current = _hover is { } h && _mapKeyboard ? h : null;
        int next;
        if (current is null)
        {
            // The first key puts the cursor on the selected frame, else on the first square on screen.
            next = MarkedIndex is { } marked && marked < count ? marked : Math.Min(count - 1, (int)(_mapScroll / layout.Pitch) * layout.Columns);
            if (e.Key == Key.Home)
            {
                next = 0;
            }
            else if (e.Key == Key.End)
            {
                next = count - 1;
            }
        }
        else
        {
            next = e.Key switch
            {
                Key.Left => current.Value - 1,
                Key.Right => current.Value + 1,
                Key.Up => current.Value - layout.Columns,
                Key.Down => current.Value + layout.Columns,
                Key.PageUp => current.Value - (rowsOnScreen * layout.Columns),
                Key.PageDown => current.Value + (rowsOnScreen * layout.Columns),
                Key.Home => 0,
                _ => count - 1,
            };
        }

        next = Math.Clamp(next, 0, count - 1);
        _mapKeyboard = true;
        SetMapHover(map, next);
        EnsureMapVisible(layout, next, centered: false);
        InvalidateVisual();
        return true;
    }

    private void MapWheel(FrameMapGraph map, PointerWheelEventArgs e)
    {
        if (Bounds.Width < 80 || Bounds.Height < 60)
        {
            return;
        }

        MapLayout layout = MapLayoutFor(map, Bounds.Size);
        double before = _mapScroll;
        SetMapScroll(layout, _mapScroll - (e.Delta.Y * layout.Pitch * 3));
        e.Handled = e.Handled || before != _mapScroll;
    }

    // ---- Text for the automation tree

    private string MapHelp(FrameMapGraph map, Size size)
    {
        var parts = new List<string>();
        string frames = Format.Count(map.Count, map.HasOwnSquares ? "point" : "frame", map.HasOwnSquares ? "points" : "frames");
        if (size.Width >= 80 && size.Height >= 60)
        {
            MapLayout layout = MapLayoutFor(map, size);
            _mapHelpColumns = layout.Columns;
            parts.Add($"Frame map of {frames}, {layout.Columns} to a row");
            if (layout.IsNarrow)
            {
                parts.Add(NarrowCaption);
            }
        }
        else
        {
            parts.Add($"Frame map of {frames}");
        }

        if (map.IsCategory)
        {
            parts.Add("Legend: " + string.Join(", ", LegendEntries(map)));
        }
        else if (map.Key.Count > 0)
        {
            string direction = ActualThemeVariant == ThemeVariant.Dark ? "lighter" : "darker";
            if (map.IsSingleValue)
            {
                parts.Add($"Every frame {map.KeyLow}");
            }
            else if (map.UsesRange)
            {
                parts.Add($"{map.KeyRange}, {direction} is higher, {map.ClassCount} classes of equal width");
            }
            else
            {
                parts.Add($"{map.KeyRange}, {direction} is higher, {map.ClassCount} values");
            }
        }

        int findings = map.FindingSquares(ShownSeverities).Length;
        if (findings > 0)
        {
            parts.Add($"{Format.Count(findings, "frame", "frames")} with findings");
        }

        if (_hoverText is { Length: > 0 } text)
        {
            parts.Add("Cursor: " + text);
        }

        return string.Join(". ", parts);
    }

    private static List<string> LegendEntries(FrameMapGraph map)
    {
        CategorySeries series = map.CategorySeries!;
        return [.. Enumerable.Range(0, series.Categories.Count).Where(c => series.Counts[c] > 0).Select(c => $"{series.Categories[c].Name} {CategoryGraph.SharePercent(series.Counts[c], series.Total)}")];
    }

    // ---- Drawing

    private void RenderMap(DrawingContext context, FrameMapGraph map)
    {
        _mapRects = 0;
        MapLayout layout = MapLayoutFor(map, Bounds.Size);
        _mapScroll = Math.Clamp(_mapScroll, 0, layout.MaxScroll);
        bool isDark = ActualThemeVariant == ThemeVariant.Dark;
        Color surface = Palette.SurfaceColor(this);
        IBrush secondary = Palette.Secondary(this);
        IBrush foreground = Palette.Foreground(this);

        context.DrawText(Text(layout.IsNarrow ? NarrowCaption : map.Caption, TextSize, secondary), new Point(4, 2));
        var view = new Rect(0, layout.ViewTop, Math.Max(0, layout.ClipRight), layout.ViewHeight);
        int scrollPx = (int)Math.Round(_mapScroll * layout.Scale);
        IBrush[] classBrushes = [];
        IBrush[] categoryBrushes = [];
        CategoryStyle[] styles = [];
        if (map.IsCategory)
        {
            styles = CategoryColors.For(map.CategorySeries!, isDark, surface);
            categoryBrushes = [.. styles.Select(s => (IBrush)new ImmutableSolidColorBrush(s.Color))];
        }
        else
        {
            classBrushes = [.. MapColors.Classes(map.ClassCount, map.IsMultiHue, isDark).Select(c => (IBrush)new ImmutableSolidColorBrush(c))];
        }

        using (context.PushClip(view))
        {
            DrawMapCells(context, map, layout, scrollPx, classBrushes, categoryBrushes, styles, surface, secondary, foreground);
        }

        DrawMapLane(context, map, layout, foreground);
        DrawMapItems(context, map, layout, classBrushes, categoryBrushes, styles, surface, secondary, foreground);
        if (MapTooltipFor(map, layout, scrollPx) is var (label, box))
        {
            context.DrawRectangle(Palette.TooltipBackground(this), new Pen(Palette.Grid(this, 0.3), 1), box, 4, 4);
            context.DrawText(label, new Point(box.X + 8, box.Y + 4));
        }
    }

    // What the pointer rests on says, in a box above the square or the key's swatch, below it where there is no room above, else
    // beside it; never over the thing it describes.
    private (FormattedText Label, Rect Box)? MapTooltipFor(FrameMapGraph map, MapLayout layout, int scrollPx)
    {
        if (_hoverText is not { } text)
        {
            return null;
        }

        Rect target;
        if (_hover is { } hover && hover < map.Count)
        {
            target = SquareRect(layout, hover, scrollPx);
        }
        else if (_hover is null)
        {
            target = _mapHoverItem;
        }
        else
        {
            return null;
        }

        FormattedText label = Text(text, TextSize, Palette.Foreground(this));
        label.MaxTextWidth = Math.Max(160, Bounds.Width - 40);
        return (label, PlaceTooltip(new Size(label.Width + 16, label.Height + 8), target, Bounds.Size));
    }

    // The box of a tooltip of this size for a target: above it when that fits in the view, else below, else to the right or left.
    // Its left edge follows the target's and it stays inside the bounds; it never intersects the target.
    internal static Rect PlaceTooltip(Size size, Rect target, Size bounds)
    {
        const double Gap = 4;
        const double Margin = 2;
        double x = Math.Clamp(target.X, Margin, Math.Max(Margin, bounds.Width - Margin - size.Width));
        double above = target.Y - Gap - size.Height;
        if (above >= Margin)
        {
            return new Rect(new Point(x, above), size);
        }

        double below = target.Bottom + Gap;
        if (below + size.Height <= bounds.Height - Margin)
        {
            return new Rect(new Point(x, below), size);
        }

        double y = Math.Clamp(target.Y, Margin, Math.Max(Margin, bounds.Height - Margin - size.Height));
        return target.Right + Gap + size.Width <= bounds.Width - Margin
            ? new Rect(new Point(target.Right + Gap, y), size)
            : new Rect(new Point(Math.Max(Margin, target.X - Gap - size.Width), y), size);
    }

    // The tooltip box now shown, for tests; null when there is none.
    internal Rect? MapTooltipRect()
    {
        if (Graph is not FrameMapGraph map || Bounds.Width < 80 || Bounds.Height < 60)
        {
            return null;
        }

        MapLayout layout = MapLayoutFor(map, Bounds.Size);
        return MapTooltipFor(map, layout, (int)Math.Round(_mapScroll * layout.Scale))?.Box;
    }

    // The line above the squares, for tests.
    internal string? MapCaption => Graph is FrameMapGraph map && Bounds.Width >= 80 && Bounds.Height >= 60
        ? (MapLayoutFor(map, Bounds.Size).IsNarrow ? NarrowCaption : map.Caption)
        : null;

    private void DrawMapCells(DrawingContext context, FrameMapGraph map, MapLayout layout, int scrollPx, IBrush[] classBrushes, IBrush[] categoryBrushes, CategoryStyle[] styles, Color surface, IBrush secondary, IBrush foreground)
    {
        double s = layout.Scale;
        int viewHeightPx = (int)Math.Round(layout.ViewHeight * s);
        int padPx = (int)Math.Round(MapPad * s);
        int firstRow = Math.Max(0, (scrollPx - padPx) / layout.PitchPx);
        int lastRow = Math.Min(layout.Rows - 1, (scrollPx + viewHeightPx) / layout.PitchPx);
        int count = map.Count;
        var outline = new Pen(Palette.Grid(this), 1);
        bool highContrast = HasHighContrast();
        Span<byte> codes = stackalloc byte[CategorySeries.MaxValuesPerUnit];
        FrameMapGraph.CellReader reader = map.IsCategory ? map.Reader() : default;
        long[] tally = map.IsCategory ? new long[map.CategorySeries!.Categories.Count] : [];
        double[] heights = map.IsCategory ? new double[tally.Length] : [];
        IBrush surfaceBrush = new ImmutableSolidColorBrush(surface);
        int firstSquare = firstRow * layout.Columns;
        int lastSquare = Math.Min(count - 1, ((lastRow + 1) * layout.Columns) - 1);

        for (int row = firstRow; row <= lastRow; row++)
        {
            int start = row * layout.Columns;
            if (row % 5 == 0)
            {
                FormattedText label = Text((start + 1).ToString("N0", CultureInfo.InvariantCulture), AxisSize, secondary);
                Rect first = SquareRect(layout, start, scrollPx);
                context.DrawText(label, new Point(layout.Left - 8 - label.Width, first.Y + ((first.Height - label.Height) / 2)));
            }

            int end = Math.Min(count, start + layout.Columns);
            for (int square = start; square < end; square++)
            {
                Rect rect = SquareRect(layout, square, scrollPx);
                if (map.IsCategory)
                {
                    DrawCategoryCell(context, map, square, rect, layout.Scale, ref reader, codes, tally, heights, categoryBrushes, styles, surface, outline, highContrast);
                }
                else if (map.ClassAt(square) is var shade and >= 0)
                {
                    context.FillRectangle(classBrushes[shade], rect);
                    _mapRects++;
                }
                else
                {
                    DrawEmptyCell(context, rect, outline);
                }
            }
        }

        // Rings: findings in the text color with a line of the surface inside, the selected frame in the accent color outside the
        // square in the gap, the keyboard cursor dashed.
        var ring = new Pen(foreground, 1.5);
        var inner = new Pen(surfaceBrush, 1);
        foreach (int square in map.FindingSquares(ShownSeverities))
        {
            if (square < firstSquare || square > lastSquare)
            {
                continue;
            }

            Rect rect = SquareRect(layout, square, scrollPx);
            context.DrawRectangle(null, ring, rect.Deflate(0.75));
            context.DrawRectangle(null, inner, rect.Deflate(2));
            _mapRects += 2;
        }

        if (MarkedIndex is { } marked && marked >= firstSquare && marked <= lastSquare)
        {
            context.DrawRectangle(null, new Pen(new ImmutableSolidColorBrush(Palette.Accent(this)), 2), SquareRect(layout, marked, scrollPx).Inflate(1));
            _mapRects++;
        }

        if (_mapKeyboard && _hover is { } cursor && cursor >= firstSquare && cursor <= lastSquare)
        {
            context.DrawRectangle(null, new Pen(foreground, 1, dashStyle: DashStyle.Dash), SquareRect(layout, cursor, scrollPx).Inflate(1.5));
            _mapRects++;
        }
    }

    // A unit without a value is an empty square with a hairline outline, so that its place is seen.
    private void DrawEmptyCell(DrawingContext context, Rect rect, IPen outline)
    {
        context.DrawRectangle(null, outline, rect.Deflate(0.5));
        _mapRects++;
    }

    private void DrawCategoryCell(DrawingContext context, FrameMapGraph map, int square, Rect rect, double scale, ref FrameMapGraph.CellReader reader, Span<byte> codes, long[] tally, double[] heights, IBrush[] brushes, CategoryStyle[] styles, Color surface, IPen outline, bool highContrast)
    {
        int n = reader.Read(square, codes);
        if (n <= 0)
        {
            DrawEmptyCell(context, rect, outline);
            return;
        }

        if (!FrameMapGraph.HasCategory(n, codes))
        {
            DrawEmptyCell(context, rect, outline);
            return;
        }

        CategorySeries series = map.CategorySeries!;
        bool Hatched(int code) => styles[code].IsHatched || (highContrast && code % 2 == 1);
        if (n == 1)
        {
            FillCategory(context, rect, codes[0], brushes, Hatched(codes[0]), surface);
            return;
        }

        if (map.Layout == FrameMapGraph.CellLayout.Split && n == series.ValuesPerUnit)
        {
            // Two values side by side, three in a row, four as 2 x 2 in reading order, a physical pixel apart; the cells of a row
            // and of a column differ by at most one pixel.
            int columns = n == 4 ? 2 : n;
            int rows = n == 4 ? 2 : 1;
            int leftPx = (int)Math.Round(rect.X * scale);
            int topPx = (int)Math.Round(rect.Y * scale);
            int widthPx = (int)Math.Round(rect.Width * scale);
            int heightPx = (int)Math.Round(rect.Height * scale);
            int gapPx = widthPx >= (3 * columns) - 1 && heightPx >= (3 * rows) - 1 ? 1 : 0;
            for (int i = 0; i < n; i++)
            {
                if (codes[i] == CategorySeries.None)
                {
                    continue;
                }

                (int x, int width) = SplitCell(widthPx, columns, i % columns, gapPx);
                (int y, int height) = SplitCell(heightPx, rows, i / columns, gapPx);
                FillCategory(context, new Rect((leftPx + x) / scale, (topPx + y) / scale, width / scale, height / scale), codes[i], brushes, Hatched(codes[i]), surface);
            }

            return;
        }

        // The share of each category in the square, from the bottom up in the order of the categories.
        Array.Clear(tally);
        for (int i = 0; i < n; i++)
        {
            if (codes[i] != CategorySeries.None)
            {
                tally[codes[i]]++;
            }
        }

        CategoryColumns.StackHeights(tally, rect.Height, heights);
        double bottom = rect.Bottom;
        for (int c = 0; c < tally.Length; c++)
        {
            if (heights[c] <= 0)
            {
                continue;
            }

            double top = bottom - heights[c];
            FillCategory(context, new Rect(rect.X, top, rect.Width, bottom - top), c, brushes, Hatched(c), surface);
            bottom = top;
        }
    }

    // Where cell index of cells starts and how wide it is, in physical pixels, in a run of sizePx with gapPx between cells: the
    // sizes differ by at most one pixel, the wider cells last.
    internal static (int Start, int Size) SplitCell(int sizePx, int cells, int index, int gapPx)
    {
        int total = sizePx - ((cells - 1) * gapPx);
        int basis = total / cells;
        int extra = total % cells;
        int firstWide = cells - extra;
        return ((index * (basis + gapPx)) + Math.Max(0, index - firstWide), basis + (index >= firstWide ? 1 : 0));
    }

    private void FillCategory(DrawingContext context, Rect rect, int code, IBrush[] brushes, bool isHatched, Color surface)
    {
        context.FillRectangle(brushes[code], rect);
        _mapRects++;
        if (isHatched)
        {
            DrawHatch(context, rect, surface);
        }
    }

    private void DrawMapLane(DrawingContext context, FrameMapGraph map, MapLayout layout, IBrush foreground)
    {
        Rect lane = layout.Lane;
        context.FillRectangle(Palette.Grid(this, 0.08), lane);
        foreach (int square in map.FindingSquares(ShownSeverities))
        {
            if (map.SeverityAt(square, ShownSeverities) is { } severity)
            {
                context.FillRectangle(Palette.SeverityBrush(this, severity), new Rect(lane.X, lane.Y + LaneY(layout, square), lane.Width, MarkHeight));
            }
        }

        if (MarkedIndex is { } marked && marked < map.Count)
        {
            context.FillRectangle(foreground, new Rect(lane.X - 1, lane.Y + LaneY(layout, marked), lane.Width + 2, MarkHeight));
        }
    }

    private void DrawMapItems(DrawingContext context, FrameMapGraph map, MapLayout layout, IBrush[] classBrushes, IBrush[] categoryBrushes, CategoryStyle[] styles, Color surface, IBrush secondary, IBrush foreground)
    {
        IReadOnlyList<MapKeyEntry> key = map.IsCategory ? [] : map.Key;
        foreach (MapItem item in layout.Items)
        {
            Rect swatch = new(item.Rect.X, item.Rect.Y + ((item.Rect.Height - 10) / 2), item.Kind == MapItemKind.Swatch ? item.Rect.Width : 10, 10);
            switch (item.Kind)
            {
                case MapItemKind.Text:
                    {
                        FormattedText text = Text(item.Label, TextSize, foreground);
                        context.DrawText(text, new Point(item.Rect.X, item.Rect.Y + ((item.Rect.Height - text.Height) / 2)));
                        break;
                    }

                case MapItemKind.Swatch:
                    {
                        // A declared step the file does not use is an outline of its color with nothing inside.
                        if (map.IsDeclared && key[item.Entry].Count == 0)
                        {
                            context.DrawRectangle(null, new Pen(classBrushes[item.Entry], 1.5), swatch.Deflate(0.75), 2, 2);
                        }
                        else
                        {
                            context.FillRectangle(classBrushes[item.Entry], swatch, 2f);
                        }

                        break;
                    }

                case MapItemKind.Legend:
                    {
                        context.FillRectangle(categoryBrushes[item.Entry], swatch, 2f);
                        if (styles[item.Entry].IsHatched)
                        {
                            DrawHatch(context, swatch, surface);
                        }

                        DrawItemLabel(context, item, foreground);
                        break;
                    }

                case MapItemKind.Finding:
                    context.DrawRectangle(null, new Pen(foreground, 1.5), swatch.Deflate(0.75), 2, 2);
                    context.DrawRectangle(null, new Pen(new ImmutableSolidColorBrush(surface), 1), swatch.Deflate(2));
                    DrawItemLabel(context, item, foreground);
                    break;
                default:
                    context.DrawRectangle(null, new Pen(Palette.Grid(this, 0.5), 1), swatch.Deflate(0.5));
                    DrawItemLabel(context, item, foreground);
                    break;
            }
        }
    }

    private void DrawItemLabel(DrawingContext context, MapItem item, IBrush foreground)
    {
        FormattedText text = Text(item.Label, TextSize, foreground);
        context.DrawText(text, new Point(item.Rect.X + 16, item.Rect.Y + ((item.Rect.Height - text.Height) / 2)));
    }
}
