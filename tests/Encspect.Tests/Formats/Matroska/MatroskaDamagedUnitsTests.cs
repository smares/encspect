using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Blocks inside a Matroska element whose CRC-32 fails (docs/design/damaged-units.md, decision 2): a Cluster's CRC-32 covers the Cluster's blocks and the Segment's every block, the
// container marks the frames of each (MappedBytes.FailsChecksum), the codec parsers of packets hand them on as CodedFrame.FailsChecksum, and the measurements leave their positions out
// as withheld samples ("checksum failed"), with the span after them that the decoder's state carries (Opus: 3,840 samples). The decoded PCM, the codec's verification and the findings
// stay as they are.
public class MatroskaDamagedUnitsTests
{
    // The files and the samples withheld: the hand-made file with a Cluster whose data was edited after its CRC-32 was computed (three blocks of 960 samples, and nothing after them),
    // and an encoder-written file with the stored CRC-32 of its eighth Cluster changed (five blocks of 960 samples, then 3,840; make_ogg_matroska_damaged_units_fixtures.py).
    public static TheoryData<string, long> Names => new()
    {
        { "matroska/crc-damaged.mka", 3 * 960 },
        { "matroska/damaged-units/external-ffmpeg-small-clusters-cluster-crc-changed.mka", (5 * 960) + 3840 },
    };

