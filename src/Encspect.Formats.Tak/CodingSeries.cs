using Encspect.Core;

namespace Encspect.Formats.Tak;

// The coding series of the frames (docs/design/coding-graphs.md, the TAK survey), one unit for each frame that has a bitrate point, recorded while the frames are walked: the
// stereo mode of a frame of two channels, the subframes' types, the channels with wasted bits, and the highest prediction order, precision and residual mode of a frame. The names, units and axes
// are those of FLAC's, TTA's, WavPack's and Monkey's Audio's where the concept is the same.
internal sealed class CodingSeries
{
    private static readonly Category[] StereoCodings =
    [
        new("Left/right stereo", Explain.StatIndependent) { Topic = Topics.LeftSide },
        new("Left/side stereo", Explain.StatLeftSide) { Topic = Topics.LeftSide },
        new("Side/right stereo", Explain.StatSideRight) { Topic = Topics.LeftSide },
        new("Mid/side stereo", Explain.StatMidSide) { Topic = Topics.MidSide },
        new("Scaled prediction", Explain.StatScaledPrediction) { Topic = Topics.Prediction },
        new("Filter prediction", Explain.StatFilterPrediction) { Topic = Topics.Prediction },
    ];

    private static readonly Category[] Types =
    [
        new("Residuals only", Explain.StatResidualsOnly) { Topic = Topics.Prediction },
        new("Linear Predictor", Explain.StatPredictor) { Topic = Topics.Prediction },
    ];

    private static readonly Category[] Wasted =
    [
        new("With wasted bits", Explain.CategoryWasted) { Topic = Topics.WastedBits },
        new("Without wasted bits", Explain.CategoryNoWasted) { Topic = Topics.WastedBits },
    ];

    private readonly bool _isStereo;
    private readonly long _sampleRate;
    private readonly UnitCategoryTally _stereo = new("Stereo Coding", StereoCodings, "frame", GraphConcept.StereoCoding);
    private readonly UnitCategoryTally _types = new("Subframe Types", Types, "subframe", GraphConcept.Prediction, isVariable: true);
    private readonly UnitCategoryTally _wasted = new("Channels With Wasted Bits", Wasted, "channel", GraphConcept.None, isVariable: true);
    private readonly ChunkedList<ushort> _orders = new();
    private readonly ChunkedList<ushort> _precisions = new();

    // The highest residual mode of each frame, plus one: 0 is a frame without a residual block that has values.
    private readonly ChunkedList<ushort> _modes = new();
    private long _predicted;
    private long _orderTotal;
    private int _orderMax;
    private int _precisionMin = int.MaxValue;
    private int _precisionMax;

    // The seconds a frame starts in, and the highest order and precision of the frames that start in each.
    private readonly ChunkedList<long> _seconds = new();
    private readonly ChunkedList<ushort> _secondOrders = new();
    private readonly ChunkedList<ushort> _secondPrecisions = new();
    private readonly ChunkedList<ushort> _secondModes = new();
    private long _coded;
    private int _modeMax = -1;

    public CodingSeries(long sampleRate, bool isStereo)
    {
        _isStereo = isStereo;
        _sampleRate = sampleRate;
    }

    public int Units => _types.Units;

    // One frame the walk read.
    public void AddFrame(WalkedFrame frame)
    {
        _stereo.Add(_isStereo && frame.StereoMode >= 0 ? frame.StereoMode switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, 4 or 5 => 4, _ => 5 } : null);
        Span<byte> types = stackalloc byte[frame.Subframes.Count];
        int order = 0;
        int precision = 0;
        for (int i = 0; i < types.Length; i++)
        {
            SubframeRecord s = frame.Subframes[i];
            types[i] = s.IsPredicted ? (byte)1 : (byte)0;
            if (s.IsPredicted && s.Order > 0)
            {
                _predicted++;
                _orderTotal += s.Order;
                order = Math.Max(order, s.Order);
                precision = Math.Max(precision, s.Precision);
                _orderMax = Math.Max(_orderMax, s.Order);
                _precisionMin = Math.Min(_precisionMin, s.Precision);
                _precisionMax = Math.Max(_precisionMax, s.Precision);
            }
        }

        _types.AddAll(types);
        Span<byte> wasted = stackalloc byte[frame.IsShort ? 0 : frame.Channels];
        for (int c = 0; c < wasted.Length; c++)
        {
            wasted[c] = frame.Wasted[c] > 0 ? (byte)0 : (byte)1;
        }

        _wasted.AddAll(wasted);
        int mode = -1;
        foreach (byte m in frame.Modes)
        {
            mode = Math.Max(mode, m);
        }

