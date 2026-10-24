using Encspect.Core;
using Encspect.Formats.Tta;
using Encspect.Host;

namespace Encspect.Tests;

// What the TTA format parser does with input the fixtures do not hold: how its sniff ranks a header, the second version's signature and data
// that is none, a forced parse of data that begins with no signature, the end the stream declares, and the findings the writer of a tag raises.
// FindingCoverageTests checks that every code the parsers emit is asserted somewhere.
public class TtaFindingTests
{
    private static readonly TtaParser Parser = new();

    private static SniffResult Sniff(byte[] data) => Parser.Sniff(new SniffContext(new ByteReader(new MemoryStream(data))));

    private static long? DeclaredEnd(byte[] data) => Parser.DeclaredEnd(new SniffContext(new ByteReader(new MemoryStream(data))));

    private static Report Analyze(byte[] data, AnalysisOptions? options = null) => Analyzer.CreateDefault().Analyze("test.tta", new MemoryStream(data), options);

    [Fact]
    public void AHeaderWhoseCrcHoldsIsCertain()
    {
        SniffResult result = Sniff(TtaBuilder.Stream(44100, 2, 16, 3));

        Assert.Equal(100, result.Class);
        Assert.Equal(1, result.VerifiedFrames);
        Assert.Equal(22 + 12 + 4, result.End);
    }

    [Fact]
    public void AHeaderWhoseCrcFailsIsLikelyWhereItsFieldsAreWhatAnEncoderWrites()
    {
        byte[] data = TtaBuilder.Header(44100, 2, 16, 100000, crc: 1);

        Assert.Equal(80, Sniff(data).Class);
        Assert.Equal(0, Sniff(data).VerifiedFrames);
    }

    [Theory]
    [InlineData(0u, 2, 16)]
    [InlineData(44100u, 0, 16)]
    public void AHeaderWhoseCrcFailsAndWhoseFieldsCannotBeOneIsPossible(uint rate, int channels, int bits)
    {
        byte[] data = TtaBuilder.Header(rate, channels, bits, 100000, crc: 1);

        Assert.Equal(70, Sniff(data).Class);
    }

    [Fact]
    public void AnUnknownAudioFormatMakesTheFieldsImpossible() =>
        Assert.Equal(70, Sniff(TtaBuilder.Header(44100, 2, 16, 100000, format: 3, crc: 1)).Class);

    [Fact]
    public void TheSignatureAloneIsPossible()
    {
        Assert.Equal(70, Sniff("TTA1"u8.ToArray()).Class);
        Assert.Equal(70, Sniff([.. "TTA1"u8, 1, 0, 2]).Class);
    }

    // The second version's header is 36 bytes (channels, bits per sample, sample rate, ...): the signature alone is four bytes of ASCII.
    [Fact]
    public void TheSecondVersionsSignatureAloneIsPossible()
    {
        Assert.Equal(70, Sniff("TTA2"u8.ToArray()).Class);
        Assert.Equal(70, Sniff([.. "TTA2"u8, .. new byte[10]]).Class);
        Assert.Equal(70, Sniff([.. "TTA2 is a text file that begins with the signature"u8]).Class);
    }

    [Fact]
    public void TheSecondVersionsSignatureWithAPlausibleHeaderIsLikely()
    {
        byte[] header = new byte[40];
        "TTA2"u8.CopyTo(header);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 44100);

