using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// What the detail window shows while a copy is written or a decoded file is read again, and what Stop and closing the window do.
public class CopyStopTests
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

    private static MenuItem CopyNodeItem(DetailWindow window) => window.TreeList.ContextMenu!.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Copy Node");

    // A copy that writes until it is canceled, and says whether it saw the cancellation.
    private sealed class BlockedCopy
    {
        public readonly ManualResetEventSlim Started = new();

        public volatile bool SawCancellation;

        public string? Write(CancellationToken token)
        {
            Started.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            SawCancellation = token.IsCancellationRequested;
            token.ThrowIfCancellationRequested();
            return "text";
        }
    }

    [AvaloniaFact]
    public void A_copy_shows_its_status_dims_the_item_shows_stop_and_stop_copies_nothing()
    {
        DetailWindow window = Open();
        var copied = new List<string>();
        Copying.Observer = copied.Add;
        try
        {
            var blocked = new BlockedCopy();
            Button stop = window.FindControl<Button>("StopDecodingButton")!;
            Assert.False(stop.IsVisible);
            Assert.True(CopyNodeItem(window).IsEnabled);

            Task<string?> copy = window.Model.PrepareCopyAsync("the node", blocked.Write);
            UiPump.Until(() => blocked.Started.IsSet);
            UiPump.Until(() => true);

            Assert.Equal("Copying the node…", window.Model.CommandStatus);
            Assert.True(window.Model.IsCopying);
            Assert.True(window.Model.IsProgressShown);
            Assert.True(window.Model.IsProgressIndeterminate);
            Assert.True(stop.IsVisible);
            Assert.False(window.FindControl<Button>("DecodeAudioButton")!.IsVisible);
            Assert.False(CopyNodeItem(window).IsEnabled);
            Assert.Equal("Copying the node…", window.FindControl<TextBlock>("CommandStatusText")!.Text);

            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiPump.Until(() => copy.IsCompleted);

            Assert.Null(copy.Result);
            Assert.True(blocked.SawCancellation);
            Assert.Equal("Copy stopped", window.Model.CommandStatus);
            Assert.False(window.Model.IsCopying);
            Assert.False(stop.IsVisible);
            Assert.True(CopyNodeItem(window).IsEnabled);
            Assert.Empty(copied);
        }
        finally
        {
            Copying.Observer = null;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_finished_copy_says_how_much_was_copied()
    {
        DetailWindow window = Open();
        string? copied = null;
        Copying.Observer = text => copied = text;
        try
        {
            window.TreeList.ContextMenu!.Open();
            window.TreeList.ContextMenu.Close();
            CopyNodeItem(window).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            UiPump.Until(() => window.Model.CommandStatus?.StartsWith("Copied ", StringComparison.Ordinal) == true);
            Assert.NotNull(copied);
            Assert.Null(window.Model.CommandError);

            window.Model.CopyDone(new string('a', 1_300_000));
            Assert.Equal("Copied 1.2 MiB", window.Model.CommandStatus);
            window.Model.CopyDone(new string('a', 12_800));
            Assert.Equal("Copied 12.5 KiB", window.Model.CommandStatus);
            window.Model.CopyDone(new string('a', 1023));
            Assert.Equal("Copied 1023 bytes", window.Model.CommandStatus);
            window.Model.CopyDone(new string('a', 1024));
            Assert.Equal("Copied 1 KiB", window.Model.CommandStatus);
        }
        finally
        {
            Copying.Observer = null;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_copy_that_finishes_during_a_node_copy_leaves_the_nodes_status()
    {
        DetailWindow window = Open();
        var blocked = new BlockedCopy();
        Task<string?> copy = window.Model.PrepareCopyAsync("the node", blocked.Write);
        UiPump.Until(() => blocked.Started.IsSet);

        window.Model.CopyDone("selection");
        Assert.Equal("Copying the node…", window.Model.CommandStatus);

        window.Model.Stop();
        UiPump.Until(() => copy.IsCompleted);
        window.Model.CopyDone("selection");
        Assert.Equal("Copied 9 bytes", window.Model.CommandStatus);
        window.Close();
    }

    [AvaloniaFact]
    public void Closing_the_window_cancels_a_copy_that_is_still_running()
    {
        DetailWindow window = Open();
        var blocked = new BlockedCopy();
        Task<string?> copy = window.Model.PrepareCopyAsync("the node", blocked.Write);
        UiPump.Until(() => blocked.Started.IsSet);

        window.Close();
        UiPump.Until(() => copy.IsCompleted);

        Assert.True(blocked.SawCancellation);
        Assert.Null(copy.Result);
    }

    [Fact]
    public void A_reload_of_a_decoded_file_keeps_the_analysis_and_stop_ends_it()
    {
        var ui = new TestDispatcher();
        var parser = new DecodeBlockingParser { BlockFrom = 3 };
        using var model = new DetailViewModel(parser.Analyzer(), ui.Post);
        using var folder = new TempFolder();
        string path = folder.Write("a.vblk", DecodeBlockingParser.File);
        Task load = model.LoadAsync(path);
        ui.Until(() => load.IsCompleted);
        Task decode = model.DecodeAudioAsync();
        ui.Until(() => decode.IsCompleted);
        Report before = model.Report!;

        Task reload = model.ReloadAsync();
        ui.Until(() => parser.Started == 3);

        Assert.True(model.IsDecoding);
        Assert.True(model.CanStop);
        Assert.True(model.IsProgressShown);
        Assert.Same(before, model.Report);
        model.Stop();
        ui.Until(() => reload.IsCompleted);

        Assert.Equal(1, parser.Canceled);
        Assert.False(model.IsDecoding);
        Assert.False(model.CanStop);
        Assert.Same(before, model.Report);
        Assert.Null(model.CommandError);
    }

    // The worker has ended and its result is posted, but the UI thread has not run it when Stop comes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stop_between_the_end_of_the_worker_and_its_callback_keeps_what_was_shown(bool decodeAudio)
    {
        bool hold = false;
        var ui = new TestDispatcher { Withhold = _ => hold };
        var parser = new DecodeBlockingParser { BlockFrom = 100 };
        using var model = new DetailViewModel(parser.Analyzer(), ui.Post);
        using var folder = new TempFolder();
        string path = folder.Write("a.vblk", DecodeBlockingParser.File);
        Task load = model.LoadAsync(path);
        ui.Until(() => load.IsCompleted);
        Task first = model.DecodeAudioAsync();
        ui.Until(() => first.IsCompleted);
        Report before = model.Report!;

        hold = true;
        Task again = decodeAudio ? model.DecodeAudioAsync() : model.ReloadAsync();
        ui.Until(() => parser.Started == 3);
        Thread.Sleep(500);
        Assert.True(model.IsDecoding);
        model.Stop();
        hold = false;
        ui.ReleaseWithheld();
        ui.Until(() => again.IsCompleted);

        Assert.Same(before, model.Report);
        Assert.False(model.IsDecoding);
        Assert.False(model.CanStop);
    }

    [Fact]
    public void Analyzing_a_decoded_file_as_another_format_can_be_stopped_too()
    {
        var ui = new TestDispatcher();
        var parser = new DecodeBlockingParser { BlockFrom = 3 };
        using var model = new DetailViewModel(parser.Analyzer(), ui.Post);
        using var folder = new TempFolder();
        string path = folder.Write("a.vblk", DecodeBlockingParser.File);
        Task load = model.LoadAsync(path);
        ui.Until(() => load.IsCompleted);
        Task decode = model.DecodeAudioAsync();
        ui.Until(() => decode.IsCompleted);
        Report before = model.Report!;

        Task again = model.AnalyzeAsAsync(null);
        ui.Until(() => parser.Started == 3);
        Assert.True(model.IsDecoding);
        model.Stop();
        ui.Until(() => again.IsCompleted);

        Assert.Equal(1, parser.Canceled);
        Assert.Same(before, model.Report);
        Assert.False(model.IsDecoding);
    }

    [AvaloniaFact]
    public void Closing_the_window_cancels_a_reload_that_is_still_running()
    {
        var parser = new DecodeBlockingParser { BlockFrom = 3 };
        using var folder = new TempFolder();
        string path = folder.Write("a.vblk", DecodeBlockingParser.File);
        var window = new DetailWindow(new DetailViewModel(parser.Analyzer())) { Width = 1100, Height = 760 };
        window.Show();
        Task load = window.Model.LoadAsync(path);
        UiPump.Until(() => load.IsCompleted);
        Task decode = window.Model.DecodeAudioAsync();
        UiPump.Until(() => decode.IsCompleted);
        Task reload = window.Model.ReloadAsync();
        UiPump.Until(() => parser.Started == 3);

        window.Close();
        UiPump.Until(() => parser.Canceled == 1);

        Assert.Equal(1, parser.Canceled);
    }
}
