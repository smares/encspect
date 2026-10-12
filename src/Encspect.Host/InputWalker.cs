namespace Encspect.Host;

// A file to analyze, or a folder that could not be listed and why.
public readonly record struct Input(string Path, string? FolderError = null);

// Expands folders into the files below them, in the ordinal order of their paths. Each folder is
// listed and sorted on its own, so output starts before the whole tree is read. Hidden files and
// folders are included. A symbolic link to a folder is followed only when it leads out of the
// tree being walked, neither into it nor to a folder above it, and to no folder visited before,
// so every folder is visited once and link loops end. The CLI walks every folder it is given, the
// GUI a folder whose subfolders are included.
public static class InputWalker
{
    // Linux's limit on links followed while resolving one path.
    private const int MaxLinks = 40;

    private static readonly char[] Separators = [.. new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }.Distinct()];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static IEnumerable<Input> Expand(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (!Directory.Exists(path))
            {
                yield return new Input(path);
                continue;
            }

            foreach (Input input in Walk(path))
            {
                yield return input;
            }
        }
    }

    private static IEnumerable<Input> Walk(string root)
    {
        var visited = new HashSet<string>(PathComparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<Entry>();
        string? realRoot = null;
        string? rootError = Try(() => realRoot = RealPath(Environment.CurrentDirectory, root));
        pending.Push(new Entry(root, string.Empty, IsFolder: true, IsLink: false, realRoot, rootError));

        while (pending.TryPop(out Entry entry))
        {
            if (!entry.IsFolder)
            {
                yield return new Input(entry.Path);
                continue;
            }

            if (entry.Error is not null)
            {
                yield return new Input(entry.Path, entry.Error);
                continue;
            }

            string real = entry.RealPath!;
            if (entry.IsLink && (IsWithin(real, realRoot!) || IsWithin(realRoot!, real)))
            {
                continue;
            }

            if (!visited.Add(real))
            {
                continue;
            }

            List<Entry> children = [];
            if (Try(() => children = List(entry.Path, real)) is { } error)
            {
                yield return new Input(entry.Path, error);
                continue;
            }

            for (int i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }
    }

    // The folder's entries, sorted so that a depth-first walk gives the ordinal order of full paths:
    // a folder sorts as its name followed by a separator.
    private static List<Entry> List(string folder, string realFolder)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        var entries = new List<Entry>();
        foreach (FileSystemInfo info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
        {
            // A link is told by its target, not by the ReparsePoint attribute: an entry whose name
            // is not valid UTF-8 cannot be looked up again and reports every attribute.
            string path = Path.Join(folder, info.Name);
            if (info is not DirectoryInfo)
            {
                entries.Add(new Entry(path, info.Name, IsFolder: false, IsLink: false, null, null));
            }
            else if (info.LinkTarget is null)
            {
                entries.Add(new Entry(path, info.Name, IsFolder: true, IsLink: false, Path.Join(realFolder, info.Name), null));
            }
            else
            {
                string? real = null;
                string? error = Try(() => real = RealPath(realFolder, info.Name));
                entries.Add(new Entry(path, info.Name, IsFolder: true, IsLink: true, real, error));
            }
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.SortKey, b.SortKey));
        return entries;
    }

    // The path with every symbolic link resolved, as realpath(3) gives it. The path is absolute, or
    // relative to folder, which holds no links. ".." after a link leads to the parent of the
    // link's target, not of the link.
    private static string RealPath(string folder, string path)
    {
        var parts = new Stack<string>();
        string current = Push(parts, folder, path);
        int links = 0;
        while (parts.TryPop(out string? part))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            string next = Path.Join(current, part);
            if (new DirectoryInfo(next).LinkTarget is not { } target)
            {
                current = next;
                continue;
            }

            if (++links > MaxLinks)
            {
                throw new IOException($"Too many levels of symbolic links: {next}");
            }

            current = Push(parts, current, target);
        }

        return current;
    }

    // Queues the components of path, the first on top, and returns the folder they start from.
    private static string Push(Stack<string> parts, string folder, string path)
    {
        if (Path.IsPathRooted(path))
        {
            string full = Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path, folder);
            folder = Path.GetPathRoot(full)!;
            path = full[folder.Length..];
        }

        string[] split = path.Split(Separators);
        for (int i = split.Length - 1; i >= 0; i--)
        {
            parts.Push(split[i]);
        }

        return folder;
    }

    private static bool IsWithin(string path, string folder)
    {
        if (path.Length == folder.Length)
        {
            return path.Equals(folder, PathComparison);
        }

        string prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    // The message of a file system error the action throws, or null.
    private static string? Try(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private readonly record struct Entry(string Path, string Name, bool IsFolder, bool IsLink, string? RealPath, string? Error)
    {
        public string SortKey => IsFolder ? Name + System.IO.Path.DirectorySeparatorChar : Name;
    }
}
