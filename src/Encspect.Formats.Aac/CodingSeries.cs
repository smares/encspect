using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.Aac;

// The categories of the coding series, shared by the series and the statistics nodes they are anchored to, so a category has
// one name and one description in the tree and in the graph.
internal static class CodingCategories
{
    // By window_sequence (ISO/IEC 14496-3 4.5.2): only long, long start, eight short, long stop.
    public static readonly string[] WindowSequenceNames = ["Only Long", "Long Start", "Eight Short", "Long Stop"];

    public static readonly Category[] WindowSequences =
    [
        new(WindowSequenceNames[0], Explain.WindowOnlyLong) { Topic = Topics.Windows },
        new(WindowSequenceNames[1], Explain.WindowLongStart) { Topic = Topics.Windows },
        new(WindowSequenceNames[2], Explain.WindowEightShort) { Topic = Topics.Windows },
        new(WindowSequenceNames[3], Explain.WindowLongStop) { Topic = Topics.Windows },
    ];

    // By window sequence: the size of the transform. Long, start and stop windows are counted together, as EncSpot counts
    // MPEG audio's block types.
    public static readonly byte[] SizeOfWindow = [0, 0, 1, 0];

    public static readonly Category[] Sizes =
    [
        new("Long, start and stop", Explain.SizeLong) { Topic = Topics.Windows },
        new("Short", Explain.SizeShort) { Topic = Topics.Windows },
    ];

    // The codes of the "Stereo Coding" series. The first four are MPEG audio's stereo codings in the order of its charts' colors.
    public const byte CodeLeftRight = 0;
    public const byte CodeMidSide = 1;
    public const byte CodeIntensity = 2;
    public const byte CodeMidSideAndIntensity = 3;
    public const byte CodeReserved = 4;

    public static readonly Category[] StereoCodings =
    [
        new("Left/right stereo", Explain.CodingLeftRight) { Topic = Topics.MidSide },
        new("Mid/side stereo", Explain.CodingMidSide) { Topic = Topics.MidSide },
        new("Intensity stereo", Explain.CodingIntensity) { Topic = Topics.Intensity },
        new("Mid/side and intensity stereo", Explain.CodingMidSideAndIntensity) { Topic = Topics.Intensity },
        new("Reserved", Explain.CodingReserved) { Topic = Topics.MidSide, IsReserved = true },
    ];
}

// The statistics nodes the coding series are anchored to.
internal sealed record CodingNodes(Node? WindowSequences, Node? BlockSizes, Node? StereoCoding, Node? CodedBandwidth);

// Records the coding series of a stream while its raw data blocks are read: the window sequence of every channel stream, the
// stereo coding of every channel pair and the coded bandwidth of every unit (an access unit, an ADTS frame or an ADIF block).
// The block reader calls Stream and Pair from the places that count the same things for the tally, so a series equals its
// tally; the caller ends each unit with BlockLog.EndUnit. Nothing here depends on the analysis it serves apart from the
// stream's setup, and a recorder belongs to one stream (docs/design/coding-graphs.md, "When the codes are made").
internal sealed class CodingRecorder(StreamSetup setup)
{
    private readonly List<byte> _windows = [];
    private readonly List<byte> _pairs = [];
    private readonly CategorySeriesBuilder _windowSeries = new("Window Sequences", CodingCategories.WindowSequences, "channel stream", concept: GraphConcept.BlockSwitching);
    private CategorySeriesBuilder? _pairSeries;
    private readonly ChunkedList<int> _hertz = new();
    private int _unitHertz = -1;
    private int _pairUnits;

    // Units ended so far.
    public int Count { get; private set; }

    // Units by coded bandwidth in whole hertz, for the statistics node: the highest over the unit's channel streams.
    public SortedDictionary<int, long> Bandwidths { get; } = [];

    // A channel stream of window_sequence windowSequence whose max_sfb bands end at line top of its window.
    public void Stream(int windowSequence, int top)
    {
        _windows.Add((byte)windowSequence);
        int hertz = Hertz(windowSequence == 2, top);
        _unitHertz = Math.Max(_unitHertz, hertz);
    }

    public void Pair(byte code) => _pairs.Add(code);

    // The top of the line-th line of a window in Hz at the core's sample rate: lines are spaced by half the sample rate over the
    // lines of the window, 1024 or 960 for a long window and an eighth of that for a short one.
    private int Hertz(bool isShort, int line)
    {
        long lines = isShort ? setup.LongLines / 8 : setup.LongLines;
        return (int)Math.Min(int.MaxValue, CodingStatistics.RoundHalfEven((long)line * setup.SampleRate, 2 * lines));
    }

