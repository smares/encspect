using Encspect.Core;

namespace Encspect.Formats.Dts;

// The figures of a stream's frames as they are read: bytes, samples, the smallest and largest, and
// per frame its offset and bitrate for the graphs. Time is counted in samples, so no rounding adds
// up. A frame's point is its offset, its length and its samples, 16 bytes; its bitrate follows from them.
internal sealed class StreamFigures(int sampleRate)
{
    private readonly ChunkedList<Point> _points = new();
    private readonly List<(long Bits, long Samples)> _seconds = [];
    private readonly List<int> _firstUnits = [];
    private CategorySeriesBuilder? _stereo;

    // The units of each stereo coding (StereoCodings), and the units that have none.
    public int[] StereoCounts { get; } = new int[StereoCodings.Length];

    public int StereoUnits => StereoCounts.Sum();

    public static readonly Category[] StereoCodings =
    [
        new("Left/right stereo", Explain.StereoLeftRight) { Topic = Topics.MidSideStereo },
        new("Mid/side stereo", Explain.StereoMidSide) { Topic = Topics.MidSideStereo },
        new("Intensity stereo", Explain.StereoIntensity) { Topic = Topics.IntensityStereo },
        new("Mid/side and intensity stereo", Explain.StereoMidSideIntensity) { Topic = Topics.IntensityStereo },
    ];

    // The stereo coding of the unit Add is about to count (a code of StereoCodings, or null for none); call it only for a unit that gets a point.
    public void AddStereo(int? code)
    {
        _stereo ??= new CategorySeriesBuilder("Stereo Coding", StereoCodings, "frame", 1, concept: GraphConcept.StereoCoding);
        while (_stereo.Count < _points.Count)
        {
            _stereo.Add(CategorySeries.None);
        }

        if (code is { } value)
        {
            StereoCounts[value]++;
            _stereo.Add((byte)value);
        }
        else
        {
            _stereo.Add(CategorySeries.None);
        }
    }

    public IReadOnlyList<CategorySeries> Categories(Series bitrate, Node anchor)
    {
        if (_stereo is null || StereoUnits == 0)
        {
            return [];
        }

        while (_stereo.Count < _points.Count)
        {
            _stereo.Add(CategorySeries.None);
        }

        TimeIndex? time = _seconds.Count == 0 ? null : new TimeIndex(Enumerable.Range(0, _seconds.Count).Select(s => (double)s).ToArray(), _firstUnits.ToArray());
        return [_stereo.Build(bitrate.X, bitrate, anchor, Explain.StereoCodingSeries, Topics.MidSideStereo, time)];
    }

    public int SampleRate => sampleRate;

    // Units with no samples (an extension substream before any header with the duration) are counted and sized, but have no bitrate.
    private int _unmeasured;

    public int Count => _points.Count + _unmeasured;

    public long Bytes { get; private set; }

    private long _samples;
    private long _lost;

    // The samples of units that are cut: in Samples and the duration, in no unit count, size or bitrate.
    private long _cut;

    // The sampling frequency the stream delivers where it is higher than the one its frames are counted at (the core's), such as with
    // X96; Samples then counts at that frequency, and the duration and bitrates stay the same.
    public int DeliveredRate { get; set; }

    public long Samples => DeliveredRate > sampleRate && sampleRate > 0 ? (long)Math.Round((_samples + _cut) * (double)DeliveredRate / sampleRate) : _samples + _cut;

    // The declared samples of the units that are cut, counted like Samples.
    public long LostSamples => DeliveredRate > sampleRate && sampleRate > 0 ? (long)Math.Round(_lost * (double)DeliveredRate / sampleRate) : _lost;

    public int Smallest { get; private set; } = int.MaxValue;

    public int Largest { get; private set; }

    private readonly BitrateDistribution _distribution = new();

    public double MinimumBitrate { get; private set; } = double.MaxValue;

    public double MaximumBitrate { get; private set; }

    public double Duration => sampleRate > 0 ? (_samples + _cut) / (double)sampleRate : 0;

    // In kbps; null without a duration.
    public double? AverageBitrate => _samples > 0 && sampleRate > 0 ? Bytes * 8.0 * sampleRate / _samples / 1000 : null;

