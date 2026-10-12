using System.Collections.ObjectModel;

namespace Encspect.Gui.ViewModels;

public enum FolderKind
{
    Folder,
    Home,
    Music,
    Desktop,
    Volume,
    Placeholder,
}

// A folder in the tree. Its subfolders are listed when it is first expanded, on a background
// thread, so a slow or unreachable volume does not block the window; until then it holds one
// placeholder, which gives it an expander. A folder that cannot be listed keeps no children and
// shows why in its tooltip.
public sealed class FolderItem : ObservableObject
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    private readonly Action<Action> _post;
    private bool _isExpanded;
    private bool _isLoaded;
    private string? _error;
    private Task? _loading;

    public FolderItem(string name, string path, FolderKind kind, Action<Action> post)
    {
        Name = name;
        Path = path;
        Kind = kind;
        _post = post;
        if (kind != FolderKind.Placeholder)
        {
            Children.Add(new FolderItem("Loading…", path, FolderKind.Placeholder, post));
        }
    }

    public string Name { get; }

    public string Path { get; }

    public FolderKind Kind { get; }

    public bool IsPlaceholder => Kind == FolderKind.Placeholder;

    public ObservableCollection<FolderItem> Children { get; } = [];

    public bool IsLoaded => _isLoaded;

    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && value)
            {
                _ = LoadChildrenAsync();
            }
        }
    }

    // Lists the subfolders once; later calls return the same task.
    public Task LoadChildrenAsync()
    {
        if (IsPlaceholder)
        {
            return Task.CompletedTask;
        }

        if (_loading is not null)
        {
            return _loading;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loading = done.Task;
        Task.Run(() =>
        {
            List<(string Name, string Path)>? folders = null;
            string? error = null;
            try
            {
                folders = ListSubfolders(Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                error = ex.Message;
            }

            _post(() =>
            {
                Children.Clear();
                if (folders is not null)
                {
                    foreach ((string name, string path) in folders)
                    {
                        Children.Add(new FolderItem(name, path, FolderKind.Folder, _post));
                    }
                }

                Error = error;
                _isLoaded = true;
                Raise(nameof(IsLoaded));
                done.SetResult();
            });
        });

        return _loading;
    }

    // Hidden folders are left out of the tree, as file managers do; a folder's files, hidden or
    // not, are all listed once it is selected.
    internal static List<(string Name, string Path)> ListSubfolders(string path)
    {
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, IgnoreInaccessible = false };
        var folders = new List<(string Name, string Path)>();
        foreach (DirectoryInfo info in new DirectoryInfo(path).EnumerateDirectories("*", options))
        {
            if (!info.Name.StartsWith('.'))
            {
                folders.Add((info.Name, System.IO.Path.Join(path, info.Name)));
            }
        }

        folders.Sort((a, b) => NameComparer.Compare(a.Name, b.Name) is var order and not 0 ? order : string.CompareOrdinal(a.Name, b.Name));
        return folders;
    }
}
