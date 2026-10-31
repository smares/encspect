using Encspect.Core;
using Encspect.Host;

namespace Encspect.Gui.ViewModels;

// One form of an entry: its own graph, offered by the view switcher.
public sealed class GraphFormChoice(GraphForm kind, string title, string description, Graph graph) : ObservableObject
{
    private bool _isSelected;

    public GraphForm Kind { get; } = kind;

    public string Title { get; } = title;

    public string Description { get; } = description;

    public Graph Graph { get; } = graph;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }
}

// One quantity of a stream, as an entry of the list of graphs: a category series, or a numeric quantity such as the
// bitrate, with the forms it can be drawn in (the view switcher) and the graph of the one chosen.
public sealed class GraphChoice : ObservableObject
{
    private bool _isSelected;
    private GraphFormChoice _form;

    public GraphChoice(string title, string heading, string? description, IReadOnlyList<GraphFormChoice> forms)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentOutOfRangeException.ThrowIfZero(forms.Count);

        Title = title;
        Heading = heading;
        Description = description;
        Forms = forms;
        _form = forms[0];
        _form.IsSelected = true;
    }

    public string Title { get; }

    // The heading it is listed under: the concept it shows ("Stereo Coding"), or "Other".
    public string Heading { get; }

    public string? Description { get; }

    // The tooltip of its entry in the list: the name, then what it shows.
    public string ToolTipText => Description is null ? Title : $"{Title}\n{Description}";

    public IReadOnlyList<GraphFormChoice> Forms { get; }

    public bool HasForms => Forms.Count > 1;

    public GraphFormChoice Form => _form;

    // The background article of the graph drawn now, or null where it has none.
    public Topic? Topic => TopicLibrary.Find(Graph.Topic);

    public string? LearnMoreText => Topic is { } topic ? $"Learn More: {topic.Title}" : null;

    public Graph Graph => _form.Graph;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    // Draws the entry in the form of that kind, or keeps the one it has where it has none; returns whether it changed. An entry
    // that has no smoothed form takes the form it smooths, Over Time, so that the choice carries from a long series to a short
    // one or to a quantity.
    internal bool SelectForm(GraphForm kind)
    {
        GraphFormChoice? choice = Forms.FirstOrDefault(f => f.Kind == kind)
            ?? (kind == GraphForm.Smoothed ? Forms.FirstOrDefault(f => f.Kind == GraphForm.OverTime) : null);
        if (choice is null || choice == _form)
        {
            return false;
        }

        _form.IsSelected = false;
        _form = choice;
        _form.IsSelected = true;
        Raise(nameof(Form));
        Raise(nameof(Graph));
        Raise(nameof(Topic));
        Raise(nameof(LearnMoreText));
        return true;
    }
}

// The entries of one heading ("Stereo Coding"), one under the other. The heading "Audio" may also have a caption, one line that says
// why its panels are missing or incomplete, and the command to decode.
public sealed class GraphChoiceGroup(string heading, IReadOnlyList<GraphChoice> choices, AudioCaption? caption = null, GraphsViewModel? owner = null)
{
    public string Heading { get; } = heading;

    public IReadOnlyList<GraphChoice> Choices { get; } = choices;

    public string? Caption { get; } = caption?.Text;

    public bool HasCaption => Caption is not null;

    // Whether the Decode Audio command is offered under the caption.
    public bool OffersDecode { get; } = caption?.OffersDecode == true;

    // The view model whose CanDecode enables the command and whose RequestDecode runs it.
    public GraphsViewModel? Owner { get; } = owner;
}

// A stream that has graphs, as a choice when a file has several: "Stream 2: 48 kHz · 0:00.5".
// The window's header lists the streams in full.
public sealed class GraphStream(int number, StreamInfo stream, IReadOnlyList<GraphChoice> graphs)
{
    public StreamInfo Stream { get; } = stream;

    public IReadOnlyList<GraphChoice> Graphs { get; } = graphs;

