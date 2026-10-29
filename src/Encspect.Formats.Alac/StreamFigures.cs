using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Alac;

// The figures of a stream's frames as they are read: bits, coded samples, the smallest and
// largest, and per frame its offset and bitrate for the graphs. Time is counted in samples, so no
// rounding adds up.
internal sealed class StreamFigures(long sampleRate)
{
    private const int BinWidth = 32;
    private const int LastBin = 1000;

    private readonly ChunkedList<Point> _points = new();
    // Only the seconds a frame starts in, in order: a header may declare far more samples than the frames' bytes
    // hold, so the seconds a frame spans are not kept.
    private readonly ChunkedList<(long Second, long Bits, long Samples)> _seconds = new();
    private readonly SortedDictionary<int, int> _bins = [];

    // The first unit that starts in each of the seconds of _seconds, for the time index of the category series.
    private readonly ChunkedList<int> _firstUnits = new();

    public int Count => _points.Count;

    public long Units { get; private set; }

    public long Bits { get; private set; }

    public long CodedSamples { get; private set; }

    public long SmallestBits { get; private set; } = long.MaxValue;

    public long LargestBits { get; private set; }

    public double MinimumBitrate { get; private set; } = double.MaxValue;

    public double MaximumBitrate { get; private set; }

    // In kbps, over the coded samples; null without them.
    public double? AverageBitrate => CodedSamples > 0 && sampleRate > 0 ? Bits * (double)sampleRate / CodedSamples / 1000 : null;

    public void Add(long offset, long bits, long samples)
    {
        double bitrate = samples > 0 && sampleRate > 0 ? bits * (double)sampleRate / samples / 1000 : 0;
        _points.Add(new Point(offset, bitrate));

        Units++;
        if (sampleRate > 0)
        {
            long second = CodedSamples / sampleRate;
            if (_seconds.Count > 0 && _seconds[_seconds.Count - 1].Second == second)
            {
                (long _, long b, long counted) = _seconds[_seconds.Count - 1];
                _seconds.SetLast((second, b + bits, counted + samples));
            }
            else
            {
                _seconds.Add((second, bits, samples));
                _firstUnits.Add(_points.Count - 1);
            }
        }

        int bin = (int)Math.Min(LastBin, Math.Floor(bitrate / BinWidth));
        _bins[bin] = _bins.GetValueOrDefault(bin) + 1;
        Bits += bits;
        CodedSamples += samples;
        SmallestBits = Math.Min(SmallestBits, bits);
        LargestBits = Math.Max(LargestBits, bits);
        MinimumBitrate = Math.Min(MinimumBitrate, bitrate);
        MaximumBitrate = Math.Max(MaximumBitrate, bitrate);
    }

    // The intervals "Bitrate Over Time" is counted over, with the first unit that starts in each; null without them.
    public TimeIndex? Time
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
                first[i] = _firstUnits[i];
            }

            return new TimeIndex(starts, first);
        }
    }

    // Per unit, its bitrate at its offset; per second of audio that a unit starts in, the bits of the
    // units that start in it over their duration, rounded half to even.
    public List<Series> Series(Node? anchor, CancellationToken cancellation)
    {
        if (Units == 0)
        {
            return [];
        }

        var series = new List<Series>();
        var offsets = new double[Count];
        var bitrates = new double[Count];
        for (int i = 0; i < Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            offsets[i] = _points[i].Offset;
            bitrates[i] = _points[i].Bitrate;
        }

        if (Count > 0)
        {
            series.Add(new("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates) { Description = Explain.BitrateSeries, Concept = GraphConcept.BitDistribution, Quantity = "Bitrate" });
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

                (long second, long bits, long samples) = _seconds[i];
                starts[i] = second;
                values[i] = samples > 0 ? Math.Round(bits * (double)sampleRate / samples / 1000, MidpointRounding.ToEven) : 0;
            }

            series.Add(new("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, values, anchor)
            {
                Description = Explain.BitrateOverTimeSeries,
                Concept = GraphConcept.BitDistribution,
                Quantity = "Bitrate",
            });
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

        statistics.Add(cover("Bitrate Mode", Units == 1 ? "unknown" : MaximumBitrate > MinimumBitrate ? "variable" : "constant", Explain.BitrateMode));
        statistics.Add(cover($"Smallest {unitName}", inBytes ? NodeValue.Integer(SmallestBits / 8, "bytes") : NodeValue.Integer(SmallestBits, "bits"), Explain.Smallest));
        statistics.Add(cover($"Largest {unitName}", inBytes ? NodeValue.Integer(LargestBits / 8, "bytes") : NodeValue.Integer(LargestBits, "bits"), Explain.Largest));
        statistics.Add(cover("Minimum Bitrate", NodeValue.Decimal(MinimumBitrate, 1, "kbps"), Explain.MinimumBitrate));
        statistics.Add(cover("Maximum Bitrate", NodeValue.Decimal(MaximumBitrate, 1, "kbps"), Explain.MaximumBitrate));
        Node distribution = statistics.Add(cover("Bitrate Distribution", null, Explain.Distribution));
        foreach ((int bin, int count) in _bins)
        {
            string range = bin == LastBin ? $"{bin * BinWidth} kbps and more" : $"{bin * BinWidth} to {(bin * BinWidth) + BinWidth - 1} kbps";
            distribution.Add(cover(range, $"{Counted(count, unitName, unitPlural)} ({Share(count, Units)})", Explain.DistributionBin));
        }
    }

    public static string Counted(long count, string unitName, string unitPlural) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? unitName.ToLowerInvariant() : unitPlural);

    // A share in percent with one decimal, rounded half to even.
    public static string Share(long count, long total) =>
        total > 0 ? (Math.Round(count * 1000.0 / total, MidpointRounding.ToEven) / 10).ToString("F1", CultureInfo.InvariantCulture) + "%" : "0.0%";

    private readonly record struct Point(long Offset, double Bitrate);
}
