using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// The figures of a stream's runs of access units as they are read: bits, coded samples, the smallest and
// largest, and per set its offset and bitrate for the graphs. Time is counted in samples, so no
// rounding adds up; a stream that changes its sampling rate counts each stretch at its own rate.
internal sealed class StreamFigures(long firstRate)
{
    private readonly long _firstRate = firstRate;
    private const int BinWidth = 32;
    private const int LastBin = 1000;

    private readonly ChunkedList<Point> _points = new();
    // Only the seconds a unit starts in, in order: a block may declare far more samples than its bytes
    // hold, so the seconds it spans are not kept.
    private readonly ChunkedList<(long Second, long Bits, long Samples, int Rate)> _seconds = new();
    // The index of the first run that starts in each of those seconds.
    private readonly ChunkedList<int> _firstRuns = new();
    private readonly SortedDictionary<int, int> _bins = [];
    private readonly Dictionary<int, long> _samplesByRate = [];
    private long _rate = firstRate;
    private long _stretchSamples;
    private long _stretchStart;
    private long _counted;

    // The stereo codings of the runs, each coding of a run with the number of its units as the weight; created at the first run that has one.
    private CategorySeriesBuilder? _stereo;
    private readonly long[] _stereoCounts = new long[StereoCodings.Categories.Length];

    public int Count => _points.Count;

    public long Units { get; private set; }

    public long Bits { get; private set; }

    public long CodedSamples { get; private set; }

    public long SmallestBits { get; private set; } = long.MaxValue;

    public long LargestBits { get; private set; }

    public double MinimumBitrate { get; private set; } = double.MaxValue;

    public double MaximumBitrate { get; private set; }

    // In kbps, over the coded samples; null without them.
    public double? AverageBitrate => CodedSamples <= 0 || _firstRate <= 0 ? null
        : _samplesByRate.Count <= 1 ? Bits * (double)_firstRate / CodedSamples / 1000
        : Bits / _samplesByRate.Sum(r => r.Value / (double)r.Key) / 1000;

    // inSeries false leaves the unit out of the per-unit series: a set of thousands of blocks would
    // cost a point for each. rate: the sampling rate of the samples, where it is not the stream's first.
    // isPartial: a run cut short by the end of the stream; it is in the series and the totals, but only
    // counts for the extremes and the distribution where no whole run came before it, since a run of one
    // unit is not what the bitrate of a run means.
    public void Add(long offset, long bits, long samples, bool inSeries = true, int rate = 0, bool isPartial = false, long firstFrame = 0, long coded = 0, ReadOnlySpan<int> codings = default, int filterOrder = -1)
    {
        long runRate = rate > 0 ? rate : _firstRate;
        double bitrate = samples > 0 && runRate > 0 ? bits * (double)runRate / samples / 1000 : 0;
        if (inSeries)
        {
            AddStereo(codings, _points.Count);
            _orders.Add(filterOrder < 0 ? (byte)255 : (byte)filterOrder);
            _points.Add(new Point(offset, bitrate, firstFrame, coded));
        }

        if (runRate != _rate && runRate > 0)
        {
            _stretchStart += _stretchSamples / Math.Max(1, _rate);
            _stretchSamples = 0;
            _rate = runRate;
        }

        if (runRate > 0)
        {
            long second = _stretchStart + (_stretchSamples / runRate);
            if (_seconds.Count > 0 && _seconds[_seconds.Count - 1].Second == second && _seconds[_seconds.Count - 1].Rate == (int)runRate)
            {
                (long _, long b, long counted, int _) = _seconds[_seconds.Count - 1];
                _seconds.SetLast((second, b + bits, counted + samples, (int)runRate));
            }
            else
            {
                _seconds.Add((second, bits, samples, (int)runRate));
                _firstRuns.Add(Math.Max(0, _points.Count - 1));
            }

            _stretchSamples += samples;
            _samplesByRate[(int)runRate] = _samplesByRate.GetValueOrDefault((int)runRate) + samples;
        }

        bool isExtreme = !isPartial || _counted == 0;
        Units++;
        Bits += bits;
        CodedSamples += samples;
        if (!isExtreme)
        {
            return;
        }

        _counted++;
        int bin = (int)Math.Min(LastBin, Math.Floor(bitrate / BinWidth));
        _bins[bin] = _bins.GetValueOrDefault(bin) + 1;
        SmallestBits = Math.Min(SmallestBits, bits);
        LargestBits = Math.Max(LargestBits, bits);
        MinimumBitrate = Math.Min(MinimumBitrate, bitrate);
        MaximumBitrate = Math.Max(MaximumBitrate, bitrate);
    }

