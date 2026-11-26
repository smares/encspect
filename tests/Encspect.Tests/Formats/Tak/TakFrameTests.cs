using Encspect.Core;
using Encspect.Formats.Tak;
using Encspect.Host;
using Builder = Encspect.Tests.Support.TakBuilder;

namespace Encspect.Tests.Formats.Tak;

// The walk over the frames: how it finds them by their sync words and headers, which of its findings a damaged header, damaged data, a cut frame, skipped bytes or a break in the frame
// numbers raise, and what it counts. The frames here hold bytes in place of coded audio (TakBuilder.Frame); the analyzer compares their CRC-24s and does not read their coding.
public class TakFrameTests
{
    // Frames of 5512 samples at 44,100 Hz (the stream info's frame size type 1), the last of 1000; the first is an info frame.
    private static byte[][] FrameParts(int count, int payload = 200) =>
        [.. Enumerable.Range(0, count).Select(i => Builder.Frame(i, payload, isLast: i == count - 1, isInfo: i == 0))];

    private static long SamplesOf(int count) => ((count - 1) * 5512L) + 1000;

    // A file of the frames with the last frame info that states the last one; samples is the stream info's count.
    private static byte[] FramesFile(byte[][] frames, bool isLastFrameInfoStored = true, long? samples = null, byte[]? beforeFrames = null)
    {
        byte[] audio = [.. frames.SelectMany(f => f)];
        long position = audio.Length - frames[^1].Length;
        return
        [
            .. "tBaK"u8, .. Builder.StreamInfo(samples: samples ?? SamplesOf(frames.Length)), .. isLastFrameInfoStored ? Builder.LastFrameInfo(position, frames[^1].Length) : [],
            .. Builder.EncoderInfo(), .. Builder.End(), .. beforeFrames ?? [], .. audio,
        ];
    }

    private static int FrameAt(byte[][] frames, int index, int extra = 0) => frames.Take(index).Sum(f => f.Length) + extra;

    private static Report Analyze(byte[] data, AnalysisOptions? options = null) => Analyzer.CreateDefault().Analyze("test.tak", new MemoryStream(data), options);

    private static Node[] FrameNodes(Report report) => [.. report.Root.Children.Single(n => n.Name == "TAK").Children.Where(c => c.Kind == NodeKind.Frame)];

    private static Finding Single(Report report, string code) => Assert.Single(report.Findings, f => f.Code == code);

