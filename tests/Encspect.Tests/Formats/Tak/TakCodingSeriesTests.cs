using Encspect.Core;
using Encspect.Formats.Tak;
using Encspect.Host;
using Encoder = Encspect.Tests.Support.TakEncoder;

namespace Encspect.Tests.Formats.Tak;

// The numeric coding series of TAK's frames and their "Over Time" forms (docs/design/coding-graphs.md, the TAK survey): the highest value in each second of the frames that start in it,
// counted in the samples coded so far, as FLAC's PredictionSeriesTests holds for FLAC.
public class TakCodingSeriesTests
{
    private const int Rate = 8000;
    private const int SizeType = 8;
    private const int FrameSize = 1024;

    // Four seconds of mono in frames of 1,024 samples (eight frames start in each second at 8,000 Hz): each second has its own order index, residual mode and share of predicted subframes.
    private static Report Analyze()
    {
        var random = new Random(7);
        int total = 4 * Rate;
        int[][] pcm = [[.. Enumerable.Range(0, total).Select(_ => random.Next(-40, 41))]];
        int count = (total + FrameSize - 1) / FrameSize;
        List<byte[]> coded = [];
        for (int i = 0; i < count; i++)
        {
            int second = (int)((long)i * FrameSize / Rate);
            var settings = new Encoder.Settings
            {
                Rate = Rate,
                Subframes = 1,
                Partitions = 0,
                StereoBit = false,
                Prediction = second == 3 ? 0 : 1,
                ResidualMode = new[] { 3, 5, 7, 1 }[second],
                OrderIndex = second switch { 0 => 0, 1 => i == 9 ? 1 : 0, 2 => i == 20 ? 14 : 2, _ => 0 },
            };
            int from = i * FrameSize;
            coded.Add(Encoder.Frame([pcm[0][from..Math.Min(total, from + FrameSize)]], settings, random));
        }

        Encoder.Built built = Encoder.Assemble(new Encoder.Settings { Rate = Rate }, 1, total, SizeType, coded);
        return Analyzer.CreateDefault().Analyze("test.tak", new MemoryStream(built.Data));
    }

    [Fact]
    public void Each_second_has_the_highest_order_mode_and_precision_of_the_frames_that_start_in_it()
    {
        StreamInfo stream = Assert.Single(Analyze().Streams);

        Series orders = stream.Series.Single(s => s.Name == "Highest Predictor Order Over Time");
        Series modes = stream.Series.Single(s => s.Name == "Highest Residual Mode Over Time");

        Assert.Equal(SeriesAxis.Seconds, orders.Axis);
        Assert.Equal([0d, 1d, 2d], orders.X.ToArray());
        Assert.Equal([4d, 8d, 256d], orders.Values.ToArray());
        Assert.Equal([0d, 1d, 2d, 3d], modes.X.ToArray());
        Assert.Equal([3d, 5d, 7d, 1d], modes.Values.ToArray());
        Assert.Equal("mode", modes.Unit);
        Assert.Equal(3, stream.Series.Single(s => s.Name == "Coefficient Precision Over Time").X.Length);
    }

    [Fact]
    public void The_frame_series_hold_an_order_of_256_and_a_mode_of_0_to_50()
    {
        StreamInfo stream = Assert.Single(Analyze().Streams);

        Series orders = stream.Series.Single(s => s.Name == "Highest Predictor Order");
        Series modes = stream.Series.Single(s => s.Name == "Highest Residual Mode");

        Assert.Equal(256d, orders.Values.ToArray().Max());
        Assert.Equal(24, orders.X.Length);
        Assert.Equal(32, modes.X.Length);
        Assert.Equal(SeriesAxis.FrameOffset, modes.Axis);
    }

    [Fact]
    public void An_unsplit_empty_block_records_no_mode_and_a_block_with_values_records_its_mode()
    {
        byte[] data = [0x0A, 0, 0, 0];
        var modes = new List<byte>();
        var bits = new LsbBitCursor(data);
        ResidualCodes.Read(ref bits, [], 0, 88, modes);
        Assert.Empty(modes);

        bits = new LsbBitCursor(data);
        ResidualCodes.Read(ref bits, [], 3, 88, modes);
        Assert.Equal([(byte)5], modes);
    }
}
