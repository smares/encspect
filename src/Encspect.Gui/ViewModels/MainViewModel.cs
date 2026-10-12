using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Channels;
using Avalonia.Collections;
using Encspect.Gui.Cache;
using Encspect.Host;

namespace Encspect.Gui.ViewModels;

// The main window: the folder tree, and the file list with its background analysis. Files are
// listed lazily and analyzed with bounded parallelism; each row keeps only its summary. With a
// summary cache, each file is looked up there first, which takes a stat and a read of 128 KiB, and
// only files the cache does not hold as they are now are analyzed, their summaries then cached.
// Opening another source, or changing whether subfolders are included, cancels the run before it.
public sealed class MainViewModel : ObservableObject, IDisposable
{
    // Rows are handed to the UI thread in batches, so a folder of thousands of files does not post
    // thousands of separate updates while it is listed.
    private const int BatchSize = 256;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

    // A sorted list sorts again as rows fill in at most this often, and once more when the run ends,
    // so it does not reshuffle on every batch.
    private static readonly TimeSpan SortInterval = TimeSpan.FromMilliseconds(250);

    private readonly Stopwatch _lastSort = Stopwatch.StartNew();

    private readonly Analyzer _analyzer;
    private readonly Action<Action> _post;
    private readonly SummaryCache? _cache;
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
    private bool _isSorting;

    public MainViewModel(Analyzer analyzer, Action<Action>? post = null, int? parallelism = null, IReadOnlyList<FolderItem>? roots = null, SummaryCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);

        _analyzer = analyzer;
        _post = post ?? UiThread.Post;
        _cache = cache;
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

    public SummaryCache? Cache => _cache;

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
            if (Set(ref _selectedRow, value) && !_isSorting)
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

    // Sorts the list again once rows got the figures it is sorted by, keeping the selected row
    // selected: the list does not move rows whose values change by itself. A list sorted by name
    // needs nothing.
    public void SortAgain()
    {
        if (RowsView.SortDescriptions.All(d => d.PropertyPath == nameof(FileRow.Name)))
        {
            return;
        }

        _lastSort.Restart();
        FileRow? selected = _selectedRow;
        _isSorting = true;
        try
        {
            RowsView.Refresh();
            SelectedRow = selected;
        }
        finally
        {
            _isSorting = false;
        }
    }

    // Lists and analyzes the source again; with a cache, only the files that changed are analyzed.
    public void Refresh()
    {
        if (_source is not null)
        {
            Start();
        }
    }

    // Empties the cache and analyzes the source again from scratch.
    public void ClearCache()
    {
        _run?.Cancellation.Cancel();
        _cache?.Clear();
        Refresh();
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

        // Two stages: rows are looked up in the cache, which is quick, and the rows it does not
        // hold go on to be analyzed, so cached rows fill in while the first analyses run.
        var lookups = Channel.CreateUnbounded<FileRow>(new UnboundedChannelOptions { SingleWriter = true });
        var analyses = Channel.CreateUnbounded<(FileRow Row, FileFingerprint? Fingerprint)>();
        var lookupOptions = new ParallelOptions { MaxDegreeOfParallelism = Parallelism * 2, CancellationToken = token };
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = token };
        Task looking = Parallel.ForEachAsync(lookups.Reader.ReadAllAsync(token), lookupOptions, (row, _) =>
        {
            Look(run, row, analyses.Writer);
            return ValueTask.CompletedTask;
        }).ContinueWith(_ => analyses.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        Task workers = Parallel.ForEachAsync(analyses.Reader.ReadAllAsync(token), options, (item, rowToken) =>
        {
            Analyze(run, item.Row, item.Fingerprint, rowToken);
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
                lookups.Writer.TryWrite(row);
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
            lookups.Writer.TryComplete();
        }

        try
        {
            await looking.ConfigureAwait(false);
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
            SortAgain();
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

    // A row the cache holds as the file is now fills in from it; any other row goes on to be
    // analyzed, with the fingerprint its summary is to be cached under.
    private void Look(Run run, FileRow row, ChannelWriter<(FileRow, FileFingerprint?)> analyses)
    {
        FileFingerprint? fingerprint = null;
        if (_cache is not null && row.FolderError is null)
        {
            fingerprint = FileFingerprint.Read(row.Path);
            if (fingerprint is not null && _cache.TryGet(row.Path, fingerprint) is { } summary)
            {
                Apply(run, row, summary, isCached: true);
                return;
            }
        }

        analyses.TryWrite((row, fingerprint));
    }

    private void Analyze(Run run, FileRow row, FileFingerprint? fingerprint, CancellationToken token)
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

        // A file that could not be read is tried again next time.
        if (fingerprint is not null && summary.Problem is null)
        {
            _cache?.Put(row.Path, fingerprint, summary);
        }

        Apply(run, row, summary, isCached: false);
    }

    // Results reach the UI thread in batches: whatever has come in by the time the UI thread gets
    // to them, so a folder of cached rows fills in with a few updates rather than one per file.
    private void Apply(Run run, FileRow row, FileSummary summary, bool isCached)
    {
        run.Results.Enqueue((row, summary, isCached));
        if (Interlocked.Exchange(ref run.IsApplyPosted, 1) == 1)
        {
            return;
        }

        _post(() =>
        {
            Volatile.Write(ref run.IsApplyPosted, 0);
            if (_run != run)
            {
                return;
            }

            bool isApplied = false;
            while (run.Results.TryDequeue(out (FileRow Row, FileSummary Summary, bool IsCached) result))
            {
                result.Row.Apply(result.Summary, result.IsCached);
                _analyzed++;
                isApplied = true;
            }

            if (isApplied && _lastSort.Elapsed >= SortInterval)
            {
                SortAgain();
            }

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
        public int IsApplyPosted;

        public CancellationTokenSource Cancellation { get; } = new();

        public System.Collections.Concurrent.ConcurrentQueue<(FileRow Row, FileSummary Summary, bool IsCached)> Results { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;
    }
}
