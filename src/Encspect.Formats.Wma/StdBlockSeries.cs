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
    private readonly CategorySeriesBuilder _reuse = new("Blocks Reusing Exponents", ReuseCategories, "block");
    private ChunkedList<(int Frame, int Value)> _gain = new();
    private ChunkedList<(long Second, int Value)> _gainSeconds = new();
    private ChunkedList<(int Frame, int Value)> _levels = new();
    private ChunkedList<(int Frame, int Tenths)> _escapes = new();
    private int _lowestLevel = int.MaxValue;
    private int _highestLevel = int.MinValue;
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

    private static readonly Category[] ReuseCategories =
    [
        new("With reused exponents", StdText.CategoryWithReusedExponents) { Topic = Topics.WmaExponents },
        new("Without reused exponents", StdText.CategoryWithoutReusedExponents) { Topic = Topics.WmaExponents },
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

        for (int i = 0; i < count; i++)
        {
            sizes[i] = notes[i].IsReusingExponents ? (byte)0 : (byte)1;
        }

        _reuse.Add(sizes);

        if (!isValid)
        {
            return;
        }

        AddValues(frameIndex, notes);
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

    // The figures of the blocks that have them: the total gain in decibels (the gain of the decoding description's 10^(t/20)), the mean noise level of
    // the flagged bands over the block's coded channels, and the escapes among the non-zero coefficients in tenths of a percent.
    private void AddValues(int frameIndex, StdBlockNotes notes)
    {
        long second = frameIndex * (long)_p.FrameLength / _p.SampleRate;
        int highestGain = 0;
        for (int i = 0; i < notes.Count; i++)
        {
            StdBlockNotes.Note note = notes[i];
            if (note.Gain > 0)
            {
                int gain = (int)Math.Min(note.Gain, int.MaxValue);
                _gain.Add((frameIndex, gain));
                highestGain = Math.Max(highestGain, gain);
            }

            if (note.Levels > 0)
            {
                // The mean to the nearest decibel, halves up.
                int level = (int)Math.Floor((2.0 * note.LevelSum + note.Levels) / (2.0 * note.Levels));
                _levels.Add((frameIndex, level));
                _lowestLevel = Math.Min(_lowestLevel, level);
                _highestLevel = Math.Max(_highestLevel, level);
            }

            if (note.NonZero > 0)
            {
                _escapes.Add((frameIndex, (int)RoundHalfEven(1000L * note.Escapes, note.NonZero)));
            }
        }

        if (highestGain > 0)
        {
            if (_gainSeconds.Count > 0 && _gainSeconds[_gainSeconds.Count - 1].Second == second)
            {
                _gainSeconds.SetLast((second, Math.Max(_gainSeconds[_gainSeconds.Count - 1].Value, highestGain)));
            }
            else
            {
                _gainSeconds.Add((second, highestGain));
            }
        }
    }

    // The node of the noise level, with the lowest and highest block value.
    public Node? AddNoiseLevelTo(Node statistics, Func<string, NodeValue?, string, string?, Node> cover)
    {
        if (_levels.Count == 0)
        {
            return null;
        }

        Node node = statistics.Add(cover("Noise Level", null, StdText.StatNoiseLevel, Topics.NoiseSubstitution));
        node.Add(cover("Lowest", NodeValue.Integer(_lowestLevel, "dB"), StdText.NoiseLevelLowest, Topics.NoiseSubstitution));
        node.Add(cover("Highest", NodeValue.Integer(_highestLevel, "dB"), StdText.NoiseLevelHighest, Topics.NoiseSubstitution));
        return node;
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
    public void AddSeriesTo(List<Series> series, double[] frameOffsets, Node statistics, CancellationToken cancellation)
    {
        if (statistics.Children.FirstOrDefault(c => c.Name == "Coded Bandwidth") is { } bandwidthNode)
        {
            AddBandwidthSeries(series, frameOffsets, bandwidthNode, cancellation);
        }

        AddGainTo(series, frameOffsets, statistics.Children.FirstOrDefault(c => c.Name == "Largest Total Gain"), cancellation);
        if (statistics.Children.FirstOrDefault(c => c.Name == "Noise Level") is { } levelNode)
        {
            AddOwn(series, frameOffsets, "Noise Level", "dB", _levels, 1, StdText.NoiseLevelSeries, levelNode, cancellation);
        }

        if (statistics.Children.FirstOrDefault(c => c.Name == "Escape-Coded Coefficients") is { } escapeNode)
        {
            AddOwn(series, frameOffsets, "Escape-Coded Coefficients", "%", _escapes, 10, StdText.EscapesSeries, escapeNode, cancellation);
        }

        Release();
    }

    private void AddBandwidthSeries(List<Series> series, double[] frameOffsets, Node bandwidthNode, CancellationToken cancellation)
    {
        if (_reach.Count == 0)
        {
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
    }

    // The quotient to the nearest integer, halves to even, as the series of the same name of the other formats round their percentages.
    private static long RoundHalfEven(long numerator, long denominator)
    {
        long quotient = Math.DivRem(numerator, denominator, out long rest);
        return rest * 2 > denominator || (rest * 2 == denominator && quotient % 2 != 0) ? quotient + 1 : quotient;
    }

    // "Total Gain" for each block that has one, at its frame's offset, and "Total Gain Over Time", each second's highest.
    private void AddGainTo(List<Series> series, double[] frameOffsets, Node? anchor, CancellationToken cancellation)
    {
        if (_gain.Count == 0)
        {
            return;
        }

        AddOwn(series, frameOffsets, "Total Gain", "dB", _gain, 1, StdText.TotalGainSeries, anchor, cancellation);
        var x = new double[_gainSeconds.Count];
        var values = new double[_gainSeconds.Count];
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = _gainSeconds[i].Second;
            values[i] = _gainSeconds[i].Value;
        }

        series.Add(new Series("Total Gain Over Time", "dB", SeriesAxis.Seconds, x, values, anchor) { Description = StdText.TotalGainOverTimeSeries, Quantity = "Total Gain" });
    }

    // A numeric series with a value for some blocks, at their frames' offsets, in units of 1 / divisor.
    private static void AddOwn(List<Series> series, double[] frameOffsets, string name, string unit, ChunkedList<(int Frame, int Value)> list, int divisor, string description, Node? anchor, CancellationToken cancellation)
    {
        if (list.Count == 0)
        {
            return;
        }

        var x = new double[list.Count];
        var values = new double[list.Count];
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            x[i] = frameOffsets[list[i].Frame];
            values[i] = (double)list[i].Value / divisor;
        }

        series.Add(new Series(name, unit, SeriesAxis.FrameOffset, x, values, anchor) { Description = description, Quantity = name });
    }

    // The per-block records are copied into the series, and the nodes read the stream afterwards: nothing here needs to stay.
    private void Release()
    {
        _reach = new();
        _seconds = new();
        _counts = [];
        _gain = new();
        _gainSeconds = new();
        _levels = new();
        _escapes = new();
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

        if (statistics.Children.FirstOrDefault(c => c.Name == "Blocks Reusing Exponents") is { } reuse && _reuse.Count == bitrate.X.Length)
        {
            categories.Add(_reuse.Build(bitrate.X, bitrate, reuse, StdText.BlocksReusingExponentsSeries, Topics.WmaExponents));
        }
    }
}
