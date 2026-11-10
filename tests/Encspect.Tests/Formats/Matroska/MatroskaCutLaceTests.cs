using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// A file cut inside a laced block: the lace is valid, the file is cut. The frames that are whole in the file are handed to the codec
// parser, the one the file ends inside as cut (its samples are lost), the frames after it not at all, and no `matroska.lacing-invalid`
// names the lace (RFC 9559, section 10.3).
public class MatroskaCutLaceTests
{
    private static readonly byte[] OpusFrame = [0xFC, 0xFF, 0xFE];

    private static Report Analyze(byte[] data) => Analyzer.CreateDefault().Analyze("cut.mka", new MemoryStream(data));

    // Three Opus frames of 3 bytes in one SimpleBlock with the lacing flag bits given (0x02 Xiph, 0x06 EBML).
    private static byte[] LacedFile(byte lacingFlags)
    {
        byte[] sizes = lacingFlags == 0x02 ? [3, 3] : [0x83, 0xBF];
        byte[] block = MatroskaBuilder.Element(0xA3, MatroskaBuilder.Concat([0x81, 0x00, 0x00, (byte)(0x80 | lacingFlags), 0x02], sizes, OpusFrame, OpusFrame, OpusFrame));
        return MatroskaBuilder.File(MatroskaBuilder.OpusTracks(), MatroskaBuilder.Cluster(0, block));
    }

    // The offset in the file of the block's first frame.
    private static int FramesAt(byte[] file) => file.AsSpan().IndexOf(OpusFrame);

    public static TheoryData<byte, int, int> Cuts =>
        new()
        {
            // lacing flags, bytes of the frames kept, frames whole in the file
            { 0x02, 1, 0 },
            { 0x02, 3, 1 },
            { 0x02, 4, 1 },
            { 0x02, 7, 2 },
            { 0x06, 1, 0 },
            { 0x06, 3, 1 },
            { 0x06, 4, 1 },
            { 0x06, 7, 2 },
        };

    [Theory]
    [MemberData(nameof(Cuts))]
    public void ACutLaceIsNotInvalidAndKeepsItsWholeFrames(byte lacing, int kept, int whole)
    {
        byte[] file = LacedFile(lacing);
        Report complete = Analyze(file);
        Assert.Equal(0, complete.Streams.Single().LostSamples);
        Assert.Equal(3 * 960 - 312, complete.Streams.Single().Samples);

        Report report = Analyze(file[..(FramesAt(file) + kept)]);

        Assert.DoesNotContain(report.Findings, f => f.Code is "matroska.lacing-invalid" or "matroska.block-short" or "parse.failed");
        Assert.Contains(report.Findings, f => f.Code == "matroska.element-truncated");
        StreamInfo stream = Assert.Single(report.Streams);
        bool endsInside = kept % 3 != 0;
        // 960 samples a frame at 48 kHz, less the pre-skip of 312.
        Assert.Equal(endsInside ? 960 : 0, stream.LostSamples);
        Assert.Equal(Math.Max(0, (960 * (whole + (endsInside ? 1 : 0))) - 312), stream.Samples);
        TestFiles.CheckInsideParents(report.Root);
    }

    [Theory]
    [InlineData(0x02)]
    [InlineData(0x06)]
    public void ACutInsideTheLacingHeaderIsNotInvalidEither(byte lacing)
    {
        byte[] file = LacedFile(lacing);
        int header = FramesAt(file) - (lacing == 0x02 ? 2 : 2);

        Report report = Analyze(file[..(header + 1)]);

        Assert.DoesNotContain(report.Findings, f => f.Code is "matroska.lacing-invalid" or "parse.failed");
        Assert.Contains(report.Findings, f => f.Code == "matroska.element-truncated");
    }

    // mkvmerge puts eight ALAC frames in an EBML-laced block: the cut copies of the fixture keep the frames that are whole.
    [Theory]
    [InlineData(90000, 73728, 4096)]
    [InlineData(30000, 24576, 4096)]
    public void ACutMkvmergeFileKeepsItsWholeFramesAndCountsTheCutOneAsLost(int length, long samples, long lost)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "alac", "external-mkvmerge-16-stereo.mka"));
        Report report = Analyze(file[..length]);

        Assert.DoesNotContain(report.Findings, f => f.Code is "matroska.lacing-invalid" or "matroska.track-empty" or "parse.failed");
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(samples, stream.Samples);
        Assert.Equal(lost, stream.LostSamples);
    }
}