    public void EndUnit()
    {
        _windowSeries.Add(CollectionsMarshal.AsSpan(_windows));
        if (_pairs.Count > 0 && _pairSeries is null)
        {
            // The first pair: the units before it have no pair and get empty units, so that a stream without pairs keeps nothing.
            _pairSeries = new("Stereo Coding", CodingCategories.StereoCodings, "channel pair", concept: GraphConcept.StereoCoding);
            for (; _pairUnits < Count; _pairUnits++)
            {
                _pairSeries.Add([]);
            }
        }

        if (_pairSeries is not null)
        {
            _pairSeries.Add(CollectionsMarshal.AsSpan(_pairs));
            _pairUnits++;
        }

        _hertz.Add(_unitHertz);
        if (_unitHertz >= 0)
        {
            Bandwidths[_unitHertz] = Bandwidths.GetValueOrDefault(_unitHertz) + 1;
        }

        _windows.Clear();
        _pairs.Clear();
        _unitHertz = -1;
        Count++;
    }

    // The series of the stream, once all its units are ended: offsets are the units' x values, which are those of the bitrate series
    // (shared) where every unit has a point, else the units' own; time is the partition the per-second series use. The numeric series
    // are added to series.
    public List<CategorySeries> Build(ReadOnlyMemory<double> offsets, Series? shared, TimeIndex? time, CodingNodes nodes, List<Series> series, CancellationToken cancellation)
    {
        var categories = new List<CategorySeries>();
        if (Count == 0)
        {
            return categories;
        }

        if (offsets.Length != Count)
        {
            throw new ArgumentException("Every unit of the coding series has an x value.", nameof(offsets));
        }

        if (nodes.WindowSequences is { } windowAnchor)
        {
            CategorySeries windows = _windowSeries.Build(offsets, shared, windowAnchor, Explain.WindowSequencesSeries, Topics.Windows, time);
            categories.Add(windows);
            categories.Add(windows.Group("Block Sizes", CodingCategories.Sizes, CodingCategories.SizeOfWindow, nodes.BlockSizes, Explain.BlockSizesSeries, Topics.Windows));
        }

        if (_pairSeries is not null && nodes.StereoCoding is { } pairAnchor)
        {
            categories.Add(_pairSeries.Build(offsets, shared, pairAnchor, Explain.StereoCodingSeries, Topics.MidSide, time));
        }

        AddBandwidth(offsets, nodes.CodedBandwidth, time, series, cancellation);
        return categories;
    }

    // "Coded Bandwidth" per unit, at the units that have a value, and "Coded Bandwidth Over Time", each interval's highest.
    private void AddBandwidth(ReadOnlyMemory<double> offsets, Node? anchor, TimeIndex? time, List<Series> series, CancellationToken cancellation)
    {
        int valued = 0;
        for (int i = 0; i < Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            valued += _hertz[i] >= 0 ? 1 : 0;
        }

        if (valued == 0)
        {
            return;
        }

        var values = new double[valued];
        ReadOnlyMemory<double> x = offsets;
        double[]? own = valued == Count ? null : new double[valued];
        for (int i = 0, n = 0; i < Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            if (_hertz[i] >= 0)
            {
                values[n] = _hertz[i];
                if (own is not null)
                {
                    own[n] = offsets.Span[i];
                }

                n++;
            }
        }

        if (own is not null)
        {
            x = own;
        }

        series.Add(new Series("Coded Bandwidth", "Hz", SeriesAxis.FrameOffset, x, values)
        {
            Description = Explain.CodedBandwidthSeries,
            Concept = GraphConcept.CodedBandwidth,
            Quantity = "Coded Bandwidth",
        });
        if (time is null)
        {
            return;
        }

        var starts = new List<double>();
        var highest = new List<double>();
        ReadOnlySpan<int> first = time.FirstUnits.Span;
        for (int interval = 0; interval < first.Length; interval++)
        {
            if (interval % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            int max = -1;
            for (int unit = first[interval]; unit < (interval + 1 < first.Length ? first[interval + 1] : Count); unit++)
            {
                max = Math.Max(max, _hertz[unit]);
            }

            if (max >= 0)
            {
                starts.Add(time.Starts.Span[interval]);
                highest.Add(max);
            }
        }

        if (starts.Count > 0)
        {
            series.Add(new Series("Coded Bandwidth Over Time", "Hz", SeriesAxis.Seconds, starts.ToArray(), highest.ToArray(), anchor)
            {
                Description = Explain.CodedBandwidthOverTimeSeries,
                Concept = GraphConcept.CodedBandwidth,
                Quantity = "Coded Bandwidth",
            });
        }
    }
}
