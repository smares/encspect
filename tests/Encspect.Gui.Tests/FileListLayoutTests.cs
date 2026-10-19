using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The columns of the file list keep room for their headers, whatever the font, and the list scrolls
// sideways where the columns do not fit. "wide" stands for a system font wider than the default
// one, such as the macOS system font. With ENCSPECT_SCREENSHOTS set to a folder, the list is also
// saved there as PNG files.
public sealed class FileListLayoutTests : IDisposable
{
    private const int ColumnCount = 13;

    private readonly TempFolder _folder = new();
    private MainWindow? _main;

    public void Dispose()
    {
        _main?.Close();
        _folder.Dispose();
    }

    [AvaloniaTheory]
    [InlineData("default", 3000)]
    [InlineData("wide", 3000)]
    [InlineData("default", 1000)]
    [InlineData("wide", 1000)]
    public void Every_header_shows_its_whole_text(string font, int width)
    {
        DataGrid grid = Open(font, width);

        // Saved after the header checks have scrolled to the last column too.
        using var shot = new Shot(() => Save($"{width}-{font}-scrolled.png"));
        int seen = ForEachHeader(grid, header =>
        {
            TextBlock text = header.GetVisualDescendants().OfType<TextBlock>().First();
            Assert.True(text.Bounds.Width >= Natural(text) - 0.5, $"The header '{header.Content}' is {text.Bounds.Width} wide for a text of {Natural(text)}.");
            header.Measure(Size.Infinity);
            Assert.True(header.Bounds.Width >= header.DesiredSize.Width - 0.5, $"The header '{header.Content}' is {header.Bounds.Width} wide, where it needs {header.DesiredSize.Width}.");
        });

        Assert.Equal(ColumnCount, seen);
    }

    [AvaloniaTheory]
    [InlineData("default")]
    [InlineData("wide")]
    public void The_findings_cell_with_three_counts_fits_its_column(string font)
    {
        DataGrid grid = Open(font, 3000);

        var cell = grid.GetVisualDescendants().OfType<StackPanel>().First(p => p.Spacing == 10 && p.Children.Count == 3);
        foreach (StackPanel count in cell.Children.Cast<StackPanel>())
        {
            count.IsVisible = true;
            count.Children.OfType<TextBlock>().Single().Text = "999";
        }

        _main!.UpdateLayout();
        var column = (Control)cell.GetVisualAncestors().OfType<DataGridCell>().First();
        Assert.Equal(cell.Margin.Left, cell.Bounds.X);
        Assert.True(cell.Children[^1].Bounds.Right <= cell.Bounds.Width, "The third count is cut off.");
        Assert.True(cell.Bounds.Right + cell.Margin.Right <= column.Bounds.Width + 0.5, $"The counts end at {cell.Bounds.Right + cell.Margin.Right} in a column {column.Bounds.Width} wide.");
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, cell.HorizontalAlignment);
    }

    [AvaloniaTheory]
    [InlineData("default")]
    [InlineData("wide")]
    public void A_narrow_window_scrolls_sideways_and_squeezes_no_column(string font)
    {
        DataGrid grid = Open(font, 800);

        ScrollBar bar = grid.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
        Assert.True(bar.IsVisible, "The list does not scroll sideways.");
        Assert.True(grid.Columns.Sum(c => c.ActualWidth) > grid.Bounds.Width);
        DataGridColumnHeader sample = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h => h.Content is string);
        TextBlock label = sample.GetVisualDescendants().OfType<TextBlock>().First();
        sample.Measure(Size.Infinity);
        double overhead = sample.DesiredSize.Width - Natural(label);
        foreach (DataGridColumn column in grid.Columns)
        {
            double needed = Natural(new TextBlock { Text = (string)column.Header, FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight }) + overhead;
            Assert.True(column.ActualWidth >= needed - 0.5, $"The column '{column.Header}' is {column.ActualWidth} wide, where its header needs {needed}.");
        }

        Assert.Equal(ColumnCount, ForEachHeader(grid, header => Assert.True(header.GetVisualDescendants().OfType<TextBlock>().First().Bounds.Width > 0, $"The header '{header.Content}' shows no text.")));
    }

    private DataGrid Open(string font, int width)
    {
        foreach (string name in new[] { "external-lame-cbr128-joint.mp3", "external-lame-vbr-v2.mp3", "damaged-bitrate.mp3", "concatenated-files.mp3" })
        {
            _folder.Copy(Fixtures.Mpeg(name), name);
        }

        _main = new MainWindow(Analyzer.CreateDefault(), new Settings()) { Width = width, Height = 600 };
        if (font == "wide")
        {
            foreach (Type type in new[] { typeof(DataGridColumnHeader), typeof(DataGridCell) })
            {
                _main.Styles.Add(new Style(s => s.Is(type))
                {
                    Setters = { new Setter(TemplatedControl.FontFamilyProperty, new FontFamily("DejaVu Sans")), new Setter(TemplatedControl.FontSizeProperty, 16.0) },
                });
            }
        }

        _main.Show();
        _main.Model.Open(FileSource.ForFolder(_folder.Path));
        UiPump.Until(() => !_main.Model.IsRunning && _main.Model.Rows.Count > 0);
        UiPump.Until(() => true);
        _main.UpdateLayout();
        Save($"{width}-{font}.png");
        return _main.FindControl<DataGrid>("Files")!;
    }

    // Runs the check on the headers as they are, then on those a scroll to each column brings into
    // view; returns how many different columns' headers were checked.
    private int ForEachHeader(DataGrid grid, Action<DataGridColumnHeader> check)
    {
        var seen = new HashSet<string>();
        object row = grid.ItemsSource.Cast<object>().First();
        foreach (DataGridColumn column in grid.Columns.Prepend(grid.Columns[0]))
        {
            grid.ScrollIntoView(row, column);
            UiPump.Until(() => true);
            _main!.UpdateLayout();
            foreach (DataGridColumnHeader header in grid.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.Content is string))
            {
                check(header);
                seen.Add((string)header.Content!);
            }
        }

        return seen.Count;
    }

    // The width the text wants when unconstrained.
    private static double Natural(TextBlock text)
    {
        var probe = new TextBlock { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    private void Save(string name)
    {
        if (Environment.GetEnvironmentVariable("ENCSPECT_SCREENSHOTS") is { Length: > 0 } folder)
        {
            UiPump.Until(() => true);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Directory.CreateDirectory(folder);
            _main!.CaptureRenderedFrame()!.Save(Path.Combine(folder, "file-list-" + name), new PngBitmapEncoderOptions());
        }
    }
}

internal sealed class Shot(Action save) : IDisposable
{
    public void Dispose() => save();
}
