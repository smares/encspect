using Encspect.Core;

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

    public IReadOnlyList<GraphFormChoice> Forms { get; }

    public bool HasForms => Forms.Count > 1;

    public GraphFormChoice Form => _form;

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
        return true;
    }
}

// The entries of one heading ("Stereo Coding"), one under the other.
public sealed class GraphChoiceGroup(string heading, IReadOnlyList<GraphChoice> choices)
{
    public string Heading { get; } = heading;

    public IReadOnlyList<GraphChoice> Choices { get; } = choices;
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
                Raise(nameof(ChoiceGroups));
                Choose(value?.Graphs.FirstOrDefault(g => g.Title == _preferredTitle) ?? value?.Graphs.FirstOrDefault());
            }
        }
    }

    public IReadOnlyList<GraphChoice> Choices => _selectedStream?.Graphs ?? [];

    // The entries under the headings of their concepts; a heading without an entry for the stream is not listed.
    public IReadOnlyList<GraphChoiceGroup> ChoiceGroups
    {
        get
        {
            var groups = new List<GraphChoiceGroup>();
            foreach (IGrouping<string, GraphChoice> group in Choices.GroupBy(c => c.Heading))
            {
                groups.Add(new GraphChoiceGroup(group.Key, [.. group]));
            }

            return groups;
        }
    }

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

    // The entries of a stream, from its series alone, in the order of the headings. A category series is one entry with
    // its forms, and so is each quantity: the series that carry the same Quantity are its forms (per frame, per second of
    // audio). Series that name none are paired by what they are, never by name: the one per-frame and the one per-second
    // series in kbps are the bitrate, and the same two in bytes the bit reservoir, until their formats set a Quantity.
    public static IReadOnlyList<GraphChoice> For(StreamInfo stream)
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
            GraphFormChoice[] forms = [.. CategoryGraph.FormsOf(series).Select(f => new GraphFormChoice(f, f switch
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
            entries.Add(new GraphChoice(series.Name, GraphHeadings.Of(series.Concept), series.Description, forms));
        }

        foreach (List<Series> quantity in Quantities(stream.Series.Where(s => s.Values.Length > 0)))
        {
            entries.Add(Entry(quantity, frameOffsets, stream.Duration));
        }

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

    private static GraphChoice Entry(List<Series> quantity, ReadOnlyMemory<double> frameOffsets, double duration)
    {
        Series? perFrame = quantity.FirstOrDefault(s => s.Axis == SeriesAxis.FrameOffset);
        Series? perSecond = quantity.FirstOrDefault(s => s.Axis == SeriesAxis.Seconds);
        Series first = perFrame ?? perSecond!;
        var forms = new List<GraphFormChoice>();
        if (perFrame is not null)
        {
            forms.Add(new GraphFormChoice(GraphForm.Frames, "Per Frame", "One point for every frame", new LineGraph(perFrame, frameOffsets, duration)));
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
            forms.Add(new GraphFormChoice(GraphForm.Summary, "Distribution", "How many frames have each value", new DistributionGraph(perFrame)));
        }

        return new GraphChoice(first.Quantity ?? first.Name, GraphHeadings.Of(first), first.Description, forms);
    }

    public void Load(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var streams = new List<GraphStream>();
        for (int i = 0; i < report.Streams.Count; i++)
        {
            IReadOnlyList<GraphChoice> entries = For(report.Streams[i]);
            if (entries.Count > 0)
            {
                streams.Add(new GraphStream(i + 1, report.Streams[i], entries));
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

    // A click on a legend entry of a category series: selects the next frame after the selected one that holds the category.
    public void ActivateCategory(int category)
    {
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
            MarkedIndex = _markedFrame is { } at ? Graph?.IndexOfFrame(at) : null;
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
        if (_preferredForm is { } preferred)
        {
            _selected?.SelectForm(preferred);
        }

        Raise(nameof(Forms));
        Raise(nameof(HasForms));
        Raise(nameof(Selected));
        Raise(nameof(Graph));
        Raise(nameof(Form));
        MarkedIndex = _markedFrame is { } at ? Graph?.IndexOfFrame(at) : null;
    }
}
