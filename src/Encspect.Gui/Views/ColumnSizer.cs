using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Encspect.Gui.Views;

// Sizes the columns of the file list from the fonts in use. Every column has a fixed pixel width,
// no star column: with one, the grid squeezes the other columns on its own and keeps the total at
// the list's width, which also stops a user from resizing them. Fixed widths let the list scroll
// sideways where the columns do not fit, and let a user drag any column wider or narrower.
// A column's width is its preferred one, or the room its header text, padding and sort glyph need
// (and where the cells have a least width of their own, that) where that is more, and it never
// goes below that need. The need is measured with the real controls' fonts, so it holds for any
// system font. The fill column (Name) takes the room left in the list, until a user sizes it.
internal sealed class ColumnSizer
{
    private readonly DataGrid _grid;
    private readonly Dictionary<DataGridColumn, (double Preferred, Func<FontSpec, double> CellMinimum)> _columns = [];
    private readonly HashSet<DataGridColumn> _sized = [];
    private (FontSpec Header, FontSpec Cell)? _applied;
    private DataGridColumn? _fill;
    private double _fillPreferred;
    private double _fillWidth = double.NaN;
    private bool _fillSizedByUser;
    private double _fillViewport = double.NaN;

    public ColumnSizer(DataGrid grid)
    {
        _grid = grid;
        grid.LayoutUpdated += (_, _) =>
        {
            Apply();
            Fill();
        };
    }

    // The font a width is measured in.
    internal readonly record struct FontSpec(FontFamily Family, double Size, FontWeight Weight, FontStyle Style, FontStretch Stretch)
    {
        public double TextWidth(string text)
        {
            var probe = new Avalonia.Controls.TextBlock { Text = text, FontFamily = Family, FontSize = Size, FontWeight = Weight, FontStyle = Style, FontStretch = Stretch };
            probe.Measure(Avalonia.Size.Infinity);
            return probe.DesiredSize.Width;
        }
    }

    public void Add(DataGridColumn column, double preferred, Func<FontSpec, double>? cellMinimum = null) =>
        _columns[column] = (preferred, cellMinimum ?? (_ => 0));

    // The column that takes the room left in the list, never below its preferred width.
    public void AddFill(DataGridColumn column, double preferred)
    {
        Add(column, preferred);
        _fill = column;
        _fillPreferred = preferred;
    }

    private void Apply()
    {
        if (_grid.GetVisualDescendants().OfType<DataGridColumnHeader>().FirstOrDefault(h => h.Content is string && h.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Any()) is not { } sample
            || sample.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().First() is not { } label)
        {
            return;
        }

        FontSpec header = Spec(label);
        // Until a row exists there is no cell to take the font from: the cells use the grid's family
        // at the theme's 13 px, and the sizer applies again when the first row arrives.
        FontSpec cell = _grid.GetVisualDescendants().OfType<DataGridCell>().FirstOrDefault() is { } c ? Spec(c) : new FontSpec(_grid.FontFamily, 13, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal);
        if (_applied == (header, cell))
        {
            return;
        }

        // What a header adds around its text (padding, sort glyph, separator, whatever the theme
        // makes them), taken from the realized header. A header measured without a template has
        // none of it, so the live one is measured and then invalidated, so that the next layout pass
        // measures it again (with its column's width where the widths change, otherwise as it was).
        sample.Measure(Avalonia.Size.Infinity);
        double overhead = sample.DesiredSize.Width - label.DesiredSize.Width;
        sample.InvalidateMeasure();
        _applied = (header, cell);
        _fillViewport = double.NaN;
        foreach ((DataGridColumn column, (double preferred, Func<FontSpec, double> cellMinimum)) in _columns)
        {
            double text = column.Header is string name ? header.TextWidth(name) : 0;
            double need = Math.Ceiling(Math.Max(text + overhead, cellMinimum(cell)));
            column.MinWidth = need;

            // A width a user chose stays, unless the font now needs more.
            double width = _sized.Add(column) ? Math.Max(preferred, need) : Math.Max(column.Width.IsAbsolute ? column.Width.Value : 0, need);
            if (!column.Width.IsAbsolute || Math.Abs(column.Width.Value - width) > 0.01)
            {
                column.Width = new DataGridLength(width);
            }

            if (column == _fill)
            {
                _fillWidth = width;
            }
        }
    }

    // The fill column takes what the other columns leave of the list's width, until a user drags it.
    private void Fill()
    {
        if (_fill is not { } fill || _fillSizedByUser || double.IsNaN(_fillWidth) || !fill.Width.IsAbsolute)
        {
            return;
        }

        if (Math.Abs(fill.Width.Value - _fillWidth) > 0.5)
        {
            _fillSizedByUser = true;
            return;
        }

        if (_grid.GetVisualDescendants().OfType<DataGridColumnHeadersPresenter>().FirstOrDefault() is not { } presenter)
        {
            return;
        }

        // Only a change of the list's own width (or a re-apply) refills Name: a drag of another
        // column keeps Name as it is, so the dragged edge follows the pointer.
        if (Math.Abs(presenter.Bounds.Width - _fillViewport) < 0.5)
        {
            return;
        }

        _fillViewport = presenter.Bounds.Width;
        double others = _grid.Columns.Where(x => x != fill).Sum(x => x.Width.IsAbsolute ? x.Width.Value : x.ActualWidth);
        double target = Math.Max(Math.Max(_fillPreferred, fill.MinWidth), Math.Floor(presenter.Bounds.Width - others));
        if (Math.Abs(target - fill.Width.Value) > 0.5)
        {
            _fillWidth = target;
            fill.Width = new DataGridLength(target);
        }
    }

    private static FontSpec Spec(TemplatedControl control) =>
        new(control.FontFamily, control.FontSize, control.FontWeight, control.FontStyle, control.FontStretch);

    private static FontSpec Spec(Avalonia.Controls.TextBlock text) =>
        new(text.FontFamily, text.FontSize, text.FontWeight, text.FontStyle, text.FontStretch);
}