    private static string[] Codes(Report report) => [.. report.Findings.Select(f => f.Code).Where(c => c.StartsWith("tak.", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private static ChecksumTally Tally(Report report, string name) => report.Streams.Single().Verification!.Checksums.Single(c => c.Name == name);

    private static Node Statistic(Report report, string name) =>
        report.Root.Children.Single(n => n.Name == "TAK").Children.Single(c => c.Name == "Statistics").Children.Single(c => c.Name == name);

    [Fact]
    public void EveryFrameIsFoundAndItsChecksumsAreCompared()
    {
        byte[][] frames = FrameParts(6);

        Report report = Analyze(FramesFile(frames));

        Assert.Empty(report.Findings);
        Node[] nodes = FrameNodes(report);
        Assert.Equal(6, nodes.Length);
        for (int i = 0; i < nodes.Length; i++)
        {
            Assert.Equal(frames[i].Length, nodes[i].Length);
            Assert.Equal(i, nodes[i].Children.Single(c => c.Name == "Header").Children.Single(c => c.Name == "Frame Number").Value?.AsInteger());
            Assert.Equal("true", nodes[i].Children.Single(c => c.Name == "IsDataCrc24Valid").Value?.ToString());
        }

        Assert.Equal(5512, nodes[0].Value?.AsInteger());
        Assert.Equal(1000, nodes[5].Value?.AsInteger());
        Assert.Equal((6, 6, 0), (Tally(report, "frame header CRC-24").Stored, Tally(report, "frame header CRC-24").Checked, Tally(report, "frame header CRC-24").Failed));
        ChecksumTally data = Tally(report, "frame data CRC-24");
        Assert.Equal((6, 6, 0), (data.Stored, data.Checked, data.Failed));
        Assert.True(data.CoversAllAudio);
        Assert.True(report.Streams.Single().Verification!.IsVerified);
        Assert.Equal(6, Statistic(report, "Frames").Value?.AsInteger());
        Assert.Equal(6, Statistic(report, "Verified Frame Checksums").Value?.AsInteger());
        Assert.Equal(9, Statistic(report, "Verified Header Checksums").Value?.AsInteger());
        Assert.DoesNotContain(report.Root.Children.Single(n => n.Name == "TAK").Children.Single(c => c.Name == "Statistics").Children, c => c.Name.StartsWith("Failed", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBitrateOfEveryFrameIsAGraphAndItsExtremesAreStatistics()
    {
        byte[][] frames = FrameParts(6);

        Report report = Analyze(FramesFile(frames));

        StreamInfo stream = report.Streams.Single();
        Series bitrate = stream.Series.Single(s => s.Name == "Bitrate");
        Assert.Equal("kbps", bitrate.Unit);
        Assert.Equal(SeriesAxis.FrameOffset, bitrate.Axis);
        Assert.Equal(6, bitrate.X.Length);
        Assert.Equal(frames[0].Length * 8.0 * 44100 / 5512 / 1000, bitrate.Values.Span[0], 6);
        Series overTime = stream.Series.Single(s => s.Name == "Bitrate Over Time");
        Assert.Equal(SeriesAxis.Seconds, overTime.Axis);
        Assert.Equal("kbps", overTime.Unit);
        Assert.Equal(frames.Min(f => f.Length), Statistic(report, "Smallest Frame").Value?.AsInteger());
        Assert.Equal(frames.Max(f => f.Length), Statistic(report, "Largest Frame").Value?.AsInteger());
        Assert.NotNull(stream.AverageBitrate);
        Assert.Equal(stream.IsVbr, Statistic(report, "Bitrate Mode").Value?.ToString() == "variable");
    }

    [Fact]
    public void AFlippedBitInAFrameHeaderFailsItsCrcAndKeepsTheFramesAroundIt()
    {
        byte[][] frames = FrameParts(6);
        byte[] file = FramesFile(frames);
        // The frame number of frame 3: a flipped bit that no neighbor explains, found again by the previous frame's data CRC-24.
        file[file.Length - frames.Skip(3).Sum(f => f.Length) + 3] ^= 0x10;

        Report report = Analyze(file);

        Finding finding = Single(report, "tak.frame-header-crc-mismatch");
        Assert.Equal("CRC-24", finding.Node?.Name);
        Assert.Equal(["tak.frame-header-crc-mismatch"], Codes(report));
        Assert.Equal(6, FrameNodes(report).Length);
        Assert.Equal((6, 6, 1), (Tally(report, "frame header CRC-24").Stored, Tally(report, "frame header CRC-24").Checked, Tally(report, "frame header CRC-24").Failed));
        Assert.Equal(0, Tally(report, "frame data CRC-24").Failed);
        Assert.False(report.Streams.Single().Verification!.IsVerified);
        Assert.Equal(1, Statistic(report, "Failed Header Checksums").Value?.AsInteger());
    }

    [Fact]
    public void AFlippedBitInTheFrameNumberIsFoundByTheExpectedNumberToo()
    {
        byte[][] frames = FrameParts(6);
        byte[] file = FramesFile(frames);
        // Frame 3 keeps its number but its sample-count padding bit... a flipped bit in the CRC-24 itself: the number is the expected one.
        int crcAt = file.Length - frames.Skip(3).Sum(f => f.Length) + 5;
        file[crcAt] ^= 0x01;

        Report report = Analyze(file);

        Assert.Equal(["tak.frame-header-crc-mismatch"], Codes(report));
        Assert.Equal(6, FrameNodes(report).Length);
    }

    [Fact]
    public void AFlippedBitInFrameDataFailsTheFramesChecksum()
    {
        byte[][] frames = FrameParts(6);
        byte[] file = FramesFile(frames);
        file[file.Length - frames.Skip(2).Sum(f => f.Length) + 100] ^= 0x04;

        Report report = Analyze(file);

        Finding finding = Single(report, "tak.frame-crc-mismatch");
        Assert.Equal("Data CRC-24", finding.Node?.Name);
        Assert.Equal(["tak.frame-crc-mismatch"], Codes(report));
        Assert.Equal(6, FrameNodes(report).Length);
        ChecksumTally data = Tally(report, "frame data CRC-24");
        Assert.Equal((6, 6, 1), (data.Stored, data.Checked, data.Failed));
        Assert.Equal(1, Statistic(report, "Failed Frame Checksums").Value?.AsInteger());
        Assert.Equal(5, Statistic(report, "Verified Frame Checksums").Value?.AsInteger());
        Assert.False(report.Streams.Single().Verification!.IsVerified);
    }

    [Fact]
    public void AFlippedBitInASyncWordMergesTheFrameIntoThePreviousOne()
    {
        byte[][] frames = FrameParts(6);
        byte[] file = FramesFile(frames);
        file[file.Length - frames.Skip(3).Sum(f => f.Length)] ^= 0x01;

        Report report = Analyze(file);

        Assert.Equal(["tak.frame-crc-mismatch", "tak.frame-number-discontinuity"], Codes(report));
        Assert.Equal(5, FrameNodes(report).Length);
        Assert.False(report.Streams.Single().Verification!.IsVerified);
    }

    [Fact]
    public void ASyncWordWithAnUnexpectedNumberAndAFailingCrcInTheDataIsNotAFrame()
    {
        // The bytes FF A0 and a frame number of 1, where frame 2 is expected, and a CRC-24 that fails: neither the header, nor the number, nor the previous frame's data CRC-24 speaks for it.
        byte[] coded = new byte[300];
        new Random(5).NextBytes(coded);
        byte[] candidate = [0xFF, 0xA0, 0x08, 0x00, 0x00, 0xAA, 0xBB, 0xCC];
        candidate.CopyTo(coded, 100);
        byte[][] frames = [Builder.Frame(0, 300, isInfo: true), Builder.Frame(1, 300, coded: coded), Builder.Frame(2, 300, isLast: true)];

        Report report = Analyze(FramesFile(frames));

        Assert.Empty(report.Findings);
        Assert.Equal(3, FrameNodes(report).Length);
    }

    [Fact]
    public void ASyncWordInTheDataThatIsNoHeaderIsNotAFrame()
    {
        byte[] coded = new byte[300];
        new Random(7).NextBytes(coded);
        coded[50] = 0xFF;
        coded[51] = 0xA0;
        byte[][] frames = [Builder.Frame(0, 300, isInfo: true), Builder.Frame(1, 300, coded: coded), Builder.Frame(2, 300, isLast: true)];

        Report report = Analyze(FramesFile(frames));

        Assert.Empty(report.Findings);
        Assert.Equal(3, FrameNodes(report).Length);
        Assert.True(report.Streams.Single().Verification!.IsVerified);
    }

    [Fact]
    public void AFrameTheDataCutsShortIsReportedAndItsDataChecksumIsNotCompared()
    {
        byte[][] frames = FrameParts(6);
        byte[] file = FramesFile(frames);

        Report report = Analyze(file[..^50]);

        Finding cut = Single(report, "tak.frame-cut");
        Assert.Equal("Frame", cut.Node?.Name);
        Assert.Equal(["tak.audio-cut", "tak.frame-cut"], Codes(report));
        Node[] nodes = FrameNodes(report);
        Assert.Equal(6, nodes.Length);
        Assert.DoesNotContain(nodes[5].Children, c => c.Name == "IsDataCrc24Valid");
        ChecksumTally data = Tally(report, "frame data CRC-24");
        Assert.Equal((6, 5, 0), (data.Stored, data.Checked, data.Failed));
        Assert.Null(report.Streams.Single().Verification!.IsVerified);
        Assert.Equal(1000, report.Streams.Single().LostSamples);
        Assert.Equal(5, Statistic(report, "Frames").Value?.AsInteger());
    }

    [Fact]
    public void AFileCutInsideTheLastFrameWithoutALastFrameInfoIsAChecksumFailure()
    {
        byte[][] frames = FrameParts(6);
        byte[] file = FramesFile(frames, isLastFrameInfoStored: false);

        Report report = Analyze(file[..^50]);

        // The last frame is flagged as the last one and no declared end says more follows: the data CRC-24 is all there is to say.
        Assert.Equal(["tak.frame-crc-mismatch"], Codes(report));
    }

    [Fact]
    public void AStreamThatEndsAfterAFrameNotFlaggedAsTheLastIsMissingFrames()
    {
        byte[][] frames = FrameParts(5).Select((f, i) => i == 4 ? Builder.Frame(4, 200) : f).ToArray();

        Report report = Analyze(FramesFile(frames, isLastFrameInfoStored: false, samples: SamplesOf(6)));

        Assert.Equal(["tak.frames-missing"], Codes(report));
        Assert.Equal(5, FrameNodes(report).Length);
        Assert.Null(report.Streams.Single().Verification!.IsVerified);
    }

    [Fact]
    public void ACutFrameThatIsNotFlaggedAsTheLastIsCutAndNotMissingFrames()
    {
        byte[][] frames = FrameParts(5).Select((f, i) => i == 4 ? Builder.Frame(4, 200) : f).ToArray();
        byte[] file = FramesFile(frames, isLastFrameInfoStored: false, samples: SamplesOf(6));

        Report report = Analyze(file[..^20]);

        Assert.Equal(["tak.frame-cut"], Codes(report));
    }

    [Fact]
    public void BytesBeforeTheFirstFrameAreSkippedAndFramesAreFoundAfterThem()
    {
        byte[][] frames = FrameParts(4);

        Report report = Analyze(FramesFile(frames, isLastFrameInfoStored: false, beforeFrames: new byte[25]));

        Finding skipped = Single(report, "tak.data-skipped");
        Assert.Equal("Skipped Data", skipped.Node?.Name);
        Assert.Equal(25, skipped.Node?.Length);
        Assert.Equal(["tak.data-skipped"], Codes(report));
        Assert.Equal(4, FrameNodes(report).Length);
        Assert.Contains(report.Root.Children.Single(n => n.Name == "TAK").Children, c => c.Name == "Skipped Data");
        Assert.Null(report.Streams.Single().Verification!.IsVerified);
    }

    [Fact]
    public void AMissingFrameBreaksTheNumbers()
    {
        byte[][] frames = [.. FrameParts(6).Where((_, i) => i != 2)];

        Report report = Analyze(FramesFile(frames, samples: SamplesOf(6)));

        Finding finding = Single(report, "tak.frame-number-discontinuity");
        Assert.Contains("number 3", finding.Message, StringComparison.Ordinal);
        Assert.Contains("is 2", finding.Message, StringComparison.Ordinal);
        Assert.Equal(["tak.frame-number-discontinuity"], Codes(report));
        Assert.Equal(5, FrameNodes(report).Length);
        Assert.Null(report.Streams.Single().Verification!.IsVerified);
    }

    [Fact]
    public void AFirstFrameThatIsNotNumberedZeroBreaksTheNumbersToo()
    {
        byte[][] frames = [Builder.Frame(7, 200, isInfo: true), Builder.Frame(8, 200, isLast: true)];

        Report report = Analyze(FramesFile(frames));

        Finding finding = Single(report, "tak.frame-number-discontinuity");
        Assert.Contains("the first frame has the number 0", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FramesThatHoldOtherSamplesThanTheStreamInfoStatesAreReported()
    {
        Report report = Analyze(FramesFile(FrameParts(4), samples: 99_999));

        Finding finding = Single(report, "tak.total-samples-mismatch");
        Assert.Contains("99999", finding.Message, StringComparison.Ordinal);
        Assert.Null(report.Streams.Single().Verification!.IsVerified);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(15)]
    public void AnInvalidFrameSizeTypeStatesNoSampleCountThatTheAnalyzerNeverKnew(int sizeType)
    {
        byte[][] frames = FrameParts(4);
        byte[] file = [.. "tBaK"u8, .. Builder.StreamInfo(sizeType: sizeType, samples: SamplesOf(4)), .. Builder.LastFrameInfo(frames.Take(3).Sum(f => f.Length), frames[3].Length), .. Builder.EncoderInfo(), .. Builder.End(), .. frames.SelectMany(f => f)];

        Report report = Analyze(file);

        Assert.DoesNotContain(report.Findings, f => f.Code == "tak.total-samples-mismatch");
    }

    [Fact]
    public void ALastFrameWithItsPaddingBitsSetIsAFrameAsTakDecoderReadsIt()
    {
        byte[][] frames = [Builder.Frame(0, 200, isInfo: true), Builder.Frame(1, 200, isLast: true, padding: 3)];

        Report report = Analyze(FramesFile(frames));

        Assert.Equal(2, FrameNodes(report).Length);
        Assert.DoesNotContain(report.Findings, f => f.Code == "tak.frames-missing");
    }

    [Fact]
    public void MetadataWithNoFrameAfterItIsReported()
    {
        byte[] file = [.. "tBaK"u8, .. Builder.StreamInfo(), .. Builder.EncoderInfo(), .. Builder.End()];

        Report report = Analyze(file);

        Assert.Equal(["tak.no-frames"], Codes(report));
        Assert.Empty(FrameNodes(report));
    }

    [Fact]
    public void AudioThatHoldsNoFrameHeaderIsSkippedAndHasNoFrames()
    {
        byte[] file = [.. "tBaK"u8, .. Builder.StreamInfo(), .. Builder.EncoderInfo(), .. Builder.End(), .. new byte[60]];

        Report report = Analyze(file);

        Assert.Equal(["tak.data-skipped", "tak.no-frames"], Codes(report));
    }

    [Fact]
    public void AnInfoFrameShowsItsStreamInfo()
    {
        Report report = Analyze(FramesFile(FrameParts(3)));

        Node header = FrameNodes(report)[0].Children.Single(c => c.Name == "Header");
        Node info = header.Children.Single(c => c.Name == "Stream Info");
        Assert.Equal(44100, info.Children.Single(c => c.Name == "Sample Rate").Value?.AsInteger());
        Assert.Equal("false", header.Children.Single(c => c.Name == "HasPreviousDistance").Value?.ToString());
        Node last = FrameNodes(report)[2].Children.Single(c => c.Name == "Header");
        Assert.Equal(1000, last.Children.Single(c => c.Name == "Samples").Value?.AsInteger());
        Assert.DoesNotContain(last.Children, c => c.Name == "Stream Info");
    }

    [Fact]
    public void FramesPastTheRecordsLimitAreCountedAndComparedButHaveNoNodes()
    {
        int count = FrameFacts.MaxEntries + 1000;

        Report report = Analyze(Builder.SmallFrames(count));

        Single(report, "tak.frames-limit");
        Assert.Equal(["tak.frames-limit"], Codes(report));
        Assert.Equal(FrameFacts.MaxEntries, FrameNodes(report).Length);
        Assert.Equal(count, Statistic(report, "Frames").Value?.AsInteger());
        ChecksumTally data = Tally(report, "frame data CRC-24");
        Assert.Equal((count, count, 0), (data.Stored, data.Checked, data.Failed));
        Assert.True(report.Streams.Single().Verification!.IsVerified);
    }

    [Fact]
    public void ANeverEndingRunOfSyncWordsIsWalkedInLinearTime()
    {
        byte[] sync = new byte[4 << 20];
        for (int i = 0; i + 1 < sync.Length; i += 2)
        {
            sync[i] = 0xFF;
            sync[i + 1] = 0xA0;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Report report = Analyze([.. "tBaK"u8, .. Builder.StreamInfo(), .. Builder.End(), .. sync]);

        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Took {watch.Elapsed}.");
    }
}
