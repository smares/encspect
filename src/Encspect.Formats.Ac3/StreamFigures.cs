using Encspect.Core;

namespace Encspect.Formats.Ac3;

// The figures of a stream's frames as they are read: bytes, samples, the smallest and largest, and
// per frame its offset and bitrate for the graphs. Time is counted in samples, so no rounding adds
// up. A frame's point packs its offset (44 bits), length (17 bits) and blocks (3 bits) into 8
// bytes; its bitrate follows from them.
internal sealed class StreamFigures(int sampleRate)
{
    private readonly ChunkedList<ulong> _points = new();
    private readonly List<(long Bits, long Samples)> _seconds = [];

    public int SampleRate => sampleRate;

    public int Count => _points.Count;

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
        // The caller keeps a program frame to 9 syncframes (Program.Add), 36 KiB at most, and a frame's samples are blocks of 256; the
        // clamps keep a point valid whatever is passed.
        offset = Math.Clamp(offset, 0, (1L << 44) - 1);
        length = Math.Clamp(length, 0, (1 << 17) - 1);
        samples = Math.Clamp(samples / 256, 0, 7) * 256;
        double bitrate = Bitrate(length, samples);

        _points.Add(((ulong)offset << 20) | ((ulong)(uint)length << 3) | (uint)(samples / 256));
        if (sampleRate > 0)
        {
            // A unit is shorter than a second, so every second has one that starts in it.
            int second = (int)(Samples / sampleRate);
            while (_seconds.Count <= second)
            {
                _seconds.Add(default);
            }

            (long bits, long counted) = _seconds[second];
            _seconds[second] = (bits + (length * 8L), counted + samples);
        }

        Bytes += length;
        Samples += samples;
        Smallest = Math.Min(Smallest, length);
        Largest = Math.Max(Largest, length);
        MinimumBitrate = Math.Min(MinimumBitrate, bitrate);
        MaximumBitrate = Math.Max(MaximumBitrate, bitrate);
    }

    // Per unit, its bitrate at its offset; per second of audio, the bits of the units that start in
    // it over their duration, rounded half to even. Only these arrays grow with the number of units.
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

            ulong point = _points[i];
            offsets[i] = point >> 20;
            bitrates[i] = Bitrate((int)((point >> 3) & 0x1FFFF), (int)(point & 7) * 256);
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

    private double Bitrate(int length, int samples) => samples > 0 && sampleRate > 0 ? length * 8.0 * sampleRate / samples / 1000 : 0;
}
