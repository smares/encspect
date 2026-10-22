using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
    public void The_window_first_opens_centered_over_the_main_window()
    {
        MainWindow main = OpenMain();
        main.Position = new PixelPoint(300, 200);
        UiPump.Until(() => true);

        DetailWindow window = main.DetailWindows.Open(main.Model.Rows[0]);
        UiPump.Until(() => true);

        // The centers of the two windows' client areas, in screen pixels.
        Rect owner = new PixelRect(main.Position, PixelSize.FromSize(main.ClientSize, main.DesktopScaling)).ToRect(1);
        Rect detail = new PixelRect(window.Position, PixelSize.FromSize(window.ClientSize, window.DesktopScaling)).ToRect(1);
        Assert.InRange(detail.Center.X - owner.Center.X, -1, 1);
        Assert.InRange(detail.Center.Y - owner.Center.Y, -1, 1);
        Assert.True(owner.Contains(detail), $"{detail} lies outside {owner}.");
        main.Close();
    }

    [AvaloniaFact]
    public void Escape_closes_the_window_and_the_next_one_opens_where_it_was()
    {
        string settingsPath = _folder["settings/settings.json"];
        MainWindow main = OpenMain(Settings.Load(settingsPath));
        DetailWindow window = main.DetailWindows.Open(main.Model.Rows[0]);

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
    public void A_maximized_window_keeps_its_normal_placement_and_opens_maximized()
    {
        string settingsPath = _folder["settings/settings.json"];
        MainWindow main = OpenMain(Settings.Load(settingsPath));
        DetailWindow window = main.DetailWindows.Open(main.Model.Rows[0]);
        window.Position = new PixelPoint(40, 50);
        window.Width = 700;
        window.Height = 500;
        UiPump.Until(() => true);

        // Maximizing moves and resizes the window, as the system does.
        window.WindowState = WindowState.Maximized;
        window.Position = new PixelPoint(0, 0);
        window.Width = 1600;
        window.Height = 1000;
        UiPump.Until(() => true);
        window.Close();

        WindowPlacement saved = Settings.Load(settingsPath).DetailWindow!;
        Assert.Equal(new WindowPlacement(40, 50, 700, 500, true), saved);

        DetailWindow again = main.DetailWindows.Open(main.Model.Rows[1]);
        Assert.Equal(WindowState.Maximized, again.WindowState);
        main.Close();
    }

    // Wayland does not let a program place its windows: the saved position is not touched, whatever
    // Position reads, and the size and maximized state come back.
    [AvaloniaFact]
    public void Where_windows_cannot_be_placed_the_saved_position_survives_and_size_and_state_return()
    {
        string settingsPath = _folder["settings/settings.json"];
        var settings = Settings.Load(settingsPath);
        settings.DetailWindow = new WindowPlacement(40, 50, 700, 500, true);
        MainWindow main = OpenMain(settings);
        main.DetailWindows.CanPlaceOverride = false;

        DetailWindow window = main.DetailWindows.Open(main.Model.Rows[0]);
        UiPump.Until(() => true);
        Assert.Equal(700, window.Width);
        Assert.Equal(500, window.Height);
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.NotEqual(new PixelPoint(40, 50), window.Position);
        window.Close();

        Assert.Equal(40, Settings.Load(settingsPath).DetailWindow!.X);
        Assert.Equal(50, Settings.Load(settingsPath).DetailWindow!.Y);
        Assert.True(Settings.Load(settingsPath).DetailWindow!.IsMaximized);

        // Resized in a later session, the position still stays.
        DetailWindow again = main.DetailWindows.Open(main.Model.Rows[1]);
        again.WindowState = WindowState.Normal;
        again.Width = 640;
        again.Height = 480;
        UiPump.Until(() => true);
        again.Close();
        WindowPlacement saved = Settings.Load(settingsPath).DetailWindow!;
        Assert.Equal((40, 50, false), (saved.X, saved.Y, saved.IsMaximized));
        Assert.Equal(640, saved.Width);
        main.Close();
    }

    [AvaloniaFact]
    public void Where_windows_cannot_be_placed_a_first_session_saves_no_position()
    {
        string settingsPath = _folder["settings/settings.json"];
        MainWindow main = OpenMain(Settings.Load(settingsPath));
        main.DetailWindows.CanPlaceOverride = false;
        main.DetailWindows.Open(main.Model.Rows[0]).Close();
        main.Close();

        WindowPlacement saved = Settings.Load(settingsPath).DetailWindow!;
        Assert.Null(saved.X);
        Assert.Null(saved.Y);

        // A session that can place windows centers the window, as there is no position to restore.
        MainWindow next = OpenMain(Settings.Load(settingsPath));
        next.Position = new PixelPoint(300, 200);
        UiPump.Until(() => true);
        DetailWindow window = next.DetailWindows.Open(next.Model.Rows[0]);
        UiPump.Until(() => true);
        Assert.InRange(window.Position.X, 300, 300 + (int)(next.ClientSize.Width * next.DesktopScaling));
        next.Close();
    }

    [AvaloniaFact]
    public void Where_windows_cannot_be_placed_new_windows_are_not_cascaded()
    {
        MainWindow main = OpenMain();
        main.DetailWindows.CanPlaceOverride = false;
        DetailWindow first = main.DetailWindows.OpenNew(main.Model.Rows[0]);
        DetailWindow second = main.DetailWindows.OpenNew(main.Model.Rows[1]);
        UiPump.Until(() => true);

        Assert.Equal(first.Position, second.Position);
        Assert.Equal(new PixelPoint(0, 0), second.Position);
        main.Close();
    }

    [Fact]
    public void An_old_settings_file_with_a_position_still_loads()
    {
        string path = _folder["old.json"];
        File.WriteAllText(path, "{\"DetailWindow\":{\"X\":7,\"Y\":9,\"Width\":600,\"Height\":400,\"IsMaximized\":false}}");
        Assert.Equal(new WindowPlacement(7, 9, 600, 400, false), Settings.Load(path).DetailWindow);
    }

    [AvaloniaFact]
    public void The_context_menu_opens_the_selected_file_in_a_new_window()
    {
        MainWindow main = OpenMain();
        DetailWindow shared = main.DetailWindows.Open(main.Model.Rows[0]);
        main.Model.SelectedRow = main.Model.Rows[2];

        MenuItem item = main.Files.ContextMenu!.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Open in New Window");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        DetailWindow other = Assert.Single(main.DetailWindows.Independent);
        Assert.True(other.IsVisible);
        Assert.Equal(main.Model.Rows[2].Path, other.Model.Path);
        Assert.Equal(main.Model.Rows[2].Path, shared.Model.Path);
        main.Close();
    }

    [AvaloniaFact]
    public void Refresh_reads_the_files_of_the_open_windows_again()
    {
        MainWindow main = OpenMain();
        FileRow selected = main.Model.Rows[1];
        DetailWindow shared = main.DetailWindows.Open(selected);
        DetailWindow other = main.DetailWindows.OpenNew(main.Model.Rows[3]);
        UiPump.Until(() => shared.Model.Tree is not null && other.Model.Tree is not null);
        Report sharedReport = shared.Model.Report!;
        Report otherReport = other.Model.Report!;
        Assert.Contains("BladeEnc", shared.Model.StreamsText, StringComparison.Ordinal);

        // A frame selected in one window, and the other's file replaced by another on disk.
        StructureTree tree = other.Model.Tree!;
        TreeRow stream = tree.Root.RowAt(0);
        tree.Expand(stream);
        TreeRow frame = tree[tree.IndexOf(stream) + 4];
        other.Model.SelectedNode = frame;
        string replacement = _folder["replacement.tmp"];
        File.Copy(Fixtures.Mpeg("external-lame-tagged.mp3"), replacement);
        File.Move(replacement, selected.Path, overwrite: true);

        main.Model.Refresh();
        UiPump.Until(() => !main.Model.IsRunning && shared.Model.Report is { } a && a != sharedReport && other.Model.Report is { } b && b != otherReport);

        Assert.Same(main.Model.Rows[1], main.Model.SelectedRow);
        Assert.Same(main.Model.SelectedRow, main.Files.SelectedItem);
        Assert.Same(main.Model.Rows[1], shared.Row);
        Assert.Same(main.Model.Rows[3], other.Row);
        Assert.Contains("LAME 3.100", shared.Model.StreamsText, StringComparison.Ordinal);
        Assert.Equal([frame.Parent!.ChildIndex, frame.ChildIndex], [other.Model.SelectedNode!.Parent!.ChildIndex, other.Model.SelectedNode.ChildIndex]);
        Assert.Equal(frame.Node.Offset, other.Model.SelectedNode.Node.Offset);
        Assert.True(shared.Model.CanGoPrevious);
        Assert.True(shared.Model.CanGoNext);
        Assert.True(other.Model.CanGoPrevious);
        Assert.False(other.Model.CanGoNext);

        // Clearing the cache reads them again too.
        sharedReport = shared.Model.Report!;
        main.Model.ClearCache();
        UiPump.Until(() => !main.Model.IsRunning && shared.Model.Report is { } c && c != sharedReport);
        main.Close();
    }

    [AvaloniaFact]
    public void Include_subfolders_keeps_the_selected_file_and_the_windows_on_it_without_reading_it_again()
    {
        MainWindow main = OpenMain();
        FileRow selected = main.Model.Rows[1];
        DetailWindow shared = main.DetailWindows.Open(selected);
        UiPump.Until(() => shared.Model.Tree is not null);
        Report report = shared.Model.Report!;

        main.Model.IncludeSubfolders = true;
        UiPump.Until(() => !main.Model.IsRunning);

        Assert.NotSame(selected, main.Model.SelectedRow);
        Assert.Equal(selected.Path, main.Model.SelectedRow!.Path);
        Assert.Same(main.Model.SelectedRow, shared.Row);
        Assert.Same(report, shared.Model.Report);
        Assert.False(shared.Model.IsLoading);
        Assert.True(shared.Model.CanGoNext);
        main.Close();
    }

    [AvaloniaFact]
    public void The_window_menu_lists_the_open_windows()
    {
        MainWindow main = OpenMain();
        DetailWindow shared = main.DetailWindows.Open(main.Model.Rows[0]);
        DetailWindow other = main.DetailWindows.OpenNew(main.Model.Rows[1]);

        // Windows of other tests may still be open; these three opened last.
        NativeMenu menu = NativeMenu.GetMenu(other)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Window").Menu!;
        NativeMenuItem[] listed = [.. menu.Items.OfType<NativeMenuItem>().Where(i => i.ToggleType == MenuItemToggleType.CheckBox)];
        Assert.Contains(menu.Items.OfType<NativeMenuItem>(), i => i.Header == "Bring All to Front");
        Assert.Equal([main.Title, shared.Title, other.Title], listed.TakeLast(3).Select(i => i.Header));
        Assert.Equal([true], listed.Where(i => i.IsChecked).Select(_ => true));
        Assert.True(listed[^1].IsChecked);

        other.Close();
        NativeMenuItem[] after = [.. NativeMenu.GetMenu(shared)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Window").Menu!
            .Items.OfType<NativeMenuItem>().Where(i => i.ToggleType == MenuItemToggleType.CheckBox)];
        Assert.Equal(listed.Length - 1, after.Length);
        Assert.Equal([main.Title, shared.Title], after.TakeLast(2).Select(i => i.Header));
        Assert.True(after[^1].IsChecked);
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
    public void The_detail_window_offers_the_formats_a_file_also_matches_and_analyzes_as_a_chosen_one()
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1100, Height = 760 };
        window.Show();
        Task load = window.Model.LoadAsync(Path.Combine(Fixtures.Directory, "ac3", "junk-adts-in-frames-cut.eac3"));
        UiPump.Until(() => load.IsCompleted);
        UiPump.Until(() => true);

        Assert.True(window.DetectionRow.IsVisible);
        Assert.Equal("Also matches:", window.DetectionText.Text);
        Button chip = window.AlternativeChoices.GetVisualDescendants().OfType<Button>().Single();
        Assert.Equal("AAC", chip.Content);

        chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        UiPump.Until(() => window.Model.ForcedFormat == "AAC" && window.Model.Report is { Detection.IsForced: true });
        Assert.Equal("AAC", window.Model.Report!.Root.Value);
        Assert.StartsWith("Analyzed as AAC as requested", window.DetectionText.Text, StringComparison.Ordinal);
        Assert.Equal("AC-3", window.AlternativeChoices.GetVisualDescendants().OfType<Button>().Single().Content);

        // The menu lists detection and every format, and a choice analyzes the file as it.
        var menu = Assert.IsType<Avalonia.Controls.MenuFlyout>(window.AnalyzeAsButton.Flyout);
        menu.ShowAt(window.AnalyzeAsButton);
        string[] headers = [.. menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!)];
        Assert.Equal(["Detect Automatically", .. window.Model.FormatNames], headers);
        MenuItem dts = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "DTS");
        dts.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        UiPump.Until(() => window.Model.ForcedFormat == "DTS" && window.Model.Report is { Detection.Format: "DTS" });
        menu.Hide();
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

        // Dragged from a file manager onto the file list.
        Point point = main.Files.TranslatePoint(new Point(200, 120), main)!.Value;
        Drop(main, point, Files(_folder["damaged-bitrate.mp3"], other["album"]));
        Assert.Equal("2 items", main.Model.Title);
        UiPump.Until(() => !main.Model.IsRunning);
        Assert.Equal(["damaged-bitrate.mp3", Path.Join("album", "one.mp3")], main.Model.Rows.Select(r => r.Name));
        Assert.All(main.Model.Rows, r => Assert.Equal("MPEG Audio", r.Format));

        // Text is no file to open.
        var text = new DataTransfer();
        text.Add(DataTransferItem.CreateText(other.Path));
        Drop(main, point, text);
        Assert.Equal("2 items", main.Model.Title);

        Drop(main, point, Files(other.Path));
        UiPump.Until(() => !main.Model.IsRunning);
        Assert.Equal(other.Path, main.Model.Title);
        Assert.Empty(main.Model.Rows);

        main.KeyPress(Key.I, Shortcuts.Command == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control, PhysicalKey.I, null);
        Assert.True(main.Model.IncludeSubfolders);
        UiPump.Until(() => !main.Model.IsRunning);
        Assert.Equal(Path.Join("album", "one.mp3"), Assert.Single(main.Model.Rows).Name);
        main.Close();
    }

    [AvaloniaFact]
    public void A_sorted_list_sorts_again_as_rows_fill_in_and_keeps_the_selection()
    {
        var model = new MainViewModel(Analyzer.CreateDefault(), roots: []);
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), model);
        main.Show();
        var rows = new List<FileRow>();
        for (int i = 0; i < 60; i++)
        {
            var row = new FileRow($"/{i}", $"{i:D2}");
            model.Rows.Add(row);
            rows.Add(row);
        }

        main.Files.Columns.Single(c => (string)c.Header! == "Duration").Sort(ListSortDirection.Ascending);
        UiPump.Until(() => model.RowsView.SortDescriptions.Count == 1);
        model.SelectedRow = rows[5];
        UiPump.Until(() => true);
        int changes = 0;
        model.SelectedRowChanged += (_, _) => changes++;
        foreach (FileRow row in rows)
        {
            row.Apply(new FileSummary(new ReportSummary(row.Path, "MPEG Audio", 1, "MPEG-1 Layer III", 100 - rows.IndexOf(row), 1, 128, 44100, null, null, null, null, null, null, [], 0, 0, 0), false, null));
        }

        model.SortAgain();
        UiPump.Until(() => true);

        Assert.Equal(["59", "58", "57"], model.RowsView.Cast<FileRow>().Take(3).Select(r => r.Name));
        Assert.Same(rows[5], model.SelectedRow);
        Assert.Same(rows[5], main.Files.SelectedItem);
        Assert.Equal(0, changes);
        main.Close();
    }

    [AvaloniaFact]
    public void A_run_ends_sorted_by_the_column_chosen_before_it()
    {
        MainWindow main = OpenMain();
        main.Files.Columns.Single(c => (string)c.Header! == "Duration").Sort(ListSortDirection.Descending);
        UiPump.Until(() => main.Model.RowsView.SortDescriptions.Count == 1);

        // Refreshing lists the rows again, unsorted until they fill in.
        main.Model.Refresh();
        Assert.True(main.Model.IsRunning);
        UiPump.Until(() => !main.Model.IsRunning);
        double?[] durations = [];
        UiPump.Until(() =>
        {
            durations = [.. main.Model.RowsView.Cast<FileRow>().Select(r => r.Duration)];
            return durations.SequenceEqual(durations.OrderDescending());
        }, 3000);
        Assert.Equal(4, durations.Length);
        main.Close();
    }

    private static void Drop(MainWindow window, Point point, DataTransfer data)
    {
        window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
        window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Copy);
        window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy);
    }

    private static DataTransfer Files(params string[] paths)
    {
        var data = new DataTransfer();
        foreach (string path in paths)
        {
            data.Add(DataTransferItem.CreateFile(LocalStorageItem.Create(path)));
        }

        return data;
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

// A file or folder as a file manager's drag hands it over: a storage item with a file URI. Avalonia
// keeps IStorageItem from being implemented by user code at compile time, so a proxy implements it
// when the test runs.
public class LocalStorageItem : DispatchProxy
{
    private string _path = string.Empty;

    public static IStorageItem Create(string path)
    {
        IStorageItem item = Create<IStorageItem, LocalStorageItem>();
        ((LocalStorageItem)(object)item)._path = path;
        return item;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "get_Path" => new Uri(_path),
        "get_Name" => Path.GetFileName(_path),
        "get_CanBookmark" => false,
        "Dispose" => null,
        _ => throw new NotSupportedException(targetMethod?.Name),
    };
}
