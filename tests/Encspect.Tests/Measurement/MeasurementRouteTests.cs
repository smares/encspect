using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Measurement;

// The measurements through the decode route, with test-only parsers and decoders (like DecodeRouteTests): what the record holds in each
// case of docs/design/spectral-analysis.md, "Attaching the result", and the host's measure.* findings. The table of crafted inputs
// below is what FindingCoverageTests reads: every code the host emits has its entry, asserted present.
public class MeasurementRouteTests
{
    // A decoder that declares the format on its first frame and lets a test write the blocks.
    internal sealed class EmittingDecoder(PcmFormat? format, Action<IPcmSink, int> write, string id = "test") : IDecoder
    {
        public string Id => id;

        public IFrameDecoder Open(DecodeContext context) => new Frames(context, format, write);

        private sealed class Frames(DecodeContext context, PcmFormat? format, Action<IPcmSink, int> write) : IFrameDecoder
        {
            private IPcmSink? _sink;
            private int _index;

            public int Decode(in CodedFrame frame)
            {
                if (format is not null)
                {
                    _sink ??= context.DeclareOutput(format);
                    write(_sink, _index);
                }

                _index++;
                context.AddWork(DecodeRouteTests.Charge(context.Setup, frame.Samples));
                return frame.Samples;
            }

