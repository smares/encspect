using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.Alac;

// The categories of the coding series, shared by the series and the statistics nodes they are anchored to, so a category has one name and one
// description in the tree and in the graph.
internal static class CodingCategories
{
    // The codes of the "Stereo Coding" series, by what matrix_dec.c reconstructs: the first channel u and the second v give left = u + v - (mixRes * v >> mixBits)
    // and right = left - v. A mixing resolution of 0 (and an uncompressed pair) leaves the channels as they are; 2^mixBits makes u the left channel itself and v
    // left minus right; half of that makes u the mean of both, which is mid/side; any other value is a weighted mix.
    public const byte CodeLeftRight = 0;
    public const byte CodeLeftSide = 1;
    public const byte CodeMidSide = 2;
    public const byte CodeWeightedMix = 3;

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
        new("Left/side stereo", Explain.CodingLeftSide) { Topic = Topics.ChannelAssignment },
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

    // Units ended so far.
    public int Count { get; private set; }

    public void EndUnit(CodingTally tally)
    {
        ReadOnlySpan<byte> elements = CollectionsMarshal.AsSpan(tally.ElementCodes);
        ReadOnlySpan<byte> pairs = CollectionsMarshal.AsSpan(tally.PairCodes);
        _elements.Add(elements);
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
