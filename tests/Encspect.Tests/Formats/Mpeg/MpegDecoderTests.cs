using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The decoder on files built in the test from the smallest Layer III frames there are (MPEG-2.5 at 8 kHz, mono, 72 bytes and 576 samples each, all zero after the
// header, so each decodes to silence): what it writes, and what it withholds after a gap and where main data are missing (docs/design/lossy-decoders.md, "Withheld output").
public class MpegDecoderTests
{
    private const int FrameLength = 72;
    private const int FrameSamples = 576;

    private static byte[] Frame(int mainDataBegin = 0)
    {
        byte[] frame = new byte[FrameLength];
        frame[0] = 0xFF;
        frame[1] = 0xE3;
        frame[2] = 0x18;
        frame[3] = 0xC0;
        frame[4] = (byte)mainDataBegin;
        return frame;
    }

    private static byte[] Stream(int frames, int mainDataBeginOfFirst = 0, int junkAfter = -1, int junkLength = 0)
    {
        var data = new List<byte>();
        for (int i = 0; i < frames; i++)
        {
            data.AddRange(Frame(i == 0 ? mainDataBeginOfFirst : 0));
            if (i == junkAfter)
            {
                data.AddRange(Enumerable.Repeat((byte)0x55, junkLength));
            }
        }

        return [.. data];
    }

    private sealed class CollectingSink : IPcmSink
    {
        public List<(long Position, int Length, float[] Samples)> Blocks { get; } = [];

        public void Write(PcmBlock block) => Blocks.Add((block.Position, block.Length, block.Floats[..block.Length].ToArray()));

        public void End(long samples)
        {
        }
    }

    private static (Report Report, CollectingSink Sink) Decode(byte[] data)
    {
        var sink = new CollectingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => sink, Measure = new MeasureOptions() } };
        Report report = Analyzer.CreateDefault().Analyze("silence.mp3", new MemoryStream(data), options);
        return (report, sink);
    }

    [Fact]
    public void SilentFramesDecodeToExactZerosAtEveryPosition()
    {
        (Report report, CollectingSink sink) = Decode(Stream(20));

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(DecodeStatus.Decoded, stream.Verification!.Decoding);
        Assert.Equal(20 * FrameSamples, stream.Verification.DecodedSamples);
        Assert.Equal(20, sink.Blocks.Count);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (long)i * FrameSamples), sink.Blocks.Select(b => b.Position));
        Assert.All(sink.Blocks, b => Assert.All(b.Samples, s => Assert.Equal(0f, s)));
        Assert.Equal(0, stream.Measurements!.WithheldSamples);
        Assert.Equal(0, stream.Measurements.LostSamples);
    }

    [Fact]
    public void AStretchOfJunkBetweenFramesWithholdsTheGranuleAndTheHistoryAfterIt()
    {
        (Report report, CollectingSink sink) = Decode(Stream(20, junkAfter: 9, junkLength: 100));

        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;

        // The frame after the junk is withheld whole and 480 samples of the next: 576 + 480.
        Assert.Equal(576 + 480, m.WithheldSamples);
        Assert.Equal(576 + 480, m.WithheldByReason.AfterGap);
        Assert.Equal(0, m.WithheldByReason.MainDataUnavailable);
        Assert.Equal((20 * FrameSamples) - (576 + 480), sink.Blocks.Sum(b => (long)b.Length));
    }

    [Fact]
    public void AFrameWhoseMainDataBeginBeforeTheStreamIsWithheldWholeAndTheNextOnesStartClean()
    {
        (Report report, CollectingSink sink) = Decode(Stream(10, mainDataBeginOfFirst: 5));

        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;

        Assert.Equal(FrameSamples, m.WithheldSamples);
        Assert.Equal(FrameSamples, m.WithheldByReason.MainDataUnavailable);
        Assert.Equal(0, m.WithheldByReason.AfterGap);
        Assert.Equal(9, sink.Blocks.Count);
        Assert.Equal(FrameSamples, sink.Blocks[0].Position);
        Assert.Equal(0, m.LostSamples);
    }
}
