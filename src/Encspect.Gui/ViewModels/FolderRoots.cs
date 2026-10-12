using System.Collections.ObjectModel;

namespace Encspect.Gui.ViewModels;

// What a probe of a volume found: whether it can be read, and the name it shows when its label
// says more than its provisional name; null keeps that.
internal readonly record struct VolumeState(bool IsReady, string? Name);

// The top of the folder tree: the home, music and desktop folders where they exist, then the
// volumes. Windows lists its drives; macOS the root and what /Volumes holds; Linux the root and the
// mounts under /media, /run/media and /mnt, leaving out the kernel's and the system's own.
//
// Asking a volume whether it is ready, or for its label, can stall on a disconnected network drive
// or a hung mount, so the tree never waits for it: the volumes show at once under a provisional
// name (the drive letter, the folder's name), and each is probed on a thread of its own, which
// fills in the label or marks it unavailable. On macOS even listing /Volumes looks at each mount,
// so its volumes are added as that listing finds them.
public static class FolderRoots
{
    private const string NotReady = "The volume is not ready.";

    public static ObservableCollection<FolderItem> Create(Action<Action> post) => Create(post, ProbeVolume);

    internal static ObservableCollection<FolderItem> Create(Action<Action> post, Func<string, VolumeState> probe)
    {
        var roots = new ObservableCollection<FolderItem>();
        AddKnown(roots, "Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FolderKind.Home, post);
        AddKnown(roots, "Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), FolderKind.Music, post);
        AddKnown(roots, "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), FolderKind.Desktop, post);

        if (OperatingSystem.IsWindows())
        {
            foreach (DriveInfo drive in Safe(DriveInfo.GetDrives, []))
            {
                AddVolume(roots, drive.Name.TrimEnd('\\'), drive.Name, post, probe);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var startup = new FolderItem("Macintosh HD", "/", FolderKind.Volume, post);
            roots.Add(startup);
            _ = Task.Run(() => ListMacVolumes(roots, startup, post, probe));
        }
        else
        {
            roots.Add(new FolderItem("File System", "/", FolderKind.Volume, post));
            string[] mountRoots = ["/media/", "/run/media/", "/mnt/"];
            foreach (DriveInfo drive in Safe(DriveInfo.GetDrives, []))
            {
                string path = drive.Name;
                if (mountRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal)))
                {
                    AddVolume(roots, Path.GetFileName(path.TrimEnd('/')), path, post, probe);
                }
            }
        }

        return roots;
    }

    // Probes the volume off the UI thread and shows what it found.
    internal static Task Probe(FolderItem volume, Func<string, VolumeState> probe, Action<Action> post) => Task.Run(() =>
    {
        VolumeState state;
        try
        {
            state = probe(volume.Path);
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"probing {volume.Path}", ex);
            state = new VolumeState(false, null);
        }

        post(() =>
        {
            if (!state.IsReady)
            {
                volume.MarkUnavailable(NotReady);
            }
            else if (state.Name is { } name)
            {
                volume.Rename(name);
            }
        });
    });

    private static void AddKnown(ObservableCollection<FolderItem> roots, string name, string path, FolderKind kind, Action<Action> post)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && roots.All(r => r.Path != path))
        {
            roots.Add(new FolderItem(name, path, kind, post));
        }
    }

    private static void AddVolume(ObservableCollection<FolderItem> roots, string name, string path, Action<Action> post, Func<string, VolumeState> probe)
    {
        var volume = new FolderItem(name, path, FolderKind.Volume, post);
        roots.Add(volume);
        _ = Probe(volume, probe, post);
    }

    // Every volume is a folder of /Volumes, and the startup disk a link back to / named as the
    // user named it, which is the name the root takes.
    private static void ListMacVolumes(ObservableCollection<FolderItem> roots, FolderItem startup, Action<Action> post, Func<string, VolumeState> probe)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        foreach (string path in Safe(() => Directory.GetFileSystemEntries("/Volumes", "*", options), []))
        {
            string name = Path.GetFileName(path);
            string? target = Safe(() => new FileInfo(path).LinkTarget, null);
            if (target is "/")
            {
                post(() => startup.Rename(name));
            }
            else if (target is null && !name.StartsWith('.'))
            {
                post(() => AddVolume(roots, name, path, post, probe));
            }
        }
    }

    private static VolumeState ProbeVolume(string path)
    {
        var drive = new DriveInfo(path);
        if (!Safe(() => drive.IsReady, false))
        {
            return new VolumeState(false, null);
        }

        // Unix reports a volume's mount point as its label.
        string label = OperatingSystem.IsWindows() ? Safe(() => drive.VolumeLabel, string.Empty) : string.Empty;
        return new VolumeState(true, label.Length > 0 ? $"{label} ({drive.Name.TrimEnd('\\')})" : null);
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }
}
