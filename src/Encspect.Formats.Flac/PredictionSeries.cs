using Encspect.Core;

namespace Encspect.Formats.Flac;

// The coding series of the subframes (docs/design/coding-graphs.md, the FLAC survey): one unit per frame that has a bitrate point, recorded while the frames are read,
// since a frame past the first 4,096 keeps no subframes. The category series hold the values of the tallies in State, which are counted over the same frames and
// subframes; the numeric ones the highest order and precision of a frame's linear predictors and the block size.
internal sealed class PredictionSeries(long sampleRate)
{
    private static readonly Category[] Types =
    [
        new("Constant", Explain.StatConstant) { Topic = Topics.Subframes },
        new("Verbatim", Explain.StatVerbatim) { Topic = Topics.Subframes },
        new("Fixed Predictor", Explain.StatFixed) { Topic = Topics.LinearPrediction },
        new("Linear Predictor", Explain.StatLpc) { Topic = Topics.LinearPrediction },
    ];

    private static readonly Category[] FixedOrders =
    [
        .. Explain.FixedOrderCategories.Select((text, order) => new Category($"Order {order}", text) { Topic = Topics.LinearPrediction }),
    ];

    private static readonly Category[] PartitionOrders =
    [
        .. Explain.PartitionOrderCategories.Select((text, order) => new Category($"Order {order}", text) { Topic = Topics.RiceCoding }),
    ];

    private static readonly Category[] Methods =
    [
        new("Rice with 5-bit parameters", Explain.CategoryRice5) { Topic = Topics.RiceCoding },
        new("Rice", Explain.CategoryRice4) { Topic = Topics.RiceCoding },
    ];

    private static readonly Category[] Wasted =
    [
        new("With wasted bits", Explain.CategoryWasted) { Topic = Topics.WastedBits },
        new("Without wasted bits", Explain.CategoryNoWasted) { Topic = Topics.WastedBits },
    ];

    private readonly CategorySeriesBuilder _types = new("Subframe Types", Types, "subframe", concept: GraphConcept.Prediction);
    private readonly CategorySeriesBuilder _fixed = new("Fixed Predictor Orders", FixedOrders, "subframe", isOrdered: true, concept: GraphConcept.Prediction);
    private readonly CategorySeriesBuilder _partitions = new("Partition Orders", PartitionOrders, "subframe", isOrdered: true);
    private readonly CategorySeriesBuilder _methods = new("Rice With 5-Bit Parameters", Methods, "subframe");
    private readonly CategorySeriesBuilder _wasted = new("Subframes With Wasted Bits", Wasted, "subframe");

    // Per frame, the highest order and precision of its linear predictors (0: none), and its block size.
    private readonly ChunkedList<byte> _orders = new();
    private readonly ChunkedList<byte> _precisions = new();
    private readonly ChunkedList<ushort> _blockSizes = new();

    // The seconds a frame starts in, and the highest order and precision of the frames that start in each.
    private readonly ChunkedList<long> _seconds = new();
    private readonly ChunkedList<byte> _secondOrders = new();
    private readonly ChunkedList<byte> _secondPrecisions = new();
    private long _coded;

    public void AddFrame(int blockSize, ReadOnlySpan<SubframeFacts> facts)
    {
        Span<byte> types = stackalloc byte[facts.Length];
        Span<byte> fixedOrders = stackalloc byte[facts.Length];
        Span<byte> partitions = stackalloc byte[facts.Length];
        Span<byte> methods = stackalloc byte[facts.Length];
        Span<byte> wasted = stackalloc byte[facts.Length];
        int fixedCount = 0;
        int residuals = 0;
        int order = 0;
        int precision = 0;
        for (int i = 0; i < facts.Length; i++)
        {
            ref readonly SubframeFacts f = ref facts[i];
            types[i] = (byte)f.Type;
            wasted[i] = f.WastedBits > 0 ? (byte)0 : (byte)1;
            if (f.Type == SubframeType.Fixed)
            {
                fixedOrders[fixedCount++] = (byte)f.Order;
            }
            else if (f.Type == SubframeType.Lpc)
            {
                order = Math.Max(order, f.Order);
                precision = Math.Max(precision, f.Precision);
            }

            if (f.ResidualMethod >= 0)
            {
                partitions[residuals] = (byte)f.PartitionOrder;
                methods[residuals++] = f.ResidualMethod == 1 ? (byte)0 : (byte)1;
            }
        }

        _types.Add(types);
        _fixed.Add(fixedOrders[..fixedCount]);
        _partitions.Add(partitions[..residuals]);
        _methods.Add(methods[..residuals]);
        _wasted.Add(wasted);
        Finish(blockSize, order, precision);
    }

    // A frame the analyzer did not read to its end: a unit with no values.
    public void AddEmpty(int blockSize)
    {
        _types.Add([]);
        _fixed.Add([]);
        _partitions.Add([]);
        _methods.Add([]);
        _wasted.Add([]);
        Finish(blockSize, 0, 0);
    }

