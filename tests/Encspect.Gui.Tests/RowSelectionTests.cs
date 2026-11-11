using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Encspect.Gui.Views;

namespace Encspect.Gui.Tests;

// A selected row looks the same in every list of the GUI: the file list (a DataGrid), the folder tree (a TreeView) and the detail
// tree and findings list (ListBoxes), in light and dark mode, with the focus in or out of the list and with the pointer over the
// row. The colors are those the controls draw, read from a rendered frame, and the text on them keeps the contrast of WCAG AA.
public class RowSelectionTests
{
    private const double MinimumContrast = 4.5;

    private static readonly string[] Controls = ["ListBox", "TreeView", "DataGrid"];

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (string variant in new[] { "light", "dark" })
        {
            foreach (string state in new[] { "focused", "unfocused", "hovered", "hovered-unfocused" })
            {
                data.Add(variant, state);
            }
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void Every_list_draws_a_selected_row_in_the_color_of_the_ListBox(string variant, string state)
    {
        Dictionary<string, (Color Background, Color Foreground)> rows = Measure(variant, state);
        if (Environment.GetEnvironmentVariable("ENCSPECT_ROWSEL_DUMP") is { Length: > 0 } dump)
        {
            File.AppendAllLines(dump, rows.Select(r => $"{variant} {state} {r.Key} background {r.Value.Background} foreground {r.Value.Foreground} contrast {CategoryColors.ContrastRatio(r.Value.Background, r.Value.Foreground):0.00}"));
        }

        foreach (string control in Controls)
        {
            Assert.Equal(rows["ListBox"].Background, rows[control].Background);
            double contrast = CategoryColors.ContrastRatio(rows[control].Background, rows[control].Foreground);
            Assert.True(contrast >= MinimumContrast, $"{control}, {variant}, {state}: text {rows[control].Foreground} on {rows[control].Background} has {contrast:0.00}:1.");
        }
    }

    private static Dictionary<string, (Color Background, Color Foreground)> Measure(string variant, string state)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        string[] items = ["Alpha", "Beta", "Gamma"];
        var list = new ListBox { ItemsSource = items, SelectedIndex = 1, Width = 200, Height = 140, Background = Brushes.Transparent };
        var tree = new TreeView { ItemsSource = items, SelectedItem = "Beta", Width = 200, Height = 140 };
        var grid = new DataGrid { ItemsSource = items, SelectedIndex = 1, Width = 200, Height = 140, HeadersVisibility = DataGridHeadersVisibility.None, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Binding = new Binding("."), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var other = new TextBox { Width = 200 };
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { list, tree, grid, other } };
        var window = new Window { Width = 900, Height = 200, Content = panel };
        try
        {
            window.Show();
            Settle();
            grid.SelectedIndex = 1;
            Settle();
            var result = new Dictionary<string, (Color, Color)>();
            foreach ((string name, Control control) in new (string, Control)[] { ("ListBox", list), ("TreeView", tree), ("DataGrid", grid) })
            {
                Control row = control.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c is ListBoxItem or TreeViewItem or DataGridRow && IsSelected(c)) ?? throw new InvalidOperationException($"{name} has no selected row: {string.Join(", ", control.GetVisualDescendants().Select(v => v.GetType().Name).Distinct())}");
                Point inRow = new(row.Bounds.Width - 8, row.Bounds.Height / 2);
                if (name == "TreeView")
                {
                    inRow = new Point(row.Bounds.Width - 8, 12);
                }

                if (state.EndsWith("unfocused", StringComparison.Ordinal))
                {
                    other.Focus();
                }
                else
                {
                    control.Focus();
                }

                if (state.StartsWith("hovered", StringComparison.Ordinal))
                {
                    window.MouseMove(row.TranslatePoint(inRow, window)!.Value);
                }
                else
                {
                    window.MouseMove(new Point(890, 190));
                }

                Settle();
                WriteableBitmap frame = window.CaptureRenderedFrame()!;
                Point at = row.TranslatePoint(inRow, window)!.Value;
                Color background = Pixel(frame, (int)at.X, (int)at.Y);
                TextBlock text = row.GetVisualDescendants().OfType<TextBlock>().First();
                result[name] = (background, ((ISolidColorBrush)text.Foreground!).Color);
            }

            return result;
        }
        finally
        {
            window.Close();
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    private static bool IsSelected(Control row) => row switch
    {
        ListBoxItem item => item.IsSelected,
        TreeViewItem item => item.IsSelected,
        DataGridRow grid => grid.IsSelected,
        _ => false,
    };

    private static void Settle()
    {
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }

    private static Color Pixel(WriteableBitmap frame, int x, int y)
    {
        using ILockedFramebuffer buffer = frame.Lock();
        unsafe
        {
            byte* p = (byte*)buffer.Address + (y * buffer.RowBytes) + (x * 4);
            return buffer.Format == PixelFormat.Bgra8888 ? Color.FromRgb(p[2], p[1], p[0]) : Color.FromRgb(p[0], p[1], p[2]);
        }
    }
}