        Assert.Equal(80, Sniff(header).Class);
        Assert.Equal(70, Sniff(header[..35]).Class);
        header[4] = 0;
        Assert.Equal(70, Sniff(header).Class);
    }

    [Fact]
    public void AHeaderFurtherInIsNoMatch()
    {
        byte[] stream = TtaBuilder.Stream(44100, 2, 16, 2);

        Assert.False(Sniff([.. new byte[10], .. stream]).IsMatch);
        Assert.False(Sniff("TTA"u8.ToArray()).IsMatch);
        Assert.False(Sniff(new byte[100]).IsMatch);
    }

    [Fact]
    public void ADeclaredEndIsTheEndOfTheLastFrame()
    {
        byte[] stream = TtaBuilder.Stream(44100, 2, 16, 3, 300);

        Assert.Equal(stream.Length, DeclaredEnd(stream));
        Assert.Equal(stream.Length, DeclaredEnd([.. stream, .. new byte[500]]));
        Assert.Equal(stream.Length, DeclaredEnd(stream[..^100]));
    }

    [Fact]
    public void ADamagedTableDeclaresNoEnd()
    {
        byte[] stream = TtaBuilder.Stream(44100, 2, 16, 3, 300);
        stream[24] ^= 1;

        Assert.Null(DeclaredEnd(stream));
        Assert.Null(DeclaredEnd(stream[..30]));
        Assert.Null(DeclaredEnd(TtaBuilder.Header(0, 2, 16, 100)));
        Assert.Null(DeclaredEnd("TTA1"u8.ToArray()));
        Assert.Null(DeclaredEnd(new byte[100]));
    }

    [Fact]
    public void ForcingTheParserOnDataWithNoSignatureGivesAFinding()
    {
        byte[] data = new byte[300];
        new Random(5).NextBytes(data);
        data[0] = 0x80;

        Report report = Analyze(data, new AnalysisOptions { ForcedFormat = "TTA" });

        Assert.Contains(report.Findings, f => f.Code == "tta.no-signature" && f.Severity == Severity.Warning);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.Empty(report.Streams);
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    [Fact]
    public void ForcingTheParserOnAHeaderCutShortGivesAFinding()
    {
        Report report = Analyze("TTA1\u0001\0\u0002"u8.ToArray(), new AnalysisOptions { ForcedFormat = "TTA" });

        Assert.Contains(report.Findings, f => f.Code == "tta.header-truncated");
        Assert.Empty(report.Streams);
    }

    // The model of a password-protected stream; the real one, written by libtta-c 2.3 with a password, is the fixture external-libtta-16-stereo-password.tta,
    // whose frame CRC-32s cover the stored bytes (TtaFixtureTests holds it to its manifest, the cross-check script to FFmpeg).
    [Fact]
    public void AnEncryptedStreamIsReportedAndItsCrcsAreStillCompared()
    {
        byte[] stream = TtaBuilder.Stream(44100, 2, 16, 3, 300);
        byte[] encrypted = [.. TtaBuilder.Header(44100, 2, 16, 3 * TtaBuilder.FrameLength(44100), format: 2), .. stream[22..]];

        Report report = Analyze(encrypted);

        Assert.Contains(report.Findings, f => f.Code == "tta.audio-format-encrypted" && f.Severity == Severity.Info);
        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Equal(true, verification.IsVerified);
    }

    [Fact]
    public void FramesPastTheRecordsAreCountedAndTheirCrcsCompared()
    {
        int count = Records.MaxEntries + 10;
        // At 100 Hz a frame is 104 samples, so the header's 32-bit sample count reaches the millions of frames.
        byte[] data = TtaBuilder.Stream(100, 1, 16, count, payload: 0);
        data[^1] ^= 1;

        Report report = Analyze(data);

        Assert.Equal(count * TtaBuilder.FrameLength(100), Assert.Single(report.Streams).Samples);
        Finding limit = Assert.Single(report.Findings, f => f.Code == "tta.entries-limit");
        Assert.Contains($"{count} entries", limit.Message);
        Finding mismatch = Assert.Single(report.Findings, f => f.Code == "tta.frame-crc-mismatch");
        Assert.Contains($"frame {count - 1} ", mismatch.Message);
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        Assert.Equal(false, report.Streams.Single().Verification!.IsVerified);
        Node stream = report.Root.Children.Single(c => c.Name == "TTA");
        Assert.Equal(Records.MaxEntries, stream.Children.Count(c => c.Name == "Frame"));
    }

    [Fact]
    public void AWriterThatWritesAnotherFormatIsNamed()
    {
        byte[] stream = TtaBuilder.Stream(44100, 2, 16, 3, 300);

        Report report = Analyze([.. stream, .. TestFiles.ApeText("Encoder", "LAME 3.100")]);

        Assert.Contains(report.Findings, f => f.Code == "tta.writer-other-format");
    }

    [Fact]
    public void ADisagreeingWriterClaimIsReported()
    {
        byte[] stream = TtaBuilder.Stream(44100, 2, 16, 3, 300);

        Report report = Analyze([.. TestFiles.Id3v23(("TSSE", "Lavf60.16.100")), .. stream, .. TestFiles.ApeText("Encoder", "LAME 3.100")]);

        Assert.Contains(report.Findings, f => f.Code == "tta.writer-claim-differs");
    }

    [Fact]
    public void ASampleCountOfNothingGivesNoStreamNodesAndNoException()
    {
        Report report = Analyze([.. TtaBuilder.Header(44100, 2, 16, 0), .. TtaBuilder.Table([])]);

        Assert.Contains(report.Findings, f => f.Code == "tta.no-samples");
        Assert.Equal(0, report.Streams.Single().Samples);
    }

    [Fact]
    public void CancelingMidStreamStopsTheAnalysis()
    {
        byte[] data = TtaBuilder.SmallFrames(300_000);
        using var cancellation = new CancellationTokenSource();
        long last = 0;
        var progress = new Progress(value =>
        {
            last = value;
            if (value >= data.Length / 2)
            {
                cancellation.Cancel();
            }
        });

        Assert.Throws<OperationCanceledException>(() => Analyze(data, new AnalysisOptions { CancellationToken = cancellation.Token, BytesRead = progress }));
        Assert.True(last < data.Length, $"progress reached {last} of {data.Length}");
    }

    private sealed class Progress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    [Fact]
    public void ARateOfOneMakesEverySampleAFrameAndTheTableIsCutByTheData()
    {
        // 4,294,967,295 frames of one sample, in a file of a few hundred bytes.
        byte[] data = [.. TtaBuilder.Header(1, 1, 16, uint.MaxValue), .. new byte[400]];

        Report report = Analyze(data);

        Assert.Contains(report.Findings, f => f.Code == "tta.seek-table-truncated");
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed");
    }

    [Fact]
    public void TheNumberOfFramesFollowsFromTheTruncatedFrameLength()
    {
        // At 32,000 Hz a frame is 33,436 samples (33,436.7 truncated).
        Assert.Equal(33436, TtaBuilder.FrameLength(32000));
        byte[] stream = TtaBuilder.Stream(32000, 2, 16, 66873, [10, 10, 10]);

        Report report = Analyze(stream);

        Assert.Empty(report.Findings);
        Assert.Equal(66873, report.Streams.Single().Samples);
        Assert.Equal(true, report.Streams.Single().Verification!.IsVerified);
    }

    [Fact]
    public void AStreamIsVerifiedByItsCrcsAloneWhereNoFrameIsMissing()
    {
        byte[] stream = TtaBuilder.Stream(44100, 2, 16, 3, 300);

        StreamVerification verification = Analyze(stream).Streams.Single().Verification!;

        Assert.Equal(DecodeStatus.NoDecoder, verification.Decoding);
        Assert.Equal(true, verification.IsVerified);
        Assert.Equal(["header CRC-32", "seek table CRC-32", "frame CRC-32"], verification.Checksums.Select(c => c.Name));
        Assert.Equal([ChecksumCoverage.Header, ChecksumCoverage.Header, ChecksumCoverage.CodedData], verification.Checksums.Select(c => c.Covers));
        Assert.Equal([false, false, true], verification.Checksums.Select(c => c.CoversAllAudio));
    }

    [Fact]
    public void ADamagedHeaderMakesTheSampleCountUntrustworthyAndTheStreamUnverified()
    {
        byte[] stream = TtaBuilder.Stream(44100, 2, 16, 3, 300);
        stream[21] ^= 1;

        StreamVerification verification = Analyze(stream).Streams.Single().Verification!;

        Assert.Equal(false, verification.IsVerified);
        Assert.False(verification.Checksums.Single(c => c.Name == "frame CRC-32").CoversAllAudio);
    }
}
