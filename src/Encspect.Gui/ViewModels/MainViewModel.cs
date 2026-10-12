using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Channels;
using Avalonia.Collections;
using Encspect.Host;

namespace Encspect.Gui.ViewModels;

// The main window: the folder tree, and the file list with its background analysis. Files are
// listed lazily and analyzed with bounded parallelism; each row keeps only its summary. Opening
// another source, or changing whether subfolders are included, cancels the run before it.
public sealed class MainViewModel : ObservableObject, IDisposable
{
    // Rows are handed to the UI thread in batches, so a folder of thousands of files does not post
    // thousands of separate updates while it is listed.
    private const int BatchSize = 256;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

    private readonly Analyzer _analyzer;
    private readonly Action<Action> _post;
    private Run? _run;
    private FileSource? _source;
    private FolderItem? _selectedFolder;
    private FileRow? _selectedRow;
    private bool _includeSubfolders;
    private bool _isRunning;
    private int _total;
    private int _analyzed;
    private bool _isListed;
    private bool _wasCanceled;

    public MainViewModel(Analyzer analyzer, Action<Action>? post = null, int? parallelism = null, IReadOnlyList<FolderItem>? roots = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);

        _analyzer = analyzer;
        _post = post ?? UiThread.Post;
        Parallelism = parallelism ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        RowsView = new DataGridCollectionView(Rows);
        Folders = roots ?? FolderRoots.Create(_post);
    }

    public event EventHandler? SelectedRowChanged;

    // How many files are analyzed at once.
    public int Parallelism { get; }

    public IReadOnlyList<FolderItem> Folders { get; }

    public ObservableCollection<FileRow> Rows { get; } = [];

    // The rows in the order the list shows them, sorted by the column the user picked.
    public DataGridCollectionView RowsView { get; }

    public FileSource? Source => _source;

    public string Title => _source?.Title ?? string.Empty;

    public bool HasSource => _source is not null;

    public bool CanIncludeSubfolders => _source is not null;

    public FolderItem? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (Set(ref _selectedFolder, value) && value is { IsPlaceholder: false })
            {
                Open(FileSource.ForFolder(value.Path));
            }
        }
    }

    public FileRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value))
            {
                SelectedRowChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (Set(ref _includeSubfolders, value) && _source is not null)
            {
                Start();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => Set(ref _isRunning, value);
    }

    public int Total => _total;

    public int Analyzed => _analyzed;

    // The progress bar runs over the files listed so far; until the listing ends, more may come.
    public bool IsListed => _isListed;

    public string Status
    {
        get
        {
            if (_source is null)
            {
                return string.Empty;
            }

            string files = Format.Count(_total, "file", "files");
            if (_isRunning)
            {
                return _isListed ? $"Analyzing {_analyzed:N0} of {files}…" : $"Analyzing {_analyzed:N0} of {files} found so far…";
            }

            return _wasCanceled ? $"Stopped after {_analyzed:N0} of {files}" : files;
        }
    }

    // The task of the current run, which ends once every row it listed is analyzed or the run is
    // canceled; its rows reach the list on the UI thread after that.
    public Task Completion => _run?.Task ?? Task.CompletedTask;

    public void Open(FileSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        Raise(nameof(Source));
        Raise(nameof(Title));
        Raise(nameof(HasSource));
        Raise(nameof(CanIncludeSubfolders));
        Start();
    }

    public void Cancel()
    {
        if (_run is { } run && _isRunning)
        {
            run.Cancellation.Cancel();
        }
    }

    // The row delta places after (or before) the given one in the list's order, or null at either
    // end.
    public FileRow? Neighbor(FileRow row, int delta)
    {
        ArgumentNullException.ThrowIfNull(row);

        int index = RowsView.IndexOf(row);
        if (index < 0)
        {
            return null;
        }

        int target = index + delta;
        return target >= 0 && target < RowsView.Count ? (FileRow)RowsView.GetItemAt(target)! : null;
    }

    public void Dispose()
    {
        _run?.Cancellation.Cancel();
        _run = null;
    }

    private void Start()
    {
        _run?.Cancellation.Cancel();
        SelectedRow = null;
        Rows.Clear();
        _total = 0;
        _analyzed = 0;
        _isListed = false;
        _wasCanceled = false;
        if (_source is null)
        {
            _run = null;
            RaiseProgress();
            return;
        }

        var run = new Run();
        _run = run;
        IsRunning = true;
        RaiseProgress();
        IEnumerable<SourceEntry> entries = _source.Enumerate(_includeSubfolders);
        run.Task = Task.Run(() => ExecuteAsync(run, entries));
    }

    private async Task ExecuteAsync(Run run, IEnumerable<SourceEntry> entries)
    {
        CancellationToken token = run.Cancellation.Token;
        var queue = Channel.CreateUnbounded<FileRow>(new UnboundedChannelOptions { SingleWriter = true });
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = token };
        Task workers = Parallel.ForEachAsync(queue.Reader.ReadAllAsync(token), options, (row, rowToken) =>
        {
            Analyze(run, row, rowToken);
            return ValueTask.CompletedTask;
        });

        try
        {
            var batch = new List<FileRow>();
            var clock = Stopwatch.StartNew();
            foreach (SourceEntry entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var row = new FileRow(entry.Path, entry.DisplayName, entry.FolderError);
                batch.Add(row);
                queue.Writer.TryWrite(row);
                if (batch.Count >= BatchSize || clock.Elapsed >= BatchInterval)
                {
                    PostRows(run, batch, isLast: false);
                    batch = [];
                    clock.Restart();
                }
            }

            PostRows(run, batch, isLast: true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            queue.Writer.TryComplete();
        }

        try
        {
            await workers.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _post(() =>
        {
            if (_run != run)
            {
                return;
            }

            _wasCanceled = token.IsCancellationRequested;
            foreach (FileRow row in Rows)
            {
                row.MarkCanceled();
            }

            IsRunning = false;
            RaiseProgress();
        });
    }

    private void PostRows(Run run, List<FileRow> batch, bool isLast) => _post(() =>
    {
        if (_run != run)
        {
            return;
        }

        foreach (FileRow row in batch)
        {
            Rows.Add(row);
        }

        _total += batch.Count;
        _isListed |= isLast;
        RaiseProgress();
    });

    private void Analyze(Run run, FileRow row, CancellationToken token)
    {
        Core.Report report;
        if (row.FolderError is { } folderError)
        {
            report = FileReports.UnreadableFolder(row.Path, folderError);
        }
        else
        {
            try
            {
                report = _analyzer.AnalyzeFile(row.Path, new AnalysisOptions { CancellationToken = token });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report = FileReports.Unreadable(row.Path, ex.Message);
            }
        }

        FileSummary summary = FileSummary.From(report);
        _post(() =>
        {
            if (_run != run)
            {
                return;
            }

            row.Apply(summary);
            _analyzed++;
            RaiseProgress();
        });
    }

    private void RaiseProgress()
    {
        Raise(nameof(Total));
        Raise(nameof(Analyzed));
        Raise(nameof(IsListed));
        Raise(nameof(Status));
    }

    private sealed class Run
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;
    }
}
