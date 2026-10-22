using System.Runtime.ExceptionServices;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The decode route, with test-only parsers and decoders (docs/design/decoders.md): what the host's sessions do with the
// frames a parser hands them, the exception boundary, the work budget, the front end's stop and sink, and the
// verification each stream ends with.
public class DecodeRouteTests
{
    private const int FrameBytes = 16;
    private const int FrameSamples = 100;

    // A decoder whose behavior the test scripts. One instance serves one test, but keeps its per-stream state in the
    // IFrameDecoder it opens, as the contract asks.
    private sealed class ScriptedDecoder(string id = "test") : IDecoder
    {
        public Func<DecodeContext, IFrameDecoder>? OnOpen { get; init; }

        public Func<CodedFrame, int>? OnDecode { get; init; }

        public Func<DecodeEnd, IReadOnlyList<ChecksumResult>>? OnFinish { get; init; }

        public Action<DecodeContext>? OnOpened { get; init; }

        public List<DecodeEnd> Ends { get; } = [];

        public List<long> FirstSamples { get; } = [];

        public int Opens { get; private set; }

        public string Id => id;

        public IFrameDecoder Open(DecodeContext context)
        {
            Opens++;
            OnOpened?.Invoke(context);
            return OnOpen?.Invoke(context) ?? new Frames(this, context);
        }

        private sealed class Frames(ScriptedDecoder owner, DecodeContext context) : IFrameDecoder
        {
            public int Decode(in CodedFrame frame)
            {
                owner.FirstSamples.Add(frame.FirstSample);
                return owner.OnDecode is { } decode ? decode(frame) : frame.Samples;
            }

