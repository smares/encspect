using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// One graph a stream offers, as a choice above the chart.
public sealed class GraphChoice(Graph graph) : ObservableObject
{
    private bool _isSelected;

    public Graph Graph { get; } = graph;

    public string Title => Graph.Title;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }
}

// A stream that has graphs, as a choice when a file has several: "Stream 2: 48 kHz · 0:00.5".
// The window's header lists the streams in full.
public sealed class GraphStream(int number, StreamInfo stream, IReadOnlyList<GraphChoice> graphs)
{
    public StreamInfo Stream { get; } = stream;

    public IReadOnlyList<GraphChoice> Graphs { get; } = graphs;

    public string Text { get; } = $"Stream {number}: " + string.Join(" · ", Parts(stream));

    private static IEnumerable<string> Parts(StreamInfo stream)
    {
        if (stream.SampleRate > 0)
        {
            yield return Format.SampleRate(stream.SampleRate);
        }

        yield return Format.Duration(stream.Duration);
    }
}

// The graphs of the detail window, drawn from the streams' series: each series as a line, and the
// distribution of each per-frame bitrate series. The chosen graph stays chosen as the window moves
// on to the next file, where that file has one of the same title.
public sealed class GraphsViewModel : ObservableObject
{
    private IReadOnlyList<GraphStream> _streams = [];
    private GraphStream? _selectedStream;
    private GraphChoice? _selected;
    private string? _preferredTitle;
    private string? _decoderNote;
    private int? _markedIndex;
    private long? _markedFrame;

    // Asks the window to select the frame at an offset in the structure tree and hex view.
    public event EventHandler<long>? FrameRequested;

    public IReadOnlyList<GraphStream> Streams
    {
        get => _streams;
        private set
        {
            if (Set(ref _streams, value))
            {
                Raise(nameof(HasStreamChoice));
                Raise(nameof(HasGraphs));
            }
        }
    }

    public bool HasStreamChoice => _streams.Count > 1;

    public bool HasGraphs => _streams.Count > 0;

    public GraphStream? SelectedStream
    {
        get => _selectedStream;
        set
        {
            if (Set(ref _selectedStream, value))
            {
                Raise(nameof(Choices));
                Choose(value?.Graphs.FirstOrDefault(g => g.Title == _preferredTitle) ?? value?.Graphs.FirstOrDefault());
            }
        }
    }

    public IReadOnlyList<GraphChoice> Choices => _selectedStream?.Graphs ?? [];

    public GraphChoice? Selected
    {
        get => _selected;
        set
        {
            _preferredTitle = value?.Title ?? _preferredTitle;
            Choose(value);
        }
    }

    public Graph? Graph => _selected?.Graph;

    // The point of the frame selected in the tree, marked on the chart.
    public int? MarkedIndex
    {
        get => _markedIndex;
        private set => Set(ref _markedIndex, value);
    }

    // One quiet line where the waveform and spectrogram would be: no format has a decoder yet.
    public string? DecoderNote
    {
        get => _decoderNote;
        private set => Set(ref _decoderNote, value);
    }

    public static IReadOnlyList<Graph> For(StreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ReadOnlyMemory<double> frameOffsets = stream.Series.FirstOrDefault(s => s.Axis == SeriesAxis.FrameOffset)?.X ?? default;
        var graphs = new List<Graph>();
        foreach (Series series in stream.Series)
        {
            if (series.Values.Length == 0)
            {
                continue;
            }

            graphs.Add(new LineGraph(series, frameOffsets, stream.Duration));

            // The bitrate is the series in kbps, whatever a format names it.
            if (series.Axis == SeriesAxis.FrameOffset && series.Unit == "kbps")
            {
                graphs.Add(new DistributionGraph(series));
            }
        }

        return graphs;
    }

    public void Load(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var streams = new List<GraphStream>();
        for (int i = 0; i < report.Streams.Count; i++)
        {
            IReadOnlyList<Graph> graphs = For(report.Streams[i]);
            if (graphs.Count > 0)
            {
                streams.Add(new GraphStream(i + 1, report.Streams[i], [.. graphs.Select(g => new GraphChoice(g))]));
            }
        }

        _markedFrame = null;
        Streams = streams;
        SelectedStream = streams.FirstOrDefault();
        DecoderNote = report.Streams.Count > 0
            ? $"Waveform and spectrogram need a decoder, which {report.Root.Value?.ToString() ?? report.Streams[0].Codec} does not have yet."
            : null;
    }

    public void Clear()
    {
        _markedFrame = null;
        Streams = [];
        SelectedStream = null;
        DecoderNote = null;
    }

    // A click on a point: selects its frame, if it leads to one.
    public void Activate(int index)
    {
        if (Graph?.FrameAt(index, _markedFrame) is { } offset)
        {
            FrameRequested?.Invoke(this, offset);
        }
    }

    // The frame selected in the tree, or null when what is selected is no frame.
    public void MarkFrame(long? offset)
    {
        _markedFrame = offset;
        MarkedIndex = offset is { } at ? Graph?.IndexOfFrame(at) : null;
    }

    private void Choose(GraphChoice? choice)
    {
        if (_selected == choice)
        {
            return;
        }

        _selected?.IsSelected = false;
        _selected = choice;
        _selected?.IsSelected = true;
        Raise(nameof(Selected));
        Raise(nameof(Graph));
        MarkedIndex = _markedFrame is { } at ? Graph?.IndexOfFrame(at) : null;
    }
}