    // The line under the heading "Audio", or null where the stream is not audio or its panels are complete.
    public AudioCaption? Caption { get; } = AudioPanels.CaptionFor(stream);

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

// The graphs of the detail window, drawn from the streams' series: a list of entries under the headings of their
// concepts, one for each quantity, each in the forms it has. The entry and the form chosen stay chosen as the window moves
// on to the next file, where that file has an entry of the same title or a form of the same kind.
public sealed class GraphsViewModel : ObservableObject
{
    private IReadOnlyList<GraphStream> _streams = [];
    private GraphStream? _selectedStream;
    private GraphChoice? _selected;
    private string? _preferredTitle;
    private GraphForm? _preferredForm;
    private bool _canDecode;
    private int? _markedIndex;
    private long? _markedFrame;
    private (Graph Graph, int Index, long Offset)? _click;
    private SeverityMask _shownSeverities = SeverityMask.All;

    // Asks the window to select the frame at an offset in the structure tree and hex view.
    public event EventHandler<long>? FrameRequested;

    // Asks the window to decode the audio, from the command under the caption of the heading "Audio".
    public event EventHandler? DecodeRequested;

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
                Raise(nameof(ChoiceGroups));
                RaiseChart();
                Choose(value?.Graphs.FirstOrDefault(g => g.Title == _preferredTitle) ?? value?.Graphs.FirstOrDefault());
            }
        }
    }

    public IReadOnlyList<GraphChoice> Choices => _selectedStream?.Graphs ?? [];

    // The entries under the headings of their concepts; a heading without an entry for the stream is not listed, except "Audio" when it
    // has a caption to show.
    public IReadOnlyList<GraphChoiceGroup> ChoiceGroups
    {
        get
        {
            var groups = new List<GraphChoiceGroup>();
            AudioCaption? caption = _selectedStream?.Caption;
            foreach (IGrouping<string, GraphChoice> group in Choices.GroupBy(c => c.Heading))
            {
                groups.Add(new GraphChoiceGroup(group.Key, [.. group], group.Key == GraphHeadings.Audio ? caption : null, this));
            }

            if (caption is not null && groups.All(g => g.Heading != GraphHeadings.Audio))
            {
                groups.Add(new GraphChoiceGroup(GraphHeadings.Audio, [], caption, this));
            }

            return groups;
        }
    }

    // Whether the window can decode the audio now (a report is shown and nothing is loading or decoding); set by the window's model.
    public bool CanDecode
    {
        get => _canDecode;
        set => Set(ref _canDecode, value);
    }

    // The line in place of the chart where the stream has no panel to draw, which is the caption of its audio.
    public string? EmptyNote => _selected is null ? _selectedStream?.Caption?.Text : null;

    public bool HasEmptyNote => EmptyNote is not null;

    // The measured figures as lines, while the entry "Figures" is chosen; null for every other entry.
    public IReadOnlyList<MeasurementLine>? Figures => (_selected?.Graph as FiguresGraph)?.Lines;

    public bool IsChartShown => _selected is not null && _selected.Graph is not FiguresGraph;

    public bool IsFiguresShown => _selected?.Graph is FiguresGraph;

    // Asks for the audio to be decoded.
    public void RequestDecode() => DecodeRequested?.Invoke(this, EventArgs.Empty);

    public GraphChoice? Selected
    {
        get => _selected;
        set
        {
            _preferredTitle = value?.Title ?? _preferredTitle;
            Choose(value);
        }
    }

    // The forms of the chosen entry, for the view switcher: none where it has one.
    public IReadOnlyList<GraphFormChoice> Forms => _selected is { HasForms: true } entry ? entry.Forms : [];

    public bool HasForms => _selected?.HasForms == true;

    // The form the chosen entry is drawn in.
    public GraphForm? Form => _selected?.Form.Kind;

    public Graph? Graph => _selected?.Graph;

    // The background article of the chosen graph, or null where it has none (a numeric series, the Coding Summary).
    public Topic? Topic => _selected?.Topic;

    public string? LearnMoreText => _selected?.LearnMoreText;

    // The severities whose findings the Frame Map rings and marks; the Findings tab's filters.
    public SeverityMask ShownSeverities
    {
        get => _shownSeverities;
        set => Set(ref _shownSeverities, value);
    }

    // The findings that lie in a frame, each with the offset of the first frame node along its path: the squares the Frame Map
    // rings. A finding on the stream, a tag, a gap or the root lies in no frame and is left out.
    public static IReadOnlyList<MapFinding> MapFindings(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var result = new List<MapFinding>();
        foreach (Finding finding in report.Findings)
        {
            if (finding.Path is not { } path)
            {
                continue;
            }

            Node node = report.Root;
            foreach (int index in path)
            {
                IReadOnlyList<Node> children = node.Children;
                if ((uint)index >= (uint)children.Count)
                {
                    break;
                }

                node = children[index];
                if (node.Kind == NodeKind.Frame)
                {
                    result.Add(new MapFinding(node.Offset, finding.Severity, finding.Message, finding.Count));
                    break;
                }
            }
        }

        return result;
    }

    // The point of the frame selected in the tree, marked on the chart.
    public int? MarkedIndex
    {
        get => _markedIndex;
        private set => Set(ref _markedIndex, value);
    }

    // The entries of a stream, from its series alone, in the order of the headings. A category series is one entry with
    // its forms, and so is each quantity: the series that carry the same Quantity are its forms (per frame, per second of
    // audio). Series that name none are paired by what they are, never by name: the one per-frame and the one per-second
    // series in kbps are the bitrate, and the same two in bytes the bit reservoir, until their formats set a Quantity.
    public static IReadOnlyList<GraphChoice> For(StreamInfo stream, IReadOnlyList<MapFinding>? findings = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ReadOnlyMemory<double> frameOffsets = stream.Series.FirstOrDefault(s => s.Axis == SeriesAxis.FrameOffset)?.X ?? default;
        var entries = new List<GraphChoice>();
        CategorySeries[] categories = [.. stream.CategorySeries.Where(s => s.Count > 0)];
        if (categories.Length > 0)
        {
            Graph summary = new CodingSummaryGraph(categories);
            entries.Add(new GraphChoice(CodingSummaryGraph.TitleText, GraphHeadings.Summary, summary.Description, [new GraphFormChoice(GraphForm.Summary, "Summary", summary.Description!, summary)]));
        }

        foreach (CategorySeries series in categories)
        {
            List<GraphFormChoice> forms = [.. CategoryGraph.FormsOf(series).Select(f => new GraphFormChoice(f, f switch
            {
                GraphForm.OverTime => "Over Time",
                GraphForm.Smoothed => "Smoothed",
                GraphForm.Frames => "Frames",
                _ => "Summary",
            }, f switch
            {
                GraphForm.OverTime => "The share of each category in every second of the stream, as a stacked area with the largest category at the bottom",
                GraphForm.Smoothed => "The same area with each second averaged with the two seconds on either side",
                GraphForm.Frames => "The category of every frame, one cell each",
                _ => "The count and share of each category over the whole stream",
            }, new CategoryGraph(series, f) { Duration = stream.Duration }))];
            if (series.Count > 0)
            {
                forms.Add(MapForm(new FrameMapGraph(series, frameOffsets, findings)));
            }

            entries.Add(new GraphChoice(series.Name, GraphHeadings.Of(series.Concept), series.Description, forms));
        }

        foreach (List<Series> quantity in Quantities(stream.Series.Where(s => s.Values.Length > 0)))
        {
            entries.Add(Entry(quantity, frameOffsets, stream.Duration, findings));
        }

        entries.AddRange(AudioPanels.EntriesFor(stream));
        return GraphHeadings.Order(entries, e => e.Heading);
    }

    private static List<List<Series>> Quantities(IEnumerable<Series> all)
    {
        var groups = new List<List<Series>>();
        var legacy = new List<Series>();
        foreach (Series series in all)
        {
            if (series.Quantity is { } quantity)
            {
                List<Series>? group = groups.FirstOrDefault(g => g[0].Quantity == quantity && !g.Any(s => s.Axis == series.Axis));
                if (group is null)
                {
                    groups.Add(group = []);
                }

                group.Add(series);
            }
            else
            {
                groups.Add([series]);
                legacy.Add(series);
            }
        }

        // Without a Quantity, a stream's one per-frame and one per-second series in the same unit (kbps, bytes) are one quantity.
        foreach (string unit in new[] { "kbps", "bytes" })
        {
            Series[] frames = [.. legacy.Where(s => s.Unit == unit && s.Axis == SeriesAxis.FrameOffset)];
            Series[] seconds = [.. legacy.Where(s => s.Unit == unit && s.Axis == SeriesAxis.Seconds)];
            if (frames.Length == 1 && seconds.Length == 1)
            {
                groups.RemoveAll(g => g.Count == 1 && g[0] == seconds[0]);
                groups.Single(g => g.Count == 1 && g[0] == frames[0]).Add(seconds[0]);
            }
        }

        return groups;
    }

    private static GraphFormChoice MapForm(FrameMapGraph graph) =>
        new(GraphForm.Map, "Frame Map", graph.RunLength > 1 ? $"Every run of {graph.RunLength} frames as a square, in rows like text" : "Every frame as a square, in rows like text", graph);

    private static GraphChoice Entry(List<Series> quantity, ReadOnlyMemory<double> frameOffsets, double duration, IReadOnlyList<MapFinding>? findings)
    {
        Series? perFrame = quantity.FirstOrDefault(s => s.Axis == SeriesAxis.FrameOffset);
        Series? perSecond = quantity.FirstOrDefault(s => s.Axis == SeriesAxis.Seconds);
        Series first = perFrame ?? perSecond!;
        var forms = new List<GraphFormChoice>();
        if (perFrame is not null)
        {
            forms.Add(new GraphFormChoice(GraphForm.Frames, "Per Frame", perFrame.FramesPerPoint > 1 ? $"One point for every run of {perFrame.FramesPerPoint} frames" : "One point for every frame", new LineGraph(perFrame, frameOffsets, duration)));
        }

        if (perSecond is not null)
        {
            forms.Add(new GraphFormChoice(GraphForm.OverTime, "Over Time", "One point for every second of audio", new LineGraph(perSecond, frameOffsets, duration)));
            if (perSecond is { Concept: GraphConcept.BitDistribution, Unit: "%" })
            {
                forms.Add(new GraphFormChoice(GraphForm.Split, "First and Second", "The share of each of the two parts in every second of audio", new ShareSplitGraph(perSecond, frameOffsets, duration)));
            }
        }

        // The bitrate is the series in kbps, whatever a format names it.
        if (perFrame is { Unit: "kbps" })
        {
            forms.Add(new GraphFormChoice(GraphForm.Summary, "Distribution", perFrame.FramesPerPoint > 1 ? $"How many runs of {perFrame.FramesPerPoint} frames have each value" : "How many frames have each value", new DistributionGraph(perFrame)));
        }

        if (perFrame is not null)
        {
            forms.Add(MapForm(new FrameMapGraph(perFrame, frameOffsets, findings)));
        }

        return new GraphChoice(first.Quantity ?? first.Name, GraphHeadings.Of(first), first.Description, forms);
    }

    // Lists the streams that have graphs or an audio caption. With showAudio, the first audio panel of the first stream that has one
    // is chosen (the spectrogram, which comes after the decode command that asked for it), instead of the entry chosen before.
    public void Load(Report report, bool showAudio = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        var streams = new List<GraphStream>();
        IReadOnlyList<MapFinding> findings = MapFindings(report);
        for (int i = 0; i < report.Streams.Count; i++)
        {
            IReadOnlyList<GraphChoice> entries = For(report.Streams[i], findings);
            var stream = new GraphStream(i + 1, report.Streams[i], entries);
            if (entries.Count > 0 || stream.Caption is not null)
            {
                streams.Add(stream);
            }
        }

        _markedFrame = null;
        _click = null;
        Streams = streams;
        SelectedStream = streams.FirstOrDefault();
        if (showAudio)
        {
            foreach (GraphStream stream in streams)
            {
                GraphChoice? panel = stream.Graphs.FirstOrDefault(g => g.Title == AudioPanels.SpectrogramTitle) ?? stream.Graphs.FirstOrDefault(g => g.Heading == GraphHeadings.Audio);
                if (panel is not null)
                {
                    SelectedStream = stream;
                    Selected = panel;
                    break;
                }
            }
        }
    }

    public void Clear()
    {
        _markedFrame = null;
        _click = null;
        Streams = [];
        SelectedStream = null;
    }

    // A click on a point: selects its frame, if it leads to one.
    public void Activate(int index)
    {
        if (Graph is { } graph && graph.FrameAt(index, _markedFrame) is { } offset)
        {
            _click = (graph, index, offset);
            FrameRequested?.Invoke(this, offset);
        }
    }

    // A click on a legend entry of a category series: selects the next frame after the selected one that holds the category.
    public void ActivateCategory(int category)
    {
        if (Graph is FrameMapGraph map)
        {
            if (map.NextFrameOf(category, _markedFrame, _shownSeverities) is { } next)
            {
                FrameRequested?.Invoke(this, next);
            }

            return;
        }

        if (Graph is CategoryGraph graph && CategoryGraph.NextUnit(graph.Series, category, _markedFrame) is { } offset)
        {
            FrameRequested?.Invoke(this, offset);
        }
    }

    // Draws the chosen entry in another form; the choice holds for the next entry that has a form of that kind.
    public void SelectForm(GraphForm kind)
    {
        if (_selected is not { } entry || !entry.Forms.Any(f => f.Kind == kind))
        {
            return;
        }


        _preferredForm = kind;
        if (entry.SelectForm(kind))
        {
            Raise(nameof(Form));
            Raise(nameof(Graph));
            RaiseChart();
            MarkedIndex = IndexOfMarked();
        }
    }

    // The frame selected in the tree, or null when what is selected is no frame.
    public void MarkFrame(long? offset)
    {
        _markedFrame = offset;
        if (_click is { } click && click.Offset != offset)
        {
            _click = null;
        }

        MarkedIndex = IndexOfMarked();
    }

    // The column of the marked frame. A time graph finds a frame by an estimate of its sample position (SampleLocator), which can
    // place a frame's start early, so the frame that a click on the panel selected keeps the column that was clicked.
    private int? IndexOfMarked()
    {
        if (_markedFrame is not { } at)
        {
            return null;
        }

        return _click is { } click && click.Offset == at && click.Graph is TimeGraph && ReferenceEquals(click.Graph, Graph)
            ? click.Index
            : Graph?.IndexOfFrame(at);
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
        if (_preferredForm is { } preferred)
        {
            _selected?.SelectForm(preferred);
        }

        Raise(nameof(Forms));
        Raise(nameof(HasForms));
        Raise(nameof(Selected));
        Raise(nameof(Graph));
        Raise(nameof(Form));
        RaiseChart();
        MarkedIndex = IndexOfMarked();
    }

    private void RaiseChart()
    {
        Raise(nameof(EmptyNote));
        Raise(nameof(HasEmptyNote));
        Raise(nameof(Figures));
        Raise(nameof(Topic));
        Raise(nameof(LearnMoreText));
        Raise(nameof(IsChartShown));
        Raise(nameof(IsFiguresShown));
    }
}
