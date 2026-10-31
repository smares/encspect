using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// Finds the frame that holds a sample position, and the position of a frame, for the Audio panels' click and mark. The tree knows
// frames by byte offset only (FrameLocator), and no node states the samples it codes, so the position comes from the stream's own
// figures: the byte offsets of its frames, which a per-frame series holds exactly and in file order, and its sample count.
//
// The frames cover the span [Base, Base + Total): Total is the stream's sample count and Base the position where the frames start,
// which is not 0 for a stream cut from a longer one (a FLAC fragment whose frames start at 4,096 or at 2^24) and which the measured
// positions give: the end of what was measured (first position, samples and the samples the gaps skipped) less the sample count.
// A gap lies inside the span, since the measured positions count the samples it skipped, so it moves no frame.
//
// The reading: the frames of a stream last equally long, but the last, which is shorter or as long. That is what FLAC (a fixed block
// size), WavPack, Monkey's Audio and TrueHD write, and what the graphs' per-second series already assume. A position p of `count`
// frames over `total` samples is then in frame floor((p - Base) * count / total). The true frame length F satisfies
// (count - 1) * F < total <= count * F, so the estimate is exact when the last frame is full (total = count * F) and otherwise
// at most one frame late (never early), and a stream whose frames differ in length (FLAC's variable block size) can be off by more.
// The answer is a frame that is near the time, not a claim about which samples a frame holds; nothing is shown that says it is exact.
public sealed class SampleLocator
{
    private readonly ReadOnlyMemory<double> _offsets;
    private readonly FrameRuns? _runs;
    private readonly long[] _cumulative = [];

    private SampleLocator(ReadOnlyMemory<double> offsets, long total, long origin, FrameRuns? runs)
    {
        _offsets = offsets;
        _runs = runs;
        if (runs is { Samples.IsEmpty: false })
        {
            long[] cumulative = new long[runs.Samples.Length];
            long sum = 0;
            for (int i = 0; i < cumulative.Length; i++)
            {
                sum += runs.Samples.Span[i];
                cumulative[i] = sum;
            }

            _cumulative = sum > 0 ? cumulative : [];
        }

        Total = total;
        Base = origin;
    }

    // The samples per channel the frames code together.
    public long Total { get; }

    // The position (untrimmed, as PcmBlock.Position) of the first frame's first sample.
    public long Base { get; }

    // The frames of the stream: its series' points, or where the points are runs, the frames the runs cover.
    public int Count => _runs is { } runs ? (int)Math.Min(int.MaxValue, runs.FrameCount) : _offsets.Length;

    // The locator of a stream, or null where it has no frames to find (a PCM stream, whose data is not framed) or no sample count.
    // measured places the frames: a stream that was measured to its end ends where the measuring did, and one that stopped early starts
    // where the measuring started; it also gives the sample count where the stream states none.
    public static SampleLocator? For(StreamInfo stream, StreamMeasurements? measured = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Series? frames = stream.Series.FirstOrDefault(s => s.Axis == SeriesAxis.FrameOffset && s.X.Length > 0);
        ReadOnlyMemory<double> offsets = frames?.X ?? default;
        long end = measured is null ? stream.Samples : measured.FirstPosition + measured.Samples + measured.LostSamples;
        long total = stream.Samples > 0 ? stream.Samples : measured is null ? 0 : measured.Samples + measured.LostSamples;
        long origin = total <= 0 ? 0 : stream.Samples > 0 && end >= total ? end - total : measured?.FirstPosition ?? 0;
        return offsets.Length == 0 || total <= 0 ? null : new SampleLocator(offsets, total, Math.Max(0, origin), frames!.Runs);
    }

    // For a stream of known frames and samples, as tests make one; the frames start at origin. With runs the offsets are the starts of
    // runs of frames: the locator then finds the frame by the stream's frame numbers and names the run that holds it.
    public static SampleLocator Of(ReadOnlyMemory<double> offsets, long total, long origin = 0, FrameRuns? runs = null) =>
        offsets.Length == 0 || total <= 0 || origin < 0 ? throw new ArgumentException("A locator needs frames and samples.") : new SampleLocator(offsets, total, origin, runs);

