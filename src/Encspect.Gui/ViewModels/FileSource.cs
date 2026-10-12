using Encspect.Host;

namespace Encspect.Gui.ViewModels;

// One file to list: its path, the name the list shows (relative to the folder it was found in),
// or a folder that could not be listed and why.
public readonly record struct SourceEntry(string Path, string DisplayName, string? FolderError = null);

// What the file list shows: a folder chosen in the tree or the folder dialog, or the files and
// folders dropped onto the window or chosen in the file dialog.
public sealed class FileSource
{
    private FileSource(string title, IReadOnlyList<string> paths, string? folder)
    {
        Title = title;
        Paths = paths;
        Folder = folder;
    }

    public string Title { get; }

    public IReadOnlyList<string> Paths { get; }

    // Null when the source is several items, even if one of them is a folder.
    public string? Folder { get; }

    public static FileSource ForFolder(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new FileSource(path, [path], path);
    }

    // A single folder is shown as that folder; anything else as a list of items.
    public static FileSource ForItems(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 1 && Directory.Exists(paths[0]))
        {
            return ForFolder(paths[0]);
        }

        string title = paths.Count == 1 ? paths[0] : Format.Count(paths.Count, "item", "items");
        return new FileSource(title, [.. paths], null);
    }

    // Files and folders in the order given; a folder lists its files in the ordinal order of their
    // names, or with its subfolders the files of its whole tree as the CLI walks it (InputWalker),
    // hidden ones included. Lazy, so the first rows show before a large tree is read.
    public IEnumerable<SourceEntry> Enumerate(bool includeSubfolders)
    {
        foreach (string path in Paths)
        {
            if (!Directory.Exists(path))
            {
                yield return new SourceEntry(path, System.IO.Path.GetFileName(path));
                continue;
            }

            if (includeSubfolders)
            {
                foreach (Input input in InputWalker.Expand([path]))
                {
                    yield return new SourceEntry(input.Path, RelativeName(path, input.Path), input.FolderError);
                }

                continue;
            }

            foreach (SourceEntry entry in ListFiles(path))
            {
                yield return entry;
            }
        }
    }

    private string RelativeName(string folder, string path)
    {
        string relative = System.IO.Path.GetRelativePath(folder, path);
        if (Folder is not null)
        {
            return relative == "." ? System.IO.Path.GetFileName(path) : relative;
        }

        // A dropped folder keeps its own name in front, so files of two dropped folders tell apart.
        return System.IO.Path.Join(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder)), relative == "." ? null : relative);
    }

    private List<SourceEntry> ListFiles(string folder)
    {
        var entries = new List<SourceEntry>();
        try
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
            foreach (FileSystemInfo info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                if (info is not DirectoryInfo)
                {
                    string path = System.IO.Path.Join(folder, info.Name);
                    entries.Add(new SourceEntry(path, RelativeName(folder, path)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [new SourceEntry(folder, System.IO.Path.GetFileName(folder), ex.Message)];
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return entries;
    }
}
