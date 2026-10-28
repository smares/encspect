using Encspect.Core;
using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Tests;

// The lookup of a frame by sample position, which the Audio panels' click and mark use: exact where the frames last equally long and the
// last is full, within one frame where the last is shorter, and the inverse of itself at every frame's first sample.
public class SampleLocatorTests
{
    private static double[] Offsets(int count) => [.. Enumerable.Range(0, count).Select(i => 1000.0 + (i * 417.0))];

    [Theory]
    [InlineData(20, 100)]
    [InlineData(1, 4096)]
    [InlineData(107, 4096)]
    [InlineData(3000, 1152)]
    public void Frames_of_equal_length_are_found_exactly(int count, int length)
    {
        SampleLocator locator = SampleLocator.Of(Offsets(count), (long)count * length);
        for (long position = 0; position < locator.Total; position += Math.Max(1, length / 7))
        {
            Assert.Equal(1000 + ((position / length) * 417), locator.FrameOffsetAt(position));
        }

        // The edges of every frame, at both sides.
        for (int frame = 0; frame < count; frame += Math.Max(1, count / 50))
        {
            Assert.Equal(frame, locator.FrameIndexAt((long)frame * length));
            Assert.Equal(frame, locator.FrameIndexAt(((long)(frame + 1) * length) - 1));
        }
    }

    [Fact]
    public void A_shorter_last_frame_leaves_a_position_at_most_one_frame_late()
    {
        const int count = 25;
        const int length = 100;
        for (int last = 1; last <= length; last++)
        {
            long total = ((count - 1) * length) + last;
            SampleLocator locator = SampleLocator.Of(Offsets(count), total);
            for (long position = 0; position < total; position++)
            {
                long truth = position / length;
                int found = locator.FrameIndexAt(position);
                Assert.InRange(found - truth, 0, 1);
            }

            // The stream's first and last samples are in its first and last frames.
            Assert.Equal(0, locator.FrameIndexAt(0));
            Assert.Equal(count - 1, locator.FrameIndexAt(total - 1));
        }
    }

    [Fact]
    public void Positions_outside_the_stream_are_clamped_to_its_ends()
    {
        SampleLocator locator = SampleLocator.Of(Offsets(10), 1000);
        Assert.Equal(0, locator.FrameIndexAt(-5));
        Assert.Equal(9, locator.FrameIndexAt(1000));
        Assert.Equal(9, locator.FrameIndexAt(long.MaxValue));
        Assert.Equal(0, locator.FrameIndexAt(long.MinValue));
    }

    [Fact]
    public void A_frame_s_first_sample_leads_back_to_the_frame_and_an_unknown_offset_to_none()
    {
        foreach ((int count, long total) in new[] { (20, 2000L), (107, 438_000L), (13, 12_345L), (500, 499L * 7) })
        {
            double[] offsets = Offsets(count);
            SampleLocator locator = SampleLocator.Of(offsets, total);
            for (int frame = 0; frame < count; frame++)
            {
                long position = locator.PositionOfFrame((long)offsets[frame])!.Value;
                Assert.Equal(frame, locator.FrameIndexAt(position));
                if (frame > 0)
                {
                    Assert.True(position > locator.PositionOfFrame((long)offsets[frame - 1]));
                }
            }

            Assert.Null(locator.PositionOfFrame(1001));
            Assert.Null(locator.PositionOfFrame(0));
        }
    }

    [Fact]
    public void Large_streams_do_not_overflow()
    {
        const long total = 1L << 42;
        SampleLocator locator = SampleLocator.Of(Offsets(2_000_000), total);
        Assert.Equal(1_999_999, locator.FrameIndexAt(total - 1));
        Assert.Equal(1_000_000, locator.FrameIndexAt(total / 2));
        Assert.Equal(total / 2, locator.PositionOfFrame((long)(1000.0 + (1_000_000 * 417.0))));
    }