    // The codings of one run, in code order with their unit counts as weights; runs before the first one that has a coding get empty values.
    private void AddStereo(ReadOnlySpan<int> codings, int run)
    {
        Span<byte> codes = stackalloc byte[StereoCodings.Categories.Length];
        Span<byte> weights = stackalloc byte[StereoCodings.Categories.Length];
        int count = 0;
        for (int code = 0; code < codings.Length; code++)
        {
            if (codings[code] > 0)
            {
                codes[count] = (byte)code;
                weights[count++] = (byte)codings[code];
                _stereoCounts[code] += codings[code];
            }
        }

        if (_stereo is null)
        {
            if (count == 0)
            {
                return;
            }

            _stereo = new CategorySeriesBuilder("Stereo Coding", StereoCodings.Categories, "access unit", concept: GraphConcept.StereoCoding);
            for (int i = 0; i < run; i++)
            {
                _stereo.Add([]);
            }
        }

        _stereo.Add(codes[..count], weights[..count]);
    }

    // The units whose two-channel presentation was read, by coding.
    public ReadOnlySpan<long> StereoCounts => _stereoCounts;

    public long StereoUnits => _stereoCounts.Sum();

    // The seconds "Bitrate Over Time" is counted over, with the first run that starts in each; null without them.
    private TimeIndex? Time
    {
        get
        {
            if (_seconds.Count == 0)
            {
                return null;
            }

            var starts = new double[_seconds.Count];
            var first = new int[_seconds.Count];
            for (int i = 0; i < starts.Length; i++)
            {
                starts[i] = _seconds[i].Second;
                first[i] = _firstRuns[i];
            }

            return new TimeIndex(starts, first);
        }
    }

    // The "Stereo Coding" series of the runs, sharing the x and the time index of the bitrate series; null where no unit had a two-channel presentation.
    public CategorySeries? StereoSeries(Series bitrate, Node anchor) =>
        _stereo?.Build(bitrate.X, bitrate, anchor, Explain.StereoCodingSeries, Topics.Rematrixing, Time);

    // Samples that a lost unit declares after the last run was closed: the run keeps the bitrate it was counted with and codes them.
    public void AddCoded(long samples)
    {
        if (_points.Count > 0)
        {
            Point last = _points[_points.Count - 1];
            _points.SetLast(last with { Samples = last.Samples + samples });
        }
    }

