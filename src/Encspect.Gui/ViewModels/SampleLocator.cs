using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// Finds the frame that holds a sample position, and the position of a frame, for the Audio panels' click and mark. The tree knows
// frames by byte offset only (FrameLocator), and no node states the samples it codes, so the position comes from the stream's own
// figures: the byte offsets of its frames, which a per-frame series holds exactly and in file order, and its sample count.
//
// The reading: the frames of a stream last equally long, but the last, which is shorter or as long. That is what FLAC (a fixed block
// size), WavPack, Monkey's Audio and TrueHD write, and what the graphs' per-second series already assume. A position p of a stream
// of `total` samples in `count` frames is then in frame floor(p * count / total). The true frame length F satisfies
// (count - 1) * F < total <= count * F, so the estimate is exact when the last frame is full (total = count * F) and otherwise
// at most one frame early, and a stream whose frames differ in length (FLAC's variable block size) can be off by more. The answer
// is a frame that is near the time, not a claim about which samples a frame holds; nothing is shown that says it is exact.
public sealed class SampleLocator
{
    private readonly ReadOnlyMemory<double> _offsets;

    private SampleLocator(ReadOnlyMemory<double> offsets, long total)
    {
        _offsets = offsets;
        Total = total;
    }

    // The samples per channel the frames code together.
    public long Total { get; }

    public int Count => _offsets.Length;

    // The locator of a stream, or null where it has no frames to find (a PCM stream, whose data is not framed) or no sample count.
    // measured widens the count to the samples that were measured when the stream states fewer.
    public static SampleLocator? For(StreamInfo stream, StreamMeasurements? measured = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ReadOnlyMemory<double> offsets = stream.Series.FirstOrDefault(s => s.Axis == SeriesAxis.FrameOffset && s.X.Length > 0)?.X ?? default;
        long total = Math.Max(stream.Samples, measured is null ? 0 : measured.FirstPosition + measured.Samples + measured.LostSamples);
        return offsets.Length == 0 || total <= 0 ? null : new SampleLocator(offsets, total);
    }

    // For a stream of known frames and samples, as tests make one.
    public static SampleLocator Of(ReadOnlyMemory<double> offsets, long total) =>
        offsets.Length == 0 || total <= 0 ? throw new ArgumentException("A locator needs frames and samples.") : new SampleLocator(offsets, total);

    // The index of the frame that holds the sample position (untrimmed, from 0), clamped to the stream.
    public int FrameIndexAt(long position)
    {
        long clamped = Math.Clamp(position, 0, Total - 1);
        return (int)Math.Min(Count - 1, (long)((UInt128)(ulong)clamped * (ulong)Count / (ulong)Total));
    }

    // The byte offset of that frame.
    public long FrameOffsetAt(long position) => (long)_offsets.Span[FrameIndexAt(position)];

    // The first sample of the frame that starts at a byte offset, by the same reading, or null where no frame starts there.
    public long? PositionOfFrame(long offset)
    {
        ReadOnlySpan<double> offsets = _offsets.Span;
        int low = 0;
        int high = offsets.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            double at = offsets[middle];
            if (at == offset)
            {
                return (long)((((UInt128)(ulong)middle * (ulong)Total) + (ulong)Count - 1) / (ulong)Count);
            }

            (low, high) = at < offset ? (middle + 1, high) : (low, middle - 1);
        }

        return null;
    }
}