    private static StreamInfo Framed(long samples, int count) => new()
    {
        Nodes = [],
        Codec = "FLAC",
        Samples = samples,
        Series = [new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, Offsets(count), new double[count])],
    };

    private static StreamMeasurements Measured(long first, long samples, long lost = 0, MeasurementStatus status = MeasurementStatus.Complete) =>
        new() { Status = status, FirstPosition = first, Samples = samples, LostSamples = lost };

    [Fact]
    public void A_stream_whose_frames_start_late_is_found_from_where_they_start()
    {
        // The shape of flac/built-first-frame-removed-md5-zero.flac: five frames of 4,096 samples, the first at 4,096, the last shorter.
        SampleLocator locator = SampleLocator.For(Framed(19904, 5), Measured(4096, 19904))!;
        Assert.Equal(4096, locator.Base);
        Assert.Equal(19904, locator.Total);
        Assert.Equal(0, locator.FrameIndexAt(4096));
        Assert.Equal(0, locator.FrameIndexAt(7000));
        Assert.Equal(1, locator.FrameIndexAt(4096 + 4096));
        Assert.Equal(4, locator.FrameIndexAt(23999));
        Assert.Equal(0, locator.FrameIndexAt(0));
        Assert.Equal(4, locator.FrameIndexAt(long.MaxValue));
        Assert.Equal(0, locator.FrameIndexAt(long.MinValue));

        // A fragment at 2^24.
        locator = SampleLocator.For(Framed(40960, 10), Measured(1L << 24, 40960))!;
        Assert.Equal(1L << 24, locator.Base);
        for (int frame = 0; frame < 10; frame++)
        {
            Assert.Equal(frame, locator.FrameIndexAt((1L << 24) + (frame * 4096L) + 5));
            Assert.Equal(frame * 4096L, locator.PositionOfFrame((long)Offsets(10)[frame])!.Value - (1L << 24));
        }
    }

    [Fact]
    public void A_stream_that_stopped_early_starts_where_the_measuring_did()
    {
        SampleLocator locator = SampleLocator.For(Framed(40960, 10), Measured(4096, 8192, status: MeasurementStatus.Stopped))!;
        Assert.Equal(4096, locator.Base);
        Assert.Equal(40960, locator.Total);
        Assert.Equal(0, locator.FrameIndexAt(4096 + 100));
    }

    [Fact]
    public void A_gap_lies_inside_the_span_and_moves_no_frame()
    {
        // Six frames of 4,096 samples, the third lost: the measured positions count its 4,096 samples as lost.
        SampleLocator locator = SampleLocator.For(Framed(6 * 4096, 6), Measured(0, 5 * 4096, 4096))!;
        Assert.Equal(0, locator.Base);
        Assert.Equal(1, locator.FrameIndexAt(4500));
        for (long position = 0; position < 6 * 4096; position++)
        {
            Assert.Equal(position / 4096, locator.FrameIndexAt(position));
        }

        // The same with the last frame short and the gap before the end: never early, at most one frame late.
        long total = (5 * 4096) + 1000;
        locator = SampleLocator.For(Framed(total, 6), Measured(0, total - 4096, 4096))!;
        for (long position = 0; position < total; position++)
        {
            Assert.InRange(locator.FrameIndexAt(position) - (position / 4096), 0, 1);
        }
    }

    [Fact]
    public void The_position_of_a_frame_is_never_later_than_its_first_sample_and_less_than_a_frame_earlier()
    {
        const int count = 25;
        const int length = 4096;
        for (int last = 1; last <= length; last += 37)
        {
            long total = ((count - 1) * (long)length) + last;
            SampleLocator locator = SampleLocator.Of(Offsets(count), total, 1000);
            for (int frame = 0; frame < count; frame++)
            {
                long truth = 1000 + (frame * (long)length);
                long found = locator.PositionOfFrame((long)Offsets(count)[frame])!.Value;
                Assert.InRange(truth - found, 0, length - 1);
            }
        }
    }

    [Fact]
    public void A_stream_without_frames_or_samples_has_no_locator_and_the_measured_samples_give_the_count_where_the_stream_states_none()
    {
        var series = new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, Offsets(4), new double[] { 1, 2, 3, 4 });
        StreamInfo Stream(long samples, IReadOnlyList<Series> all) => new() { Nodes = [], Codec = "FLAC", Samples = samples, Series = all };

        Assert.Null(SampleLocator.For(Stream(100, [])));
        Assert.Null(SampleLocator.For(Stream(0, [series])));
        Assert.Equal(100, SampleLocator.For(Stream(100, [series]))!.Total);
        Assert.Equal(0, SampleLocator.For(Stream(100, [series]))!.Base);

        var measured = new StreamMeasurements { Status = MeasurementStatus.Complete, FirstPosition = 10, Samples = 150, LostSamples = 5 };
        SampleLocator withCount = SampleLocator.For(Stream(100, [series]), measured)!;
        Assert.Equal(100, withCount.Total);
        Assert.Equal(65, withCount.Base);
        Assert.Equal(4, withCount.Count);

        SampleLocator withoutCount = SampleLocator.For(Stream(0, [series]), measured)!;
        Assert.Equal(155, withoutCount.Total);
        Assert.Equal(10, withoutCount.Base);
    }
}
