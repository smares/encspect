using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Packets on an Ogg page whose CRC fails (docs/design/damaged-units.md, decision 2): the container marks every packet with a byte on the page (MappedBytes.FailsChecksum), the codec
// parsers hand them on as CodedFrame.FailsChecksum, and the measurements leave their positions out as withheld samples ("checksum failed"), with the span after them that the
// decoder's state carries: Vorbis the next packet's samples (the overlap), Opus 3,840 samples and Speex 20 frames (recursive state, the spans a gap's reset withholds), FLAC none.
// The decoded PCM, the codec's verification and the findings stay as they are, and the page CRC is no tally of the codec's.
public class OggDamagedUnitsTests
{
    // The files and the samples withheld, each worked out from the pages: a hand-made Speex file with a page that fails, and encoder-written files with the stored CRC of one page
    // changed (make_ogg_matroska_damaged_units_fixtures.py).
    public static TheoryData<string, long> Names => new()
    {
        // The last page's two packets and the 3,200 samples a gap's reset withholds after the first.
        { "ogg/bad-crc.spx", 3200 },
        // Page 3 holds the packets that end between granule positions 11,776 and 18,880 (7,104 samples), and the next packet returns 1,024.
        { "ogg/damaged-units/external-oggenc-q10-page-crc-changed.ogg", 7104 + 1024 },
        // Eight packets of 960 samples on the page, then 3,840.
        { "ogg/damaged-units/external-libopus-stereo-hybrid-20ms-repaged-page-crc-changed.opus", 8 * 960 + 3840 },
        // Eight packets of 320 samples, then 20 frames.
        { "ogg/damaged-units/external-speexenc-wb-repaged-page-crc-changed.spx", 8 * 320 + 20 * 320 },
        // One frame of 16,384 samples that spans the failing page and the next: both pages' bytes are the frame's, and no other frame is withheld.
        { "flac/damaged-units/external-ogg-24-stereo-spanning-page-crc-changed.ogg", 16384 },
    };

