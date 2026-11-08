using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// Withheld output and trims in the measurer and through the decode route (docs/design/lossy-decoders.md, "Withheld output" and decision 8):
// the positions a decoder reports with DecodeContext.Withhold are withheld, the rest of a jump is lost; the positions outside the range
// IDecodeSession.Trim states are trimmed and in no figure.
public class MeasurementWithheldTests
{
    private static readonly PcmFormat Mono = new(44100, 1, 16, SampleFormat.Integer);

    private static StreamMeasurer Measurer() => new(Mono, new MeasureOptions(), CancellationToken.None, _ => true);

    private static void Write(StreamMeasurer measurer, long position, int length, int value = 1000) =>
        measurer.Write(new PcmBlock(position, length, 1, length, Enumerable.Repeat(value, length).ToArray()));

    [Fact]
    public void WithheldPositionsOfAJumpAreWithheldAndTheRestIsLost()
    {
        StreamMeasurer measurer = Measurer();
        Write(measurer, 0, 1000);
        measurer.Withhold(1600, 400, WithholdReason.AfterGap);
        Write(measurer, 2000, 1000);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((2000L, 600L, 400L, 1), (m.Samples, m.LostSamples, m.WithheldSamples, m.Gaps));
        Assert.Equal(new WithheldCounts(400, 0, 0, 0), m.WithheldByReason);
        Assert.Equal(1000, m.InnerSkippedSamples);
        Assert.Equal(0, m.TrimmedSamples);
    }

    [Fact]
    public void AJumpThatIsWithheldWholeLosesNothingAndSpansOfTwoReasonsAreCountedApart()
    {
        StreamMeasurer measurer = Measurer();
        Write(measurer, 0, 500);
        measurer.Withhold(500, 300, WithholdReason.AfterGap);
        measurer.Withhold(800, 200, WithholdReason.EmptyFrame);
        measurer.Withhold(900, 50, WithholdReason.EmptyFrame);
        Write(measurer, 1000, 500);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((1000L, 0L, 500L), (m.Samples, m.LostSamples, m.WithheldSamples));
        Assert.Equal(new WithheldCounts(300, 200, 0, 0), m.WithheldByReason);
    }

    [Fact]
    public void ASpanOutsideAnyJumpCountsNothingAndDoesNotCarryOver()
    {
        StreamMeasurer measurer = Measurer();
        Write(measurer, 0, 500);
        measurer.Withhold(100, 100, WithholdReason.AfterGap);
        Write(measurer, 500, 500);
        Write(measurer, 2000, 500);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((1500L, 1000L, 0L), (m.Samples, m.LostSamples, m.WithheldSamples));
    }

    [Fact]
    public void SpansBeforeTheFirstBlockAndAfterTheLastAreCarvedOutOfTheLeadingAndTrailingLostSamples()
    {
        StreamMeasurer measurer = Measurer();
        measurer.Withhold(0, 300, WithholdReason.MainDataUnavailable);
        Write(measurer, 500, 1000);
        measurer.Withhold(1700, 100, WithholdReason.AfterGap);
        measurer.SetCodedRange(0, 2000);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((200L, 400L, 300L, 100L), (m.LeadingLostSamples, m.TrailingLostSamples, m.LeadingWithheldSamples, m.TrailingWithheldSamples));
        Assert.Equal((600L, 400L), (m.LostSamples, m.WithheldSamples));
        Assert.Equal(new WithheldCounts(100, 0, 300, 0), m.WithheldByReason);
        Assert.Equal(2, m.Gaps);
    }

    [Fact]
    public void ThePendingSpansAreBounded()
    {
        StreamMeasurer measurer = Measurer();
        Write(measurer, 0, 10);
        for (int i = 0; i < 5000; i++)
        {
            measurer.Withhold(100 + (i * 20), 10, (WithholdReason)(i % 2));
        }

        Write(measurer, 200000, 10);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Null(measurer.Failure);
        Assert.Equal(50000L, m.WithheldSamples);
        Assert.Equal(200000 - 10 - 50000L, m.LostSamples);
    }

