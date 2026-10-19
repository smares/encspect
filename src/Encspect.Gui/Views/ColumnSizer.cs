using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Encspect.Gui.Views;

// Sizes the columns of the file list from the fonts in use. A column's width has to leave room for
// its header text, the header's padding and sort glyph, and where the cells have a least width of
// their own, for those: the grid would otherwise squeeze the columns past the first screenful to
// its minimum width, where their headers vanish. The room is measured with the real controls'
// fonts, so it holds for any system font; the preferred widths only apply where they are wider.
// Each column is also held at its width, so that a narrow window scrolls the list horizontally
// instead of squeezing the columns; a column can be widened, not narrowed below that.
internal sealed class ColumnSizer
{
    private readonly DataGrid _grid;
    private readonly Dictionary<DataGridColumn, (double Preferred, double BaseMinimum, Func<FontSpec, double> CellMinimum)> _columns = [];
    private readonly HashSet<DataGridColumn> _sized = [];
    private (FontSpec Header, FontSpec Cell)? _applied;

    public ColumnSizer(DataGrid grid)
    {
        _grid = grid;
        grid.LayoutUpdated += (_, _) => Apply();
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
        _columns[column] = (preferred, column.MinWidth, cellMinimum ?? (_ => 0));

    private void Apply()
    {
        if (_grid.GetVisualDescendants().OfType<DataGridColumnHeader>().FirstOrDefault(h => h.Content is string && h.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Any()) is not { } sample
            || sample.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().First() is not { } label)
        {
            return;
        }

        FontSpec header = Spec(label);
        FontSpec cell = _grid.GetVisualDescendants().OfType<DataGridCell>().FirstOrDefault() is { } c ? Spec(c) : new FontSpec(_grid.FontFamily, 13, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal);
        if (_applied == (header, cell))
        {
            return;
        }

        // What the header adds around its text, taken from the realized header itself: its padding,
        // sort glyph and separator, whatever the theme makes them.
        sample.Measure(Avalonia.Size.Infinity);
        double overhead = sample.DesiredSize.Width - header.TextWidth((string)sample.Content!);
        _applied = (header, cell);
        foreach ((DataGridColumn column, (double preferred, double baseMinimum, Func<FontSpec, double> cellMinimum)) in _columns)
        {
            double minimum = Math.Max(baseMinimum, Math.Ceiling(Math.Max(header.TextWidth((string)column.Header) + overhead, cellMinimum(cell))));
            if (column.Width.IsAbsolute)
            {
                // Held at its width: below the preferred one the grid squeezes the column to its
                // minimum, whenever the columns together are wider than the list.
                minimum = Math.Max(minimum, _sized.Add(column) ? preferred : column.Width.Value);
                column.Width = new DataGridLength(minimum);
            }

            column.MinWidth = minimum;
        }
    }

    private static FontSpec Spec(TemplatedControl control) =>
        new(control.FontFamily, control.FontSize, control.FontWeight, control.FontStyle, control.FontStretch);

    private static FontSpec Spec(Avalonia.Controls.TextBlock text) =>
        new(text.FontFamily, text.FontSize, text.FontWeight, text.FontStyle, text.FontStretch);
}