    private static (Report Report, string Pcm) Analyze(string name, byte[]? data = null, bool measure = true)
    {
        using Stream stream = data is null ? File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name)) : new MemoryStream(data);
        var sink = new PcmHashingSink();
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = measure ? new MeasureOptions() : null } });
        return (report, sink.Hash());
    }

    private static IEnumerable<Node> Walk(Node node) => new[] { node }.Concat(node.Children.SelectMany(Walk));

    private static long FailedPages(Report report) =>
        Walk(report.Root).Where(n => n.Name == "Failed Page Checksums").Sum(n => n.Value!.AsInteger()!.Value);

    [Theory]
    [MemberData(nameof(Names))]
    public void TheWithheldPositionsAreThoseOfThePacketsOnTheFailedPageAndTheSpanAfterThem(string name, long withheld)
    {
        (Report report, _) = Analyze(name);
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Equal(1, FailedPages(report));
        Assert.Equal(withheld, m.WithheldSamples);
        Assert.Equal(withheld, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(1, m.Gaps);
        Assert.Equal(0, m.LostSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed");
    }

    // The files are the undamaged ones with the stored CRC of a page changed (Opus and Speex written again with fewer packets to a page): the decoded PCM and the samples decoded
    // are the undamaged files', and the codec's verification holds nothing of the page.
    [Theory]
    [InlineData("ogg/external-oggenc-q10.ogg", "ogg/damaged-units/external-oggenc-q10-page-crc-changed.ogg")]
    [InlineData("ogg/external-libopus-stereo-hybrid-20ms.opus", "ogg/damaged-units/external-libopus-stereo-hybrid-20ms-repaged-page-crc-changed.opus")]
    [InlineData("ogg/external-speexenc-wb.spx", "ogg/damaged-units/external-speexenc-wb-repaged-page-crc-changed.spx")]
    [InlineData("flac/external-ogg-24-stereo-spanning.ogg", "flac/damaged-units/external-ogg-24-stereo-spanning-page-crc-changed.ogg")]
    public void ChangingAPageCrcChangesNeitherTheDecodedPcmNorTheSamplesDecodedNorTheVerification(string undamaged, string damaged)
    {
        (Report before, string beforePcm) = Analyze(undamaged);
        (Report after, string afterPcm) = Analyze(damaged);

        Assert.Equal(beforePcm, afterPcm);
        Assert.Equal(before.Streams[0].Verification!.DecodedSamples, after.Streams[0].Verification!.DecodedSamples);
        Assert.True(before.Streams[0].Verification!.Checksums.SequenceEqual(after.Streams[0].Verification!.Checksums));
        Assert.Equal(0, before.Streams[0].Measurements!.WithheldSamples);
        Assert.True(after.Streams[0].Measurements!.WithheldSamples > 0);
        Assert.Equal(before.Streams[0].Samples, after.Streams[0].Samples);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void MeasuringTheMeasurementsAddsWhatTheMeasureGoldenHolds(string name, long withheld)
    {
        Assert.True(withheld > 0);
        string actual = Encspect.Tests.Measurement.MeasurementFixtureTests.Golden(Analyze(name).Report, Path.GetFileNameWithoutExtension(name));
        string path = Fixtures.MpegCompanion(name, ".measure.json");
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void WithholdingChangesNeitherTheDecodedSamplesNorTheVerificationNorAFinding(string name, long withheld)
    {
        Assert.True(withheld > 0);
        (Report plain, string plainPcm) = Analyze(name, measure: false);
        (Report measured, string measuredPcm) = Analyze(name);

        Assert.Equal(plainPcm, measuredPcm);
        Assert.Equal(plain.Streams[0].Verification!.DecodedSamples, measured.Streams[0].Verification!.DecodedSamples);
        Assert.True(plain.Streams[0].Verification!.Checksums.SequenceEqual(measured.Streams[0].Verification!.Checksums));
        Assert.Equal(plain.Findings.Select(f => f.Code).Order(), measured.Findings.Where(f => !f.Code.StartsWith("measure.", StringComparison.Ordinal)).Select(f => f.Code).Order());
    }

    // Files whose pages all hold their CRC withhold nothing.
    [Theory]
    [InlineData("ogg/external-oggenc-q10.ogg")]
    [InlineData("ogg/external-libopus-stereo-hybrid-20ms.opus")]
    [InlineData("ogg/external-speexenc-wb.spx")]
    [InlineData("flac/external-ogg-24-stereo-spanning.ogg")]
    public void PagesThatHoldTheirCrcWithholdNothing(string name)
    {
        (Report report, _) = Analyze(name);
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Equal(0, FailedPages(report));
        Assert.Equal(0, m.WithheldSamples);
        Assert.Equal(0, m.Gaps);
    }

    // The stored CRC of every audio page changed (the two header pages keep theirs, since the stream is named by its first page): every audio packet fails, and nothing is measured,
    // with no error (decision 7 of the design).
    [Fact]
    public void AStreamWhoseEveryAudioPageFailsMeasuresNothingWithoutAnError()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ogg/external-oggenc-q10.ogg"));
        int page = 0;
        for (int at = 0; at + 27 <= data.Length; page++)
        {
            data[at + 22] ^= page < 2 ? (byte)0 : (byte)0x55;
            int segments = data[at + 26];
            int length = 27 + segments;
            for (int i = 0; i < segments; i++)
            {
                length += data[at + 27 + i];
            }

            at += length;
        }

        (Report report, string pcm) = Analyze("every-page.ogg", data);
        (Report intact, string intactPcm) = Analyze("ogg/external-oggenc-q10.ogg");
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Equal(intactPcm, pcm);
        Assert.Equal(0, m.Samples);
        Assert.Equal(intact.Streams[0].Measurements!.Samples, m.WithheldSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed");
    }
}