    private static (Report Report, string Pcm) Analyze(string name, byte[]? data = null, bool measure = true)
    {
        using Stream stream = data is null ? File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name)) : new MemoryStream(data);
        var sink = new PcmHashingSink();
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = measure ? new MeasureOptions() : null } });
        return (report, sink.Hash());
    }

    private static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));

    // The offsets of the Clusters' CRC-32 element data.
    private static List<int> ClusterCrcs(byte[] data)
    {
        var offsets = new List<int>();
        for (int at = 0; at + 8 < data.Length; at++)
        {
            if (data[at] == 0x1F && data[at + 1] == 0x43 && data[at + 2] == 0xB6 && data[at + 3] == 0x75)
            {
                int size = Ebml(data, at + 4, out int length);
                int body = at + 4 + length;
                if (size >= 6 && data[body] == 0xBF && data[body + 1] == 0x84)
                {
                    offsets.Add(body + 2);
                }
            }
        }

        return offsets;
    }

    private static int Ebml(byte[] data, int at, out int length)
    {
        length = 1;
        while ((data[at] & (0x80 >> (length - 1))) == 0)
        {
            length++;
        }

        long value = data[at] & ((0x80 >> (length - 1)) - 1);
        for (int i = 1; i < length; i++)
        {
            value = (value << 8) | data[at + i];
        }

        return (int)Math.Min(value, int.MaxValue);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheWithheldPositionsAreThoseOfTheBlocksInTheFailedClusterAndTheSpanAfterThem(string name, long withheld)
    {
        (Report report, _) = Analyze(name);
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Contains(report.Findings, f => f.Code == "matroska.crc-mismatch");
        Assert.Equal(withheld, m.WithheldSamples);
        Assert.Equal(withheld, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(1, m.Gaps);
        Assert.Equal(0, m.LostSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed");
    }

    [Fact]
    public void ChangingAClusterCrcChangesNeitherTheDecodedPcmNorTheSamplesDecodedNorTheVerification()
    {
        (Report before, string beforePcm) = Analyze("matroska/external-ffmpeg-small-clusters.mka");
        (Report after, string afterPcm) = Analyze("matroska/damaged-units/external-ffmpeg-small-clusters-cluster-crc-changed.mka");

        Assert.Equal(beforePcm, afterPcm);
        Assert.Equal(before.Streams[0].Verification!.DecodedSamples, after.Streams[0].Verification!.DecodedSamples);
        Assert.True(before.Streams[0].Verification!.Checksums.SequenceEqual(after.Streams[0].Verification!.Checksums));
        Assert.Equal(0, before.Streams[0].Measurements!.WithheldSamples);
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

    // Clusters that hold their CRC-32, ones without it and ones whose CRC-32 element is not compared (misplaced, the wrong size) withhold nothing.
    [Theory]
    [InlineData("matroska/crc-valid.mka")]
    [InlineData("matroska/crc-misplaced.mka")]
    [InlineData("matroska/external-ffmpeg-small-clusters.mka")]
    [InlineData("matroska/opus-stereo-clusters.mka")]
    public void ClustersWithoutAFailedCrcWithholdNothing(string name)
    {
        (Report report, _) = Analyze(name);
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.DoesNotContain(report.Findings, f => f.Code == "matroska.crc-mismatch");
        Assert.Equal(0, m.WithheldSamples);
        Assert.Equal(0, m.Gaps);
    }

    // One Cluster, so every block of the track fails: nothing is measured, with no error (decision 7 of the design), and each codec parser of packets hands the flag on: FLAC, Vorbis, ALAC,
    // TTA and WavPack, whose frames or packets are the blocks' frames, and Opus.
    [Theory]
    [InlineData("matroska/external-ffmpeg-flac.mka")]
    [InlineData("matroska/external-ffmpeg-vorbis.mka")]
    [InlineData("matroska/external-ffmpeg-opus.mka")]
    [InlineData("matroska/external-ffmpeg-alac.mka")]
    [InlineData("matroska/external-ffmpeg-tta.mka")]
    [InlineData("matroska/external-ffmpeg-wavpack.mka")]
    [InlineData("matroska/external-ffmpeg-pcm.mka")]
    public void ACodecParserOfPacketsPassesTheContainersFailureOn(string name)
    {
        byte[] data = Read(name);
        int crc = Assert.Single(ClusterCrcs(data));
        data[crc] ^= 0x55;
        (Report intact, string intactPcm) = Analyze(name);
        (Report report, string pcm) = Analyze(Path.GetFileName(name), data);
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Equal(intactPcm, pcm);
        Assert.Contains(report.Findings, f => f.Code == "matroska.crc-mismatch");
        Assert.Equal(0, m.Samples);
        Assert.True(m.WithheldSamples > 0);
        Assert.Equal(intact.Streams[0].Measurements!.Samples, m.WithheldSamples);
        Assert.Equal(intact.Streams[0].Verification!.DecodedSamples, report.Streams[0].Verification!.DecodedSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed");
    }

    // The Segment's CRC-32 covers every Cluster: the same file with a CRC-32 that holds withholds nothing, and one that does not withholds every block.
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 72000)]
    public void ASegmentCrcThatFailsMarksEveryBlock(bool isChanged, long withheld)
    {
        byte[] source = Read("matroska/external-ffmpeg-small-clusters.mka");
        byte[] data = WithSegmentCrc(source, isChanged);
        (Report report, string pcm) = Analyze("segment-crc.mka", data);
        (_, string intactPcm) = Analyze("matroska/external-ffmpeg-small-clusters.mka");
        StreamMeasurements m = report.Streams[0].Measurements!;

        Assert.Equal(intactPcm, pcm);
        Assert.Equal(isChanged, report.Findings.Any(f => f.Code == "matroska.crc-mismatch"));
        Assert.Equal(withheld, m.WithheldSamples);
        Assert.Equal(withheld == 0 ? 72000 : 0, m.Samples);
    }

    // The file with a CRC-32 element as the first child of its Segment, which covers the rest of the Segment: the Segment is rewritten with an eight-octet size.
    private static byte[] WithSegmentCrc(byte[] data, bool isChanged)
    {
        int segment = data.AsSpan().IndexOf<byte>([0x18, 0x53, 0x80, 0x67]);
        Assert.True(segment > 0);
        int size = Ebml(data, segment + 4, out int length);
        int start = segment + 4 + length;
        byte[] rest = data[start..(start + size)];
        uint crc = Crc32(rest) ^ (isChanged ? 0x55u : 0u);
        byte[] element = [0xBF, 0x84, (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24)];
        long total = element.Length + rest.Length;
        byte[] header = [0x18, 0x53, 0x80, 0x67, 0x01, .. Enumerable.Range(0, 7).Select(i => (byte)(total >> (8 * (6 - i))))];
        return [.. data[..segment], .. header, .. element, .. rest, .. data[(start + size)..]];
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}
