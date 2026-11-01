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
    private bool _isDecoding;
    // The audio of the file shown was decoded: a reload or another format keeps decoding it. A verification that is still running does not count.
    private bool _isDecodeRequested;
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
    private bool _isGraphsShown;
    private string? _commandError;
    private string? _folderError;
    private string? _forcedFormat;
    private bool _isTopicOpen;
    private Topic? _shownTopic;

    public DetailViewModel(Analyzer analyzer, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);

        _analyzer = analyzer;
        _post = post ?? UiThread.Post;
        Graphs.FrameRequested += (_, offset) => SelectFrameAt(offset, Graphs.FramesAfterRequested);
        Graphs.DecodeRequested += (_, _) => _ = DecodeAudioAsync();
    }

    public GraphsViewModel Graphs { get; } = new();

    // The pane below the tree and hex view shows the findings or the graphs.
    public bool IsGraphsShown
    {
        get => _isGraphsShown;
        set
        {
            if (Set(ref _isGraphsShown, value))
            {
                Raise(nameof(IsFindingsShown));
            }
        }
    }

    public bool IsFindingsShown => !_isGraphsShown;

    // "No graphs" shows once a report without anything to graph is loaded.
    public bool HasNoGraphs => _report is not null && !Graphs.HasGraphs;

    public string? Path => _path;

    public string Title => _path is null ? string.Empty : System.IO.Path.GetFileName(_path);

    public string? Folder => System.IO.Path.GetDirectoryName(_path);

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value))
            {
                Raise(nameof(IsProgressShown));
                RaiseCanDecode();
            }
        }
    }

    // Decoding the audio, to compare the checksums over it and to measure it, which the file shown stays on screen for.
    public bool IsDecoding
    {
        get => _isDecoding;
        private set
        {
            if (Set(ref _isDecoding, value))
            {
                Raise(nameof(IsProgressShown));
                RaiseCanDecode();
            }
        }
    }

    public bool IsProgressShown => _isLoading || _isDecoding;

    // A stream of the report has a decoder that did not run because decoding was not asked for (a PCM stream, which declares no
    // checksum, included: its decoding is for the measurements).
    public bool CanDecodeAudio => _report is not null && !_isLoading && !_isDecoding && _report.Streams.Any(s => s.Verification is { Decoding: DecodeStatus.NotRequested });

    // "Verified: yes (frame CRC-16 40 of 40, STREAMINFO MD5 1 of 1)", a line for each stream with a verification; null where none has.
    public string? VerificationText
    {
        get
        {
            string[] lines = _report is null ? [] : [.. _report.Streams.Where(s => s.Verification is { HasChecksums: true }).Select(s => "Verified: " + Encspect.Host.VerificationText.Describe(s.Verification!))];
            return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
        }
    }

    public bool HasVerificationText => VerificationText is not null;

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
                RaiseCanDecode();
                Raise(nameof(VerificationText));
                Raise(nameof(HasVerificationText));
            }
        }
    }

    public bool HasReport => _report is not null;

    // The names of the formats the file can be analyzed as (Analyzer.FormatNames).
    public IReadOnlyList<string> FormatNames => _analyzer.FormatNames;

    // The format the file is analyzed as on the user's request, or null where detection chooses.
    // Loading another file clears it; refreshing the file keeps it.
    public string? ForcedFormat => _forcedFormat;

    // The formats whose evidence is real besides the one analyzed (FormatDetection.Alternatives),
    // each of which the file can be analyzed as. Where the analysis is forced, they include the
    // format detection would have chosen.
    public IReadOnlyList<AlternativeItem> Alternatives => _report?.Detection is { } detection ? [.. detection.Alternatives.Select(a => new AlternativeItem(a))] : [];

    // What the header says about how the format was chosen, when it is not the plain case: other
    // formats that match (the choices follow it), or an analysis the user forced.
    public string? DetectionText => _report?.Detection switch
    {
        { IsForced: true, Detected: { } detected } detection => $"Analyzed as {detection.Format} as requested; detection would take {detected}.",
        { IsForced: true } detection => $"Analyzed as {detection.Format} as requested; detection finds no format.",
        { Alternatives.Count: > 0 } => "Also matches:",
        _ => null,
    };

    public bool HasDetectionText => DetectionText is not null;

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
                Graphs.MarkFrame(FrameOf(value)?.Node.Offset);
                Raise(nameof(SelectionText));
                Raise(nameof(SelectionDescription));
                Raise(nameof(SelectionTopic));
                Raise(nameof(LearnMoreText));
                if (_isTopicOpen && SelectionTopic is { } topic)
                {
                    ShownTopic = topic;
                }
            }
        }
    }

    // What the selected node is, as its parser describes it.
    public string? SelectionDescription => _selectedNode?.Node.Description;

    // The background article of the selected node, or of its nearest ancestor that has one.
    public Topic? SelectionTopic
    {
        get
        {
            for (TreeRow? row = _selectedNode; row is not null; row = row.Parent)
            {
                if (TopicLibrary.Find(row.Node.Topic) is { } topic)
                {
                    return topic;
                }
            }

            return null;
        }
    }

    public string? LearnMoreText => SelectionTopic is { } topic ? $"Learn More: {topic.Title}" : null;

    // The side pane with a background article, or with the list of topics while ShownTopic is
    // null. Closed by default; the window remembers whether it was open.
    public bool IsTopicOpen
    {
        get => _isTopicOpen;
        set => Set(ref _isTopicOpen, value);
    }

    public Topic? ShownTopic
    {
        get => _shownTopic;
        set
        {
            if (Set(ref _shownTopic, value))
            {
                Raise(nameof(IsTopicIndexShown));
            }
        }
    }

    public bool IsTopicIndexShown => _shownTopic is null;

    public IReadOnlyList<Topic> AllTopics => TopicLibrary.All;

    // Opens the pane on a topic by its ID, or on the list of topics for null or an unknown ID.
    public void ShowTopic(string? id)
    {
        ShownTopic = TopicLibrary.Find(id);
        IsTopicOpen = true;
    }

    // Closes the pane, or opens it on the selected node's topic, or the list where it has none. With a preferred topic (the
    // selected graph's) it opens on that, or moves an open pane to it, and closes only a pane that shows it already.
    public void ToggleTopics(Topic? preferred = null)
    {
        if (preferred is not null && _isTopicOpen && _shownTopic != preferred)
        {
            ShownTopic = preferred;
            return;
        }

        if (!_isTopicOpen)
        {
            ShownTopic = preferred ?? SelectionTopic;
        }

        IsTopicOpen = !_isTopicOpen;
    }

    // What the selected node is and where: "Bitrate index: 1010 · 0x1A2+0, 4 bits".
    public string SelectionText => _selectedNode is { } row
        ? (row.Value is { } value ? $"{row.Name}: {value}" : row.Name) + " · " + Format.Location(row.Node)
        : string.Empty;

    public HexDocument? Document
    {
        get => _document;
        private set
        {
            if (_document is not null)
            {
                _document.Changed -= OnDocumentChanged;
            }

            Set(ref _document, value);
            if (value is not null)
            {
                value.Changed += OnDocumentChanged;
            }

            Raise(nameof(HasFileChanged));
        }
    }

    // The file on disk is no longer the one analyzed, so the hex view shows none of its bytes.
    public bool HasFileChanged => _document?.HasChanged == true;

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

    // Why the last export or decoding failed, until the next one or the next file.
    public string? CommandError
    {
        get => _commandError;
        private set => Set(ref _commandError, value);
    }

    // Analyzes the file on a background thread; a folder that could not be listed shows why. The
    // task ends once the result is shown, or at once when another load replaced this one.
    public Task LoadAsync(string path, string? folderError = null) => Load(path, folderError, keep: null, forced: null, decode: false, keepShown: false);

    // Analyzes the file shown again, as it is on disk now, and selects the node at the place the
    // selected one was (its child indexes), or the root when the new tree has none there.
    public Task ReloadAsync()
    {
        if (_path is not { } path)
        {
            return Task.CompletedTask;
        }

        var keep = new List<int>();
        for (TreeRow? row = _selectedNode; row?.Parent is not null; row = row.Parent)
        {
            keep.Insert(0, row.ChildIndex);
        }

        return Load(path, _folderError, keep, _forcedFormat, _isDecodeRequested, keepShown: false);
    }

    // Analyzes the file shown again as the given format (the name of one of FormatNames), or by
    // detection for null, and selects the node at the place the selected one was. The choice stays
    // until another file is loaded.
    public Task AnalyzeAsAsync(string? format)
    {
        if (_path is not { } path)
        {
            return Task.CompletedTask;
        }

        var keep = new List<int>();
        for (TreeRow? row = _selectedNode; row?.Parent is not null; row = row.Parent)
        {
            keep.Insert(0, row.ChildIndex);
        }

        return Load(path, _folderError, keep, format, _isDecodeRequested, keepShown: false);
    }

    // Analyzes the file shown again with its audio decoded, which compares the checksums over the samples and measures them (Decode
    // Audio). The file stays on screen while it runs and is replaced when it ends; StopDecoding leaves it as it is. A reload keeps the request
    // until another file is loaded.
    public Task DecodeAudioAsync()
    {
        if (_path is not { } path || !CanDecodeAudio)
        {
            return Task.CompletedTask;
        }

        var keep = new List<int>();
        for (TreeRow? row = _selectedNode; row?.Parent is not null; row = row.Parent)
        {
            keep.Insert(0, row.ChildIndex);
        }

        return Load(path, _folderError, keep, _forcedFormat, decode: true, keepShown: true);
    }

    // Cancels a Decode Audio that is running, which leaves the analysis shown as it was.
    public void StopDecoding()
    {
        if (_isDecoding)
        {
            _loading?.Cancel();
        }
    }

    private Task Load(string path, string? folderError, IReadOnlyList<int>? keep, string? forced, bool decode, bool keepShown)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        _loading?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loading = cancellation;
        int generation = ++_generation;

        if (!keepShown && !decode)
        {
            _isDecodeRequested = false;
        }

        _path = path;
        _folderError = folderError;
        _forcedFormat = forced;
        Raise(nameof(ForcedFormat));
        Raise(nameof(Path));
        Raise(nameof(Title));
        Raise(nameof(Folder));
        if (keepShown)
        {
            CommandError = null;
            IsDecoding = true;
        }
        else
        {
            Clear();
            IsDecoding = false;
            IsLoading = true;
        }

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
        var options = new AnalysisOptions
        {
            CancellationToken = cancellation.Token,
            BytesRead = bytesRead,
            ForcedFormat = forced,
            Decoding = decode ? new DecodeOptions { Measure = new MeasureOptions() } : null,
        };

        Task.Run(() =>
        {
            Report? report = null;
            HexDocument? document = null;
            string? failure = null;
            try
            {
                report = folderError is not null ? FileReports.UnreadableFolder(path, folderError) : Analyze(path, options, out length, out document);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex.Message;
                report = keepShown ? null : FileReports.Unreadable(path, ex.Message);
            }
            catch (Exception ex)
            {
                ErrorLog.Write($"analyzing {path}", ex);
                failure = "The analysis failed: " + ex.Message;
                report = keepShown ? null : FileReports.Unreadable(path, failure);
            }
            finally
            {
                _post(() =>
                {
                    if (generation != _generation || report is null)
                    {
                        document?.Dispose();
                        if (keepShown && generation == _generation)
                        {
                            IsDecoding = false;
                            if (failure is not null)
                            {
                                CommandError = "Not decoded: " + failure;
                            }
                        }
                    }
                    else
                    {
                        Show(report, document, keep, showAudio: decode && keepShown);
                        _isDecodeRequested = decode;
                    }

                    shown.TrySetResult();
                });
            }
        });

        return shown.Task;
    }

    // Selects the frame that holds the offset, found through the tree's containment from the nodes
    // of the stream the graphs show, and returns its row; null when no frame holds it.
    // framesAfter: select the frame that many frames after it, among its siblings (a run of frames, which the graph names by its first).
    public TreeRow? SelectFrameAt(long offset, int framesAfter = 0)
    {
        if (_tree is not { } tree)
        {
            return null;
        }

        Node root = tree.Root.Node;
        IEnumerable<IReadOnlyList<int>> starts = Graphs.SelectedStream?.Stream.NodePaths.OfType<IReadOnlyList<int>>() ?? [];
        foreach (IReadOnlyList<int> start in starts.Append([]))
        {
            Node? node = NodeAt(root, start);
            if (node is null || node.Offset > offset || offset >= node.End || FrameLocator.PathFrom(node, offset) is not { } below)
            {
                continue;
            }

            if (framesAfter > 0)
            {
                below = FrameLocator.Advance(node, below, framesAfter);
            }

            if (tree.Reveal([.. start, .. below]) is { } row)
            {
                if (framesAfter > 0)
                {
                    Node landed = node;
                    foreach (int index in below)
                    {
                        landed = landed.Children[index];
                    }

                    Graphs.ClickLandedAt(landed.Offset);
                }

                SelectedNode = row;
                return row;
            }
        }

        return null;
    }

    public Task ExportAsync(Stream output, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(output);
        Report report = _report ?? throw new InvalidOperationException("No report is loaded.");
        return Task.Run(() => ReportExport.Write(report, output, format));
    }

    // Writes the report to a temporary file next to the target, which then replaces it, so a
    // failed export leaves no half-written file. A target that exists and is not a regular file,
    // such as a device, is written directly.
    public Task ExportAsync(string path, ReportFormat format)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Report report = _report ?? throw new InvalidOperationException("No report is loaded.");
        return Task.Run(() =>
        {
            string target = System.IO.Path.GetFullPath(path);
            if (!IsRegularOrMissing(target))
            {
                using var direct = new FileStream(target, FileMode.Open, FileAccess.Write);
                ReportExport.Write(report, direct, format);
                return;
            }

            string temporary = System.IO.Path.Join(System.IO.Path.GetDirectoryName(target), $".{System.IO.Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ReportExport.Write(report, output, format);
                }

                File.Move(temporary, target, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                }

                throw;
            }
        });
    }

    // Runs an export and shows why it failed. The report is never changed by an export, so any
    // failure leaves the state sound and is shown rather than thrown.
    public async Task TryExportAsync(Func<Task> export)
    {
        ArgumentNullException.ThrowIfNull(export);

        CommandError = null;
        try
        {
            await export();
        }
        catch (Exception ex)
        {
            if (ex is not (IOException or UnauthorizedAccessException))
            {
                ErrorLog.Write("exporting the report", ex);
            }

            CommandError = "Not exported: " + ex.Message;
        }
    }

    // Expands or collapses a row. Collapsing a row the selection lies below selects the row, as a
    // tree view does, rather than leaving nothing selected.
    public void Toggle(TreeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (_tree is not { } tree)
        {
            return;
        }

        if (row.IsExpanded && IsBelow(_selectedNode, row))
        {
            SelectedNode = row;
        }

        tree.Toggle(row);
    }

    public void Dispose()
    {
        _loading?.Cancel();
        _generation++;
        Clear();
    }

    private Report Analyze(string path, AnalysisOptions options, out long length, out HexDocument? document)
    {
        // A folder or a file that is gone has no length; the analysis then reports what it is.
        var info = new FileInfo(path);
        length = info.Exists ? info.Length : 0;
        DateTime lastWrite = info.Exists ? info.LastWriteTimeUtc : default;
        document = null;
        Report report = _analyzer.AnalyzeFile(path, options);
        if (!FileReports.IsNotAnalyzed(report))
        {
            document = HexDocument.Open(path, length, lastWrite);
        }

        return report;
    }

    private static bool IsRegularOrMissing(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            using FileStream? stream = FileType.Open(path, out string? notRegular);
            return notRegular is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsBelow(TreeRow? row, TreeRow ancestor)
    {
        for (TreeRow? current = row?.Parent; current is not null; current = current.Parent)
        {
            if (current == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    private void Show(Report report, HexDocument? document, IReadOnlyList<int>? keep, bool showAudio = false)
    {
        if (_document is not null && !ReferenceEquals(_document, document))
        {
            _document.Dispose();
        }

        SelectedFinding = null;
        IsDecoding = false;
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
        Graphs.Load(report, showAudio);
        Raise(nameof(HasNoGraphs));
        RaiseDetection();
        SelectedNode = keep is { Count: > 0 } && Tree.Reveal(keep) is { } kept ? kept : Tree.Root;
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
        Graphs.Clear();
        Raise(nameof(HasNoGraphs));
        RaiseDetection();
        RaiseCounts();
        CommandError = null;
        IsLoading = false;
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => Raise(nameof(HasFileChanged));

    // The frame a row is or lies in, which the graphs mark.
    private static TreeRow? FrameOf(TreeRow? row)
    {
        while (row is not null && row.Kind != NodeKind.Frame)
        {
            row = row.Parent;
        }

        return row;
    }

    private static Node? NodeAt(Node root, IReadOnlyList<int> path)
    {
        Node node = root;
        foreach (int index in path)
        {
            IReadOnlyList<Node> children = node.Children;
            if ((uint)index >= (uint)children.Count)
            {
                return null;
            }

            node = children[index];
        }

        return node;
    }

    // The command's enabling, here and on the Audio heading's button.
    private void RaiseCanDecode()
    {
        Raise(nameof(CanDecodeAudio));
        Graphs.CanDecode = CanDecodeAudio;
    }

    private void RaiseDetection()
    {
        Raise(nameof(Alternatives));
        Raise(nameof(DetectionText));
        Raise(nameof(HasDetectionText));
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
        Graphs.ShownSeverities = (_showErrors ? SeverityMask.Error : 0) | (_showWarnings ? SeverityMask.Warning : 0) | (_showInfos ? SeverityMask.Info : 0);
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
