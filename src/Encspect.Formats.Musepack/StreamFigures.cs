using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Musepack;

// The figures of a stream's frames (SV7) or audio packets (SV8) as they are read: bits, coded
// samples, the smallest and largest, and per unit its offset and bitrate for the graphs. Time is
// counted in samples, so no rounding adds up.
internal sealed class StreamFigures(int sampleRate)
{
    private const int BinWidth = 32;

    private readonly ChunkedList<Point> _points = new();
    private readonly List<(long Bits, long Samples)> _seconds = [];
    private readonly SortedDictionary<int, int> _bins = [];

    public int Count => _points.Count;

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
        if (sampleRate > 0)
        {
            int second = (int)Math.Min(int.MaxValue - 1, CodedSamples / sampleRate);

            // A packet may last longer than a second, so seconds that none starts in stay empty.
            while (_seconds.Count <= second)
            {
                _seconds.Add(default);
            }

            (long b, long counted) = _seconds[second];
            _seconds[second] = (b + bits, counted + samples);
        }

        int bin = (int)Math.Min(int.MaxValue / 2, Math.Floor(bitrate / BinWidth));
        _bins[bin] = _bins.GetValueOrDefault(bin) + 1;
        Bits += bits;
        CodedSamples += samples;
        SmallestBits = Math.Min(SmallestBits, bits);
        LargestBits = Math.Max(LargestBits, bits);
        MinimumBitrate = Math.Min(MinimumBitrate, bitrate);
        MaximumBitrate = Math.Max(MaximumBitrate, bitrate);
    }

    // Per unit, its bitrate at its offset; per second of audio, the bits of the units that start in
    // it over their duration, rounded half to even.
    public List<Series> Series(Node? anchor, CancellationToken cancellation)
    {
        if (Count == 0)
        {
            return [];
        }

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

        var series = new List<Series> { new("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates) { Description = Explain.BitrateSeries } };
        if (_seconds.Count > 0)
        {
            double[] starts = Enumerable.Range(0, _seconds.Count).Select(s => (double)s).ToArray();
            double[] values = _seconds.Select(s => s.Samples > 0 ? Math.Round(s.Bits * (double)sampleRate / s.Samples / 1000, MidpointRounding.ToEven) : 0).ToArray();
            series.Add(new("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, values, anchor) { Description = Explain.BitrateOverTimeSeries });
        }

        return series;
    }

    // The bitrate figures, the size range and the distribution; cover makes a node over the stream's
    // bytes, with its description. sizes in bits (SV7 frames) or bytes (SV8 packets).
    public void AddTo(Node statistics, Func<string, NodeValue?, string, Node> cover, string unitName, string unitPlural, bool inBytes, out Node bitrate)
    {
        bitrate = statistics.Add(cover("Average Bitrate", AverageBitrate is { } average ? NodeValue.Decimal(average, 1, "kbps") : "none", Explain.AverageBitrate));
        if (Count == 0)
        {
            return;
        }

        statistics.Add(cover("Bitrate Mode", "variable", Explain.BitrateMode));
        statistics.Add(cover($"Smallest {unitName}", inBytes ? NodeValue.Integer(SmallestBits / 8, "bytes") : NodeValue.Integer(SmallestBits, "bits"), Explain.Smallest));
        statistics.Add(cover($"Largest {unitName}", inBytes ? NodeValue.Integer(LargestBits / 8, "bytes") : NodeValue.Integer(LargestBits, "bits"), Explain.Largest));
        statistics.Add(cover("Minimum Bitrate", NodeValue.Decimal(MinimumBitrate, 1, "kbps"), Explain.MinimumBitrate));
        statistics.Add(cover("Maximum Bitrate", NodeValue.Decimal(MaximumBitrate, 1, "kbps"), Explain.MaximumBitrate));
        Node distribution = statistics.Add(cover("Bitrate Distribution", null, Explain.Distribution));
        foreach ((int bin, int count) in _bins)
        {
            distribution.Add(cover($"{bin * BinWidth} to {(bin * BinWidth) + BinWidth - 1} kbps", $"{Counted(count, unitName, unitPlural)} ({Share(count, Count)})", Explain.DistributionBin));
        }
    }

    public static string Counted(long count, string unitName, string unitPlural) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? unitName.ToLowerInvariant() : unitPlural);

    // A share in percent with one decimal, rounded half to even.
    public static string Share(long count, long total) =>
        total > 0 ? (Math.Round(count * 1000.0 / total, MidpointRounding.ToEven) / 10).ToString("F1", CultureInfo.InvariantCulture) + "%" : "0.0%";

    private readonly record struct Point(long Offset, double Bitrate);
}
