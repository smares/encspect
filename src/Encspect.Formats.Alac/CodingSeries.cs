using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.Alac;

// The categories of the coding series, shared by the series and the statistics nodes they are anchored to, so a category has one name and one
// description in the tree and in the graph.
internal static class CodingCategories
{
    // The codes of the "Stereo Coding" series, by what matrix_dec.c reconstructs: the first channel u and the second v give left = u + v - (mixRes * v >> mixBits)
    // and right = left - v. A mixing resolution of 0 (and an uncompressed pair) leaves the channels as they are; 2^mixBits makes u the left channel itself and v
    // left minus right; half of that makes u the mean of both, which is mid/side; any other value is a weighted mix. The second channel is always left minus
    // right, so no pair is side/right: that category (code 2) stands in the list only so that "Mid/side stereo" and "Weighted mix" have the codes, and the
    // colors, they have in FLAC and Dolby TrueHD.
    public const byte CodeLeftRight = 0;
    public const byte CodeLeftSide = 1;
    public const byte CodeMidSide = 3;
    public const byte CodeWeightedMix = 4;

    public static byte PairCode(bool isUncompressed, byte mixBits, sbyte mixResolution)
    {
        if (isUncompressed || mixResolution == 0)
        {
            return CodeLeftRight;
        }

        if (mixBits < 8)
        {
            int whole = 1 << mixBits;
            if (mixResolution == whole)
            {
                return CodeLeftSide;
            }

            if (mixBits > 0 && mixResolution == whole >> 1)
            {
                return CodeMidSide;
            }
        }

        return CodeWeightedMix;
    }

    // The codes of the "Element Coding" series.
    public const byte CodeCompressed = 0;
    public const byte CodeUncompressed = 1;

    public static readonly Category[] StereoCodings =
    [
        new("Left/right stereo", Explain.CodingLeftRight) { Topic = Topics.Prediction },
        new("Left/side stereo", Explain.CodingLeftSide) { Topic = Topics.LeftSide },
        new("Side/right stereo", Explain.CodingSideRight) { Topic = Topics.LeftSide },
        new("Mid/side stereo", Explain.CodingMidSide) { Topic = Topics.MidSide },
        new("Weighted mix", Explain.CodingWeightedMix) { Topic = Topics.Prediction },
    ];

    public static readonly Category[] ElementCodings =
    [
        new("Compressed", Explain.CodingCompressed) { Topic = Topics.Alac },
        new("Uncompressed", Explain.CodingUncompressed) { Topic = Topics.Alac },
    ];
}

// The statistics nodes the coding series are anchored to.
internal sealed record CodingNodes(Node? StereoCoding, Node? ElementCoding);

// Records the coding series of a stream while its frames are read: the stereo coding of every channel pair and the coding of every audio element, one
// unit for each frame that has a point in the bitrate graph. The tally collects the codes of a frame while the walk counts the same things, so a
// series equals its tally; the state ends each unit. A recorder belongs to one stream (docs/design/coding-graphs.md, "When the codes are made").
internal sealed class CodingRecorder
{
    private readonly CategorySeriesBuilder _elements = new("Element Coding", CodingCategories.ElementCodings, "element");
    private CategorySeriesBuilder? _pairs;
    private int _pairUnits;

    // Per unit, the highest predictor order of its channels, 255 for a unit without a compressed channel.
    private readonly ChunkedList<byte> _orders = new();

    // Units ended so far.
    public int Count { get; private set; }

    public void EndUnit(CodingTally tally)
    {
        ReadOnlySpan<byte> elements = CollectionsMarshal.AsSpan(tally.ElementCodes);
        ReadOnlySpan<byte> pairs = CollectionsMarshal.AsSpan(tally.PairCodes);
        _elements.Add(elements);
        _orders.Add(tally.UnitHighestOrder < 0 ? (byte)255 : (byte)tally.UnitHighestOrder);
        if (pairs.Length > 0 && _pairs is null)
        {
            // The first pair: the units before it have none and get empty units, so that a stream without pairs keeps nothing.
            _pairs = new CategorySeriesBuilder("Stereo Coding", CodingCategories.StereoCodings, "channel pair", concept: GraphConcept.StereoCoding);
            for (; _pairUnits < Count; _pairUnits++)
            {
                _pairs.Add([]);
            }
        }

        if (_pairs is not null)
        {
            _pairs.Add(pairs);
            _pairUnits++;
        }

        Count++;
        Clear(tally);
    }

    // A frame that gets no unit: its codes are dropped.
    public static void Clear(CodingTally tally)
    {
        tally.ElementCodes.Clear();
        tally.PairCodes.Clear();
        tally.UnitHighestOrder = -1;
    }

    // The highest predictor order of each unit's channels, at the units that have a compressed channel (x: their offsets).
    public Series? BuildOrders(ReadOnlyMemory<double> offsets)
    {
        if (Count == 0 || offsets.Length != Count)
        {
            return null;
        }

        int count = 0;
        for (int i = 0; i < _orders.Count; i++)
        {
            count += _orders[i] != 255 ? 1 : 0;
        }

        if (count == 0)
        {
            return null;
        }

        // Where every unit has a value the series shares the bitrate series' x values.
        ReadOnlySpan<double> all = offsets.Span;
        double[] xs = count == Count ? [] : new double[count];
        var y = new double[count];
        for (int i = 0, n = 0; i < _orders.Count; i++)
        {
            if (_orders[i] != 255)
            {
                if (xs.Length > 0)
                {
                    xs[n] = all[i];
                }

                y[n++] = _orders[i];
            }
        }

        return new Series("Highest Predictor Order", "order", SeriesAxis.FrameOffset, xs.Length > 0 ? xs : offsets, y)
        {
            Description = Explain.HighestOrderSeries,
            Concept = GraphConcept.Prediction,
            Quantity = "Highest Predictor Order",
        };
    }

    // The series of the stream, once all its units are ended: offsets are the units' x values, those of the bitrate series (shared), and time is the
    // partition the per-second series use.
    public List<CategorySeries> Build(ReadOnlyMemory<double> offsets, Series? shared, TimeIndex? time, CodingNodes nodes)
    {
        var series = new List<CategorySeries>();
        if (Count == 0 || offsets.Length != Count)
        {
            return series;
        }

        if (_pairs is not null && nodes.StereoCoding is { } stereo)
        {
            series.Add(_pairs.Build(offsets, shared, stereo, Explain.StereoCodingSeries, Topics.Prediction, time));
        }

        if (nodes.ElementCoding is { } coding)
        {
            series.Add(_elements.Build(offsets, shared, coding, Explain.ElementCodingSeries, Topics.Alac, time));
        }

        return series;
    }
}
