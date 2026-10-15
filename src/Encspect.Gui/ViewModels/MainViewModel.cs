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
// Listing the same source again keeps the selected file selected once it is listed again; Refresh
// and Clear Summary Cache also mean the open detail windows read their files again.
public sealed class MainViewModel : ObservableObject, IDisposable
{
    // Rows are handed to the UI thread in batches, so a folder of thousands of files does not post
    // thousands of separate updates while it is listed.
    private const int BatchSize = 256;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

    // A sorted list sorts again as rows fill in, and once more when the run ends, so it does not
    // reshuffle on every batch: at most this often, and never sooner after a sort than four times
    // what that sort took, so a long list that takes long to sort leaves the UI thread free.
    private static readonly TimeSpan SortInterval = TimeSpan.FromMilliseconds(250);
    private const int SortCostFactor = 4;

    private readonly Stopwatch _sinceSort = Stopwatch.StartNew();
    private TimeSpan _sortCost;

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
    private string? _reselectPath;
    private string? _error;

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
                OpenSource(FileSource.ForFolder(value.Path));
            }
        }
    }

    public FileRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            // A row chosen while the list fills in wins over the one it was to select again.
            if (value is not null)
            {
                _reselectPath = null;
            }

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
                Start(keepSelection: true, isRereading: false);
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

    // What went wrong that the window should show: a run that failed, or an operation of the
    // window the system refused. Cleared when the list is listed again.
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

    // Whether the current listing was asked for to read the files again (Refresh, Clear Summary
    // Cache), rather than to show the same files otherwise (Include Subfolders).
    public bool IsRereading { get; private set; }

    // The task of the current run, which ends once every row it listed is analyzed or the run is
    // canceled; its rows reach the list on the UI thread after that.
    public Task Completion => _run?.Task ?? Task.CompletedTask;

    // Opens a source chosen other than in the folder tree, such as a dropped folder, so the tree
    // no longer marks the folder it showed last, and choosing that folder again opens it.
    public void Open(FileSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (_selectedFolder is not null)
        {
            _selectedFolder = null;
            Raise(nameof(SelectedFolder));
        }

        OpenSource(source);
    }

    public void ShowError(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        Error = message;
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

        var cost = Stopwatch.StartNew();
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
            _sortCost = cost.Elapsed;
            _sinceSort.Restart();
        }
    }

    // Lists and analyzes the source again; with a cache, only the files that changed are analyzed.
    public void Refresh()
    {
        if (_source is not null)
        {
            Start(keepSelection: true, isRereading: true);
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

    private void OpenSource(FileSource source)
    {
        _source = source;
        Raise(nameof(Source));
        Raise(nameof(Title));
        Raise(nameof(HasSource));
        Raise(nameof(CanIncludeSubfolders));
        Start(keepSelection: false, isRereading: false);
    }

    private void Start(bool keepSelection, bool isRereading)
    {
        _run?.Cancellation.Cancel();
        IsRereading = isRereading;
        _reselectPath = keepSelection ? _selectedRow?.Path : null;
        SelectedRow = null;
        Rows.Clear();
        _total = 0;
        _analyzed = 0;
        _isListed = false;
        _wasCanceled = false;
        Error = null;
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

    // Every way the run ends, also an exception nothing expected, ends with the final post, so the
    // window never stays "running"; an unexpected exception stops the run and shows.
    private async Task ExecuteAsync(Run run, IEnumerable<SourceEntry> entries)
    {
        CancellationToken token = run.Cancellation.Token;
        Exception? failure = null;

        // Two stages: rows are looked up in the cache, which is quick, and the rows it does not
        // hold go on to be analyzed, so cached rows fill in while the first analyses run.
        var lookups = Channel.CreateUnbounded<FileRow>(new UnboundedChannelOptions { SingleWriter = true });
        var analyses = Channel.CreateUnbounded<(FileRow Row, FileFingerprint? Fingerprint)>();
        Task looking = LookAllAsync(run, lookups.Reader, analyses.Writer, token);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = token };
        Task workers = Parallel.ForEachAsync(analyses.Reader.ReadAllAsync(token), options, (item, rowToken) =>
        {
            Analyze(run, item.Row, item.Fingerprint, rowToken);
            return ValueTask.CompletedTask;
        });

        try
        {
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
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure = ex;
                run.Cancellation.Cancel();
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
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }
        finally
        {
            if (failure is not null)
            {
                run.Cancellation.Cancel();
                ErrorLog.Write("the folder's analysis stopped", failure);
            }

            _post(() => Finish(run, failure));
        }
    }

    private async Task LookAllAsync(Run run, ChannelReader<FileRow> rows, ChannelWriter<(FileRow, FileFingerprint?)> analyses, CancellationToken token)
    {
        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism * 2, CancellationToken = token };
            await Parallel.ForEachAsync(rows.ReadAllAsync(token), options, (row, _) =>
            {
                Look(run, row, analyses);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        }
        finally
        {
            analyses.TryComplete();
        }
    }

    private void Finish(Run run, Exception? failure)
    {
        if (_run != run)
        {
            return;
        }

        _wasCanceled = run.Cancellation.IsCancellationRequested;
        _reselectPath = null;

        // Whatever a worker queued whose drain was not posted (or has not run yet) is applied before
        // the rows still waiting are marked canceled.
        DrainResults(run);
        foreach (FileRow row in Rows)
        {
            row.MarkCanceled();
        }

        if (failure is not null)
        {
            Error = "The analysis stopped: " + failure.Message;
        }

        IsRunning = false;
        SortAgain();
        RaiseProgress();
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
            row.IsListed = true;
            if (row.State == FileState.Analyzed)
            {
                _analyzed++;
            }

            if (_reselectPath is not null && row.Path == _reselectPath)
            {
                SelectedRow = row;
            }
        }

        _total += batch.Count;
        _isListed |= isLast;
        if (isLast)
        {
            _reselectPath = null;
        }

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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The host turns a parser's failure into a finding, so this is the host's or the
                // system's (out of memory): the row says so and the run goes on.
                ErrorLog.Write($"analyzing {row.Path}", ex);
                report = FileReports.Unreadable(row.Path, "The analysis failed: " + ex.Message);
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
    // to them, so a folder of cached rows fills in with a few updates rather than one per file. A
    // cached row can be done before its batch of rows is listed; it counts as analyzed once it is,
    // so the count never runs ahead of the files listed.
    private void Apply(Run run, FileRow row, FileSummary summary, bool isCached)
    {
        run.Results.Enqueue((row, summary, isCached));
        if (Interlocked.Exchange(ref run.IsApplyPosted, 1) == 1)
        {
            return;
        }

        _post(() =>
        {
            // A full fence, not a plain store: on x86 a store can wait in the store buffer while the
            // dequeue below finds the queue empty, and a worker that enqueued meanwhile would still
            // see the flag set, post nothing and strand its result.
            Interlocked.Exchange(ref run.IsApplyPosted, 0);
            if (_run != run)
            {
                return;
            }

            bool isApplied = DrainResults(run);
            if (isApplied && _sinceSort.Elapsed >= SortInterval && _sinceSort.Elapsed >= SortCostFactor * _sortCost)
            {
                SortAgain();
            }

            RaiseProgress();
        });
    }

    // Applies the results that have come in, on the UI thread.
    private bool DrainResults(Run run)
    {
        bool isApplied = false;
        while (run.Results.TryDequeue(out (FileRow Row, FileSummary Summary, bool IsCached) result))
        {
            result.Row.Apply(result.Summary, result.IsCached);
            if (result.Row.IsListed)
            {
                _analyzed++;
            }

            isApplied = true;
        }

        return isApplied;
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
