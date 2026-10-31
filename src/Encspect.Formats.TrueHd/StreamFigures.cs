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
    private readonly SortedDictionary<int, int> _bins = [];
    private readonly Dictionary<int, long> _samplesByRate = [];
    private long _rate = firstRate;
    private long _stretchSamples;
    private long _stretchStart;
    private long _counted;

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
    public void Add(long offset, long bits, long samples, bool inSeries = true, int rate = 0, bool isPartial = false, long firstFrame = 0)
    {
        long runRate = rate > 0 ? rate : _firstRate;
        double bitrate = samples > 0 && runRate > 0 ? bits * (double)runRate / samples / 1000 : 0;
        if (inSeries)
        {
            _points.Add(new Point(offset, bitrate, firstFrame));
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
        for (int i = 0; i < Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            offsets[i] = _points[i].Offset;
            bitrates[i] = _points[i].Bitrate;
            firstFrames[i] = _points[i].FirstFrame;
        }

        if (Count > 0)
        {
            series.Add(new("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates) { Description = Explain.BitrateSeries, Runs = new FrameRuns(firstFrames, frameCount) });
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

    private readonly record struct Point(long Offset, double Bitrate, long FirstFrame);
}
