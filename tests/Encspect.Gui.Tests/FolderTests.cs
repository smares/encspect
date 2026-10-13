using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Tests;

public class FolderTests
{
    [Fact]
    public void A_folder_lists_its_subfolders_only_when_expanded()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp["b/inner"]);
        Directory.CreateDirectory(temp["A"]);
        Directory.CreateDirectory(temp[".hidden"]);
        temp.Write("file.mp3", [1, 2, 3]);
        var ui = new TestDispatcher();
        var folder = new FolderItem("temp", temp.Path, FolderKind.Folder, ui.Post);

        Assert.False(folder.IsLoaded);
        Assert.True(Assert.Single(folder.Children).IsPlaceholder);

        folder.IsExpanded = true;
        ui.Until(() => folder.IsLoaded);

        Assert.Equal(["A", "b"], folder.Children.Select(c => c.Name));
        Assert.Null(folder.Error);
        FolderItem b = folder.Children[1];
        Assert.False(b.IsLoaded);
        Assert.True(Assert.Single(b.Children).IsPlaceholder);

        Task load = b.LoadChildrenAsync();
        ui.Until(() => load.IsCompleted);
        Assert.Equal("inner", Assert.Single(b.Children).Name);
    }

    [Fact]
    public void A_folder_that_cannot_be_listed_shows_why_and_has_no_children()
    {
        using var temp = new TempFolder();
        var ui = new TestDispatcher();
        var folder = new FolderItem("gone", temp["gone"], FolderKind.Folder, ui.Post);

        folder.IsExpanded = true;
        ui.Until(() => folder.IsLoaded);

        Assert.True(folder.HasError);
        Assert.NotEmpty(folder.Error!);
        Assert.Empty(folder.Children);
    }

    [Fact]
    public void The_roots_start_with_the_home_folder_and_list_volumes_without_waiting_for_them()
    {
        using var gate = new ManualResetEventSlim();
        IReadOnlyList<FolderItem> roots = FolderRoots.Create(new TestDispatcher().Post, _ =>
        {
            gate.Wait();
            return new VolumeState(true, null);
        });

        Assert.Equal(FolderKind.Home, roots[0].Kind);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), roots[0].Path);
        Assert.Contains(roots, r => r.Kind == FolderKind.Volume);
        Assert.All(roots, r => Assert.False(r.IsLoaded));
        gate.Set();
    }

    [Fact]
    public void A_volume_shows_at_once_and_its_probe_fills_in_its_name_or_marks_it_unavailable()
    {
        var ui = new TestDispatcher();
        using var gate = new ManualResetEventSlim();
        var slow = new FolderItem("D:", "/slow", FolderKind.Volume, ui.Post);
        var gone = new FolderItem("E:", "/gone", FolderKind.Volume, ui.Post);
        var broken = new FolderItem("F:", "/broken", FolderKind.Volume, ui.Post);

        Task slowProbe = FolderRoots.Probe(slow, _ =>
        {
            gate.Wait();
            return new VolumeState(true, "Music (D:)");
        }, ui.Post);
        Task goneProbe = FolderRoots.Probe(gone, _ => new VolumeState(false, null), ui.Post);
        Task brokenProbe = FolderRoots.Probe(broken, _ => throw new InvalidOperationException("No such volume."), ui.Post);
        // A probe posts its result just before its task completes, so wait for the results, not the
        // tasks: the condition can hold after the last run of the queue, before the post runs.
        ui.Until(() => goneProbe.IsCompleted && brokenProbe.IsCompleted && gone.HasError && broken.HasError);

        Assert.False(slowProbe.IsCompleted);
        Assert.Equal("D:", slow.Name);
        Assert.True(gone.HasError);
        Assert.True(broken.HasError);
        Assert.Equal("E:", gone.Name);

        gate.Set();
        ui.Until(() => slow.Name == "Music (D:)");
        Assert.False(slow.HasError);
    }

    [Fact]
    public void A_folder_whose_listing_fails_unexpectedly_still_ends_loading()
    {
        var ui = new TestDispatcher();
        var folder = new FolderItem("bad", "bad\0name", FolderKind.Folder, ui.Post);

        Task load = folder.LoadChildrenAsync();
        ui.Until(() => load.IsCompleted);

        Assert.True(folder.IsLoaded);
        Assert.True(folder.HasError);
        Assert.Empty(folder.Children);
    }

    [Fact]
    public void A_folder_source_lists_its_files_or_with_subfolders_its_whole_tree()
    {
        using var temp = new TempFolder();
        temp.Write("b.mp3", [1]);
        temp.Write("a.mp3", [1]);
        temp.Write(".hidden.mp3", [1]);
        temp.Write("sub/c.mp3", [1]);
        FileSource source = FileSource.ForFolder(temp.Path);

        Assert.Equal([".hidden.mp3", "a.mp3", "b.mp3"], source.Enumerate(includeSubfolders: false).Select(e => e.DisplayName));
        Assert.Equal([".hidden.mp3", "a.mp3", "b.mp3", Path.Join("sub", "c.mp3")], source.Enumerate(includeSubfolders: true).Select(e => e.DisplayName));
    }

    [Fact]
    public void Dropped_items_list_files_as_given_and_folders_by_their_name()
    {
        using var temp = new TempFolder();
        string file = temp.Write("one.mp3", [1]);
        temp.Write("album/two.mp3", [1]);

        FileSource source = FileSource.ForItems([file, temp["album"]]);

        Assert.Null(source.Folder);
        Assert.Equal("2 items", source.Title);
        Assert.Equal(["one.mp3", Path.Join("album", "two.mp3")], source.Enumerate(includeSubfolders: false).Select(e => e.DisplayName));
        Assert.Equal(temp["album"], FileSource.ForItems([temp["album"]]).Folder);
    }

    [Fact]
    public void A_missing_item_is_listed_and_left_to_the_analysis_to_report()
    {
        using var temp = new TempFolder();

        SourceEntry entry = Assert.Single(FileSource.ForItems([temp["missing.mp3"]]).Enumerate(includeSubfolders: true));

        Assert.Equal(temp["missing.mp3"], entry.Path);
        Assert.Null(entry.FolderError);
    }
}
