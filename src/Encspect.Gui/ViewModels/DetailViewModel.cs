using System.Collections.ObjectModel;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Gui.ViewModels;

// One file in detail: its structure tree, hex view, findings and streams. Every file it shows is
// analyzed again, never taken from the file list. Loading another file cancels the analysis
// before it.
public sealed class DetailViewModel : ObservableObject, IDisposable
{
    private readonly Analyzer _analyzer;
    private readonly Action<Action> _post;
    private CancellationTokenSource? _loading;
    private int _generation;
    private string? _path;
    private bool _isLoading;
    private double _progress;
    private Report? _report;
    private StructureTree? _tree;
    private TreeRow? _selectedNode;
    private HexDocument? _document;
    private HexHighlight? _highlight;
    private BitStrip? _bitStrip;
    private IReadOnlyList<FindingItem> _allFindings = [];
    private IReadOnlyList<StreamItem> _streams = [];
    private FindingItem? _selectedFinding;
    private bool _showErrors = true;
    private bool _showWarnings = true;
    private bool _showInfos = true;
    private bool _canGoPrevious;
    private bool _canGoNext;

    public DetailViewModel(Analyzer analyzer, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);

        _analyzer = analyzer;
        _post = post ?? UiThread.Post;
    }

    public string? Path => _path;

    public string Title => _path is null ? string.Empty : System.IO.Path.GetFileName(_path);

    public string? Folder => System.IO.Path.GetDirectoryName(_path);

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    // How far the analysis has read, from 0 to 1.
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public Report? Report
    {
        get => _report;
        private set
        {
            if (Set(ref _report, value))
            {
                Raise(nameof(HasReport));
            }
        }
    }

    public bool HasReport => _report is not null;

    public StructureTree? Tree
    {
        get => _tree;
        private set => Set(ref _tree, value);
    }

    public TreeRow? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (Set(ref _selectedNode, value))
            {
                Highlight = value is null ? null : HexHighlight.For(value.Node);
                BitStrip = BitStrip.For(_highlight, _document);
                Raise(nameof(SelectionText));
            }
        }
    }

    // What the selected node is and where: "Bitrate index: 1010 · 0x1A2+0, 4 bits".
    public string SelectionText => _selectedNode is { } row
        ? (row.Value is { } value ? $"{row.Name}: {value}" : row.Name) + " · " + Format.Location(row.Node)
        : string.Empty;

    public HexDocument? Document
    {
        get => _document;
        private set => Set(ref _document, value);
    }

    public HexHighlight? Highlight
    {
        get => _highlight;
        private set => Set(ref _highlight, value);
    }

    public BitStrip? BitStrip
    {
        get => _bitStrip;
        private set => Set(ref _bitStrip, value);
    }

    public IReadOnlyList<StreamItem> Streams
    {
        get => _streams;
        private set
        {
            if (Set(ref _streams, value))
            {
                Raise(nameof(StreamsText));
            }
        }
    }

    public string StreamsText => _isLoading ? "Analyzing…" : _streams.Count > 0 ? string.Join(Environment.NewLine, _streams.Select(s => s.Text)) : (_report?.Root.Value?.ToString() ?? string.Empty);

    public ObservableCollection<FindingItem> Findings { get; } = [];

    public int ErrorCount => _allFindings.Count(f => f.Severity == Severity.Error);

    public int WarningCount => _allFindings.Count(f => f.Severity == Severity.Warning);

    public int InfoCount => _allFindings.Count(f => f.Severity == Severity.Info);

    // "No findings." shows once a report without findings is loaded.
    public bool HasNoFindings => _report is not null && _allFindings.Count == 0;

    public string ErrorFilterText => Format.Count(ErrorCount, "error", "errors");

    public string WarningFilterText => Format.Count(WarningCount, "warning", "warnings");

    public string InfoFilterText => Format.Count(InfoCount, "info", "infos");

    public bool ShowErrors
    {
        get => _showErrors;
        set
        {
            if (Set(ref _showErrors, value))
            {
                FilterFindings();
            }
        }
    }

    public bool ShowWarnings
    {
        get => _showWarnings;
        set
        {
            if (Set(ref _showWarnings, value))
            {
                FilterFindings();
            }
        }
    }

    public bool ShowInfos
    {
        get => _showInfos;
        set
        {
            if (Set(ref _showInfos, value))
            {
                FilterFindings();
            }
        }
    }

    // Selecting a finding selects its node in the tree, expanding the rows on the way.
    public FindingItem? SelectedFinding
    {
        get => _selectedFinding;
        set
        {
            if (Set(ref _selectedFinding, value) && value?.Path is { } path && _tree?.Reveal(path) is { } row)
            {
                SelectedNode = row;
            }
        }
    }

    public bool CanGoPrevious
    {
        get => _canGoPrevious;
        set => Set(ref _canGoPrevious, value);
    }

    public bool CanGoNext
    {
        get => _canGoNext;
        set => Set(ref _canGoNext, value);
    }

    // Analyzes the file on a background thread. The task ends once the result is shown, or at
    // once when another load replaced this one.
    public Task LoadAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        _loading?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loading = cancellation;
        int generation = ++_generation;

        _path = path;
        Raise(nameof(Path));
        Raise(nameof(Title));
        Raise(nameof(Folder));
        Clear();
        IsLoading = true;
        Progress = 0;
        Raise(nameof(StreamsText));

        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long length = 0;
        var bytesRead = new ImmediateProgress(read => _post(() =>
        {
            if (generation == _generation && length > 0)
            {
                Progress = Math.Clamp((double)read / length, 0, 1);
            }
        }));
        var options = new AnalysisOptions { CancellationToken = cancellation.Token, BytesRead = bytesRead };

        Task.Run(() =>
        {
            Report report;
            HexDocument? document = null;
            try
            {
                length = new FileInfo(path).Length;
                report = _analyzer.AnalyzeFile(path, options);
                if (!FileReports.IsNotAnalyzed(report))
                {
                    document = HexDocument.Open(path);
                }
            }
            catch (OperationCanceledException)
            {
                shown.TrySetResult();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report = FileReports.Unreadable(path, ex.Message);
            }

            _post(() =>
            {
                if (generation != _generation)
                {
                    document?.Dispose();
                }
                else
                {
                    Show(report, document);
                }

                shown.TrySetResult();
            });
        });

        return shown.Task;
    }

    public Task ExportAsync(Stream output, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(output);
        Report report = _report ?? throw new InvalidOperationException("No report is loaded.");
        return Task.Run(() => ReportExport.Write(report, output, format));
    }

    public void Dispose()
    {
        _loading?.Cancel();
        _generation++;
        Clear();
    }

    private void Show(Report report, HexDocument? document)
    {
        Document = document;
        Report = report;
        Tree = new StructureTree(report.Root);
        _allFindings = report.Findings.Select(f => new FindingItem(f)).ToList();
        RaiseCounts();
        FilterFindings();
        IsLoading = false;
        Progress = 1;
        Streams = report.Streams.Select(s => new StreamItem(s)).ToList();
        Raise(nameof(StreamsText));
        SelectedNode = Tree.Root;
    }

    private void Clear()
    {
        SelectedFinding = null;
        SelectedNode = null;
        Tree = null;
        Report = null;
        _document?.Dispose();
        Document = null;
        _allFindings = [];
        Findings.Clear();
        Streams = [];
        RaiseCounts();
        IsLoading = false;
    }

    private void RaiseCounts()
    {
        foreach (string name in (string[])[nameof(ErrorCount), nameof(WarningCount), nameof(InfoCount), nameof(ErrorFilterText), nameof(WarningFilterText), nameof(InfoFilterText), nameof(HasNoFindings)])
        {
            Raise(name);
        }
    }

    private void FilterFindings()
    {
        Findings.Clear();
        foreach (FindingItem finding in _allFindings)
        {
            bool shown = finding.Severity switch
            {
                Severity.Error => _showErrors,
                Severity.Warning => _showWarnings,
                _ => _showInfos,
            };
            if (shown)
            {
                Findings.Add(finding);
            }
        }
    }

    // Progress<T> posts to the context it was made on; the analysis reports from its own thread
    // and the view model posts itself.
    private sealed class ImmediateProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
