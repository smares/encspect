using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

public class HexCursorTests
{
    private static DetailWindow Open()
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1100, Height = 760 };
        window.Show();
        Task load = window.Model.LoadAsync(Fixtures.Mpeg("external-lame-vbr-v2.mp3"));
        UiPump.Until(() => load.IsCompleted);
        window.Model.SelectedNode = window.Model.Tree!.Root;
        UiPump.Until(() => true);
        return window;
    }

    private static Point Cell(DetailWindow window, long offset) => window.Hex.TranslatePoint(window.Hex.CellCenter(offset)!.Value, window)!.Value;

    private static void Press(DetailWindow window, Key key, PhysicalKey physical) => window.KeyPress(key, RawInputModifiers.None, physical, null);

    [AvaloniaFact]
    public void A_click_sets_a_cursor_and_leaves_the_trees_selection()
    {
        DetailWindow window = Open();
        TreeRow selected = window.Model.SelectedNode!;
        HexHighlight? highlight = window.Model.Highlight;
        Assert.Null(window.Hex.ByteCursor);

        Point point = Cell(window, 20);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        Assert.Equal(20, window.Hex.ByteCursor);
        Assert.Contains("offset 0x14 (20)", window.Hex.StatusText);
        Assert.Contains("Byte 0x", window.Hex.StatusText);
        Assert.Same(selected, window.Model.SelectedNode);
        Assert.Same(highlight, window.Model.Highlight);
        Assert.NotNull(window.CaptureRenderedFrame());
        window.Close();
    }

    [AvaloniaFact]
    public void The_arrow_and_page_keys_move_the_cursor_and_keep_it_on_screen()
    {
        DetailWindow window = Open();
        Point point = Cell(window, 20);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(21, window.Hex.ByteCursor);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(19, window.Hex.ByteCursor);
        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal(35, window.Hex.ByteCursor);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal(19, window.Hex.ByteCursor);

        Press(window, Key.PageDown, PhysicalKey.PageDown);
        long expected = 19 + (16L * (window.Hex.VisibleRows - 1));
        Assert.Equal(expected, window.Hex.ByteCursor);
        long row = expected / 16;
        Assert.InRange(row, window.Hex.TopRow, window.Hex.TopRow + window.Hex.VisibleRows - 1);
        Press(window, Key.PageUp, PhysicalKey.PageUp);
        Assert.Equal(19, window.Hex.ByteCursor);

        // Never before the first byte.
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal(3, window.Hex.ByteCursor);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal(0, window.Hex.ByteCursor);
        window.Close();
    }

    [AvaloniaFact]
    public void A_double_click_selects_the_deepest_node_that_holds_the_byte()
    {
        DetailWindow window = Open();
        StructureTree tree = window.Model.Tree!;
        TreeRow stream = tree.Root.RowAt(0);
        tree.Expand(stream);
        long target = Enumerable.Range(0, stream.LoadChildren().Count).Select(i => stream.RowAt(i)).First(r => r.Kind == NodeKind.Frame).Node.Offset + 1;

        window.Model.SelectedNode = tree.Root;
        UiPump.Until(() => true);
        window.Hex.ByteCursor = target;
        UiPump.Until(() => true);
        Point point = Cell(window, target);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        TreeRow selected = window.Model.SelectedNode!;
        Assert.NotSame(tree.Root, selected);
        Assert.True(selected.Node.Offset <= target && target < selected.Node.End);
        Assert.False(selected.Node.Children.Any(c => c.Length > 0 && c.Offset <= target && target < c.End && c.Ranges is null), $"{selected.Name} @{selected.Node.Offset}+{selected.Node.Length} target {target} children {string.Join(",", selected.Node.Children.Select(c => $"{c.Name}@{c.Offset}+{c.Length}"))}");
        Assert.Equal(target, window.Hex.ByteCursor);
        window.Close();
    }

    [AvaloniaFact]
    public void The_deepest_row_at_an_offset_is_found_by_bisection_and_is_null_outside_the_file()
    {
        DetailWindow window = Open();
        StructureTree tree = window.Model.Tree!;
        Assert.Null(tree.DeepestAt(0, tree.Root.Node.End + 10));
        Assert.Null(tree.DeepestAt(5, 0));
        TreeRow row = tree.DeepestAt(0, 0)!;
        Assert.True(row.Node.Offset <= 0 && row.Node.End > 0);
        window.Close();
    }

    private static void Click(Avalonia.Controls.MenuItem item) => item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));

    // What a menu item copies: the copy of Copy Node runs on a background thread, so the UI is pumped until it arrives.
    private static string Copied(Avalonia.Controls.MenuItem item)
    {
        string? copied = null;
        Copying.Observer = text => copied = text;
        try
        {
            Click(item);
            UiPump.Until(() => copied is not null);
            return copied!;
        }
        finally
        {
            Copying.Observer = null;
        }
    }

    // Opening the menu is what enables its items.
    private static Avalonia.Controls.MenuItem Item(Avalonia.Controls.ContextMenu? menu, string header)
    {
        menu!.Open();
        menu.Close();
        return menu.Items.OfType<Avalonia.Controls.MenuItem>().Single(i => (string?)i.Header == header);
    }

    [AvaloniaFact]
    public void The_trees_menu_copies_a_nodes_value_and_the_node_as_the_full_report_writes_it()
    {
        DetailWindow window = Open();
        StructureTree tree = window.Model.Tree!;
        TreeRow stream = tree.Root.RowAt(0);
        tree.Expand(stream);
        TreeRow frame = Enumerable.Range(0, stream.LoadChildren().Count).Select(i => stream.RowAt(i)).First(r => r.Kind == NodeKind.Frame);

        window.Model.SelectedNode = frame;
        string text = Copied(Item(window.TreeList.ContextMenu, "Copy Node"));

        // The same lines, at the node's depth, in the report the command line writes.
        var full = new StringWriter();
        ReportTextWriter.Write(window.Model.Report!, full, showOffsets: true);
        string[] lines = full.ToString().Split(Environment.NewLine);
        string[] copy = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        int indent = (frame.Depth + 1) * 2;
        int start = Array.IndexOf(lines, new string(' ', indent) + copy[0].TrimStart());
        Assert.True(start >= 0);
        List<string> expected = [];
        for (int i = start; i < lines.Length && (i == start || lines[i].StartsWith(new string(' ', indent + 2), StringComparison.Ordinal)); i++)
        {
            expected.Add(lines[i][(indent - 2)..]);
        }

        Assert.True(expected.Count > 1);
        Assert.Equal(expected, copy);

        TreeRow field = Enumerable.Range(0, frame.LoadChildren().Count).Select(i => { tree.Expand(frame); return frame.RowAt(i); }).First(r => r.Value is not null);
        window.Model.SelectedNode = field;
        Assert.Equal(field.Value, Copied(Item(window.TreeList.ContextMenu, "Copy Value")));

        // A node with no value has nothing to copy as one.
        window.Model.SelectedNode = tree.Root;
        Assert.Equal(tree.Root.Value is not null, Item(window.TreeList.ContextMenu, "Copy Value").IsEnabled);
        window.Close();
    }

    [Fact]
    public void A_node_whose_text_is_over_the_cap_is_not_built()
    {
        var root = new Node("File", 0, 10_000_000);
        for (int i = 0; i < 700_000; i++)
        {
            root.Add("Frame", i * 10L, 8);
        }

        var report = new Report("big.mp3", root, []);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(DetailViewModel.TryNodeText(report, root, DetailViewModel.MaxNodeTextChars, TestContext.Current.CancellationToken));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));

        string text = DetailViewModel.TryNodeText(report, root.Children[5], DetailViewModel.MaxNodeTextChars, TestContext.Current.CancellationToken)!;
        Assert.Equal("  Frame [0x32, 8 bytes]" + Environment.NewLine, text);
        Assert.Equal("Not copied: the node's text is over 16 MiB; export the report instead", DetailViewModel.NodeTextTooLong);
    }

    [AvaloniaFact]
    public void The_findings_menu_copies_the_message_alone_or_with_its_code_and_position()
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1100, Height = 760 };
        window.Show();
        Task load = window.Model.LoadAsync(Fixtures.Mpeg("damaged-bitrate.mp3"));
        UiPump.Until(() => load.IsCompleted);
        FindingItem finding = window.Model.Findings.First(f => f.Finding.Node is not null);
        window.Model.SelectedFinding = finding;

        Assert.Equal(finding.Message, Copied(Item(window.FindingList.ContextMenu, "Copy Message")));
        string full = Copied(Item(window.FindingList.ContextMenu, "Copy Message with Code and Position"));
        Assert.StartsWith($"[{finding.Severity}] {finding.Code}: ", full);
        Assert.Contains(finding.Message, full);
        Assert.Contains($"[0x{finding.Finding.Node!.Offset:X}", full);
        window.Close();
    }

    [AvaloniaFact]
    public void The_hex_views_menu_copies_the_rows_the_selection_touches()
    {
        DetailWindow window = Open();
        window.Model.SelectedNode = window.Model.Tree!.Root.RowAt(0);
        UiPump.Until(() => true);
        HexHighlight highlight = window.Model.Highlight!;
        byte[] file = File.ReadAllBytes(Fixtures.Mpeg("external-lame-vbr-v2.mp3"));

        string text = Copied(Item(window.Hex.ContextMenu, "Copy Selection"));

        // Built here from the file's bytes, a row at a time.
        long first = highlight.Start / 16 * 16;
        long last = Math.Min(file.Length, (highlight.End + 15) / 16 * 16);
        var expected = new StringBuilder();
        for (long offset = first; offset < last; offset += 16)
        {
            byte[] row = file[(int)offset..(int)Math.Min(file.Length, offset + 16)];
            string left = string.Join(' ', row.Take(8).Select(b => b.ToString("X2")));
            string right = string.Join(' ', row.Skip(8).Select(b => b.ToString("X2")));
            string hex = row.Length > 8 ? left + "  " + right : left;
            string chars = new([.. row.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.')]);
            expected.Append(offset.ToString("X8")).Append("  ").Append(hex.PadRight(49)).Append(' ').Append(chars).Append('\n');
        }

        Assert.Equal(expected.ToString(), text);
        window.Close();
    }

    [AvaloniaFact]
    public void A_double_click_leaves_the_view_where_it_is_when_the_cursor_is_inside_the_new_selection_and_on_screen()
    {
        DetailWindow window = Open();
        StructureTree tree = window.Model.Tree!;
        TreeRow stream = tree.Root.RowAt(0);
        tree.Expand(stream);
        TreeRow big = Enumerable.Range(0, stream.LoadChildren().Count).Select(i => stream.RowAt(i)).First(r => r.Kind != NodeKind.Frame && r.Node.Length > 5000);

        // A byte deep inside a large node, scrolled to by the cursor.
        long deep = big.Node.Offset + 3000;
        window.Hex.ByteCursor = deep;
        UiPump.Until(() => true);
        long top = window.Hex.TopRow;
        Assert.True(top > big.Node.Offset / 16);

        window.Model.SelectedNode = big;
        UiPump.Until(() => true);

        Assert.Equal(top, window.Hex.TopRow);
        Assert.Equal(deep, window.Hex.ByteCursor);
        window.Close();
    }

    // The cursor on the last row on screen, and the view gets a row lower (another font's metrics, a taller bit strip): the cursor stays on screen.
    [AvaloniaFact]
    public void A_cursor_on_the_last_row_stays_on_screen_when_the_view_gets_lower_and_a_node_is_selected()
    {
        DetailWindow window = Open();
        StructureTree tree = window.Model.Tree!;
        TreeRow stream = tree.Root.RowAt(0);
        tree.Expand(stream);
        TreeRow big = Enumerable.Range(0, stream.LoadChildren().Count).Select(i => stream.RowAt(i)).First(r => r.Kind != NodeKind.Frame && r.Node.Length > 5000);
        long deep = big.Node.Offset + 3000;
        window.Hex.ByteCursor = deep;
        UiPump.Until(() => true);
        Assert.Equal((deep / 16) - window.Hex.VisibleRows + 1, window.Hex.TopRow);

        // Several rows lower, in two steps, with the selection changing in between.
        for (int i = 0; i < 2; i++)
        {
            window.Height -= 40;
            UiPump.Until(() => true);
            window.Model.SelectedNode = i == 0 ? big : tree.Root;
            UiPump.Until(() => true);
            Assert.InRange(deep / 16, window.Hex.TopRow, window.Hex.TopRow + window.Hex.VisibleRows - 1);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Selecting_a_node_never_resizes_the_hex_view()
    {
        DetailWindow window = Open();
        StructureTree tree = window.Model.Tree!;
        TreeRow stream = tree.Root.RowAt(0);
        tree.Expand(stream);
        double height = window.Hex.Bounds.Height;
        Assert.True(height > 0);

        // Nodes with a long description and a short one, and a field of a few bits with its strip.
        List<TreeRow> rows = [tree.Root, stream];
        TreeRow frame = Enumerable.Range(0, stream.LoadChildren().Count).Select(i => stream.RowAt(i)).First(r => r.Kind == NodeKind.Frame);
        tree.Expand(frame);
        rows.Add(frame);
        foreach (TreeRow child in Enumerable.Range(0, frame.LoadChildren().Count).Select(i => frame.RowAt(i)).ToList())
        {
            rows.Add(child);
            if (child.HasChildren)
            {
                tree.Expand(child);
                rows.AddRange(Enumerable.Range(0, child.LoadChildren().Count).Select(i => child.RowAt(i)));
            }
        }

        Assert.Contains(rows, r => r.Node.BitLength is not null);
        Assert.Contains(rows, r => (r.Description?.Length ?? 0) < 60);
        Assert.Contains(rows, r => (r.Description?.Length ?? 0) > 150);

        foreach (TreeRow row in rows)
        {
            window.Model.SelectedNode = row;
            UiPump.Until(() => true);
            Assert.Equal(height, window.Hex.Bounds.Height);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Home_and_end_move_the_cursor_in_the_row_and_with_control_in_the_file()
    {
        DetailWindow window = Open();
        long length = window.Model.Document!.Length;
        Point point = Cell(window, 37);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal(37, window.Hex.ByteCursor);

        Press(window, Key.Home, PhysicalKey.Home);
        Assert.Equal(32, window.Hex.ByteCursor);
        Press(window, Key.End, PhysicalKey.End);
        Assert.Equal(47, window.Hex.ByteCursor);
        window.KeyPress(Key.End, RawInputModifiers.Control, PhysicalKey.End, null);
        Assert.Equal(length - 1, window.Hex.ByteCursor);
        Press(window, Key.End, PhysicalKey.End);
        Assert.Equal(length - 1, window.Hex.ByteCursor);
        window.KeyPress(Key.Home, RawInputModifiers.Control, PhysicalKey.Home, null);
        Assert.Equal(0, window.Hex.ByteCursor);
        window.Close();
    }
}
