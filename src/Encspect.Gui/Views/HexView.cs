using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Views;

// The file's bytes, 16 to a row, with the selected node's bytes marked and, for a field of a few
// bits, a bar under the bits it takes. Only the rows on screen are read and drawn, so the size of
// the file does not matter.
public sealed class HexView : Grid
{
    public static readonly StyledProperty<HexDocument?> DocumentProperty =
        AvaloniaProperty.Register<HexView, HexDocument?>(nameof(Document));

    public static readonly StyledProperty<HexHighlight?> HighlightProperty =
        AvaloniaProperty.Register<HexView, HexHighlight?>(nameof(Highlight));

    private readonly HexCanvas _canvas = new();
    private readonly ScrollBar _scrollBar = new() { Orientation = Orientation.Vertical, SmallChange = 1 };
    private bool _updatingScrollBar;

    public HexView()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        Children.Add(_canvas);
        SetColumn(_scrollBar, 1);
        Children.Add(_scrollBar);
        _canvas.ViewChanged += (_, _) => UpdateScrollBar();
        _scrollBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_updatingScrollBar)
            {
                _canvas.TopRow = (long)Math.Round(_scrollBar.Value);
            }
        };
    }

    public HexDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public HexHighlight? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    // The first row on screen; exposed for tests.
    public long TopRow => _canvas.TopRow;

    public int VisibleRows => _canvas.VisibleRows;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            _canvas.Document = Document;
        }
        else if (change.Property == HighlightProperty)
        {
            _canvas.Highlight = Highlight;
        }
    }

    private void UpdateScrollBar()
    {
        _updatingScrollBar = true;
        try
        {
            long maximum = _canvas.MaxTopRow;
            _scrollBar.Maximum = maximum;
            _scrollBar.ViewportSize = Math.Max(1, _canvas.VisibleRows);
            _scrollBar.LargeChange = Math.Max(1, _canvas.VisibleRows - 1);
            _scrollBar.Value = _canvas.TopRow;
            _scrollBar.IsVisible = maximum > 0;
        }
        finally
        {
            _updatingScrollBar = false;
        }
    }
}

internal sealed class HexCanvas : Control
{
    private const int BytesPerRow = 16;
    private const double FontSize = 12.5;
    private const double Padding = 10;

    private HexDocument? _document;
    private HexHighlight? _highlight;
    private long _topRow;
    private double _charWidth;
    private double _lineHeight;
    private Typeface _typeface;