    [Fact]
    public void TheTrimmedRangeLeavesTheTrimmedSamplesOutOfEveryFigure()
    {
        StreamMeasurer measurer = Measurer();
        measurer.Trim(300, 1700);
        Write(measurer, 0, 200, 30000);
        Write(measurer, 200, 400);
        Write(measurer, 600, 1000);
        Write(measurer, 1600, 100);
        Write(measurer, 1700, 300, 30000);
        Write(measurer, 2000, 400, 30000);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((1400L, 300L), (m.Samples, m.FirstPosition));
        Assert.Equal((300L, 700L, 1000L), (m.LeadingTrimmedSamples, m.TrailingTrimmedSamples, m.TrimmedSamples));
        Assert.Equal((0L, 0), (m.LostSamples, m.Gaps));
        Assert.InRange(m.Levels!.Stream.SamplePeakTenths, -310, -300);
    }

    [Fact]
    public void ALostBlockInsideTheTrimmedRangeIsStillLost()
    {
        StreamMeasurer measurer = Measurer();
        measurer.Trim(1000, long.MaxValue);
        Write(measurer, 0, 500);
        Write(measurer, 800, 400);
        Write(measurer, 1200, 300);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((700L, 1000L), (m.LeadingTrimmedSamples, m.FirstPosition));
        Assert.Equal((500L, 300L), (m.Samples, m.LostSamples));
        Assert.Equal(1, m.Gaps);
    }

    [Fact]
    public void WideningTheTrimAfterABlockPassedItsEndLeavesTheEndWhereItWas()
    {
        StreamMeasurer measurer = Measurer();
        measurer.Trim(0, 1000);
        Write(measurer, 0, 1000);
        Write(measurer, 1000, 1000);
        measurer.Trim(0, 5000);
        Write(measurer, 2000, 1000);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((1000L, 2000L, 0L, 0), (m.Samples, m.TrailingTrimmedSamples, m.LostSamples, m.Gaps));
    }

    [Fact]
    public void OverflowedSpansBeforeTheFirstBlockStayInTheLeadingRegion()
    {
        StreamMeasurer measurer = Measurer();
        for (int i = 0; i < 2000; i++)
        {
            measurer.Withhold(i * 20, 10, (WithholdReason)(i % 2));
        }

        Write(measurer, 100000, 10);
        Write(measurer, 200000, 10);
        measurer.SetCodedRange(0, 200010);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal(20000L, m.LeadingWithheldSamples);
        Assert.Equal(80000L, m.LeadingLostSamples);
        Assert.Equal(99990L, m.InnerLostSamples);
        Assert.Equal(199990L, m.SkippedSamples);
    }

    // Through the decode route.

    private sealed class WithholdingDecoder : IDecoder
    {
        public string Id => "test";

        public Action<DecodeContext, IDecodeSession?>? OnOpen { get; init; }

        public IFrameDecoder Open(DecodeContext context) => new Frames(context);

        private sealed class Frames(DecodeContext context) : IFrameDecoder
        {
            private IPcmSink? _sink;
            private readonly int[] _data = new int[100];

            public int Decode(in CodedFrame frame)
            {
                _sink ??= context.DeclareOutput(new PcmFormat(44100, 1, 16, SampleFormat.Integer));
                context.AddWork(DecodeRouteTests.Charge(context.Setup, frame.Samples));
                int skip = frame.FollowsGap ? 50 : 0;
                context.Withhold(frame.FirstSample, skip, WithholdReason.AfterGap);
                _sink.Write(new PcmBlock(frame.FirstSample + skip, frame.Samples - skip, 1, frame.Samples - skip, _data.AsSpan(0, frame.Samples - skip)));
                return frame.Samples - skip;
            }

