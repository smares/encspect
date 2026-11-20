using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// Units whose checksum fails (docs/design/damaged-units.md): their output is still written, and the measurements leave it out as withheld samples
// ("checksum failed") and resume after it as after a gap.
public class MeasurementDamagedUnitsTests
{
    private static readonly PcmFormat Mono = new(44100, 1, 16, SampleFormat.Integer);

    private static StreamMeasurer Measurer() => new(Mono, new MeasureOptions(), CancellationToken.None, _ => true);

    private static void Write(StreamMeasurer measurer, long position, int length, int value = 1000) =>
        measurer.Write(new PcmBlock(position, length, 1, length, Enumerable.Repeat(value, length).ToArray()));

    [Fact]
    public void AFailedUnitIsWithheldAsOneGapAndLosesNothing()
    {
        StreamMeasurer measurer = Measurer();
        Write(measurer, 0, 1000);
        measurer.WithholdWritten(1000, 500);
        Write(measurer, 1000, 500);
        Write(measurer, 1500, 1000);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((2000L, 0L, 500L, 1), (m.Samples, m.LostSamples, m.WithheldSamples, m.Gaps));
        Assert.Equal(new WithheldCounts(0, 0, 0, 0, 500), m.WithheldByReason);
        Assert.Equal(500, m.InnerWithheldSamples);
    }

    [Fact]
    public void ARunOfFailedUnitsIsOneGap()
    {
        StreamMeasurer measurer = Measurer();
        Write(measurer, 0, 1000);
        measurer.WithholdWritten(1000, 250);
        measurer.WithholdWritten(1250, 250);
        Write(measurer, 1000, 250);
        Write(measurer, 1250, 250);
        measurer.WithholdWritten(1500, 100);
        Write(measurer, 1500, 100);
        Write(measurer, 1600, 400);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((1400L, 0L, 600L, 1), (m.Samples, m.LostSamples, m.WithheldSamples, m.Gaps));
    }

    [Fact]
    public void ASpanInsideOneBlockIsCutOutOfIt()
    {
        StreamMeasurer measurer = Measurer();
        measurer.WithholdWritten(1000, 500);
        measurer.WithholdWritten(2200, 100);
        Write(measurer, 0, 3000);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((2400L, 0L, 600L, 2), (m.Samples, m.LostSamples, m.WithheldSamples, m.Gaps));
    }

    [Fact]
    public void AFailedFirstAndLastUnitAreLeadingAndTrailingWithheld()
    {
        StreamMeasurer measurer = Measurer();
        measurer.WithholdWritten(0, 300);
        Write(measurer, 0, 300);
        Write(measurer, 300, 700);
        measurer.WithholdWritten(1000, 200);
        Write(measurer, 1000, 200);
        measurer.SetCodedRange(0, 1200);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((700L, 300L), (m.Samples, m.FirstPosition));
        Assert.Equal((300L, 200L, 0L), (m.LeadingWithheldSamples, m.TrailingWithheldSamples, m.LostSamples));
        Assert.Equal(2, m.Gaps);
        Assert.Equal(500L, m.WithheldByReason[WithholdReason.ChecksumFailed]);
    }

    [Fact]
    public void AFailedUnitAndAWithheldSpanThatFollowsItAreOneGapCountedByTheirOwnReasons()
    {
        StreamMeasurer measurer = Measurer();
        Write(measurer, 0, 1000);
        measurer.WithholdWritten(1000, 200);
        Write(measurer, 1000, 200);
        measurer.Withhold(1200, 100, WithholdReason.AfterGap);
        Write(measurer, 1300, 700);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((1700L, 0L, 300L, 1), (m.Samples, m.LostSamples, m.WithheldSamples, m.Gaps));
        Assert.Equal(new WithheldCounts(100, 0, 0, 0, 200), m.WithheldByReason);
    }

    [Fact]
    public void SpansPastTheCapAreMeasuredAndNothingFails()
    {
        StreamMeasurer measurer = Measurer();
        for (int i = 0; i < StreamMeasurer.MaxPendingSpans + 76; i++)
        {
            measurer.WithholdWritten(10 + (2 * i), 1);
        }

        Write(measurer, 0, 3000);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Null(measurer.Failure);
        Assert.Equal((long)StreamMeasurer.MaxPendingSpans, m.WithheldSamples);
        Assert.Equal(3000 - StreamMeasurer.MaxPendingSpans, m.Samples);
    }

