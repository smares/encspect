using Encspect.Core;

namespace Encspect.Formats.Aac;

// The figures of a stream's frames or access units as they are read: bytes, samples, the smallest
// and largest, and per unit its offset and bitrate for the graphs. Every unit lasts samples /
// sampleRate seconds, AAC's fixed frame length; time is counted in samples, so no rounding adds up.
internal sealed class StreamFigures(int sampleRate)
{
    private readonly ChunkedList<Point> _points = new();
    private readonly List<(long Bits, long Samples)> _seconds = [];

    // The graph of bitrate over time keeps at most this many entries, whatever the sample rate and the
    // length: when a stream outgrows it, neighboring entries merge and each covers twice the time. A
    // stated rate of 1 Hz would otherwise add a thousand entries for every access unit.
    public const int MaxSeconds = 1 << 17;

    private long _spanSeconds = 1;

    public int SampleRate => sampleRate;

    // The units counted without a record of their own (AddRun).
    public long Unrecorded { get; private set; }

    public long Count => _points.Count + Unrecorded;

    public long Bytes { get; private set; }

    public long Samples { get; private set; }

    public int Smallest { get; private set; } = int.MaxValue;

    public int Largest { get; private set; }

    public double MinimumBitrate { get; private set; } = double.MaxValue;

    public double MaximumBitrate { get; private set; }

    public double Duration => sampleRate > 0 ? Samples / (double)sampleRate : 0;

    // In kbps; null without a duration.
    public double? AverageBitrate => Samples > 0 && sampleRate > 0 ? Bytes * 8.0 * sampleRate / Samples / 1000 : null;

    public void Add(long offset, int length, int samples)
    {
        double bitrate = samples > 0 && sampleRate > 0 ? length * 8.0 * sampleRate / samples / 1000 : 0;
        _points.Add(new Point(offset, bitrate));
        if (sampleRate > 0)
        {
            long second = Samples / (sampleRate * _spanSeconds);
            while (second >= MaxSeconds)
            {
                Merge();
                second = Samples / (sampleRate * _spanSeconds);
            }

            while (_seconds.Count <= second)
            {
                _seconds.Add(default);
            }

            (long bits, long counted) = _seconds[(int)second];
            _seconds[(int)second] = (bits + (length * 8L), counted + samples);
        }

        Bytes += length;
        Samples += samples;
        Smallest = Math.Min(Smallest, length);
        Largest = Math.Max(Largest, length);
        MinimumBitrate = Math.Min(MinimumBitrate, bitrate);
        MaximumBitrate = Math.Max(MaximumBitrate, bitrate);
    }

    private void Merge()
    {
        for (int i = 0; i * 2 < _seconds.Count; i++)
        {
            (long bits, long samples) = _seconds[i * 2];
            if (i * 2 + 1 < _seconds.Count)
            {
                bits += _seconds[i * 2 + 1].Bits;
                samples += _seconds[i * 2 + 1].Samples;
            }

            _seconds[i] = (bits, samples);
        }

        _seconds.RemoveRange((_seconds.Count + 1) / 2, _seconds.Count - ((_seconds.Count + 1) / 2));
        _spanSeconds *= 2;
    }

    // Units of a stream's end that are only counted: past the limit on records, the bytes, samples and bitrate extremes of a run of count
    // units join the figures, but the graphs do not cover them. Call it after the last Add.
    public void AddRun(long count, long bytes, long samples, int smallest, int largest, double minimumBitrate, double maximumBitrate)
    {
        Unrecorded += count;
        Bytes += bytes;
        Samples += samples;
        Smallest = Math.Min(Smallest, smallest);
        Largest = Math.Max(Largest, largest);
        MinimumBitrate = Math.Min(MinimumBitrate, minimumBitrate);
        MaximumBitrate = Math.Max(MaximumBitrate, maximumBitrate);
    }

    // Per unit, its bitrate at its offset; per second of audio, the bits of the units that start in
    // it over their duration, rounded half to even. Only these arrays grow with the number of units.
    public List<Series> Series(Node? anchor, CancellationToken cancellation)
    {
        int recorded = _points.Count;
        if (recorded == 0)
        {
            return [];
        }

        var offsets = new double[recorded];
        var bitrates = new double[recorded];
        for (int i = 0; i < recorded; i++)
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
            double[] starts = Enumerable.Range(0, _seconds.Count).Select(s => (double)s * _spanSeconds).ToArray();
            double[] values = _seconds.Select(s => s.Samples > 0 ? Math.Round(s.Bits * (double)sampleRate / s.Samples / 1000, MidpointRounding.ToEven) : 0).ToArray();
            series.Add(new("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, values, anchor) { Description = Explain.BitrateOverTimeSeries });
        }

        return series;
    }

    // The statistics node: counts, duration and bitrates, with the extra figures a caller adds.
    // cover makes a node over the stream's bytes, with its description.
    public Node Statistics(Func<string, NodeValue?, NodeKind, string, Node> cover, string unitName, string unitPlural, out Node bitrate)
    {
        Node statistics = cover("Statistics", null, NodeKind.Statistics, Explain.Statistics);
        statistics.Add(cover(unitPlural, NodeValue.Integer(Count, unitPlural.ToLowerInvariant(), unitName.ToLowerInvariant()), NodeKind.Detail, Explain.StatUnits));
        statistics.Add(cover("Samples", NodeValue.Integer(Samples, "samples", "sample"), NodeKind.Detail, Explain.StatSamples));
        statistics.Add(cover("Duration", NodeValue.Decimal(Duration, 3, "s"), NodeKind.Detail, Explain.StatDuration));
        bitrate = statistics.Add(cover("Average Bitrate", AverageBitrate is { } average ? NodeValue.Decimal(average, 1, "kbps") : "none", NodeKind.Detail, Explain.AverageBitrate));
        if (Count > 0)
        {
            statistics.Add(cover($"Smallest {unitName}", NodeValue.Integer(Smallest, "bytes"), NodeKind.Detail, Explain.Smallest));
            statistics.Add(cover($"Largest {unitName}", NodeValue.Integer(Largest, "bytes"), NodeKind.Detail, Explain.Largest));
            statistics.Add(cover("Minimum Bitrate", NodeValue.Decimal(MinimumBitrate, 1, "kbps"), NodeKind.Detail, Explain.MinimumBitrate));
            statistics.Add(cover("Maximum Bitrate", NodeValue.Decimal(MaximumBitrate, 1, "kbps"), NodeKind.Detail, Explain.MaximumBitrate));
        }

        return statistics;
    }

    private readonly record struct Point(long Offset, double Bitrate);
}
