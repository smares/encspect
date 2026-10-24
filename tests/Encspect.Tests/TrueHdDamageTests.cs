using Encspect.Core;
using Encspect.Formats.TrueHd;
using Encspect.Host;

namespace Encspect.Tests;

// What the review of the TrueHD and MLP parser and decoder found, kept as tests: a decoder that fails a unit and then decodes on stale state, a damaged
// unit next to the end of the read window, MLP the analyzer does not read, the figures of a stream that is not damaged but is not read, and the statistics.
public class TrueHdDamageTests
{
    private const int Mebibyte = 1 << 20;

    private static Analyzer With(Func<TrueHdParser> parser) =>
        new(DefaultParsers.Tags, [.. DefaultParsers.Formats.Select(f => f is TrueHdParser ? parser() : f)], DefaultParsers.Codecs, DefaultParsers.Decoders);

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "truehd", name));

    private static List<(int Offset, int Length)> Units(byte[] data)
    {
        var units = new List<(int, int)>();
        for (int at = 0; at + 4 <= data.Length;)
        {
            int length = (((data[at] & 0xF) << 8) | data[at + 1]) * 2;
            units.Add((at, length));
            at += length;
        }

        return units;
    }

    // Takes the canonical bytes of the decoded audio.
    private sealed class CollectingSink : IPcmSink
    {
        private int _bits;

        public List<byte> Bytes { get; } = [];

        public void Declare(PcmFormat format) => _bits = format.BitsPerSample;

        public void Write(PcmBlock block)
        {
            var buffer = new byte[CanonicalPcm.ByteCount(block, _bits)];
            CanonicalPcm.Write(block, _bits, buffer);
            Bytes.AddRange(buffer);
        }

        public void End(long samples)
        {
        }
    }

    private static (Report Report, byte[] Audio) Decode(byte[] data)
    {
        var sink = new CollectingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = stream => { sink.Declare(stream.Format); return sink; } } };
        Report report = Analyzer.CreateDefault().Analyze("decoded.thd", new MemoryStream(data), options);
        return (report, [.. sink.Bytes]);
    }

    // A 5.1 stream, then a 4.0 stream at the same rate (both of two substreams), then the 5.1 stream again, each cut before its end of stream marker: the
    // decoder, opened for six channels, rejects the 4.0 units after it has read their restart headers, which must leave no accumulator behind that a
    // later restart header compares.
    [Fact]
    public void ASpliceToAnotherChannelCountFailsNoLosslessCheckAndTheStretchesDecodeAsTheyWouldAlone()
    {
        (Report report, byte[] audio) = Decode(Fixture("built-splice-channel-count.thd"));

        Assert.DoesNotContain(report.Findings, f => f.Code is "truehd.lossless-check-failed" or "decode.failed" or "parse.failed");
        Finding invalid = Assert.Single(report.Findings, f => f.Code == "decode.frame-invalid");
        Assert.Equal(33, invalid.Count);
        Assert.Contains("opened with 6", invalid.Message, StringComparison.Ordinal);
        Assert.NotNull(invalid.Path);
        List<ChecksumTally> lossless = [.. report.Streams.Single().Verification!.Checksums.Where(c => c.Name.StartsWith("lossless", StringComparison.Ordinal))];
        Assert.Equal(2, lossless.Count);
        Assert.All(lossless, c => Assert.Equal((6L, 4L, 0L), (c.Stored, c.Checked, c.Failed)));

        byte[] plain = Fixture("external-truehd-51-48k-24.thd");
        List<(int Offset, int Length)> units = Units(plain);
        byte[] first = Decode(plain[..(units[32].Offset + units[32].Length)]).Audio;
        byte[] third = Decode(plain[..(units[40].Offset + units[40].Length)]).Audio;
        Assert.Equal(33 * 40 * 6 * 3, first.Length);
        Assert.Equal([.. first, .. third], audio);
    }

    // A file of units repeated until the first read window ends inside the unit after a damaged one: the unit is analyzed as it is with a window that holds
    // it all, whichever bytes the window holds when the next unit is looked at.
    [Fact]
    public void ADamagedUnitNextToTheEndOfTheFirstMebibyteIsAnalyzedWithItsOwnBytes()
    {
        byte[] tiny = Fixture("built-tiny-truehd-stereo.thd");
        List<(int Offset, int Length)> tinyUnits = Units(tiny);

        // The first k units of the fixture, repeated, until some unit without a major sync ends where the first window still holds the next unit's header
        // but not the bytes the search for a unit after a damaged one looks at (up to 128 of them); the k that puts such a unit there is looked for.
        byte[] bytes = [];
        List<(int Offset, int Length)> units = [];
        int candidate = -1;
        for (int k = 1; k <= tinyUnits.Count && candidate < 0; k++)
        {
            byte[] piece = tiny[..(tinyUnits[k - 1].Offset + tinyUnits[k - 1].Length)];
            var data = new List<byte>((2 * Mebibyte) + (piece.Length * 2));
            while (data.Count < (2 * Mebibyte) + 8192)
            {
                data.AddRange(piece);
            }

            bytes = [.. data];
            units = Units(bytes);
            candidate = units.FindLastIndex(u => bytes[u.Offset + 4] != 0xF8 && u.Offset + u.Length + 4 <= Mebibyte && u.Offset + u.Length + 90 > Mebibyte);
        }

        Assert.True(candidate > 0, "No unit ends where the first window ends inside the next; choose another fixture.");

        byte[] damaged = [.. bytes];
        damaged[units[candidate].Offset] ^= 0x10;
        var options = new AnalysisOptions { ForcedFormat = "thd" };
        Report actual = With(() => new TrueHdParser()).Analyze("window.thd", new MemoryStream(damaged), options);
        Report expected = With(() => new TrueHdParser { WindowSize = 8 * Mebibyte }).Analyze("window.thd", new MemoryStream(damaged), options);

        Assert.Equal(expected.Findings.Select(f => (f.Code, f.Message, f.Count)), actual.Findings.Select(f => (f.Code, f.Message, f.Count)));
        Assert.Contains(actual.Findings, f => f.Code == "truehd.parity-mismatch");
        Assert.DoesNotContain(actual.Findings, f => f.Code == "truehd.directory-invalid");
        Assert.Equal(expected.Streams.Single().Samples, actual.Streams.Single().Samples);
        Assert.Equal(Summary(expected), Summary(actual));
    }

    // The same with a window of a few hundred bytes, so that every unit of a file is next to a window's end in some run, for a damaged unit of each kind.
    [Fact]
    public void ADamagedUnitAtAnyBoundaryOfASmallWindowIsReadAsWithAWholeWindow()
    {
        byte[] plain = Fixture("external-truehd-stereo-48k-24-odd-length.thd");
        List<(int Offset, int Length)> units = Units(plain);
        Assert.True(units.Count > 100);
        foreach (int window in new[] { 300, 1000 })
        {
            for (int unit = 1; unit < 100; unit++)
            {
                byte[] damaged = [.. plain];
                damaged[units[unit].Offset] ^= 0x10;
                var options = new AnalysisOptions { ForcedFormat = "thd" };
                Report whole = With(() => new TrueHdParser { WindowSize = Mebibyte }).Analyze("whole.thd", new MemoryStream(damaged), options);
                Report small = With(() => new TrueHdParser { WindowSize = window }).Analyze("small.thd", new MemoryStream(damaged), options);

                Assert.True(Summary(whole) == Summary(small), $"A window of {window} bytes reads unit {unit} differently.");
            }
        }
    }

    private static string Summary(Report report) =>
        string.Join("|", report.Findings.Select(f => $"{f.Code}:{f.Count}:{f.Message}")) + "|" + string.Join(",", report.Root.Children.Single(c => c.Kind == NodeKind.Stream).Children
            .Where(c => c.Name is "Access Unit" or "Skipped Data").Select(c => $"{c.Offset}+{c.Length}:{c.Description?.Length}"));

    // MLP with channel groups at different sampling rates is not read: every major sync of such a stream is unsupported, the units after it carry no
    // sync, and nothing is decoded, counted or verified; none of it is damage.
    [Fact]
    public void AStreamWhoseMajorSyncsGiveTheChannelGroupsOtherRatesIsReportedAsNotReadAndNotDamaged()
    {
        byte[] data = Fixture("external-mlp-51-48k-24.mlp");
        foreach ((int offset, int length) in Units(data).Where(u => data[u.Offset + 4] == 0xF8))
        {
            data[offset + 8] = (byte)((data[offset + 8] & 0xF0) | (data[offset + 8] >> 4));
            data[offset + 9] = (byte)((data[offset + 9] & 0xF0) | 1);
            ushort crc = Checks.MajorSync(data.AsSpan(offset + 4, 28));
            data[offset + 4 + 26] = (byte)(crc >> 8);
            data[offset + 4 + 27] = (byte)crc;
        }

        Report report = Analyzer.CreateDefault().Analyze("rates.mlp", new MemoryStream(data), new AnalysisOptions { Decoding = new DecodeOptions() });

        Assert.Empty(report.Streams);
        Assert.NotEmpty(report.Findings);
        Assert.All(report.Findings, f => Assert.True(f.Severity == Severity.Info && f.Code == "truehd.unsupported", $"{f.Severity} {f.Code}: {f.Message}"));
        Assert.Contains(report.Findings, f => f.Message.Contains("different sampling rates", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Message.Contains("carry no major sync", StringComparison.Ordinal));
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    // A negative output shift is not read either, and is not damage: the units that follow are said to follow it, as information.
    [Fact]
    public void AnUnsupportedSubstreamIsNoDamageAndTheUnitsAfterItAreNotReportedAsFollowingDamage()
    {
        byte[] crafted = TrueHdFindingTests.Flip("built-tiny-truehd-stereo.thd", 0, 15);

        Report report = Analyzer.CreateDefault().Analyze("shift.thd", new MemoryStream(crafted));

        Assert.Contains(report.Findings, f => f.Code == "truehd.unsupported" && f.Message.Contains("cannot be read", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Code == "truehd.unsupported" && f.Message.Contains("follow a unit", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Findings, f => f.Severity != Severity.Info);
        Assert.DoesNotContain(report.Findings, f => f.Code is "truehd.units-after-damage" or "truehd.no-units");
    }

    // The statistics count the substreams of which either check fails once, not the larger of the two failures.
    [Fact]
    public void ASubstreamWhoseParityByteAndAnotherWhoseCrcDifferAreTwoFailedSubstreams()
    {
        byte[] data = Fixture("external-truehd-stereo-48k-24.thd");
        List<(int Offset, int Length)> units = Units(data);
        data[units[5].Offset + units[5].Length - 2] ^= 0x10;
        data[units[7].Offset + units[7].Length - 1] ^= 0x01;

        Report report = Analyzer.CreateDefault().Analyze("checks.thd", new MemoryStream(data));

        Node statistics = report.Root.Children.Single(c => c.Kind == NodeKind.Stream).Children.Single(c => c.Kind == NodeKind.Statistics);
        long failed = statistics.Children.Single(c => c.Name == "Failed Substream Checksums").Value!.AsInteger()!.Value;
        long verified = statistics.Children.Single(c => c.Name == "Verified Substream Checksums").Value!.AsInteger()!.Value;
        Assert.Equal(2, failed);
        Assert.Equal(units.Count - 2, verified);
    }

    // The fourth substream of a stream that signals Dolby Atmos is not compared, so its parity byte and CRC-8 show what they hold and no verdict.
    [Fact]
    public void TheFourthSubstreamsLastBytesAreShownWithoutAVerdict()
    {
        Report report = Fixtures.AnalyzeMpeg("truehd/built-atmos-signaled.thd");

        Node unit = report.Root.Children.Single(c => c.Kind == NodeKind.Stream).Children.First(c => c.Name == "Access Unit");
        Node fourth = unit.Children.Single(c => c.Name == "Substream 3");
        Assert.Contains(fourth.Children, c => c.Name == "Parity Byte");
        Assert.All(fourth.Children.Where(c => c.Name is "Parity Byte" or "CRC-8"), c => Assert.Empty(c.Children));
        Node third = unit.Children.Single(c => c.Name == "Substream 2");
        Assert.All(third.Children.Where(c => c.Name is "Parity Byte" or "CRC-8"), c => Assert.Contains(c.Children, k => k.Name == "IsValid"));
    }

    // For MLP a channel assignment past the last matrix channel is invalid whatever the arrangement lists (arrangements 21 to 31 list none).
    [Fact]
    public void AnMlpChannelAssignmentPastTheLastMatrixChannelIsInvalidAndATrueHdOneIsNot()
    {
        var header = new RestartHeader { IsSyncValid = true, LastChannel = 1, LastMatrixChannel = 1, Assignments = 2UL << 6 };

        Assert.Equal(SubstreamFault.ChannelAssignment, header.Validate(isMlp: true, channelListSize: 8));
        Assert.Equal(SubstreamFault.None, header.Validate(isMlp: false, channelListSize: 8));
    }

    // What a restart header says about coded channels that FFmpeg does not check.
    [Fact]
    public void CodedChannelsPastTheLastMatrixChannelAndMatrixChannelsNoSubstreamCodesAreFound()
    {
        var stereo = new RestartHeader { IsSyncValid = true, FirstChannel = 0, LastChannel = 1, LastMatrixChannel = 1 };
        var surround = new RestartHeader { IsSyncValid = true, FirstChannel = 2, LastChannel = 5, LastMatrixChannel = 5 };

        Assert.Equal((false, -1), Analysis.ChannelProblems(stereo, []));
        Assert.Equal((false, -1), Analysis.ChannelProblems(surround, [(0, 1)]));
        Assert.Equal((false, 0), Analysis.ChannelProblems(surround, []));
        Assert.Equal((false, 2), Analysis.ChannelProblems(stereo with { LastMatrixChannel = 3 }, []));
        Assert.Equal((true, -1), Analysis.ChannelProblems(stereo with { LastMatrixChannel = 0 }, []));
        Assert.Equal((false, -1), Analysis.ChannelProblems(stereo with { LastMatrixChannel = 3 }, [(2, 3)]));
    }

    // The samples of a 20-bit stream are output as 24 bits, so that no bit of them is dropped.
    [Fact]
    public void ATwentyBitStreamIsOutputAsTwentyFourBits()
    {
        var sink = new CollectingSink();
        byte[] data = Fixture("external-mlp-stereo-48k-24.mlp");
        // Group 1's depth code 2 (24 bits) becomes 1 (20 bits), with the major sync's CRC worked out again: the samples keep their 24-bit values, whose
        // low four bits are then not zero, and the sink must get all of them.
        foreach ((int offset, int length) in Units(data).Where(u => data[u.Offset + 4] == 0xF8))
        {
            data[offset + 8] = (byte)((data[offset + 8] & 0x0F) | 0x10);
            ushort crc = Checks.MajorSync(data.AsSpan(offset + 4, 28));
            data[offset + 4 + 26] = (byte)(crc >> 8);
            data[offset + 4 + 27] = (byte)crc;
        }

        PcmFormat? format = null;
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = stream => { format = stream.Format; sink.Declare(stream.Format); return sink; } } };
        Report report = Analyzer.CreateDefault().Analyze("twenty.mlp", new MemoryStream(data), options);

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        Assert.Equal(20, report.Streams.Single().BitsPerSample);
        Assert.Equal(24, format!.BitsPerSample);
        Assert.Equal(report.Streams.Single().Samples * 2 * 3, sink.Bytes.Count);

        byte[] original = Fixture("external-mlp-stereo-48k-24.mlp");
        var reference = new CollectingSink();
        Analyzer.CreateDefault().Analyze("original.mlp", new MemoryStream(original), new AnalysisOptions { Decoding = new DecodeOptions { Output = s => { reference.Declare(s.Format); return reference; } } });
        Assert.Equal([.. reference.Bytes], [.. sink.Bytes]);
    }

    // The units past the limit of records are counted as the others are: the figures, the verification and the graphs do not depend on the limit.
    [Fact]
    public void TheFiguresOfAStreamAreTheSameWhateverTheLimitOfRecords()
    {
        byte[] data = Fixture("external-truehd-stereo-48k-24.thd");
        var options = new AnalysisOptions { Decoding = new DecodeOptions() };
        Report whole = Analyzer.CreateDefault().Analyze("whole.thd", new MemoryStream(data), options);
        Report limited = With(() => new TrueHdParser { RecordLimit = 50 }).Analyze("limited.thd", new MemoryStream(data), options);

        StreamInfo a = whole.Streams.Single();
        StreamInfo b = limited.Streams.Single();
        Assert.Equal(a.Samples, b.Samples);
        Assert.Equal(a.Duration, b.Duration);
        Assert.Equal(a.AverageBitrate, b.AverageBitrate);
        Assert.Equal(a.IsVbr, b.IsVbr);
        Assert.Equal(a.Verification!.IsVerified, b.Verification!.IsVerified);
        Assert.Equal(a.Verification.Checksums.Select(c => (c.Name, c.Stored, c.Checked, c.Failed)), b.Verification.Checksums.Select(c => (c.Name, c.Stored, c.Checked, c.Failed)));
        Assert.Equal(a.Series.Select(s => s.Name), b.Series.Select(s => s.Name));
        for (int i = 0; i < a.Series.Count; i++)
        {
            Assert.Equal(a.Series[i].Values.ToArray(), b.Series[i].Values.ToArray());
        }
    }
}