    [Fact]
    public void ASpanInTheKeptRangeBesideTheEndTrimIsWithheldAndTheTrimmedPartIsTrimmed()
    {
        StreamMeasurer measurer = Measurer();
        measurer.Trim(0, 2000);
        measurer.WithholdWritten(500, 300);
        Write(measurer, 0, 3000);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((1700L, 1000L, 300L, 1), (m.Samples, m.TrailingTrimmedSamples, m.WithheldSamples, m.Gaps));
    }

    [Fact]
    public void ASpanThatCrossesTheEndTrimCountsOnlyItsPartInsideTheStream()
    {
        StreamMeasurer measurer = Measurer();
        measurer.Trim(0, 2000);
        measurer.WithholdWritten(1800, 700);
        Write(measurer, 0, 3000);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((1800L, 200L, 1000L), (m.Samples, m.WithheldSamples, m.TrimmedSamples));
    }

    [Fact]
    public void ASpanThatCrossesTheStartTrimCountsOnlyItsPartInsideTheStream()
    {
        StreamMeasurer measurer = Measurer();
        measurer.Trim(1000, long.MaxValue);
        measurer.WithholdWritten(500, 1000);
        Write(measurer, 0, 500);
        Write(measurer, 500, 1000);
        Write(measurer, 1500, 1500);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((1500L, 500L, 1000L), (m.Samples, m.WithheldSamples, m.TrimmedSamples));
        Assert.Equal(500L, m.WithheldByReason[WithholdReason.ChecksumFailed]);
    }

    [Fact]
    public void ASpanThatCrossesAnInnerTrimCountsOnlyItsPartOutsideIt()
    {
        StreamMeasurer measurer = Measurer();
        measurer.TrimInner(1000, 200);
        measurer.WithholdWritten(900, 200);
        Write(measurer, 0, 3000);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((2700L, 100L, 200L), (m.Samples, m.WithheldSamples, m.InnerTrimmedSamples));
    }

    [Fact]
    public void AStreamWhoseEveryUnitFailsCountsOnlyThePositionsInsideTheTrim()
    {
        StreamMeasurer measurer = Measurer();
        measurer.Trim(100, 900);
        measurer.WithholdWritten(0, 1000);
        Write(measurer, 0, 1000);
        measurer.SetCodedRange(0, 1000);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal((0L, 800L), (m.Samples, m.WithheldSamples));
    }