        _modeMax = Math.Max(_modeMax, mode);
        Finish(frame.Samples, order, precision, mode + 1);
    }

    private void Finish(int samples, int order, int precision, int mode)
    {
        _orders.Add((ushort)order);
        _precisions.Add((ushort)precision);
        _modes.Add((ushort)mode);
        if (_sampleRate > 0)
        {
            long second = _coded / _sampleRate;
            if (_seconds.Count > 0 && _seconds[_seconds.Count - 1] == second)
            {
                _secondOrders.SetLast((ushort)Math.Max(_secondOrders[_secondOrders.Count - 1], order));
                _secondPrecisions.SetLast((ushort)Math.Max(_secondPrecisions[_secondPrecisions.Count - 1], precision));
                _secondModes.SetLast((ushort)Math.Max(_secondModes[_secondModes.Count - 1], mode));
            }
            else
            {
                _seconds.Add(second);
                _secondOrders.Add((ushort)order);
                _secondPrecisions.Add((ushort)precision);
                _secondModes.Add((ushort)mode);
            }
        }

        _coded += samples;
    }

    // A frame whose coded data could not be read: a unit with no values.
    public void AddEmpty(int samples)
    {
        _stereo.Add(null);
        _types.AddAll([]);
        _wasted.AddAll([]);
        Finish(samples, 0, 0, 0);
    }

    // The tallies, under the stream's statistics. Returns the nodes the series hang under.
    public Anchors AddTo(Node statistics, Func<string, NodeValue?, string, string?, Node> cover)
    {
        Node? stereo = _stereo.AddTo(statistics, cover, Explain.StatStereoCoding, Topics.MidSide, "Frame", "Frames");
        Node? types = _types.AddTo(statistics, cover, Explain.StatSubframeTypes, Topics.Prediction, "Subframe", "Subframes");
        Node? order = null;
        Node? precision = null;
        if (_predicted > 0)
        {
            statistics.Add(cover("Average Predictor Order", NodeValue.Decimal(_orderTotal / (double)_predicted, 1), Explain.StatAverageOrder, Topics.Prediction));
            order = statistics.Add(cover("Highest Predictor Order", NodeValue.Integer(_orderMax), Explain.StatHighestOrder, Topics.Prediction));
            precision = statistics.Add(cover("Coefficient Precision", _precisionMin == _precisionMax ? NodeValue.Integer(_precisionMin, "bits") : $"{_precisionMin} to {_precisionMax} bits", Explain.StatPrecision, Topics.Prediction));
        }

        Node? wasted = _wasted.AddTo(statistics, cover, Explain.StatWasted, Topics.WastedBits, "Channel", "Channels");
        Node? modes = _modeMax >= 0 ? statistics.Add(cover("Highest Residual Mode", NodeValue.Integer(_modeMax), Explain.StatHighestMode, Topics.Residual)) : null;
        return new Anchors(stereo, types, wasted, order, precision, modes);
    }

    public (List<Series> Series, List<CategorySeries> Categories) Build(Series bitrate, Anchors nodes, CancellationToken cancellation)
    {
        var series = new List<Series>();
        var categories = new List<CategorySeries>();
        if (_types.Units != bitrate.X.Length)
        {
            return (series, categories);
        }

        void Add(CategorySeries? built)
        {
            if (built is not null)
            {
                categories.Add(built);
            }
        }

        if (nodes.Stereo is not null)
        {
            Add(_stereo.Build(bitrate, nodes.Stereo, Explain.StereoCodingSeries, Topics.MidSide, null));
        }

        if (nodes.Types is not null)
        {
            Add(_types.Build(bitrate, nodes.Types, Explain.SubframeTypesSeries, Topics.Prediction, null));
        }

        if (nodes.Wasted is not null)
        {
            Add(_wasted.Build(bitrate, nodes.Wasted, Explain.WastedSeries, Topics.WastedBits, null));
        }

        if (nodes.Order is not null)
        {
            Numeric(series, bitrate, _orders, "Highest Predictor Order", "order", Explain.HighestOrderSeries, "Highest Predictor Order Over Time", Explain.HighestOrderOverTimeSeries, _secondOrders, nodes.Order, 0, GraphConcept.Prediction, cancellation);
        }

        if (nodes.Precision is not null)
        {
            Numeric(series, bitrate, _precisions, "Coefficient Precision", "bits", Explain.PrecisionSeries, "Coefficient Precision Over Time", Explain.PrecisionOverTimeSeries, _secondPrecisions, nodes.Precision, 0, GraphConcept.Prediction, cancellation);
        }

        if (nodes.Modes is not null)
        {
            Numeric(series, bitrate, _modes, "Highest Residual Mode", "mode", Explain.HighestModeSeries, "Highest Residual Mode Over Time", Explain.HighestModeOverTimeSeries, _secondModes, nodes.Modes, 1, GraphConcept.None, cancellation);
        }

        return (series, categories);
    }

    // A series' x is the bitrate series' where every frame has a value, else the offsets of the frames that do (the order and the precision have the same frames, as every frame
    // with a predictor has both).
    // Values are stored with offset added, 0 meaning none; the series holds the frames that have one, and its Over Time form the highest in each second that has one.
    private void Numeric(List<Series> series, Series bitrate, ChunkedList<ushort> values, string name, string unit, string description, string overTimeName, string overTimeDescription,
        ChunkedList<ushort> perSecond, Node anchor, int offset, GraphConcept concept, CancellationToken cancellation)
    {
        int count = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            count += values[i] > 0 ? 1 : 0;
        }

        if (count > 0)
        {
            var x = new double[count];
            var y = new double[count];
            ReadOnlySpan<double> offsets = bitrate.X.Span;
            for (int i = 0, n = 0; i < values.Count; i++)
            {
                if (values[i] > 0)
                {
                    x[n] = offsets[i];
                    y[n++] = values[i] - offset;
                }
            }

            series.Add(new Series(name, unit, SeriesAxis.FrameOffset, x, y) { Description = description, Concept = concept, Quantity = name });
        }

        int valued = 0;
        for (int i = 0; i < perSecond.Count; i++)
        {
            valued += perSecond[i] > 0 ? 1 : 0;
        }

        if (valued > 0)
        {
            var starts = new double[valued];
            var highest = new double[valued];
            for (int i = 0, n = 0; i < perSecond.Count; i++)
            {
                if (perSecond[i] > 0)
                {
                    starts[n] = _seconds[i];
                    highest[n++] = perSecond[i] - offset;
                }
            }

            series.Add(new Series(overTimeName, unit, SeriesAxis.Seconds, starts, highest, anchor) { Description = overTimeDescription, Concept = concept, Quantity = name });
        }
    }

    public sealed record Anchors(Node? Stereo, Node? Types, Node? Wasted, Node? Order, Node? Precision, Node? Modes);
}
