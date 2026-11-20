using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// The series over the blocks of a version 1 or 2 stream, recorded while its frames are read, since no block records are kept: the size of each block,
// whether it has noise substitution and its coded bandwidth. A frame is a unit of the category series (its blocks are the values, so they share the
// x values of "Bitrate"); a block's coded bandwidth has an x value of its own, its frame's offset, since blocks share that offset.
internal sealed class StdBlockSeries
{
    private readonly StdParameters _p;
    private readonly CategorySeriesBuilder _sizes;
    private readonly CategorySeriesBuilder? _noise;
    private ChunkedList<(int Frame, int Hertz)> _reach = new();
    private ChunkedList<(long Second, int Hertz)> _seconds = new();
    private SortedDictionary<int, int> _counts = [];
    private byte[] _codes = new byte[64];

    public StdBlockSeries(StdParameters parameters)
    {
        _p = parameters;
        _sizes = new CategorySeriesBuilder("Block Sizes", SizeCategories(parameters), "block", isOrdered: true, concept: GraphConcept.BlockSwitching);
        if (parameters.UsesNoiseSubstitution)
        {
            _noise = new CategorySeriesBuilder("Noise-Substituted Blocks", NoiseCategories, "block");
        }
    }

    private static readonly Category[] NoiseCategories =
    [
        new("With noise substitution", StdText.CategoryWithNoise) { Topic = Topics.NoiseSubstitution },
        new("Without noise substitution", StdText.CategoryWithoutNoise) { Topic = Topics.NoiseSubstitution },
    ];

    private static Category[] SizeCategories(StdParameters p)
    {
        var categories = new Category[Math.Max(1, p.SizeCount)];
        for (int k = 0; k < categories.Length; k++)
        {
            categories[k] = new Category($"{p.BlockLength(k).ToString(CultureInfo.InvariantCulture)} samples", StdText.CategoryBlockSize) { Topic = Topics.BlockSwitching };
        }

        return categories;
    }

    public bool HasBandwidth => _counts.Count > 0;

    // One frame: its blocks, in the order they were read. The bandwidth of a frame that is not valid is left out.
    public void Add(int frameIndex, bool isValid, StdBlockNotes notes)
    {
        int count = notes.Count;
        if (_codes.Length < count)
        {
            _codes = new byte[count];
        }

        Span<byte> sizes = _codes.AsSpan(0, count);
        for (int i = 0; i < count; i++)
        {
            byte size = notes[i].Size;
            sizes[i] = size == StdBlockNotes.UnknownSize ? CategorySeries.None : size;
        }

        _sizes.Add(sizes);
        if (_noise is not null)
        {
            for (int i = 0; i < count; i++)
            {
                sizes[i] = notes[i].IsNoiseSubstituted ? (byte)0 : (byte)1;
            }

            _noise.Add(sizes);
        }

        if (!isValid)
        {
            return;
        }

        int highest = 0;
        for (int i = 0; i < count; i++)
        {
            int hertz = notes[i].Hertz;
            if (hertz > 0)
            {
                _reach.Add((frameIndex, hertz));
                _counts[hertz] = _counts.GetValueOrDefault(hertz) + 1;
                highest = Math.Max(highest, hertz);
            }
        }

        if (highest > 0)
        {
            long second = frameIndex * (long)_p.FrameLength / _p.SampleRate;
            if (_seconds.Count > 0 && _seconds[_seconds.Count - 1].Second == second)
            {
                _seconds.SetLast((second, Math.Max(_seconds[_seconds.Count - 1].Hertz, highest)));
            }
            else
            {
                _seconds.Add((second, highest));
            }
        }
    }

    // The node of the coded bandwidth, with its lowest, most common and highest value; cover makes a node over the stream's bytes.
    public Node? AddBandwidthTo(Node statistics, Func<string, NodeValue?, string, string?, Node> cover)
    {
        if (_counts.Count == 0)
        {
            return null;
        }

        Node node = statistics.Add(cover("Coded Bandwidth", null, StdText.StatCodedBandwidth, Topics.CodedBandwidth));
        node.Add(cover("Lowest", NodeValue.Integer(_counts.Keys.First(), "Hz"), StdText.BandwidthLowest, Topics.CodedBandwidth));
        node.Add(cover("Most Common", NodeValue.Integer(_counts.MaxBy(c => c.Value).Key, "Hz"), StdText.BandwidthMostCommon, Topics.CodedBandwidth));
        node.Add(cover("Highest", NodeValue.Integer(_counts.Keys.Last(), "Hz"), StdText.BandwidthHighest, Topics.CodedBandwidth));
        return node;
    }

    // "Coded Bandwidth" for each block that has one, at its frame's offset, and "Coded Bandwidth Over Time".
    public void AddSeriesTo(List<Series> series, double[] frameOffsets, Node? bandwidthNode, CancellationToken cancellation)
    {
        if (_reach.Count == 0 || bandwidthNode is null)
        {
            Release();
            return;
        }

        var x = new double[_reach.Count];
        var hertz = new double[_reach.Count];
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            x[i] = frameOffsets[_reach[i].Frame];
            hertz[i] = _reach[i].Hertz;
        }

        series.Add(new Series("Coded Bandwidth", "Hz", SeriesAxis.FrameOffset, x, hertz)
        {
            Description = StdText.CodedBandwidthSeries,
            Concept = GraphConcept.CodedBandwidth,
            Quantity = "Coded Bandwidth",
        });
        var secondX = new double[_seconds.Count];
        var secondHertz = new double[_seconds.Count];
        for (int i = 0; i < secondX.Length; i++)
        {
            secondX[i] = _seconds[i].Second;
            secondHertz[i] = _seconds[i].Hertz;
        }

        series.Add(new Series("Coded Bandwidth Over Time", "Hz", SeriesAxis.Seconds, secondX, secondHertz, bandwidthNode)
        {
            Description = StdText.CodedBandwidthOverTimeSeries,
            Concept = GraphConcept.CodedBandwidth,
            Quantity = "Coded Bandwidth",
        });
        Release();
    }

    // The per-block records are copied into the series, and the nodes read the stream afterwards: nothing here needs to stay.
    private void Release()
    {
        _reach = new();
        _seconds = new();
        _counts = [];
    }

    // The category series, anchored to their tally nodes; one with no node is left out.
    public void AddCategoriesTo(List<CategorySeries> categories, Series bitrate, Node statistics)
    {
        if (statistics.Children.FirstOrDefault(c => c.Name == "Block Sizes") is { } sizes && _sizes.Count == bitrate.X.Length)
        {
            categories.Add(_sizes.Build(bitrate.X, bitrate, sizes, StdText.BlockSizesSeries, Topics.BlockSwitching));
        }

        if (_noise is not null && statistics.Children.FirstOrDefault(c => c.Name == "Noise-Substituted Blocks") is { } noise && _noise.Count == bitrate.X.Length)
        {
            categories.Add(_noise.Build(bitrate.X, bitrate, noise, StdText.NoiseSubstitutedBlocksSeries, Topics.NoiseSubstitution));
        }
    }
}