    public HexCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) =>
        {
            _lineHeight = 0;
            InvalidateMeasure();
            InvalidateVisual();
        };
    }

    public event EventHandler? ViewChanged;

    public HexDocument? Document
    {
        get => _document;
        set
        {
            _document = value;
            _topRow = 0;
            OnViewChanged();
        }
    }

    public HexHighlight? Highlight
    {
        get => _highlight;
        set
        {
            _highlight = value;
            Reveal();
            InvalidateVisual();
        }
    }

    public long TopRow
    {
        get => _topRow;
        set
        {
            long clamped = Math.Clamp(value, 0, MaxTopRow);
            if (clamped != _topRow)
            {
                _topRow = clamped;
                OnViewChanged();
            }
        }
    }

    public long RowCount => _document is null ? 0 : (_document.Length + BytesPerRow - 1) / BytesPerRow;

    // Rows of bytes that fit below the header line.
    public int VisibleRows => _lineHeight > 0 ? Math.Max(1, (int)((Bounds.Height - (2 * Padding)) / _lineHeight) - 1) : 1;

    public long MaxTopRow => Math.Max(0, RowCount - VisibleRows);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        EnsureMetrics();
        if (_document is null)
        {
            return;
        }

        IBrush foreground = Palette.Foreground(this);
        IBrush secondary = Palette.Secondary(this);
        IBrush byteFill = Palette.AccentFill(this, 0.32);
        IBrush bitFill = Palette.AccentFill(this, 0.18);
        IBrush bitBar = Palette.AccentFill(this, 1);

        int digits = Math.Max(8, (int)Math.Ceiling(Math.Log(Math.Max(16, _document.Length), 16)));
        double hexX = Padding + ((digits + 2) * _charWidth);
        double asciiX = hexX + ((BytesPerRow * 3) + 2) * _charWidth;

        DrawText(context, string.Join(' ', Enumerable.Range(0, 8).Select(i => i.ToString("X2", CultureInfo.InvariantCulture))), hexX, Padding, secondary);
        DrawText(context, string.Join(' ', Enumerable.Range(8, 8).Select(i => i.ToString("X2", CultureInfo.InvariantCulture))), hexX + (HexColumn(8) * _charWidth), Padding, secondary);

        int rows = VisibleRows;
        byte[] bytes = new byte[rows * BytesPerRow];
        long first = _topRow * BytesPerRow;
        int count = _document.Read(first, bytes);
        var hex = new char[(BytesPerRow * 3) + 1];
        var ascii = new char[BytesPerRow];
        for (int row = 0; row * BytesPerRow < count; row++)
        {
            double y = Padding + ((row + 1) * _lineHeight);
            long offset = first + (row * BytesPerRow);
            int inRow = Math.Min(BytesPerRow, count - (row * BytesPerRow));
            Array.Fill(hex, ' ');
            Array.Fill(ascii, ' ');
            for (int i = 0; i < inRow; i++)
            {
                byte value = bytes[(row * BytesPerRow) + i];
                int column = HexColumn(i);
                hex[column] = "0123456789ABCDEF"[value >> 4];
                hex[column + 1] = "0123456789ABCDEF"[value & 15];
                ascii[i] = value is >= 0x20 and < 0x7F ? (char)value : '.';

                if (_highlight?.BitsOf(offset + i) is not { } bits)
                {
                    continue;
                }

                bool isWholeByte = bits.Start == 0 && bits.End == 8 && _highlight.BitLength is null;
                double cellX = hexX + (column * _charWidth);
                context.FillRectangle(isWholeByte ? byteFill : bitFill, new Rect(cellX - (_charWidth / 2), y, 3 * _charWidth, _lineHeight));
                context.FillRectangle(isWholeByte ? byteFill : bitFill, new Rect(asciiX + (i * _charWidth), y, _charWidth, _lineHeight));
                if (!isWholeByte)
                {
                    double start = cellX + (bits.Start / 8.0 * 2 * _charWidth);
                    double end = cellX + (bits.End / 8.0 * 2 * _charWidth);
                    context.FillRectangle(bitBar, new Rect(start, y + _lineHeight - 3.5, Math.Max(2, end - start), 3));
                }
            }

            DrawText(context, offset.ToString("X" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), Padding, y, secondary);
            DrawText(context, new string(hex), hexX, y, foreground);
            DrawText(context, new string(ascii, 0, inRow), asciiX, y, foreground);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();
        double width = (2 * Padding) + ((8 + 2 + (BytesPerRow * 3) + 2 + BytesPerRow) * _charWidth);
        return new Size(width, Math.Min(availableSize.Height, 200));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _topRow = Math.Clamp(_topRow, 0, MaxTopRow);
        Reveal();
        OnViewChanged();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        TopRow -= (long)Math.Round(e.Delta.Y * 3);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        long? target = e.Key switch
        {
            Key.Up => _topRow - 1,
            Key.Down => _topRow + 1,
            Key.PageUp => _topRow - Math.Max(1, VisibleRows - 1),
            Key.PageDown => _topRow + Math.Max(1, VisibleRows - 1),
            Key.Home => 0,
            Key.End => MaxTopRow,
            _ => null,
        };
        if (target is { } row)
        {
            TopRow = row;
            e.Handled = true;
        }
    }

    // The column of a byte's two hex digits: three characters a byte, and one more space after the
    // eighth.
    private static int HexColumn(int index) => (index * 3) + (index >= 8 ? 1 : 0);

    // Scrolls the start of the highlight into view, a quarter down, unless it is on screen.
    private void Reveal()
    {
        if (_highlight is null || _document is null)
        {
            return;
        }

        long row = _highlight.Start / BytesPerRow;
        if (row < _topRow || row >= _topRow + VisibleRows)
        {
            TopRow = row - (VisibleRows / 4);
        }
    }

    private void OnViewChanged()
    {
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void EnsureMetrics()
    {
        if (_lineHeight > 0)
        {
            return;
        }

        _typeface = Palette.Monospace(this);
        var sample = new FormattedText("0123456789ABCDEF", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, Brushes.Black);
        _charWidth = sample.WidthIncludingTrailingWhitespace / 16;
        _lineHeight = Math.Ceiling(sample.Height + 3);
    }

    private void DrawText(DrawingContext context, string text, double x, double y, IBrush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, brush);
        context.DrawText(formatted, new Point(x, y + 1.5));
    }
}
