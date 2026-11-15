using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The values of the multichannel side information that ISO/IEC 13818-3 forbids or leaves unassigned (2.5.2.15, 2.5.3.2.4) are a warning on the frames that hold them.
public class MpegMultichannelValueTests
{
    private const string Code = "mpeg-audio.multichannel-value-forbidden";

    [Fact]
    public void FramesWithForbiddenValuesAreCountedOnceEachAndTheFirstIsNamed()
    {
        Report report = Fixtures.AnalyzeMpeg("mpeg-audio/layer2/mc-forbidden-values.mp2");

        Finding finding = Assert.Single(report.Findings, f => f.Code == Code);
        Assert.Equal(4, finding.Count);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("lfe_allocation 15", finding.Message, StringComparison.Ordinal);
        Assert.Equal(0, finding.Node!.Offset);
    }

    // Each of the four frames alone is the one the finding counts: the fixture's other frames are clean, so dropping a frame drops one count.
    [Theory]
    [InlineData(0, "lfe_allocation 15")]
    [InlineData(1, "dematrix_procedure 3")]
    [InlineData(2, "dyn_cross_mode 15")]
    [InlineData(3, "scalefactor index of 63")]
    public void EachKindOfValueIsFoundInItsFrame(int frame, string text)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "mpeg-audio/layer2/mc-forbidden-values.mp2"));
        const int length = 1152;

        // Frames 4 and 5 are clean, and a file needs two verified frames to carry the extension: keep the one under test and the clean ones.
        byte[] kept = [.. data[(frame * length)..((frame + 1) * length)], .. data[(4 * length)..]];
        Report report = Analyzer.CreateDefault().Analyze("one.mp2", new MemoryStream(kept));

        Finding finding = Assert.Single(report.Findings, f => f.Code == Code);
        Assert.Equal(1, finding.Count);
        Assert.Contains(text, finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mpeg-audio/layer2/external-mctoolame-5-1.mp2")]
    [InlineData("mpeg-audio/layer2/external-mctoolame-5-1-crosstalk-96.mp2")]
    [InlineData("mpeg-audio/layer2/external-philips-5-1.mp2")]
    [InlineData("mpeg-audio/layer2/mc-3-2-lfe.mp2")]
    public void EncoderOutputHoldsNoForbiddenValue(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        Assert.DoesNotContain(report.Findings, f => f.Code == Code);
    }
}
