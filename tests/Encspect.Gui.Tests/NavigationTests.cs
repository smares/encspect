using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// Previous and Next while the list fills in, is sorted and is listed again.
public sealed class NavigationTests
{
    [Fact]
    public void A_growing_list_does_not_look_the_row_up_for_each_row_added()
    {
        var rows = new ObservableCollection<object>();
        var neighbors = new ListNeighbors(rows);
        rows.CollectionChanged += (_, e) => neighbors.Update(e);
        object row = new();
        rows.Add(row);
        neighbors.Track(row);
        Assert.False(neighbors.HasPrevious);
        Assert.False(neighbors.HasNext);

        for (int i = 0; i < 50_000; i++)
        {
            rows.Add(new object());
        }

        Assert.True(neighbors.HasNext);
        Assert.False(neighbors.HasPrevious);
        rows.Insert(0, new object());
        Assert.True(neighbors.HasPrevious);
        Assert.Equal(1, neighbors.Lookups);

        // A row of an earlier listing is not looked for at all, until it is added itself.
        object stale = new();
        neighbors.Track(stale);
        Assert.False(neighbors.IsListed);
        for (int i = 0; i < 50_000; i++)
        {
            rows.Add(new object());
        }

        Assert.False(neighbors.HasNext);
        rows.Insert(10, stale);
        Assert.True(neighbors.IsListed);
        Assert.True(neighbors.HasPrevious);
        Assert.True(neighbors.HasNext);
        Assert.Equal(2, neighbors.Lookups);

        // Anything but an added row looks it up again.
        rows.RemoveAt(0);
        Assert.Equal(3, neighbors.Lookups);
    }

    [Fact]
    public void Neighbors_stay_right_as_rows_are_inserted_in_sorted_order()
    {
        var model = new MainViewModel(Analyzer.CreateDefault(), new TestDispatcher().Post, roots: []);
        model.RowsView.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(FileRow.Duration), ListSortDirection.Ascending));
        var neighbors = new ListNeighbors(model.RowsView);
        model.RowsView.CollectionChanged += (_, e) => neighbors.Update(e);
        var random = new Random(5);
        FileRow tracked = Row(500);
        model.Rows.Add(tracked);
        neighbors.Track(tracked);

        for (int i = 0; i < 400; i++)
        {
            model.Rows.Add(Row(random.Next(1000)));
            int index = model.RowsView.IndexOf(tracked);
            Assert.Equal(index > 0, neighbors.HasPrevious);
            Assert.Equal(index < model.RowsView.Count - 1, neighbors.HasNext);
        }

        Assert.Equal(1, neighbors.Lookups);
    }

    [AvaloniaFact]
    public void An_open_detail_window_costs_no_lookup_per_row_listed()
    {
        var model = new MainViewModel(Analyzer.CreateDefault(), roots: []);
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), model);
        main.Show();
        FileRow first = Row(1);
        model.Rows.Add(first);
        DetailWindow window = main.DetailWindows.Open(first);
        Assert.False(window.Model.CanGoNext);

        for (int i = 0; i < 20_000; i++)
        {
            model.Rows.Add(Row(i));
        }

        Assert.True(window.Model.CanGoNext);
        Assert.False(window.Model.CanGoPrevious);
        Assert.Equal(1, main.DetailWindows.NeighborLookups(window));
        main.Close();
    }

    [AvaloniaFact]
    public void Previous_and_next_scroll_the_list_to_the_selected_row()
    {
        var model = new MainViewModel(Analyzer.CreateDefault(), roots: []);
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), model) { Width = 1000, Height = 600 };
        main.Show();
        for (int i = 0; i < 200; i++)
        {
            model.Rows.Add(Row(i));
        }

        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        DetailWindow window = main.DetailWindows.Open(model.Rows[0]);
        for (int i = 0; i < 120; i++)
        {
            window.NextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Same(model.Rows[120], model.SelectedRow);
        DataGridRow[] shown = [.. main.Files.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsVisible)];
        Assert.Contains(shown, r => r.DataContext == model.Rows[120]);
        Assert.DoesNotContain(shown, r => r.DataContext == model.Rows[0]);
        main.Close();
    }

    private static FileRow Row(double duration)
    {
        var row = new FileRow($"/{Guid.NewGuid():N}.mp3", $"{duration}");
        row.Apply(new FileSummary(new ReportSummary(row.Path, "MPEG Audio", 1, "MPEG-1 Layer III", duration, 1152, 128, 44100, null, null, null, [], 0, 0, 0), false, null));
        return row;
    }
}