    // The index of the frame that holds the sample position (untrimmed, from 0), clamped to the stream; a frame of the stream's
    // numbering, counted from 0, where the points are runs.
    public int FrameIndexAt(long position)
    {
        if (_runs is { } runs && _cumulative.Length > 0)
        {
            (int run, int into) = RunAt(position);
            return (int)(runs.FirstFrames.Span[run] - 1 + into);
        }

        long clamped = position <= Base ? 0 : Math.Min(Total - 1, position - Base);
        return (int)Math.Min(Count - 1, (long)((UInt128)(ulong)clamped * (ulong)Count / (ulong)Total));
    }

    // The byte offset of that frame, or where the points are runs, of the first frame of the run that holds it.
    public long FrameOffsetAt(long position)
    {
        if (_runs is { } runs)
        {
            return (long)_offsets.Span[_cumulative.Length > 0 ? RunAt(position).Run : Math.Max(0, runs.RunOf(FrameIndexAt(position) + 1))];
        }

        return (long)_offsets.Span[FrameIndexAt(position)];
    }

    // How many frames after the first of its run the frame of that position is; 0 where the points are frames.
    public int FramesIntoRunAt(long position)
    {
        if (_runs is not { } runs)
        {
            return 0;
        }

        if (_cumulative.Length > 0)
        {
            return RunAt(position).Into;
        }

        long number = FrameIndexAt(position) + 1L;
        return (int)(number - runs.FirstFrames.Span[Math.Max(0, runs.RunOf(number))]);
    }

    // Where the runs state their samples, a position is placed by them: the samples are spread over the runs as they state, and over
    // the frames of a run evenly. A frame that codes nothing (one the stream cannot read) then takes no part of the time, which a
    // spread over all frames would give it, and the frame found is never earlier than the true one.
    private (int Run, int Into) RunAt(long position)
    {
        long clamped = position <= Base ? 0 : Math.Min(Total - 1, position - Base);
        long all = _cumulative[^1];
        long scaled = (long)((UInt128)(ulong)clamped * (ulong)all / (ulong)Total);
        int low = 0;
        int high = _cumulative.Length - 1;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            (low, high) = _cumulative[middle] > scaled ? (low, middle) : (middle + 1, high);
        }

        int run = low;
        long start = run == 0 ? 0 : _cumulative[run - 1];
        long samples = _cumulative[run] - start;
        long frames = _runs!.FramesIn(run);
        int into = samples <= 0 ? 0 : (int)Math.Min(frames - 1, (long)((UInt128)(ulong)(scaled - start) * (ulong)frames / (ulong)samples));
        return (run, into);
    }

    // The first sample of the frame that starts at a byte offset, by the same reading, or null where no frame starts there. Where the
    // points are runs, the first sample of the first frame of the run that holds the offset.
    public long? PositionOfFrame(long offset)
    {
        ReadOnlySpan<double> offsets = _offsets.Span;
        if (_runs is { } runs)
        {
            int run = 0;
            for (int first = 0, last = offsets.Length - 1; first <= last;)
            {
                int middle = (first + last) >>> 1;
                (run, first, last) = offsets[middle] <= offset ? (middle, middle + 1, last) : (run, first, middle - 1);
            }

            if (_cumulative.Length > 0)
            {
                long before = run == 0 ? 0 : _cumulative[run - 1];
                return Base + (long)((((UInt128)(ulong)before * (ulong)Total) + (ulong)_cumulative[^1] - 1) / (ulong)_cumulative[^1]);
            }

            return Base + (long)((((UInt128)(ulong)(runs.FirstFrames.Span[run] - 1) * (ulong)Total) + (ulong)Count - 1) / (ulong)Count);
        }

        int low = 0;
        int high = offsets.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            double at = offsets[middle];
            if (at == offset)
            {
                return Base + (long)((((UInt128)(ulong)middle * (ulong)Total) + (ulong)Count - 1) / (ulong)Count);
            }

            (low, high) = at < offset ? (middle + 1, high) : (low, middle - 1);
        }

        return null;
    }
}
