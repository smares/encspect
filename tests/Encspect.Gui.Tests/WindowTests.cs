using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

public sealed class WindowTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public WindowTests()
    {
        foreach (string name in new[] { "external-lame-cbr128-joint.mp3", "external-lame-vbr-v2.mp3", "damaged-bitrate.mp3", "external-blade-stereo.mp3" })
        {
            _folder.Copy(Fixtures.Mpeg(name), name);
        }
    }

    public void Dispose() => _folder.Dispose();

    [AvaloniaFact]
    public void One_detail_window_is_reused_and_follows_the_selection()
    {
        MainWindow main = OpenMain();
        FileRow[] rows = [.. main.Model.Rows];

        DetailWindow window = main.DetailWindows.Open(rows[0]);
        Assert.True(window.IsVisible);
        Assert.Equal(rows[0].Path, window.Model.Path);

        Assert.Same(window, main.DetailWindows.Open(rows[1]));
        Assert.Equal(rows[1].Path, window.Model.Path);
        Assert.Same(rows[1], main.Model.SelectedRow);

        main.Model.SelectedRow = rows[2];
        Assert.Equal(rows[2].Path, window.Model.Path);
        UiPump.Until(() => window.Model.Tree is not null);
        Assert.Equal(rows[2].Path, window.Model.Report!.Source);
        Assert.Empty(main.DetailWindows.Independent);
        main.Close();
    }

    [AvaloniaFact]
    public void Previous_and_next_step_through_the_list_in_its_order()
    {
        MainWindow main = OpenMain();
        // The grid sorts on the UI thread's next turn, as after a click on the header.
        main.Files.Columns.Single(c => (string)c.Header! == "Duration").Sort(ListSortDirection.Descending);
        UiPump.Until(() => main.Model.RowsView.SortDescriptions.Count == 1);
        FileRow[] order = main.Model.RowsView.Cast<FileRow>().ToArray();
        Assert.Equal(order.Select(r => r.Duration).OrderDescending(), order.Select(r => r.Duration));

        DetailWindow window = main.DetailWindows.Open(order[0]);
        Assert.False(window.Model.CanGoPrevious);
        Assert.True(window.Model.CanGoNext);

        window.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Same(order[1], main.Model.SelectedRow);
        Assert.Equal(order[1].Path, window.Model.Path);

        window.KeyPress(Key.Down, Shortcuts.Command == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control, PhysicalKey.ArrowDown, null);
        Assert.Equal(order[2].Path, window.Model.Path);

        window.PreviousButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.PreviousButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(order[0].Path, window.Model.Path);
        Assert.False(window.Model.CanGoPrevious);
        main.Close();
    }

    [AvaloniaFact]
    public void Escape_closes_the_window_and_the_next_one_opens_where_it_was()
    {
        string settingsPath = _folder["settings/settings.json"];
        MainWindow main = OpenMain(Settings.Load(settingsPath));
        DetailWindow window = main.DetailWindows.Open(main.Model.Rows[0]);

        // Centered over the main window the first time.
        PixelPoint expected = new(
            main.Position.X + (int)((main.ClientSize.Width - window.Width) * main.DesktopScaling / 2),
            main.Position.Y + (int)((main.ClientSize.Height - window.Height) * main.DesktopScaling / 2));
        Assert.Equal(expected, window.Position);

        window.Position = new PixelPoint(40, 50);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(window.IsVisible);
        Assert.Null(main.DetailWindows.Shared);
        Assert.True(File.Exists(settingsPath));

        DetailWindow again = main.DetailWindows.Open(main.Model.Rows[1]);
        Assert.NotSame(window, again);
        Assert.Equal(new PixelPoint(40, 50), again.Position);
        main.Close();

        // And in the next session.
        MainWindow next = OpenMain(Settings.Load(settingsPath));
        Assert.Equal(new PixelPoint(40, 50), next.DetailWindows.Open(next.Model.Rows[0]).Position);
        next.Close();
    }

    [AvaloniaFact]
    public void Enter_and_a_double_click_open_the_selected_file()
    {
        MainWindow main = OpenMain();
        main.Model.SelectedRow = main.Model.Rows[1];
        main.Files.Focus();

        main.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        Assert.NotNull(main.DetailWindows.Shared);
        Assert.Equal(main.Model.Rows[1].Path, main.DetailWindows.Shared.Model.Path);
        Assert.Same(main.Model.Rows[1], main.Model.SelectedRow);
        main.DetailWindows.Shared.Close();

        DataGridRow row = main.Files.GetVisualDescendants().OfType<DataGridRow>().First(r => r.DataContext == main.Model.Rows[2]);
        Point center = row.TranslatePoint(new Point(40, row.Bounds.Height / 2), main)!.Value;
        main.MouseDown(center, MouseButton.Left);
        main.MouseUp(center, MouseButton.Left);
        main.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
        main.MouseUp(center, MouseButton.Left);
        Assert.NotNull(main.DetailWindows.Shared);
        Assert.Equal(main.Model.Rows[2].Path, main.DetailWindows.Shared.Model.Path);
        main.Close();
    }

    [AvaloniaFact]
    public void A_new_window_stays_on_its_file()
    {
        MainWindow main = OpenMain();
        DetailWindow shared = main.DetailWindows.Open(main.Model.Rows[0]);

        DetailWindow other = main.DetailWindows.OpenNew(main.Model.Rows[3]);
        main.Model.SelectedRow = main.Model.Rows[1];

        Assert.NotSame(shared, other);
        Assert.Equal(main.Model.Rows[3].Path, other.Model.Path);
        Assert.Equal(main.Model.Rows[1].Path, shared.Model.Path);
        Assert.Single(main.DetailWindows.Independent);

        main.Close();
        Assert.False(other.IsVisible);
        Assert.False(shared.IsVisible);
    }

    [AvaloniaFact]
    public void The_tree_list_realizes_only_the_rows_on_screen()
    {
        string path = _folder["long.mp3"];
        byte[] fixture = File.ReadAllBytes(Fixtures.Mpeg("external-blade-flags.mp3"));
        using (FileStream output = File.Create(path))
        {
            for (int i = 0; i < 40; i++)
            {
                output.Write(fixture);
            }
        }

        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1100, Height = 760 };
        window.Show();
        Task load = window.Model.LoadAsync(path);
        UiPump.Until(() => load.IsCompleted);
        StructureTree tree = window.Model.Tree!;
        TreeRow stream = tree.Root.RowAt(0);

        tree.Expand(stream);
        window.Model.SelectedNode = tree[tree.Count - 1];
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(tree.Count > 3000);
        int realized = window.TreeList.GetRealizedContainers().Count();
        Assert.InRange(realized, 5, 80);
        Assert.InRange(stream.Rows!.Count(r => r is not null), realized - 5, realized + 40);
        Assert.Contains(window.TreeList.GetRealizedContainers(), c => c.DataContext == tree[tree.Count - 1]);
        window.Close();
    }

    [AvaloniaFact]
    public void The_hex_view_scrolls_to_the_selected_bytes()
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1100, Height = 760 };
        window.Show();
        Task load = window.Model.LoadAsync(Fixtures.Mpeg("external-lame-vbr-v2.mp3"));
        UiPump.Until(() => load.IsCompleted);
        StructureTree tree = window.Model.Tree!;
        TreeRow stream = tree.Root.RowAt(0);
        tree.Expand(stream);
        TreeRow last = tree[tree.Count - 1];
        Assert.Equal(NodeKind.Frame, last.Kind);

        window.Model.SelectedNode = last;
        UiPump.Until(() => true);

        long row = last.Node.Offset / 16;
        Assert.InRange(row, window.Hex.TopRow, window.Hex.TopRow + window.Hex.VisibleRows - 1);
        Assert.NotNull(window.CaptureRenderedFrame());
        window.Close();
    }

    [AvaloniaFact]
    public void Dropped_files_and_folders_are_analyzed_and_a_shortcut_includes_subfolders()
    {
        MainWindow main = OpenMain();
        using var other = new TempFolder();
        other.Copy(Fixtures.Mpeg("external-blade-stereo.mp3"), "album/one.mp3");

        main.OpenItems([_folder["damaged-bitrate.mp3"], other["album"]]);
        UiPump.Until(() => !main.Model.IsRunning);
        Assert.Equal(["damaged-bitrate.mp3", Path.Join("album", "one.mp3")], main.Model.Rows.Select(r => r.Name));
        Assert.All(main.Model.Rows, r => Assert.Equal("MPEG Audio", r.Format));

        main.OpenItems([other.Path]);
        UiPump.Until(() => !main.Model.IsRunning);
        Assert.Empty(main.Model.Rows);

        main.KeyPress(Key.I, Shortcuts.Command == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control, PhysicalKey.I, null);
        Assert.True(main.Model.IncludeSubfolders);
        UiPump.Until(() => !main.Model.IsRunning);
        Assert.Equal(Path.Join("album", "one.mp3"), Assert.Single(main.Model.Rows).Name);
        main.Close();
    }

    private MainWindow OpenMain(Settings? settings = null)
    {
        var main = new MainWindow(Analyzer.CreateDefault(), settings ?? new Settings(), new MainViewModel(Analyzer.CreateDefault(), roots: []));
        main.Show();
        main.Model.Open(FileSource.ForFolder(_folder.Path));
        UiPump.Until(() => !main.Model.IsRunning);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return main;
    }
}