            public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];
        }
    }

    private static Report AnalyzeWithGap(Func<int, CodedFrame, CodedFrame>? shape = null) =>
        new Analyzer([], [new DecodeRouteTests.FrameParser { IsLossy = true, Shape = (i, f) => shape?.Invoke(i, f) ?? (i == 1 ? f with { IsUnreadable = true } : f) }], [], [new WithholdingDecoder()])
            .Analyze("sample.bin", new MemoryStream(new byte[64]), new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } });

    [Fact]
    public void ADecoderThatWithholdsAfterAnUnreadableUnitIsCountedApartFromLostSamples()
    {
        Report report = AnalyzeWithGap();

        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;
        Assert.Equal(MeasurementStatus.Incomplete, m.Status);
        Assert.Equal((250L, 100L, 50L, 1), (m.Samples, m.LostSamples, m.WithheldSamples, m.Gaps));
        Assert.Equal(50L, m.WithheldByReason[WithholdReason.AfterGap]);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed");
        Assert.Equal(250L, Assert.Single(report.Streams).Verification!.DecodedSamples);
    }

    [Fact]
    public void TheWritersTellWithheldAndTrimmedSamples()
    {
        Report report = AnalyzeWithGap();
        StreamMeasurements m = report.Streams[0].Measurements!;

        string text = string.Join('\n', MeasurementText.Lines(m, isLossy: true).Select(l => l.Text));
        Assert.Contains("1 gap skipped 100 samples; 50 samples withheld (after a gap)", text);
        Assert.Contains("1 gap, 500 samples withheld (after a gap)", string.Join('\n', MeasurementText.Lines(m with { LostSamples = 0, WithheldSamples = 500, WithheldByReason = new WithheldCounts(500, 0, 0, 0) }, isLossy: true).Select(l => l.Text)));
        Assert.DoesNotContain("trimmed", text);

        using var stream = new MemoryStream();
        ReportJsonWriter.Write([report], stream);
        using JsonDocument json = JsonDocument.Parse(stream.ToArray());
        JsonElement measurements = json.RootElement[0].GetProperty("streams")[0].GetProperty("measurements");
        Assert.Equal(100, measurements.GetProperty("lostSamples").GetInt64());
        Assert.Equal(50, measurements.GetProperty("withheldSamples").GetInt64());
        Assert.Equal(50, measurements.GetProperty("withheldReasons").GetProperty("afterGap").GetInt64());
        Assert.False(measurements.TryGetProperty("trimmedSamples", out _));

        StreamMeasurements trimmed = m with { LeadingTrimmedSamples = 30, TrailingTrimmedSamples = 12, WithheldSamples = 0, WithheldByReason = default };
        Assert.Contains("42 samples trimmed", string.Join('\n', MeasurementText.Lines(trimmed, isLossy: false).Select(l => l.Text)));
    }

    [Fact]
    public void ABadWithholdIsADecoderBugAndASessionTrimsWhatTheParserStates()
    {
        var context = new DecodeContext(new DecodeSetup { Stream = new Node("Stream", 0, 0), Channels = 1, BitsPerSample = 16 }, CancellationToken.None, (_, _, _, _, _) => { }, _ => throw new InvalidOperationException(), _ => { });
        Assert.Throws<ArgumentOutOfRangeException>(() => context.Withhold(-1, 1, WithholdReason.AfterGap));
        Assert.Throws<ArgumentOutOfRangeException>(() => context.Withhold(0, -1, WithholdReason.AfterGap));
        Assert.Throws<ArgumentOutOfRangeException>(() => context.Withhold(0, 1, (WithholdReason)99));
        context.Withhold(0, 0, WithholdReason.AfterGap);
        context.Withhold(0, 5, WithholdReason.AfterGap);

        Report report = new Analyzer([], [new TrimmingParser()], [], [new WithholdingDecoder()]).Analyze("sample.bin", new MemoryStream(new byte[64]), new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } });
        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;
        Assert.Equal((300L, 100L, 100L, 100L), (m.Samples, m.FirstPosition, m.LeadingTrimmedSamples, m.TrailingTrimmedSamples));
        Assert.Equal(0, m.LostSamples);
    }

    [Fact]
    public void ARangeThatEndsBeforeItStartsIsAParserBug()
    {
        Report report = new Analyzer([], [new TrimmingParser { First = 400, End = 100 }], [], [new WithholdingDecoder()]).Analyze("sample.bin", new MemoryStream(new byte[64]), new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } });
        Assert.Contains(report.Findings, f => f.Code == "parse.failed");
    }

    private sealed class TrimmingParser : IFormatParser
    {
        public long First { get; init; } = 100;

        public long End { get; init; } = 400;

        public string Name => "Trim";

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context)
        {
            Node stream = context.Root.Add("Stream", 0, 64);
            byte[] data = new byte[64];
            IDecodeSession session = context.StartDecoding("test", new DecodeSetup { Stream = stream, Channels = 1, BitsPerSample = 16, Samples = 300, IsLossy = true });
            session.Trim(First, End);
            for (int i = 0; i < 5; i++)
            {
                session.Decode(new CodedFrame(new MappedBytes(data.AsMemory(i * 10, 10), i * 10), "Frame", i * 100, 100));
            }

            context.AddStream(new StreamInfo { Nodes = [stream], Codec = "Test", Verification = session.Finish([]) });
        }
    }
}