            public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];
        }
    }

    private static Report Analyze(IFormatParser parser, IEnumerable<IDecoder> decoders, DecodeOptions? decoding = null) =>
        new Analyzer([], [parser], [], decoders).Analyze("sample.bin", new MemoryStream(new byte[64]), new AnalysisOptions { Decoding = decoding ?? new DecodeOptions { Measure = new MeasureOptions() } });

    private static PcmBlock Ints(long position, int length, int channels, int[] storage) => new(position, length, channels, length, storage.AsSpan(0, length * channels));

    private static readonly int[] Zeros = new int[1 << 16];

    private static void Silence(IPcmSink sink, int index) => sink.Write(Ints(index * 100, 100, 2, Zeros));

    [Fact]
    public void AMeasuredStreamHasItsRecordAndAnUnmeasuredAnalysisHasNone()
    {
        var format = new PcmFormat(44100, 2, 16, SampleFormat.Integer) { ChannelLayout = [Speaker.FrontLeft, Speaker.FrontRight] };

        StreamMeasurements m = Assert.Single(Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(format, Silence)]).Streams).Measurements!;
        Assert.Equal(MeasurementStatus.Complete, m.Status);
        Assert.Equal((400L, 0L, 0, 44100, 2, 2), (m.Samples, m.LostSamples, m.Gaps, m.SampleRate, m.Channels, m.ChannelsMeasured));
        Assert.Equal([Speaker.FrontLeft, Speaker.FrontRight], m.ChannelLayout);
        Assert.Equal(1, m.Spectrum!.Frames);

        Report plain = Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(format, Silence)], new DecodeOptions());
        Assert.Null(Assert.Single(plain.Streams).Measurements);

        // Without decoding there is no session to measure.
        Report notDecoding = new Analyzer([], [new DecodeRouteTests.FrameParser()], [], [new EmittingDecoder(format, Silence)]).Analyze("s.bin", new MemoryStream(new byte[64]));
        Assert.Null(Assert.Single(notDecoding.Streams).Measurements);
    }

    [Fact]
    public void AStreamWithoutADecoderIsNotPossibleAndSaysWhy()
    {
        Report report = Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(null, Silence, "other")]);

        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;
        Assert.Equal((MeasurementStatus.NotPossible, "no decoder for test"), (m.Status, m.Reason));
        Assert.Equal("no decoder for TTA", Assert.Single(Analyze(new DecodeRouteTests.FrameParser { CodecId = "tta" }, [new EmittingDecoder(null, Silence, "other")]).Streams).Measurements!.Reason);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("measure.", StringComparison.Ordinal));
    }

    [Fact]
    public void AConfigurationTheHostRefusesIsNotPossible()
    {
        Report report = Analyze(new DecodeRouteTests.FrameParser { Channels = 0 }, [new EmittingDecoder(new PcmFormat(44100, 2, 16, SampleFormat.Integer), Silence)]);

        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;
        Assert.Equal(MeasurementStatus.NotPossible, m.Status);
        Assert.StartsWith("it declares 0 channels", m.Reason);
    }

    [Fact]
    public void ADecoderThatDeclaresNoOutputIsNotMeasuredAndRaisesNoFinding()
    {
        Report report = Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(null, Silence)]);

        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;
        Assert.Equal((MeasurementStatus.NotPossible, "the stream decodes to DSD, which is not measured"), (m.Status, m.Reason));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void ASampleRateOfZeroIsNotPossible()
    {
        Report report = Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(new PcmFormat(0, 2, 16, SampleFormat.Integer), Silence)]);

        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;
        Assert.Equal(MeasurementStatus.NotPossible, m.Status);
        Assert.Contains("sample rate", m.Reason);
    }

    [Fact]
    public void AStopBeforeTheFirstBlockLeavesStoppedWithNoFigures()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        Report report = Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(new PcmFormat(44100, 2, 16, SampleFormat.Integer), Silence)], new DecodeOptions { Stop = stop.Token, Measure = new MeasureOptions() });

        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;
        Assert.Equal(MeasurementStatus.Stopped, m.Status);
        Assert.Null(m.Spectrum);
    }

    [Fact]
    public void AStopInTheMiddleEndsMeasuringWithStoppedAndTheSamplesDecoded()
    {
        using var stop = new CancellationTokenSource();
        var format = new PcmFormat(44100, 2, 16, SampleFormat.Integer);
        var decoder = new EmittingDecoder(format, (sink, index) =>
        {
            Silence(sink, index);
            if (index == 1)
            {
                stop.Cancel();
            }
        });

        Report report = Analyze(new DecodeRouteTests.FrameParser(), [decoder], new DecodeOptions { Stop = stop.Token, Measure = new MeasureOptions() });

        StreamMeasurements m = Assert.Single(report.Streams).Measurements!;
        Assert.Equal(MeasurementStatus.Stopped, m.Status);
        Assert.Equal(200, m.Samples);
        Assert.Contains(report.Findings, f => f.Code == "decode.stopped");
    }

    // The table of crafted inputs: each measure.* code the host raises, asserted present.
    public static TheoryData<string> Codes() => new()
    {
        "measure.limit", "measure.failed", "measure.streams-limited", "measure.channels-limited", "measure.positions-jump", "measure.format-changed", "measure.non-finite-samples",
    };

    private static Report Raise(string code)
    {
        var format = new PcmFormat(44100, 2, 16, SampleFormat.Integer);
        switch (code)
        {
            case "measure.limit":
                return Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(format, Silence)], new DecodeOptions { Measure = new MeasureOptions { MaxWork = 1000 } });
            case "measure.failed":
                return Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(format, Silence)], new DecodeOptions { Measure = new MeasureOptions { Probe = stage => { if (stage == "block") { throw new InvalidOperationException("test"); } } } });
            case "measure.channels-limited":
                int[] wide = new int[100 * 40];
                return Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(new PcmFormat(44100, 40, 16, SampleFormat.Integer), (sink, index) => sink.Write(Ints(index * 100, 100, 40, wide)))]);
            case "measure.positions-jump":
                return Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(format, (sink, index) => sink.Write(Ints(index == 2 ? 50 : index * 100, 100, 2, Zeros)))]);
            case "measure.format-changed":
                float[] floats = new float[200];
                return Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(format, (sink, index) =>
                {
                    if (index == 2)
                    {
                        sink.Write(new PcmBlock(200, 100, 2, 100, floats));
                    }
                    else
                    {
                        Silence(sink, index);
                    }
                })]);
            case "measure.non-finite-samples":
                float[] bad = new float[200];
                bad.AsSpan().Fill(float.NaN);
                return Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(new PcmFormat(44100, 2, 32, SampleFormat.Float32), (sink, index) => sink.Write(new PcmBlock(index * 100, 100, 2, 100, bad)))]);
            default:
                return RaiseStreamsLimited();
        }
    }

    // Three streams of 32 channels at 352.8 kHz hold 26 MiB of state each at once; the third does not fit the bound of 64 MiB.
    private static Report RaiseStreamsLimited()
    {
        int[] wide = new int[100 * 32];
        var decoder = new EmittingDecoder(new PcmFormat(352800, 32, 16, SampleFormat.Integer), (sink, index) => sink.Write(Ints(index * 100, 100, 32, wide)));
        return new Analyzer([], [new ThreeStreams()], [], [decoder]).Analyze("sample.bin", new MemoryStream(new byte[64]), new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } });
    }

    // Three sessions open at the same time, each given one frame, then finished.
    private sealed class ThreeStreams : IFormatParser
    {
        public string Name => "Test";

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context)
        {
            var sessions = new List<(IDecodeSession Session, Node Stream)>();
            for (int i = 0; i < 3; i++)
            {
                Node stream = context.Root.Add("Stream", 0, 64);
                IDecodeSession session = context.StartDecoding("test", new DecodeSetup { Stream = stream, Channels = 32, BitsPerSample = 16, Samples = 100 });
                if (session.IsDecoding)
                {
                    session.Decode(new CodedFrame(new MappedBytes(new byte[16], 0), "Frame", 0, 100));
                }

                sessions.Add((session, stream));
            }

            foreach ((IDecodeSession session, Node stream) in sessions)
            {
                context.AddStream(new StreamInfo { Nodes = [stream], Codec = "Test", Verification = session.Finish([]) });
            }
        }
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void EveryMeasureFindingIsRaisedByACraftedStream(string code)
    {
        Report report = Raise(code);

        Finding finding = Assert.Single(report.Findings, f => f.Code == code);
        Assert.Equal("Stream", finding.Node!.Name);
        StreamMeasurements m = report.Streams[code == "measure.streams-limited" ? 2 : 0].Measurements!;
        Assert.Equal(code switch
        {
            "measure.failed" => MeasurementStatus.Failed,
            "measure.format-changed" => MeasurementStatus.FormatChanged,
            "measure.streams-limited" => MeasurementStatus.NotPossible,
            "measure.channels-limited" or "measure.non-finite-samples" => MeasurementStatus.Complete,
            _ => MeasurementStatus.Stopped,
        }, m.Status);

        // None of them changes the verification or the decode findings.
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal));
        Assert.All(report.Streams, s => Assert.Equal(DecodeStatus.Decoded, s.Verification!.Decoding));
    }

    [Fact]
    public void TheLimitFindingNamesTheBudgetAndWhatWasMeasured()
    {
        Finding finding = Assert.Single(Raise("measure.limit").Findings, f => f.Code == "measure.limit");

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("Measuring stopped after 0.0 s of the stream: its work passed the budget of 1,000 work units.", finding.Message);
    }

    [Fact]
    public void AFormatChangeKeepsWhatCameBeforeAndTheLimitsOfTheStreamAreInfos()
    {
        Report changed = Raise("measure.format-changed");
        StreamMeasurements m = changed.Streams[0].Measurements!;

        Assert.Equal(200, m.Samples);
        Assert.Equal(1, m.Spectrum!.Frames);
        Assert.Equal(Severity.Info, changed.Findings.Single(f => f.Code == "measure.format-changed").Severity);
        Assert.Equal(Severity.Info, Raise("measure.positions-jump").Findings.Single(f => f.Code == "measure.positions-jump").Severity);
        Assert.Equal(Severity.Error, Raise("measure.failed").Findings.Single(f => f.Code == "measure.failed").Severity);
        Assert.Equal(Severity.Warning, Raise("measure.non-finite-samples").Findings.Single(f => f.Code == "measure.non-finite-samples").Severity);
    }

    [Fact]
    public void ABugInTheMeasurerDropsItsFiguresAndLeavesDecodingAlone()
    {
        Report report = Raise("measure.failed");

        StreamMeasurements m = report.Streams[0].Measurements!;
        Assert.Equal(MeasurementStatus.Failed, m.Status);
        Assert.Null(m.Spectrum);
        Assert.Equal(400, report.Streams[0].Verification!.DecodedSamples);
        Assert.Contains("InvalidOperationException: test", report.Findings.Single(f => f.Code == "measure.failed").Message);
    }

    // A bug in making the measurer (called inside DeclareOutput, by the decoder) or in making its figures (at the close) is the stream's
    // measure.failed too: never a parse.failed or decode.failed, and the verification stays what it is.
    [Theory]
    [InlineData("estimate")]
    [InlineData("open")]
    [InlineData("block")]
    [InlineData("result")]
    [InlineData("bytes")]
    public void AFailureAtAnyStageBecomesMeasureFailedAndLeavesDecodingAlone(string stage)
    {
        var format = new PcmFormat(44100, 2, 16, SampleFormat.Integer);
        Report report = Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(format, Silence)],
            new DecodeOptions { Measure = new MeasureOptions { Probe = s => { if (s == stage) { throw new InvalidOperationException("test " + s); } } } });

        StreamMeasurements m = report.Streams[0].Measurements!;
        Assert.Equal(MeasurementStatus.Failed, m.Status);
        Assert.Null(m.Spectrum);
        Assert.Equal((44100, 2), (m.SampleRate, m.Channels));
        Assert.Contains($"InvalidOperationException: test {stage}", report.Findings.First(f => f.Code == "measure.failed").Message);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Equal(400, report.Streams[0].Verification!.DecodedSamples);
        Assert.Equal(DecodeStatus.Decoded, report.Streams[0].Verification!.Decoding);
    }

    // The record stays in the report, so its share stays reserved: 600 streams whose grids hold 513 columns each (0.25 MiB) would keep 150 MB
    // without the bound, and the bound of 64 MiB lets about 250 of them be measured; the rest say so.
    [Fact]
    public void TheRecordsOfManyStreamsStayWithinTheMemoryBound()
    {
        int[] block = new int[4096];
        var decoder = new EmittingDecoder(new PcmFormat(44100, 1, 16, SampleFormat.Integer), (sink, index) => sink.Write(new PcmBlock(index == 0 ? 0 : 1L << 40, 4096, 1, 4096, block)));

        Report report = new Analyzer([], [new ManyStreams(600)], [], [decoder]).Analyze("sample.bin", new MemoryStream(new byte[64]), new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } });

        long kept = report.Streams.Sum(s => s.Measurements!.Spectrogram?.Power.Length ?? 0L);
        int measured = report.Streams.Count(s => s.Measurements!.Status == MeasurementStatus.Complete);
        Assert.InRange(kept, 1, DecodeRun_MeasureMemoryBound);
        Assert.InRange(measured, 20, 599);
        Assert.Equal(600 - measured, report.Findings.Where(f => f.Code == "measure.streams-limited").Sum(f => f.Count));
    }

    private const long DecodeRun_MeasureMemoryBound = 64L << 20;

    // Streams one after another, each decoded two frames and finished before the next starts.
    private sealed class ManyStreams(int count) : IFormatParser
    {
        public string Name => "Test";

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context)
        {
            for (int i = 0; i < count; i++)
            {
                Node stream = context.Root.Add("Stream", 0, 64);
                IDecodeSession session = context.StartDecoding("test", new DecodeSetup { Stream = stream, Channels = 1, BitsPerSample = 16, Samples = 200 });
                for (int frame = 0; frame < 2 && session.IsDecoding; frame++)
                {
                    session.Decode(new CodedFrame(new MappedBytes(new byte[16], 0), "Frame", frame * 100, 100));
                }

                context.AddStream(new StreamInfo { Nodes = [stream], Codec = "Test", Verification = session.Finish([]) });
            }
        }
    }

    [Fact]
    public void MoreThanThirtyTwoChannelsAreMeasuredAsThirtyTwo()
    {
        StreamMeasurements m = Raise("measure.channels-limited").Streams[0].Measurements!;

        Assert.Equal((40, 32), (m.Channels, m.ChannelsMeasured));
        Assert.Equal(32, m.Spectrum!.PerChannel.Count);
    }

    [Fact]
    public void ABudgetLoweredBelowTheMeasurementsChangesNoVerificationNorDecodeFinding()
    {
        var format = new PcmFormat(44100, 2, 16, SampleFormat.Integer);
        Report plain = Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(format, Silence)], new DecodeOptions());
        Report starved = Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(format, Silence)], new DecodeOptions { Measure = new MeasureOptions { MaxWork = 1 } });

        Assert.Equal(plain.Streams[0].Verification!.DecodedSamples, starved.Streams[0].Verification!.DecodedSamples);
        Assert.Equal(plain.Findings.Select(f => f.Code), starved.Findings.Where(f => !f.Code.StartsWith("measure.", StringComparison.Ordinal)).Select(f => f.Code));
        Assert.Equal(0, starved.Streams[0].Measurements!.Samples);
    }

    [Fact]
    public void TheMeasurementsOfAFloatStreamClampHugeValuesAndCountNaN()
    {
        float[] values = new float[200];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (i % 4) switch { 0 => float.NaN, 1 => float.NegativeInfinity, 2 => 1e30f, _ => 0.5f };
        }

        Report report = Analyze(new DecodeRouteTests.FrameParser(), [new EmittingDecoder(new PcmFormat(44100, 2, 32, SampleFormat.Float32), (sink, index) => sink.Write(new PcmBlock(index * 100, 100, 2, 100, values)))]);

        StreamMeasurements m = report.Streams[0].Measurements!;
        Assert.True(m.HasClampedSamples);
        Assert.Equal(400, m.NonFiniteSamples);
        Assert.All(m.Spectrum!.Combined.ToArray(), v => Assert.True(v < 1000));
    }
}