    [Fact]
    public void AStreamWhoseEveryUnitFailsMeasuresNothingAndSaysWhy()
    {
        StreamMeasurer measurer = Measurer();
        measurer.WithholdWritten(0, 400);
        for (int i = 0; i < 4; i++)
        {
            Write(measurer, i * 100, 100);
        }

        measurer.SetCodedRange(0, 400);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Null(measurer.Failure);
        Assert.Equal((MeasurementStatus.Complete, 0L, 400L), (m.Status, m.Samples, m.WithheldSamples));
        Assert.Equal(400L, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Null(m.Spectrum);
        Assert.Contains("400 samples withheld (checksum failed)", string.Join('\n', MeasurementText.Lines(m, false)));
    }

    // A block that lies wholly in a failed unit is left out before it is looked at, so a damaged unit whose block does not fit the declared output
    // does not end the measuring.
    [Fact]
    public void ABlockOfAnotherFormatWhollyInsideASpanIsLeftOutAndDoesNotStopTheMeasuring()
    {
        StreamMeasurer measurer = Measurer();
        Write(measurer, 0, 100);
        measurer.WithholdWritten(100, 100);
        measurer.Write(new PcmBlock(100, 100, 2, 100, new int[200]));
        Write(measurer, 200, 100);
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.False(measurer.HasFormatChanged);
        Assert.Null(measurer.Failure);
        Assert.Equal((200L, 100L), (m.Samples, m.WithheldSamples));
    }

    // Through the decode session: a decoder that writes every unit, as FLAC and TTA do.

    private sealed class WritingDecoder : IDecoder
    {
        public string Id => "test";

        public bool WithholdsInTheDecoder { get; init; }

        public IFrameDecoder Open(DecodeContext context) => new Frames(context, WithholdsInTheDecoder);

        private sealed class Frames(DecodeContext context, bool withholds) : IFrameDecoder
        {
            private IPcmSink? _sink;

            public int Decode(in CodedFrame frame)
            {
                _sink ??= context.DeclareOutput(new PcmFormat(44100, 1, 16, SampleFormat.Integer));
                context.AddWork(DecodeRouteTests.Charge(context.Setup, frame.Samples));
                if (withholds && frame.FirstSample == 100)
                {
                    context.WithholdFromMeasurements(frame.FirstSample, 40);
                }

                _sink.Write(new PcmBlock(frame.FirstSample, frame.Samples, 1, frame.Samples, new int[frame.Samples]));
                return frame.Samples;
            }

            public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [new ChecksumResult("stream MD5", 1, end.IsComplete ? 0 : 1)];
        }
    }

    private static (Report Report, PcmHashingSink Sink) Analyze(Func<int, CodedFrame, CodedFrame> shape, bool withholdsInTheDecoder = false)
    {
        var sink = new PcmHashingSink();
        Report report = new Analyzer([], [new DecodeRouteTests.FrameParser { Shape = shape }], [], [new WritingDecoder { WithholdsInTheDecoder = withholdsInTheDecoder }])
            .Analyze("sample.bin", new MemoryStream(new byte[64]), new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = new MeasureOptions() } });
        return (report, sink);
    }

    [Fact]
    public void AFailedUnitIsWithheldFromTheMeasurementsAndLeavesTheDecodingAsItIs()
    {
        (Report report, PcmHashingSink sink) = Analyze((i, f) => i == 1 ? f with { FailsChecksum = true } : f);
        StreamInfo stream = Assert.Single(report.Streams);
        StreamMeasurements m = stream.Measurements!;

        Assert.Equal((300L, 0L, 100L, 1), (m.Samples, m.LostSamples, m.WithheldSamples, m.Gaps));
        Assert.Equal(100L, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(400L, stream.Verification!.DecodedSamples);
        Assert.Equal(400L, sink.Samples);
        Assert.True(stream.Verification.Checksums.All(c => c.Failed == 0));
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed");
    }

    [Fact]
    public void AFailedFirstUnitIsKnownBeforeTheOutputIsDeclared()
    {
        (Report report, PcmHashingSink sink) = Analyze((i, f) => i == 0 ? f with { FailsChecksum = true } : f);
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Equal((300L, 100L, 100L, 100L), (m.Samples, m.FirstPosition, m.WithheldSamples, m.LeadingWithheldSamples));
        Assert.Equal(400L, sink.Samples);
    }

    [Fact]
    public void AStreamWhereEveryUnitFailsMeasuresNothingWithoutAnError()
    {
        (Report report, PcmHashingSink sink) = Analyze((_, f) => f with { FailsChecksum = true });
        StreamInfo stream = Assert.Single(report.Streams);
        StreamMeasurements m = stream.Measurements!;

        Assert.Equal((MeasurementStatus.Complete, 0L, 400L), (m.Status, m.Samples, m.WithheldSamples));
        Assert.Equal(400L, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(400L, stream.Verification!.DecodedSamples);
        Assert.Equal(400L, sink.Samples);
        Assert.DoesNotContain(report.Findings, f => f.Severity >= Severity.Warning);
    }

    [Fact]
    public void ADecoderReportsPositionsItStillWritesWithTheContext()
    {
        (Report report, PcmHashingSink sink) = Analyze((_, f) => f, withholdsInTheDecoder: true);
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Equal((360L, 40L, 1), (m.Samples, m.WithheldSamples, m.Gaps));
        Assert.Equal(400L, sink.Samples);
    }

    [Fact]
    public void AnUnreadableFailedUnitIsLostAndNotWithheld()
    {
        (Report report, _) = Analyze((i, f) => i == 1 ? f with { FailsChecksum = true, IsUnreadable = true } : f);
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Equal((100L, 0L), (m.LostSamples, m.WithheldSamples));
    }
}
