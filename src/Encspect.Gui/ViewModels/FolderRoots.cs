namespace Encspect.Gui.ViewModels;

// The top of the folder tree: the home, music and desktop folders where they exist, then the
// volumes. Windows lists its drives; macOS the root and what /Volumes holds; Linux the root and the
// mounts under /media, /run/media and /mnt, leaving out the kernel's and the system's own.
public static class FolderRoots
{
    public static IReadOnlyList<FolderItem> Create(Action<Action> post)
    {
        var roots = new List<FolderItem>();
        AddKnown(roots, "Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FolderKind.Home, post);
        AddKnown(roots, "Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), FolderKind.Music, post);
        AddKnown(roots, "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), FolderKind.Desktop, post);
        foreach ((string name, string path) in Volumes())
        {
            roots.Add(new FolderItem(name, path, FolderKind.Volume, post));
        }

        return roots;
    }

    private static void AddKnown(List<FolderItem> roots, string name, string path, FolderKind kind, Action<Action> post)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && roots.All(r => r.Path != path))
        {
            roots.Add(new FolderItem(name, path, kind, post));
        }
    }

    private static List<(string Name, string Path)> Volumes()
    {
        var volumes = new List<(string Name, string Path)>();
        if (OperatingSystem.IsWindows())
        {
            foreach (DriveInfo drive in Safe(DriveInfo.GetDrives, []))
            {
                if (IsReady(drive))
                {
                    string label = Safe(() => drive.VolumeLabel, string.Empty);
                    string letter = drive.Name.TrimEnd('\\');
                    volumes.Add((string.IsNullOrEmpty(label) ? letter : $"{label} ({letter})", drive.Name));
                }
            }

            return volumes;
        }

        volumes.Add((OperatingSystem.IsMacOS() ? "Macintosh HD" : "File System", "/"));
        if (OperatingSystem.IsMacOS())
        {
            // Every volume is a folder of /Volumes; the startup disk is a link back to /.
            foreach (DirectoryInfo volume in Safe(() => new DirectoryInfo("/Volumes").GetDirectories(), []))
            {
                if (volume.LinkTarget is null)
                {
                    volumes.Add((volume.Name, volume.FullName));
                }
            }

            return volumes;
        }

        string[] mountRoots = ["/media/", "/run/media/", "/mnt/"];
        foreach (DriveInfo drive in Safe(DriveInfo.GetDrives, []))
        {
            string path = drive.RootDirectory.FullName;
            if (mountRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal)) && IsReady(drive))
            {
                volumes.Add((System.IO.Path.GetFileName(path), path));
            }
        }

        return volumes;
    }

    private static bool IsReady(DriveInfo drive) => Safe(() => drive.IsReady, false);

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
