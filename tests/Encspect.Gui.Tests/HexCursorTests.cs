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
}
