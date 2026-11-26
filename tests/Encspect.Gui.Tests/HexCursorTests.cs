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
        Assert.DoesNotContain(selected.Node.Children, c => c.Length > 0 && c.Offset <= target && target < c.End && c.Ranges is null);
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

    // Opening the menu is what enables its items.
    private static Avalonia.Controls.MenuItem Item(Avalonia.Controls.ContextMenu? menu, string header)
    {
        menu!.Open();
        menu.Close();
        return menu.Items.OfType<Avalonia.Controls.MenuItem>().Single(i => (string?)i.Header == header);
    }

    [AvaloniaFact]
    public void The_trees_menu_copies_a_nodes_value_and_the_node_with_the_nodes_under_it()
    {
        DetailWindow window = Open();
        StructureTree tree = window.Model.Tree!;
        TreeRow stream = tree.Root.RowAt(0);
        tree.Expand(stream);
        TreeRow frame = Enumerable.Range(0, stream.LoadChildren().Count).Select(i => stream.RowAt(i)).First(r => r.Kind == NodeKind.Frame);

        window.Model.SelectedNode = frame;
        Click(Item(window.TreeList.ContextMenu, "Copy Node"));
        string text = Copying.Last!;
        Assert.StartsWith("  " + frame.Name, text);
        Assert.Contains(Environment.NewLine + "    ", text);
        Assert.Equal(text, window.Model.NodeText(frame));
        Assert.Contains($"[0x{frame.Node.Offset:X}, ", text);

        TreeRow field = Enumerable.Range(0, frame.LoadChildren().Count).Select(i => { tree.Expand(frame); return frame.RowAt(i); }).First(r => r.Value is not null);
        window.Model.SelectedNode = field;
        Click(Item(window.TreeList.ContextMenu, "Copy Value"));
        Assert.Equal(field.Value, Copying.Last);

        // A node with no value has nothing to copy as one.
        window.Model.SelectedNode = tree.Root;
        Assert.Equal(tree.Root.Value is not null, Item(window.TreeList.ContextMenu, "Copy Value").IsEnabled);
        window.Close();
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

        Click(Item(window.FindingList.ContextMenu, "Copy Message"));
        Assert.Equal(finding.Message, Copying.Last);
        Click(Item(window.FindingList.ContextMenu, "Copy Message with Code and Position"));
        Assert.StartsWith($"[{finding.Severity}] {finding.Code}: ", Copying.Last);
        Assert.Contains(finding.Message, Copying.Last);
        Assert.Contains($"[0x{finding.Finding.Node!.Offset:X}", Copying.Last);
        window.Close();
    }

    [AvaloniaFact]
    public void The_hex_views_menu_copies_the_rows_the_selection_touches()
    {
        DetailWindow window = Open();
        window.Model.SelectedNode = window.Model.Tree!.Root.RowAt(0);
        UiPump.Until(() => true);
        HexHighlight highlight = window.Model.Highlight!;

        Click(Item(window.Hex.ContextMenu, "Copy Selection"));
        string text = Copying.Last!;
        string[] rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(((highlight.End + 15) / 16) - (highlight.Start / 16), rows.Length);
        Assert.StartsWith($"{highlight.Start / 16 * 16:X8}  ", rows[0]);
        Assert.Equal(text, HexView.RowsText(window.Model.Document!, highlight.Start, highlight.End));
        window.Close();
    }
}