    // The same units counted at another sampling frequency, for a stream whose first units did not know theirs (they hold no samples).
    public StreamFigures WithSampleRate(int rate)
    {
        var figures = new StreamFigures(rate)
        {
            _unmeasured = _unmeasured,
            _lost = _lost,
            _cut = _cut,
            Smallest = Smallest,
            Largest = Largest,
        };
        for (int i = 0; i < _points.Count; i++)
        {
            Point point = _points[i];
            figures.Add(point.Offset, point.Length, point.Samples);
        }

        return figures;
    }

    public void AddLost(int samples) => _lost += samples;

    public void AddCut(int samples) => _cut += samples;

    public void Add(long offset, int length, int samples)
    {
        if (samples == 0)
        {
            _unmeasured++;
            Smallest = Math.Min(Smallest, length);
            Largest = Math.Max(Largest, length);
            return;
        }

        double bitrate = Bitrate(length, samples);
        _points.Add(new Point(offset, length, samples));
        if (sampleRate > 0)
        {
            // A unit is shorter than a second, so every second has one that starts in it.
            int second = (int)(_samples / sampleRate);
            while (_seconds.Count <= second)
            {
                _seconds.Add(default);
                _firstUnits.Add(_points.Count - 1);
            }

            (long bits, long counted) = _seconds[second];
            _seconds[second] = (bits + (length * 8L), counted + samples);
        }

        Bytes += length;
        _samples += samples;
        Smallest = Math.Min(Smallest, length);
        Largest = Math.Max(Largest, length);
        MinimumBitrate = Math.Min(MinimumBitrate, bitrate);
        MaximumBitrate = Math.Max(MaximumBitrate, bitrate);
        _distribution.Add(bitrate);
    }

    // Per unit, its bitrate at its offset; per second of audio, the bits of the units that start in
    // it over their duration, rounded half to even. Only these arrays grow with the number of units.
    public List<Series> Series(Node? anchor, CancellationToken cancellation)
    {
        if (_points.Count == 0)
        {
            return [];
        }

        var offsets = new double[_points.Count];
        var bitrates = new double[_points.Count];
        for (int i = 0; i < _points.Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            Point point = _points[i];
            offsets[i] = point.Offset;
            bitrates[i] = Bitrate(point.Length, point.Samples);
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
    public Node Statistics(Func<string, NodeValue?, NodeKind, string, Node> cover, string unitName, string unitPlural, out Node bitrate, Action<Node>? afterSamples = null)
    {
        Node statistics = cover("Statistics", null, NodeKind.Statistics, Explain.Statistics);
        statistics.Add(cover(unitPlural, NodeValue.Integer(Count, unitPlural.ToLowerInvariant(), unitName.ToLowerInvariant()), NodeKind.Detail, Explain.StatUnits));
        statistics.Add(cover("Samples", NodeValue.Integer(Samples, "samples", "sample"), NodeKind.Detail, Explain.StatSamples));
        afterSamples?.Invoke(statistics);
        statistics.Add(cover("Duration", NodeValue.Decimal(Duration, 3, "s"), NodeKind.Detail, Explain.StatDuration));
        bitrate = statistics.Add(cover("Average Bitrate", AverageBitrate is { } average ? NodeValue.Decimal(average, 1, "kbps") : "none", NodeKind.Detail, Explain.AverageBitrate));
        if (Count > 0)
        {
            statistics.Add(cover($"Smallest {unitName}", NodeValue.Integer(Smallest, "bytes"), NodeKind.Detail, Explain.Smallest));
            statistics.Add(cover($"Largest {unitName}", NodeValue.Integer(Largest, "bytes"), NodeKind.Detail, Explain.Largest));
        }

        if (_points.Count > 0)
        {
            statistics.Add(cover("Minimum Bitrate", NodeValue.Decimal(MinimumBitrate, 1, "kbps"), NodeKind.Detail, Explain.MinimumBitrate));
            statistics.Add(cover("Maximum Bitrate", NodeValue.Decimal(MaximumBitrate, 1, "kbps"), NodeKind.Detail, Explain.MaximumBitrate));
            _distribution.AddTo(statistics, (name, value, description) => cover(name, value, NodeKind.Detail, description), unitName, unitPlural);
        }

        return statistics;
    }

    private readonly record struct Point(long Offset, int Length, int Samples);

    private double Bitrate(int length, int samples) => samples > 0 && sampleRate > 0 ? length * 8.0 * sampleRate / samples / 1000 : 0;
}