            public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end)
            {
                owner.Ends.Add(end);
                _ = context;
                return owner.OnFinish?.Invoke(end) ?? [];
            }
        }
    }

    // A format parser over 64 bytes that reads four frames of 16 bytes and decodes them, as the design's FLAC outline does.
    private sealed class FrameParser : IFormatParser
    {
        public string CodecId { get; init; } = "test";

        public int Frames { get; init; } = 4;

        public bool Finishes { get; init; } = true;

        public int Channels { get; init; } = 2;

        public int BitsPerSample { get; init; } = 16;

        public long? Samples { get; init; } = 400;

        public bool IsLossy { get; init; }

        public Func<int, CodedFrame, CodedFrame>? Shape { get; init; }

        public IReadOnlyList<ChecksumTally> Stored { get; init; } =
        [
            new ChecksumTally { Name = "frame CRC", Covers = ChecksumCoverage.CodedData, CoversAllAudio = true, Stored = 4, Checked = 4 },
            new ChecksumTally { Name = "stream MD5", Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = true, Stored = 1 },
        ];

        public int FramesRead { get; private set; }

        public IDecodeSession? Session { get; private set; }

        public StreamVerification? Verification { get; private set; }

        public string Name => "Test";

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context)
        {
            Node stream = context.Root.Add("Stream", 0, 64);
            byte[] data = new byte[64];
            Session = context.StartDecoding(CodecId, new DecodeSetup
            {
                Stream = stream,
                Channels = Channels,
                BitsPerSample = BitsPerSample,
                Samples = Samples,
                IsLossy = IsLossy,
            });

            for (int i = 0; i < Frames; i++)
            {
                FramesRead++;
                stream.Add("Frame", i * FrameBytes, FrameBytes);
                if (Session.IsDecoding)
                {
                    var frame = new CodedFrame(new MappedBytes(data.AsMemory(i * FrameBytes, FrameBytes), i * FrameBytes), "Frame", i * FrameSamples, FrameSamples);
                    Session.Decode(Shape is null ? frame : Shape(i, frame));
                }
            }

            if (Finishes)
            {
                Verification = Session.Finish(Stored);
                context.AddStream(new StreamInfo { Nodes = [stream], Codec = "Test", Verification = Verification });
            }
        }
    }

    private static Report Analyze(IFormatParser parser, IEnumerable<IDecoder> decoders, AnalysisOptions? options = null) =>
        new Analyzer([], [parser], [], decoders).Analyze("sample.bin", new MemoryStream(new byte[64]), options);

    private static readonly AnalysisOptions Verify = new() { Decoding = new DecodeOptions() };

    private static ChecksumTally Tally(string name, bool decoded, long stored, long checkedValues, bool all = true, long failed = 0) =>
        new() { Name = name, Covers = decoded ? ChecksumCoverage.DecodedAudio : ChecksumCoverage.CodedData, CoversAllAudio = all, Stored = stored, Checked = checkedValues, Failed = failed };

    private static IReadOnlyList<ChecksumResult> Md5(long checkedValues = 1, long failed = 0) => [new ChecksumResult("stream MD5", checkedValues, failed)];

    // The rule for IsVerified, case by case.

    [Fact]
    public void IsVerifiedIsFalseWhereAStoredChecksumDiffers()
    {
        var verification = new StreamVerification
        {
            Decoding = DecodeStatus.Decoded,
            Checksums = [Tally("a", false, 4, 4), Tally("b", true, 1, 1, failed: 1)],
        };
        Assert.False(verification.IsVerified);
    }

    [Fact]
    public void IsVerifiedIsTrueOnlyWhenEverythingStoredWasComparedAndSomethingCoversAllTheAudio()
    {
        StreamVerification Of(params ChecksumTally[] tallies) => new() { Decoding = DecodeStatus.Decoded, Checksums = tallies };

        Assert.True(Of(Tally("crc", false, 4, 4), Tally("md5", true, 1, 1)).IsVerified);
        Assert.Null(Of(Tally("crc", false, 4, 4), Tally("md5", true, 1, 0)).IsVerified);
        Assert.Null(Of(Tally("crc", false, 4, 3)).IsVerified);
        Assert.Null(Of(Tally("crc", false, 4, 4, all: false)).IsVerified);
        Assert.Null(Of().IsVerified);
        Assert.Null(Of(Tally("header", false, 4, 4, all: false)).IsVerified);
    }

    [Fact]
    public void ATallyWithNothingStoredCountsForNothing()
    {
        StreamVerification Of(params ChecksumTally[] tallies) => new() { Decoding = DecodeStatus.Decoded, Checksums = tallies };

        Assert.Null(Of(Tally("md5", true, 0, 0)).IsVerified);
        Assert.Null(Of(Tally("md5", true, 0, 0), Tally("crc", false, 4, 4, all: false)).IsVerified);
        Assert.True(Of(Tally("md5", true, 0, 0), Tally("crc", false, 4, 4)).IsVerified);
    }

    [Fact]
    public void APerFrameChecksumThatDoesNotCoverAllTheAudioDoesNotVerifyThoughEveryValueHolds()
    {
        // A gap, or fewer samples than the stream states: the parser clears CoversAllAudio.
        var verification = new StreamVerification { Decoding = DecodeStatus.NotRequested, Checksums = [Tally("frame CRC-16", false, 40, 40, all: false)] };
        Assert.Null(verification.IsVerified);
    }

    // What the host does with a stream.

    [Fact]
    public void WithoutDecodingAskedForTheVerificationListsWhatWasNotCompared()
    {
        var decoder = new ScriptedDecoder();
        var parser = new FrameParser();

        Report report = Analyze(parser, [decoder]);

        Assert.Equal(0, decoder.Opens);
        Assert.False(parser.Session!.IsDecoding);
        StreamVerification verification = Assert.Single(report.Streams).Verification!;
        Assert.Equal(DecodeStatus.NotRequested, verification.Decoding);
        Assert.Equal(0, verification.DecodedSamples);
        Assert.Null(verification.IsVerified);
        Assert.Equal(["frame CRC", "stream MD5"], verification.Checksums.Select(c => c.Name));
        ChecksumTally md5 = verification.Checksums[1];
        Assert.Equal((1L, 0L, 1L), (md5.Stored, md5.Checked, md5.NotChecked));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void WithoutADecoderTheStreamSaysSoAndOnlyDecodingAskedForAddsAFinding()
    {
        Report quiet = Analyze(new FrameParser(), []);
        Assert.Equal(DecodeStatus.NoDecoder, Assert.Single(quiet.Streams).Verification!.Decoding);
        Assert.Empty(quiet.Findings);

        Report asked = Analyze(new FrameParser(), [new ScriptedDecoder("other")], Verify);
        Assert.Equal(DecodeStatus.NoDecoder, Assert.Single(asked.Streams).Verification!.Decoding);
        Finding finding = Assert.Single(asked.Findings);
        Assert.Equal(("decode.no-decoder", Severity.Info), (finding.Code, finding.Severity));
        Assert.Equal("Stream", finding.Node!.Name);
        Assert.Equal([0], finding.Path);
    }

    [Fact]
    public void AParserRunWithoutARouterGetsASessionThatDecodesNothing()
    {
        var context = new ParseContext(new ByteReader(new MemoryStream(new byte[8]), CancellationToken.None), new Node("File", 0, 8));
        IDecodeSession session = context.StartDecoding("test", new DecodeSetup { Stream = context.Root, Channels = 1, BitsPerSample = 8, IsLossy = true });
        Assert.False(session.IsDecoding);
        StreamVerification verification = session.Finish([Tally("crc", false, 2, 2)]);
        Assert.Equal(DecodeStatus.NoDecoder, verification.Decoding);
        Assert.True(verification.IsLossy);
        Assert.Same(verification, session.Finish([]));
    }

    [Fact]
    public void DecodedResultsFillTheTalliesOverDecodedAudio()
    {
        var decoder = new ScriptedDecoder { OnFinish = _ => Md5() };

        Report report = Analyze(new FrameParser(), [decoder], Verify);

        StreamVerification verification = Assert.Single(report.Streams).Verification!;
        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.Equal(400, verification.DecodedSamples);
        Assert.True(verification.IsVerified);
        Assert.Equal((1L, 0L), (verification.Checksums[1].Checked, verification.Checksums[1].Failed));
        Assert.Equal((4L, 0L), (verification.Checksums[0].Checked, verification.Checksums[0].Failed));
        Assert.Empty(report.Findings);
        Assert.Equal([0L, 100, 200, 300], decoder.FirstSamples);
    }

    [Fact]
    public void AFailedChecksumOverDecodedAudioMakesTheStreamNotVerified()
    {
        var decoder = new ScriptedDecoder { OnFinish = _ => Md5(failed: 1) };

        StreamVerification verification = Assert.Single(Analyze(new FrameParser(), [decoder], Verify).Streams).Verification!;

        Assert.False(verification.IsVerified);
    }

    [Fact]
    public void ADecoderThatComparedNothingLeavesTheStreamUnknown()
    {
        var decoder = new ScriptedDecoder { OnFinish = _ => [] };

        StreamVerification verification = Assert.Single(Analyze(new FrameParser(), [decoder], Verify).Streams).Verification!;

        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.Null(verification.IsVerified);
    }

    [Theory]
    [InlineData(400L, false, true)]
    [InlineData(399L, false, false)]
    [InlineData(-1L, false, true)]
    [InlineData(400L, true, false)]
    public void TheStreamIsCompleteWhenNoFrameFollowedAGapAndTheSamplesEqualTheStatedCount(long statedOrMinusOne, bool gap, bool isComplete)
    {
        long? stated = statedOrMinusOne < 0 ? null : statedOrMinusOne;
        var decoder = new ScriptedDecoder { OnFinish = _ => [] };
        var parser = new FrameParser { Samples = stated, Shape = (i, frame) => i == 2 ? frame with { FollowsGap = gap } : frame };

        Analyze(parser, [decoder], Verify);

        Assert.Equal(new DecodeEnd(isComplete, 0), Assert.Single(decoder.Ends));
    }

    [Fact]
    public void TheDecodeReturnAdvancesTheStreamAndBlocksBeforeAGroupsEndCountOnce()
    {
        // WavPack: 0 for the blocks before a group's final block, the group's samples on the final one.
        int[] returns = [0, 0, 300, 100];
        int call = 0;
        var decoder = new ScriptedDecoder { OnDecode = _ => returns[call++], OnFinish = _ => Md5() };
        var parser = new FrameParser { Samples = 400 };

        StreamVerification verification = Assert.Single(Analyze(parser, [decoder], Verify).Streams).Verification!;

        Assert.Equal(400, verification.DecodedSamples);
        Assert.True(verification.IsVerified);
        Assert.Equal(new DecodeEnd(true, 0), Assert.Single(decoder.Ends));
    }

    [Fact]
    public void AFrameDecodedToAnotherNumberOfSamplesIsTheDecodersToReport()
    {
        // The host trusts the return value: 90 instead of 100 leaves the count short of the stated total.
        var decoder = new ScriptedDecoder { OnDecode = f => f.FirstSample == 100 ? 90 : f.Samples, OnFinish = _ => Md5() };

        StreamVerification verification = Assert.Single(Analyze(new FrameParser(), [decoder], Verify).Streams).Verification!;

        Assert.Equal(390, verification.DecodedSamples);
        Assert.False(decoder.Ends[0].IsComplete);
    }

    [Fact]
    public void ANegativeReturnIsADecoderBug()
    {
        var decoder = new ScriptedDecoder { OnDecode = _ => -1 };

        Report report = Analyze(new FrameParser(), [decoder], Verify);

        Assert.Equal(DecodeStatus.Failed, Assert.Single(report.Streams).Verification!.Decoding);
        Assert.Contains(report.Findings, f => f.Code == "decode.failed");
    }

    // The budget.

    [Fact]
    public void TheBudgetCountsCanonicalBytesAndStopsEverySessionWithAnInfo()
    {
        // 100 samples of 2 channels at 16 bits are 400 bytes a frame: the third frame passes 800.
        var decoder = new ScriptedDecoder { OnFinish = end => end.IsComplete ? Md5() : [] };
        var parser = new FrameParser();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { MaxPcmBytes = 800 } };

        Report report = Analyze(parser, [decoder], options);

        StreamVerification verification = Assert.Single(report.Streams).Verification!;
        Assert.Equal(DecodeStatus.Stopped, verification.Decoding);
        Assert.Equal(300, verification.DecodedSamples);
        Assert.Equal(3, decoder.FirstSamples.Count);
        Assert.False(parser.Session!.IsDecoding);
        Assert.Equal(4, parser.FramesRead);
        Assert.False(decoder.Ends[0].IsComplete);
        Assert.Null(verification.IsVerified);
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("decode.limit", Severity.Info), (finding.Code, finding.Severity));
    }

    [Fact]
    public void TheBudgetCountsWholeBytesOfTheSampleWidth()
    {
        // 8-bit mono: 100 bytes a frame; 12-bit counts 2 bytes a sample.
        Report narrow = Analyze(new FrameParser { Channels = 1, BitsPerSample = 8 }, [new ScriptedDecoder()], new AnalysisOptions { Decoding = new DecodeOptions { MaxPcmBytes = 399 } });
        Assert.Equal(400, Assert.Single(narrow.Streams).Verification!.DecodedSamples);

        Report twelve = Analyze(new FrameParser { Channels = 1, BitsPerSample = 12 }, [new ScriptedDecoder()], new AnalysisOptions { Decoding = new DecodeOptions { MaxPcmBytes = 399 } });
        Assert.Equal(DecodeStatus.Stopped, Assert.Single(twelve.Streams).Verification!.Decoding);
        Assert.Equal(200, twelve.Streams[0].Verification!.DecodedSamples);
    }

    [Fact]
    public void AGroupsBlocksCountOnceAgainstTheBudget()
    {
        int[] returns = [0, 0, 300, 100];
        int call = 0;
        var decoder = new ScriptedDecoder { OnDecode = _ => returns[call++] };
        var options = new AnalysisOptions { Decoding = new DecodeOptions { MaxPcmBytes = 1600 } };

        StreamVerification verification = Assert.Single(Analyze(new FrameParser(), [decoder], options).Streams).Verification!;

        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.Equal(400, verification.DecodedSamples);
    }

    [Fact]
    public void ABudgetOfZeroStopsAfterTheFirstFrameAndANegativeOneIsRefused()
    {
        Report report = Analyze(new FrameParser(), [new ScriptedDecoder()], new AnalysisOptions { Decoding = new DecodeOptions { MaxPcmBytes = 0 } });
        Assert.Equal(100, Assert.Single(report.Streams).Verification!.DecodedSamples);

        Assert.Throws<ArgumentOutOfRangeException>(() => Analyze(new FrameParser(), [new ScriptedDecoder()], new AnalysisOptions { Decoding = new DecodeOptions { MaxPcmBytes = -1 } }));
    }

    [Fact]
    public void TheBudgetIsSharedByTheSessionsOfOneAnalysis()
    {
        var first = new FrameParser();
        var second = new SecondStreamParser();
        var decoder = new ScriptedDecoder();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { MaxPcmBytes = 1000 } };

        Report report = new Analyzer([], [second], [], [decoder]).Analyze("sample.bin", new MemoryStream(new byte[64]), options);

        Assert.Equal(2, report.Streams.Count);
        Assert.All(report.Streams, s => Assert.Equal(DecodeStatus.Stopped, s.Verification!.Decoding));
        Assert.Equal(2, report.Findings.Count(f => f.Code == "decode.limit"));
        Assert.Equal(3, decoder.FirstSamples.Count);
        _ = first;
    }

    // Two streams in one file: the second session is stopped by the budget the first one spent.
    private sealed class SecondStreamParser : IFormatParser
    {
        public string Name => "Two";

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context)
        {
            Node a = context.Root.Add("Stream A", 0, 32);
            Node b = context.Root.Add("Stream B", 32, 32);
            IDecodeSession one = context.StartDecoding("test", new DecodeSetup { Stream = a, Channels = 2, BitsPerSample = 16 });
            IDecodeSession two = context.StartDecoding("test", new DecodeSetup { Stream = b, Channels = 2, BitsPerSample = 16 });
            byte[] data = new byte[64];
            for (int i = 0; i < 2; i++)
            {
                var frame = new CodedFrame(new MappedBytes(data.AsMemory(0, 16), 0), "Frame", i * FrameSamples, FrameSamples);
                one.Decode(frame);
                two.Decode(frame);
            }

            context.AddStream(new StreamInfo { Nodes = [a], Codec = "Test", Verification = one.Finish([]) });
            context.AddStream(new StreamInfo { Nodes = [b], Codec = "Test", Verification = two.Finish([]) });
        }
    }

    // The exception boundary.

    [Fact]
    public void InvalidDataAndTruncationFromADecodeAreFindingsOnTheFrameAndDecodingCarriesOn()
    {
        var decoder = new ScriptedDecoder
        {
            OnDecode = f => f.FirstSample switch
            {
                100 => throw new InvalidDataException("the predictor order is 33"),
                200 => throw new EndOfStreamException(),
                _ => f.Samples,
            },
            OnFinish = _ => Md5(),
        };

        Report report = Analyze(new FrameParser(), [decoder], Verify);

        Assert.Equal(4, decoder.FirstSamples.Count);
        StreamVerification verification = Assert.Single(report.Streams).Verification!;
        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.Equal(200, verification.DecodedSamples);
        Assert.Equal(new DecodeEnd(false, 2), Assert.Single(decoder.Ends));
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("decode.frame-invalid", Severity.Warning, 2), (finding.Code, finding.Severity, finding.Count));
        Assert.Contains("predictor order is 33", finding.Message);
        Assert.Equal(("Frame", FrameBytes, FrameBytes), (finding.Node!.Name, (int)finding.Node.Offset, (int)finding.Node.Length));
        Assert.Equal([0, 1], finding.Path);
    }

    [Fact]
    public void ASourceOfNodesForNonContiguousFramesKeepsTheirRanges()
    {
        var decoder = new ScriptedDecoder { OnDecode = _ => throw new InvalidDataException("bad") };
        var parser = new FrameParser
        {
            Frames = 1,
            Shape = (_, frame) => frame with
            {
                Bytes = new MappedBytes(new byte[8], [new ByteRange(4, 4), new ByteRange(20, 4)]),
            },
        };

        Finding finding = Assert.Single(Analyze(parser, [decoder], Verify).Findings);

        Assert.Equal((4L, 20L), (finding.Node!.Offset, finding.Node.Length));
        Assert.Equal(2, finding.Node.Ranges!.Count);
    }

    [Fact]
    public void InvalidDataFromOpenIsNotPossibleWithItsMessage()
    {
        var decoder = new ScriptedDecoder { OnOpen = _ => throw new InvalidDataException("the block size is 0") };
        var parser = new FrameParser();

        Report report = Analyze(parser, [decoder], Verify);

        Assert.False(parser.Session!.IsDecoding);
        Assert.Equal(DecodeStatus.NotPossible, Assert.Single(report.Streams).Verification!.Decoding);
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("decode.not-possible", Severity.Info), (finding.Code, finding.Severity));
        Assert.Contains("the block size is 0", finding.Message);
        Assert.Equal(1, decoder.Opens);
    }

    [Fact]
    public void AnyOtherExceptionFromOpenIsDecodeFailed()
    {
        var decoder = new ScriptedDecoder { OnOpen = _ => throw new InvalidOperationException("broken") };

        Report report = Analyze(new FrameParser(), [decoder], Verify);

        Assert.Equal(DecodeStatus.Failed, Assert.Single(report.Streams).Verification!.Decoding);
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("decode.failed", Severity.Error), (finding.Code, finding.Severity));
        Assert.Contains("InvalidOperationException", finding.Message);
        Assert.Contains("broken", finding.Message);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    [Fact]
    public void AnExceptionFromDecodeStopsThatSessionAndDropsItsResults()
    {
        var decoder = new ScriptedDecoder { OnDecode = f => f.FirstSample == 100 ? throw new NullReferenceException() : f.Samples, OnFinish = _ => Md5() };
        var parser = new FrameParser();

        Report report = Analyze(parser, [decoder], Verify);

        Assert.Equal(2, decoder.FirstSamples.Count);
        Assert.Equal(4, parser.FramesRead);
        Assert.Empty(decoder.Ends);
        StreamVerification verification = Assert.Single(report.Streams).Verification!;
        Assert.Equal(DecodeStatus.Failed, verification.Decoding);
        Assert.Equal(0, verification.Checksums[1].Checked);
        Assert.Equal("decode.failed", Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void AnExceptionFromFinishIsDecodeFailedAndDropsTheResults()
    {
        foreach (Exception thrown in new Exception[] { new InvalidDataException("x"), new InvalidOperationException("y") })
        {
            var decoder = new ScriptedDecoder { OnFinish = _ => throw thrown };

            Report report = Analyze(new FrameParser(), [decoder], Verify);

            StreamVerification verification = Assert.Single(report.Streams).Verification!;
            Assert.Equal(DecodeStatus.Failed, verification.Decoding);
            Assert.Equal(0, verification.Checksums[1].Checked);
            Assert.Equal("decode.failed", Assert.Single(report.Findings).Code);
        }
    }

    [Fact]
    public void ResultsThatNameNoDeclaredChecksumOrCompareMoreThanIsStoredAreDecodeFailed()
    {
        IReadOnlyList<ChecksumResult>[] bad =
        [
            [new ChecksumResult("not declared", 1, 0)],
            [new ChecksumResult("stream MD5", 2, 0)],
            [new ChecksumResult("stream MD5", 1, 2)],
            [new ChecksumResult("stream MD5", -1, 0)],
            [new ChecksumResult("stream MD5", 1, 0), new ChecksumResult("stream MD5", 1, 0)],
            [new ChecksumResult("frame CRC", 4, 0)],
        ];

        foreach (IReadOnlyList<ChecksumResult> results in bad)
        {
            var decoder = new ScriptedDecoder { OnFinish = _ => results };

            Report report = Analyze(new FrameParser(), [decoder], Verify);

            StreamVerification verification = Assert.Single(report.Streams).Verification!;
            Assert.Equal(DecodeStatus.Failed, verification.Decoding);
            Assert.Equal(0, verification.Checksums[1].Checked);
            Assert.Equal(4, verification.Checksums[0].Checked);
            Assert.Equal("decode.failed", Assert.Single(report.Findings).Code);
        }
    }

    [Fact]
    public void CancellationOfTheAnalysisPassesThrough()
    {
        using var cancel = new CancellationTokenSource();
        var decoder = new ScriptedDecoder { OnDecode = f => { cancel.Cancel(); return f.Samples; } };
        var options = new AnalysisOptions { CancellationToken = cancel.Token, Decoding = new DecodeOptions() };

        Assert.ThrowsAny<OperationCanceledException>(() => Analyze(new FrameParser(), [decoder], options));
    }

    [Fact]
    public void AnOperationCanceledExceptionFromTheDecoderIsNotADecodeFailure()
    {
        var decoder = new ScriptedDecoder { OnDecode = _ => throw new OperationCanceledException() };

        Assert.Throws<OperationCanceledException>(() => Analyze(new FrameParser(), [decoder], Verify));
    }

    // The front end's stop and sink.

    [Fact]
    public void TheStopTokenEndsDecodingOnlyAndTheReportSaysHowFarItGot()
    {
        using var stop = new CancellationTokenSource();
        var decoder = new ScriptedDecoder { OnDecode = f => { if (f.FirstSample == 100) { stop.Cancel(); } return f.Samples; }, OnFinish = _ => Md5(0) };
        var parser = new FrameParser();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Stop = stop.Token } };

        Report report = Analyze(parser, [decoder], options);

        Assert.Equal(4, parser.FramesRead);
        Assert.Equal(2, decoder.FirstSamples.Count);
        StreamVerification verification = Assert.Single(report.Streams).Verification!;
        Assert.Equal(DecodeStatus.Stopped, verification.Decoding);
        Assert.Equal(200, verification.DecodedSamples);
        Assert.Null(verification.IsVerified);
        Assert.False(Assert.Single(decoder.Ends).IsComplete);
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("decode.stopped", Severity.Info), (finding.Code, finding.Severity));
        Assert.Contains("2 frames", finding.Message);
    }

    // A decoder that declares its output and writes one block a frame.
    private static ScriptedDecoder Writing(Action<PcmBlock>? afterWrite = null)
    {
        IPcmSink? sink = null;
        return new ScriptedDecoder
        {
            OnOpened = context => sink = context.DeclareOutput(new PcmFormat(44100, 2, 16, SampleFormat.Integer)),
            OnDecode = f =>
            {
                int[] samples = new int[f.Samples * 2];
                sink!.Write(new PcmBlock(f.FirstSample, f.Samples, 2, f.Samples, samples));
                return f.Samples;
            },
            OnFinish = _ => { sink!.End(0); return Md5(); },
        };
    }

    private sealed class RecordingSink : IPcmSink
    {
        public List<(long Position, int Length)> Blocks { get; } = [];

        public long? Ended { get; private set; }

        public void Write(PcmBlock block) => Blocks.Add((block.Position, block.Length));

        public void End(long samples) => Ended = samples;
    }

    [Fact]
    public void TheOutputGetsTheStreamAndThePcmAndIsEndedWithTheSamples()
    {
        var sink = new RecordingSink();
        DecodedStream? asked = null;
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = stream => { asked = stream; return sink; } } };

        Report report = Analyze(new FrameParser(), [Writing()], options);

        Assert.Equal("test", asked!.CodecId);
        Assert.Equal("Stream", asked.Stream.Name);
        Assert.Equal(new PcmFormat(44100, 2, 16, SampleFormat.Integer), asked.Format);
        Assert.Equal([(0L, 100), (100L, 100), (200L, 100), (300L, 100)], sink.Blocks);
        Assert.Equal(400, sink.Ended);
        Assert.True(Assert.Single(report.Streams).Verification!.IsVerified);
    }

    [Fact]
    public void ANullOutputOrANullSinkOnlyChecks()
    {
        Assert.True(Assert.Single(Analyze(new FrameParser(), [Writing()], Verify).Streams).Verification!.IsVerified);

        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => null } };
        Assert.True(Assert.Single(Analyze(new FrameParser(), [Writing()], options).Streams).Verification!.IsVerified);
    }

    [Fact]
    public void ADecoderMayDeclareItsOutputOnlyOnce()
    {
        var decoder = new ScriptedDecoder
        {
            OnOpened = context =>
            {
                context.DeclareOutput(new PcmFormat(44100, 2, 16, SampleFormat.Integer));
                context.DeclareOutput(new PcmFormat(44100, 2, 16, SampleFormat.Integer));
            },
        };

        Report report = Analyze(new FrameParser(), [decoder], Verify);

        Assert.Equal(DecodeStatus.Failed, Assert.Single(report.Streams).Verification!.Decoding);
    }

    [Fact]
    public void ASinkThatThrowsEndsDecodingAtTheNextFrameAndAnalyzeRethrowsItWithoutParseFailed()
    {
        var thrown = new InvalidOperationException("the sink is full");
        var sink = new ThrowingSink(thrown);
        var parser = new FrameParser();
        ScriptedDecoder decoder = Writing();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => sink } };

        InvalidOperationException caught = Assert.Throws<InvalidOperationException>(() => Analyze(parser, [decoder], options));

        Assert.Same(thrown, caught);
        Assert.Equal(1, sink.Writes);
        Assert.Equal(4, parser.FramesRead);
        Assert.Single(decoder.FirstSamples);
    }

    private sealed class ThrowingSink(Exception exception) : IPcmSink
    {
        public int Writes { get; private set; }

        public void Write(PcmBlock block)
        {
            Writes++;
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        public void End(long samples) => throw exception;
    }

    [Fact]
    public void AnOutputCallbackThatThrowsIsRethrownTheSameWay()
    {
        var thrown = new IOException("no sink");
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => throw thrown } };

        Assert.Same(thrown, Assert.Throws<IOException>(() => Analyze(new FrameParser(), [Writing()], options)));
    }

    [Fact]
    public void ASinkExceptionEndsTheOtherSessionsOfTheAnalysisToo()
    {
        var thrown = new InvalidOperationException("sink");
        int writes = 0;
        var decoder = new ScriptedDecoder
        {
            OnOpened = context => { if (writes++ == 0) { context.DeclareOutput(new PcmFormat(44100, 2, 16, SampleFormat.Integer)).Write(new PcmBlock(0, 1, 2, 1, new int[2])); } },
        };
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new ThrowingSink(thrown) } };

        Assert.Same(thrown, Assert.Throws<InvalidOperationException>(() => new Analyzer([], [new SecondStreamParser()], [], [decoder]).Analyze("sample.bin", new MemoryStream(new byte[64]), options)));
        Assert.Empty(decoder.FirstSamples);
    }

    // Findings from the decoder.

    [Fact]
    public void FindingsOfOneKindAreReportedOncePerStreamWithACountPointingAtTheFirst()
    {
        DecodeContext? context = null;
        var decoder = new ScriptedDecoder
        {
            OnOpened = c => context = c,
            OnDecode = f =>
            {
                context!.AddFinding(Severity.Warning, "test.mismatch", $"frame at {f.FirstSample}", new Node("Frame", f.Bytes.FileStart, 16));
                return f.Samples;
            },
        };

        Report report = Analyze(new FrameParser(), [decoder], Verify);

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("test.mismatch", 4, "frame at 0"), (finding.Code, finding.Count, finding.Message));
        Assert.Equal(0, finding.Node!.Offset);
        Assert.Equal([0, 0], finding.Path);
    }

    [Fact]
    public void ACountedFindingAndAFindingWithoutANodeUseTheStream()
    {
        DecodeContext? context = null;
        var decoder = new ScriptedDecoder
        {
            OnOpened = c => { context = c; c.AddFinding(Severity.Info, "test.note", "about the stream", count: 3); },
        };

        Finding finding = Assert.Single(Analyze(new FrameParser { Frames = 0 }, [decoder], Verify).Findings);

        Assert.Equal(3, finding.Count);
        Assert.Equal("Stream", finding.Node!.Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => context!.AddFinding(Severity.Info, "x", "y", count: 0));
    }

    // Ending sessions.

    private sealed class Inner(string id, Func<CodecContext, IDecodeSession, StreamVerification?> run) : ICodecParser
    {
        public string Id => id;

        public IDecodeSession? Session { get; private set; }

        public void Parse(CodecContext context)
        {
            Session = context.StartDecoding("test", new DecodeSetup { Stream = context.Parent, Channels = 2, BitsPerSample = 16, Samples = 100 });
            foreach (MappedBytes packet in context.Packets!)
            {
                Session.Decode(new CodedFrame(packet, "Packet", 0, 100));
            }

            if (run(context, Session) is { } verification)
            {
                context.AddStream(new StreamInfo { Nodes = [context.Parent], Codec = "Inner", Verification = verification });
            }
        }
    }

    private sealed class Container(ICodecParser codec) : IFormatParser
    {
        public string Name => "Container";

        public int RouteCalls { get; private set; }

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context)
        {
            Node payload = context.Root.Add("Payload", 0, 32);
            RouteCalls++;
            Assert.True(context.ParsePackets(codec.Id, payload, [new MappedBytes(new byte[16], 0)]));
        }
    }

    [Fact]
    public void ASessionARoutedParserLeavesOpenIsEndedWhenTheRouteReturnsWithItsFindingsMerged()
    {
        DecodeContext? context = null;
        var decoder = new ScriptedDecoder
        {
            OnOpened = c => context = c,
            OnFinish = end =>
            {
                context!.AddFinding(Severity.Warning, "test.counted", "counted at the end", null, 5);
                return Md5();
            },
        };
        var inner = new Inner("inner", (_, _) => null);

        Report report = new Analyzer([], [new Container(inner)], [inner], [decoder]).Analyze("sample.bin", new MemoryStream(new byte[64]), Verify);

        DecodeEnd end = Assert.Single(decoder.Ends);
        Assert.False(end.IsComplete);
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("test.counted", 5), (finding.Code, finding.Count));
        Assert.Equal("Payload", finding.Node!.Name);
        Assert.Empty(report.Streams);
        Assert.False(inner.Session!.IsDecoding);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    [Fact]
    public void ASessionARoutedParserFinishesCarriesItsVerificationIntoTheCallersReport()
    {
        var decoder = new ScriptedDecoder { OnFinish = _ => [] };
        var inner = new Inner("inner", (_, session) => session.Finish([Tally("crc", false, 1, 1)]));

        Report report = new Analyzer([], [new Container(inner)], [inner], [decoder]).Analyze("sample.bin", new MemoryStream(new byte[64]), Verify);

        StreamVerification verification = Assert.Single(report.Streams).Verification!;
        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.True(Assert.Single(decoder.Ends).IsComplete);
    }

    [Fact]
    public void ASessionTheFormatParserLeavesOpenIsEndedWhenItReturns()
    {
        var decoder = new ScriptedDecoder { OnFinish = _ => Md5() };
        var parser = new FrameParser { Finishes = false };

        Report report = Analyze(parser, [decoder], Verify);

        Assert.Equal(new DecodeEnd(false, 0), Assert.Single(decoder.Ends));
        Assert.Empty(report.Streams);
        Assert.False(parser.Session!.IsDecoding);
    }

    [Fact]
    public void ASessionIsEndedAlsoWhenTheParserFails()
    {
        var decoder = new ScriptedDecoder { OnFinish = _ => [] };
        var parser = new FrameParser { Finishes = false, Shape = (i, frame) => i == 2 ? throw new EndOfStreamException() : frame };

        Report report = Analyze(parser, [decoder], Verify);

        Assert.Single(decoder.Ends);
        Assert.Contains(report.Findings, f => f.Code == "parse.truncated");
    }

    [Fact]
    public void AnUnfinishedSessionWhoseDecoderFailsAtTheEndIsReported()
    {
        var decoder = new ScriptedDecoder { OnFinish = _ => throw new InvalidOperationException("late") };

        Report report = Analyze(new FrameParser { Finishes = false }, [decoder], Verify);

        Assert.Equal("decode.failed", Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void SessionsOfNestedRoutesEachEndInTheirOwnRoute()
    {
        var decoder = new ScriptedDecoder { OnFinish = _ => [] };
        var leaf = new Inner("leaf", (_, _) => null);
        var middle = new MiddleCodec("middle", "leaf");

        Report report = new Analyzer([], [new Container(middle)], [middle, leaf], [decoder]).Analyze("sample.bin", new MemoryStream(new byte[64]), Verify);

        Assert.Equal(2, decoder.Ends.Count);
        Assert.All(decoder.Ends, e => Assert.False(e.IsComplete));
        Assert.Empty(report.Findings);
    }

    private sealed class MiddleCodec(string id, string target) : ICodecParser
    {
        public string Id => id;

        public void Parse(CodecContext context)
        {
            IDecodeSession own = context.StartDecoding("test", new DecodeSetup { Stream = context.Parent, Channels = 1, BitsPerSample = 8 });
            Assert.True(context.ParsePackets(target, context.Parent, context.Packets!));
            Assert.True(own.IsDecoding);
        }
    }

    // Lending buffers: the walk stores, the decoder reads what it stored.

    [Fact]
    public void TheSessionLendsBuffersTheDecoderThenReadsFromThem()
    {
        DecodeContext? context = null;
        var seen = new List<long>();
        var decoder = new ScriptedDecoder
        {
            OnOpened = c => context = c,
            OnDecode = f =>
            {
                Assert.True(f.IsWalked);
                SampleBuffers lent = context!.LentBuffers;
                seen.Add(lent.Channel(1)[3]);
                seen.Add(lent.WideChannel(1)[3]);
                return f.Samples;
            },
        };
        var parser = new LendingParser();

        Report report = Analyze(parser, [decoder], Verify);

        Assert.Equal([42L, 4_000_000_000L], seen);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    private sealed class LendingParser : IFormatParser
    {
        public bool NothingLentWithoutDecoding { get; private set; }

        public string Name => "Lending";

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context)
        {
            Node stream = context.Root.Add("Stream", 0, 64);
            IDecodeSession session = context.StartDecoding("test", new DecodeSetup { Stream = stream, Channels = 2, BitsPerSample = 32 });
            SampleBuffers lent = session.Lend(8, 2, needsWide: true);
            if (session.IsDecoding)
            {
                Assert.Equal((2, 8, 16, 16), (lent.Channels, lent.Stride, lent.Narrow.Length, lent.Wide.Length));
                lent.Channel(1)[3] = 42;
                lent.WideChannel(1)[3] = 4_000_000_000;
                session.Decode(new CodedFrame(new MappedBytes(new byte[16], 0), "Frame", 0, 8) { IsWalked = true });
            }
            else
            {
                NothingLentWithoutDecoding = lent.IsEmpty;
            }

            session.Finish([]);
        }
    }

    [Fact]
    public void NothingIsLentWhereNothingIsDecodedOrTheFrameIsPastTheLimit()
    {
        var parser = new LendingParser();
        Analyze(parser, [new ScriptedDecoder()], null);
        Assert.True(parser.NothingLentWithoutDecoding);

        Report report = Analyze(new CaptureParser(), [new ScriptedDecoder()], Verify);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    private sealed class CaptureParser : IFormatParser
    {
        public string Name => "Capture";

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context)
        {
            IDecodeSession session = context.StartDecoding("test", new DecodeSetup { Stream = context.Root, Channels = 1, BitsPerSample = 8 });
            Assert.True(session.Lend(1 << 21, 16).IsEmpty);
            Assert.False(session.Lend(16, 1).IsEmpty);
            session.Finish([]);
        }
    }

    // The setup and the rest of the record.

    [Fact]
    public void IsLossyIsCopiedFromTheSetupWhateverTheStatus()
    {
        Assert.True(Assert.Single(Analyze(new FrameParser { IsLossy = true }, [new ScriptedDecoder()]).Streams).Verification!.IsLossy);
        Assert.True(Assert.Single(Analyze(new FrameParser { IsLossy = true }, []).Streams).Verification!.IsLossy);
        Assert.False(Assert.Single(Analyze(new FrameParser(), [new ScriptedDecoder()], Verify).Streams).Verification!.IsLossy);
    }

    [Fact]
    public void TheHostFillsInOnlyTheTalliesOverDecodedAudioAndKeepsTheParsersOthers()
    {
        var parser = new FrameParser
        {
            Stored =
            [
                Tally("header CRC", false, 4, 3, all: false),
                new ChecksumTally { Name = "stream MD5", Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = true, Stored = 1, Checked = 1, Failed = 1 },
            ],
        };

        StreamVerification verification = Assert.Single(Analyze(parser, [new ScriptedDecoder()], Verify).Streams).Verification!;

        Assert.Equal((4L, 3L), (verification.Checksums[0].Stored, verification.Checksums[0].Checked));
        Assert.Equal((0L, 0L), (verification.Checksums[1].Checked, verification.Checksums[1].Failed));
    }

    [Fact]
    public void FinishIsOnceAndTwoChecksumsOfOneNameAreRefused()
    {
        var parser = new FrameParser { Stored = [Tally("a", false, 1, 1), Tally("a", false, 1, 1)] };

        Report report = Analyze(parser, [new ScriptedDecoder()]);

        Assert.Contains(report.Findings, f => f.Code == "parse.failed");
    }

    [Fact]
    public void TwoDecodersWithOneIdAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new Analyzer([], [new FrameParser()], [], [new ScriptedDecoder(), new ScriptedDecoder()]));
    }

    [Fact]
    public void TheTreeIsTheSameWithAndWithoutDecoding()
    {
        string Tree(AnalysisOptions? options)
        {
            var writer = new StringWriter();
            Report report = Analyze(new FrameParser(), [new ScriptedDecoder { OnFinish = _ => Md5() }], options);
            ReportTextWriter.Write(report, writer, showOffsets: true);
            return string.Join('\n', writer.ToString().ReplaceLineEndings("\n").Split('\n').Where(l => !l.Contains("Verified:", StringComparison.Ordinal)));
        }

        Assert.Equal(Tree(null), Tree(Verify));
    }

    [Fact]
    public void ADefaultAnalyzerHasNoDecodersYetAndStaysQuiet()
    {
        Assert.Empty(DefaultParsers.Decoders);
        Report report = Analyze(new FrameParser(), DefaultParsers.Decoders, Verify);
        Assert.Contains(report.Findings, f => f.Code == "decode.no-decoder");
    }
}