    // Per unit, its bitrate at its offset; per second of audio that a unit starts in, the bits of the
    // units that start in it over their duration, rounded half to even.
    public List<Series> Series(Node? anchor, long frameCount, CancellationToken cancellation)
    {
        if (Units == 0)
        {
            return [];
        }

        var series = new List<Series>();
        var offsets = new double[Count];
        var bitrates = new double[Count];
        var firstFrames = new long[Count];
        var runSamples = new long[Count];
        for (int i = 0; i < Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            offsets[i] = _points[i].Offset;
            bitrates[i] = _points[i].Bitrate;
            firstFrames[i] = _points[i].FirstFrame;
            runSamples[i] = _points[i].Samples;
        }

        if (Count > 0)
        {
            series.Add(new("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates) { Description = Explain.BitrateSeries, Runs = new FrameRuns(firstFrames, frameCount, runSamples) });
        }

        if (_seconds.Count > 0)
        {
            var starts = new double[_seconds.Count];
            var values = new double[_seconds.Count];
            for (int i = 0; i < _seconds.Count; i++)
            {
                if (i % 4096 == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                (long second, long bits, long samples, int rate) = _seconds[i];
                starts[i] = second;
                values[i] = samples > 0 ? Math.Round(bits * (double)rate / samples / 1000, MidpointRounding.ToEven) : 0;
            }

            series.Add(new("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, values, anchor) { Description = Explain.BitrateOverTimeSeries });
        }

        return series;
    }

    // The bitrate figures, the size range and the distribution; cover makes a node over the stream's
    // bytes, with its description.
    public void AddTo(Node statistics, Func<string, NodeValue?, string, Node> cover, string unitName, string unitPlural, bool inBytes, out Node bitrate)
    {
        bitrate = statistics.Add(cover("Average Bitrate", AverageBitrate is { } average ? NodeValue.Decimal(average, 1, "kbps") : "none", Explain.AverageBitrate));
        if (Units == 0)
        {
            return;
        }

        statistics.Add(cover("Bitrate Mode", _counted == 1 ? "unknown" : MaximumBitrate > MinimumBitrate ? "variable" : "constant", Explain.BitrateMode));
        statistics.Add(cover($"Smallest {unitName}", inBytes ? NodeValue.Integer(SmallestBits / 8, "bytes") : NodeValue.Integer(SmallestBits, "bits"), Explain.Smallest));
        statistics.Add(cover($"Largest {unitName}", inBytes ? NodeValue.Integer(LargestBits / 8, "bytes") : NodeValue.Integer(LargestBits, "bits"), Explain.Largest));
        statistics.Add(cover("Minimum Bitrate", NodeValue.Decimal(MinimumBitrate, 1, "kbps"), Explain.MinimumBitrate));
        statistics.Add(cover("Maximum Bitrate", NodeValue.Decimal(MaximumBitrate, 1, "kbps"), Explain.MaximumBitrate));
        Node distribution = statistics.Add(cover("Bitrate Distribution", null, Explain.Distribution));
        foreach ((int bin, int count) in _bins)
        {
            string range = bin == LastBin ? $"{bin * BinWidth} kbps and more" : $"{bin * BinWidth} to {(bin * BinWidth) + BinWidth - 1} kbps";
            distribution.Add(cover(range, $"{Counted(count, unitName, unitPlural)} ({Share(count, _counted)})", Explain.DistributionBin));
        }
    }

    public static string Counted(long count, string unitName, string unitPlural) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? unitName.ToLowerInvariant() : unitPlural);

    // A share in percent with one decimal, rounded half to even.
    public static string Share(long count, long total) =>
        total > 0 ? (Math.Round(count * 1000.0 / total, MidpointRounding.ToEven) / 10).ToString("F1", CultureInfo.InvariantCulture) + "%" : "0.0%";

    // Per run, the highest filter order of its units, 255 for a run without a coded channel in a read unit.
    private readonly ChunkedList<byte> _orders = new();

    // The highest prediction filter order of each run, at the runs that have one (sharing the bitrate series' x where all do).
    public Series? OrderSeries(Series bitrate)
    {
        if (_orders.Count != bitrate.X.Length)
        {
            return null;
        }

        int valued = 0;
        for (int i = 0; i < _orders.Count; i++)
        {
            valued += _orders[i] != 255 ? 1 : 0;
        }

        if (valued == 0)
        {
            return null;
        }

        ReadOnlySpan<double> offsets = bitrate.X.Span;
        double[] x = valued == _orders.Count ? [] : new double[valued];
        var y = new double[valued];
        for (int i = 0, n = 0; i < _orders.Count; i++)
        {
            if (_orders[i] != 255)
            {
                if (x.Length > 0)
                {
                    x[n] = offsets[i];
                }

                y[n++] = _orders[i];
            }
        }

        return new Series("Highest Predictor Order", "order", SeriesAxis.FrameOffset, x.Length > 0 ? x : bitrate.X, y)
        {
            Description = Explain.HighestOrderSeries,
            Concept = GraphConcept.Prediction,
            Quantity = "Highest Predictor Order",
        };
    }

    private readonly record struct Point(long Offset, double Bitrate, long FirstFrame, long Samples);
}
