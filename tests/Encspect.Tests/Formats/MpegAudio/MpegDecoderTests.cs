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

        public void Write(PcmBlock block)
        {
            var samples = new float[block.Channels * block.Length];
            for (int c = 0; c < block.Channels; c++)
            {
                block.Floats.Slice(c * block.Stride, block.Length).CopyTo(samples.AsSpan(c * block.Length));
            }

            Blocks.Add((block.Position, block.Length, samples));
        }

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

    // A granule whose Huffman values run past its part2_3_length reads on into the bytes that follow in the frame, as minimp3 does, not into zeros: here 20 pairs of table 1
    // against 8 bits of part2_3_length, and every byte of the frame is 0xFF, which table 1 reads as pairs of zeros (zero bits would read as pairs of ones).
    [Fact]
    public void HuffmanValuesThatRunPastTheirGranuleReadTheBytesOfTheFrameThatFollow()
    {
        var data = new List<byte>();
        for (int i = 0; i < 4; i++)
        {
            byte[] frame = MpegWalkTests.MonoFrame(part23Length: 8, bigValues: 20);
            Array.Fill(frame, (byte)0xFF, 21, frame.Length - 21);
            data.AddRange(frame);
        }

        (Report report, CollectingSink sink) = Decode([.. data]);

        Assert.Contains(report.Findings, f => f.Code == "mpeg-audio.huffman-overrun");
        Assert.Equal(4, sink.Blocks.Count);
        Assert.All(sink.Blocks, b => Assert.All(b.Samples, s => Assert.Equal(0f, s)));
    }

    [Fact]
    public void AMultichannelStreamMeasuredAsItsBaseStereoSaysSo()
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, "mpeg-audio", "layer2", "mc-3-2-lfe.mp2");
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        using FileStream file = File.OpenRead(path);
        Report report = Analyzer.CreateDefault().Analyze("mc-3-2-lfe.mp2", file, options);

        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;

        Assert.Equal(2, m.Channels);
        Assert.Equal("the decoder outputs 2 channels of the stream's 6, and only those were measured", m.Reason);
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
    public void AFrameWhoseMainDataBeginBeforeTheStreamIsWithheldWholeAndTheNextOneLikeAfterAGap()
    {
        (Report report, CollectingSink sink) = Decode(Stream(10, mainDataBeginOfFirst: 5));

        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;

        // The first frame is withheld whole; the output after it depends on what it would have left, so the next frame is withheld like after a gap.
        Assert.Equal(FrameSamples + 576 + 480, m.WithheldSamples);
        Assert.Equal(FrameSamples, m.WithheldByReason.MainDataUnavailable);
        Assert.Equal(576 + 480, m.WithheldByReason.AfterGap);
        Assert.Equal(8, sink.Blocks.Count);
        Assert.Equal((2 * FrameSamples) + 480, sink.Blocks[0].Position);
        Assert.Equal(0, m.LostSamples);
    }

    // A frame whose first granule codes ten pairs of the Huffman table 1 (a bit each) in 4 bits of part2_3_length.
    private static byte[] OverrunFrame()
    {
        byte[] frame = Frame();
        var bits = new List<bool>();
        void Write(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        Write(0, 8);
        Write(0, 1);
        Write(4, 12);
        Write(10, 9);
        Write(100, 8);
        Write(0, 9);
        Write(0, 1);
        Write(1, 5);
        Write(0, 10);
        Write(15, 4);
        Write(0, 3);
        Write(0, 1);
        Write(0, 1);
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                frame[4 + (i / 8)] |= (byte)(0x80 >> (i % 8));
            }
        }

        Array.Fill(frame, (byte)0xFF, 4 + 9, FrameLength - 13);
        return frame;
    }

    [Fact]
    public void HuffmanDataThatRunsPastItsPartLengthIsReportedOnceWithTheCountOfFrames()
    {
        byte[] data = [.. Enumerable.Range(0, 6).SelectMany(_ => OverrunFrame())];

        Report report = Analyzer.CreateDefault().Analyze("overrun.mp3", new MemoryStream(data));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "mpeg-audio.huffman-overrun");
        Assert.Equal(6, finding.Count);
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    // ---- Damage to real audio: what is written after the withheld span equals what the intact file decodes to (docs/design/lossy-decoders.md, "Withheld output") ----

    private const string Music = "mpeg-audio/external-lame-stereo-music.mp3";

    private static byte[] MusicBytes() => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, Music));

    // The offsets of the audio frames of a file and the frame length at each.
    private static long[] FrameOffsets(byte[] data)
    {
        Report report = Analyzer.CreateDefault().Analyze("music.mp3", new MemoryStream(data));
        return [.. report.Streams[0].Series.First(s => s.Name == "Bitrate").X.ToArray().Select(x => (long)x)];
    }

    // The samples a decode writes, by position, with which positions were written.
    private static (float[][] Samples, bool[] Written, StreamMeasurements Measurements) Written(byte[] data)
    {
        (Report report, CollectingSink sink) = Decode(data);
        long end = sink.Blocks.Max(b => b.Position + b.Length);
        float[][] samples = [new float[end], new float[end]];
        bool[] written = new bool[end];
        foreach ((long position, int length, float[] values) in sink.Blocks)
        {
            for (int c = 0; c < 2; c++)
            {
                Array.Copy(values, c * length, samples[c], position, length);
            }

            Array.Fill(written, true, (int)position, length);
        }

        return (samples, written, report.Streams[0].Measurements!);
    }

    private static void AssertEqualsIntact(float[][] intact, (float[][] Samples, bool[] Written, StreamMeasurements Measurements) damaged, long positionShift, long shiftFrom = long.MaxValue, long laterShift = 0)
    {
        int compared = 0;
        for (int p = 0; p < damaged.Written.Length; p++)
        {
            if (!damaged.Written[p])
            {
                continue;
            }

            for (int c = 0; c < 2; c++)
            {
                Assert.True(intact[c][p + (p >= shiftFrom ? laterShift : positionShift)] == damaged.Samples[c][p], $"position {p} channel {c}: {damaged.Samples[c][p]}, the intact file has {intact[c][p + (p >= shiftFrom ? laterShift : positionShift)]}");
            }

            compared++;
        }

        Assert.True(compared > 100_000, $"only {compared} samples were compared");
    }

    [Fact]
    public void ATrackSplitFromAContinuousStreamDecodesExactlyAfterTheWithheldSpan()
    {
        byte[] data = MusicBytes();
        long[] offsets = FrameOffsets(data);
        (float[][] intact, _, _) = Written(data);

        // Frame 100 on: its main data and those of the frames after it begin in frames the file no longer has.
        (float[][] Samples, bool[] Written, StreamMeasurements Measurements) damaged = Written(data[(int)offsets[100]..]);

        AssertEqualsIntact(intact, damaged, 100 * 1152);
        Assert.True(damaged.Measurements.WithheldByReason.MainDataUnavailable > 0);
        Assert.Equal(576 + 480, damaged.Measurements.WithheldByReason.AfterGap);
    }

    [Fact]
    public void AFrameCutByTheNextWithholdsTheFramesWhoseMainDataLieInIt()
    {
        byte[] data = MusicBytes();
        long[] offsets = FrameOffsets(data);
        (float[][] intact, _, _) = Written(data);

        // The last 100 bytes of frame 100 are gone, and the frames after it reach back into them.
        byte[] damagedData = [.. data[..(int)(offsets[101] - 100)], .. data[(int)offsets[101]..]];
        (float[][] Samples, bool[] Written, StreamMeasurements Measurements) damaged = Written(damagedData);

        AssertEqualsIntact(intact, damaged, 0);
        Assert.True(damaged.Measurements.WithheldByReason.MainDataUnavailable > 0);
    }

    [Fact]
    public void AFrameWhoseHeaderIsDestroyedWithholdsTheFramesWhoseMainDataReachBackAcrossIt()
    {
        byte[] data = MusicBytes();
        long[] offsets = FrameOffsets(data);
        (float[][] intact, _, _) = Written(data);

        // The sync bytes of frame 100 are zero: its bytes are junk of a frame's size, and the audio after it counts from where the frame before ends.
        byte[] damagedData = (byte[])data.Clone();
        damagedData[offsets[100]] = 0;
        damagedData[offsets[100] + 1] = 0;
        (float[][] Samples, bool[] Written, StreamMeasurements Measurements) damaged = Written(damagedData);

        AssertEqualsIntact(intact, damaged, 0, shiftFrom: 100 * 1152, laterShift: 1152);
        Assert.True(damaged.Measurements.WithheldByReason.MainDataUnavailable > 0);
    }

    [Fact]
    public void JunkTooSmallToHoldAFrameLeavesTheAudioAfterItExact()
    {
        byte[] data = MusicBytes();
        long[] offsets = FrameOffsets(data);
        (float[][] intact, _, _) = Written(data);

        byte[] damagedData = [.. data[..(int)offsets[100]], .. Enumerable.Repeat((byte)0x55, 10), .. data[(int)offsets[100]..]];
        (float[][] Samples, bool[] Written, StreamMeasurements Measurements) damaged = Written(damagedData);

        AssertEqualsIntact(intact, damaged, 0);
        Assert.Equal(0, damaged.Measurements.WithheldByReason.MainDataUnavailable);
        Assert.Equal(576 + 480, damaged.Measurements.WithheldByReason.AfterGap);
    }
}