    private void Finish(int blockSize, int order, int precision)
    {
        _orders.Add((byte)order);
        _precisions.Add((byte)precision);
        _blockSizes.Add((ushort)Math.Min(blockSize, ushort.MaxValue));
        if (sampleRate > 0)
        {
            long second = _coded / sampleRate;
            if (_seconds.Count > 0 && _seconds[_seconds.Count - 1] == second)
            {
                _secondOrders.SetLast((byte)Math.Max(_secondOrders[_secondOrders.Count - 1], order));
                _secondPrecisions.SetLast((byte)Math.Max(_secondPrecisions[_secondPrecisions.Count - 1], precision));
            }
            else
            {
                _seconds.Add(second);
                _secondOrders.Add((byte)order);
                _secondPrecisions.Add((byte)precision);
            }
        }

        _coded += blockSize;
    }

    // The nodes are those of the tallies each series is held equal to; a series is built where its tally node exists.
    public (List<Series> Series, List<CategorySeries> Categories) Build(Series bitrate, Anchors nodes, CancellationToken cancellation)
    {
        var series = new List<Series>();
        var categories = new List<CategorySeries>();
        if (_types.Count != bitrate.X.Length)
        {
            return (series, categories);
        }

        void Add(CategorySeriesBuilder builder, Node? anchor, string description, string topic)
        {
            if (anchor is not null)
            {
                categories.Add(builder.Build(bitrate.X, bitrate, anchor, description, topic));
            }
        }

        Add(_types, nodes.Types, Explain.SubframeTypesSeries, Topics.Subframes);
        Add(_fixed, nodes.FixedOrders, Explain.FixedOrdersSeries, Topics.LinearPrediction);
        Add(_partitions, nodes.PartitionOrders, Explain.PartitionOrdersSeries, Topics.RiceCoding);
        Add(_methods, nodes.Method, Explain.RiceSeries, Topics.RiceCoding);
        Add(_wasted, nodes.Wasted, Explain.WastedSeries, Topics.WastedBits);

        ReadOnlySpan<double> offsets = bitrate.X.Span;
        if (nodes.HighestOrder is not null)
        {
            Numeric(series, offsets, _orders, "Highest Predictor Order", "order", Explain.HighestOrderSeries, "Highest Predictor Order Over Time", Explain.HighestOrderOverTimeSeries, _secondOrders, nodes.HighestOrder, cancellation);
        }

        if (nodes.Precision is not null)
        {
            Numeric(series, offsets, _precisions, "Coefficient Precision", "bits", Explain.PrecisionSeries, "Coefficient Precision Over Time", Explain.PrecisionOverTimeSeries, _secondPrecisions, nodes.Precision, cancellation);
        }

        AddBlockSizes(series, bitrate);
        return (series, categories);
    }

    private void Numeric(List<Series> series, ReadOnlySpan<double> offsets, ChunkedList<byte> values, string name, string unit, string description, string overTimeName, string overTimeDescription,
        ChunkedList<byte> perSecond, Node anchor, CancellationToken cancellation)
    {
        int count = 0;
        for (int i = 0; i < values.Count; i++)
        {
            count += values[i] > 0 ? 1 : 0;
        }

        if (count > 0)
        {
            var x = new double[count];
            var y = new double[count];
            for (int i = 0, n = 0; i < values.Count; i++)
            {
                if (i % 4096 == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                if (values[i] > 0)
                {
                    x[n] = offsets[i];
                    y[n++] = values[i];
                }
            }

            series.Add(new Series(name, unit, SeriesAxis.FrameOffset, x, y) { Description = description, Concept = GraphConcept.Prediction, Quantity = name });
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
                    highest[n++] = perSecond[i];
                }
            }

            series.Add(new Series(overTimeName, unit, SeriesAxis.Seconds, starts, highest, anchor) { Description = overTimeDescription, Concept = GraphConcept.Prediction, Quantity = name });
        }
    }

    // Only where the frames differ in size but for a shorter last one: a fixed block size has one value.
    private void AddBlockSizes(List<Series> series, Series bitrate)
    {
        int frames = _blockSizes.Count;
        bool differs = false;
        for (int i = 1; i < frames - 1 && !differs; i++)
        {
            differs = _blockSizes[i] != _blockSizes[0];
        }

        if (!differs)
        {
            return;
        }

        var sizes = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            sizes[i] = _blockSizes[i];
        }

        series.Add(new Series("Block Size", "samples", SeriesAxis.FrameOffset, bitrate.X, sizes) { Description = Explain.BlockSizeSeries });
    }

    // The tally nodes the series hang under.
    public sealed record Anchors(Node? Types, Node? FixedOrders, Node? PartitionOrders, Node? Method, Node? Wasted, Node? HighestOrder, Node? Precision);
}
