using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Formats.MpegAudio;

// The values of the multichannel side information that ISO/IEC 13818-3 forbids or leaves unassigned (2.5.2.15) are a warning on the frames that hold them;
// lfe_allocation 15 is valid (16-bit samples), as the conformance streams test01 and test17 use it.
public class MpegMultichannelValueTests
{
    private const string Code = "mpeg-audio.multichannel-value-forbidden";

    [Fact]
    public void FramesWithForbiddenValuesAreCountedOnceEachAndTheFirstIsNamed()
    {
        Report report = Fixtures.AnalyzeMpeg("mpeg-audio/layer2/mc-forbidden-values.mp2");

        Finding finding = Assert.Single(report.Findings, f => f.Code == Code);
        Assert.Equal(3, finding.Count);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("dematrix_procedure 3", finding.Message, StringComparison.Ordinal);
        Assert.Equal(1152, finding.Node!.Offset);
    }

    // Each of the three frames alone is the one the finding counts: the fixture's other frames are clean, so dropping a frame drops one count.
    [Theory]
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

    // Phantom center coding keeps the center in the additional channels (13818-3 2.5.2.15): 0, 3 and 4 in 3/1. The first three frames of the fixture hold a tc_allocation outside them; the others are clean.
    [Theory]
    [InlineData("mc-forbidden-phantom-3-1", 3, "tc_allocation 1")]
    public void APhantomCenterOutsideTheAllowedAllocationsIsFound(string name, int count, string text)
    {
        Report report = Fixtures.AnalyzeMpeg($"mpeg-audio/layer2/{name}.mp2");

        Finding finding = Assert.Single(report.Findings, f => f.Code == Code);
        Assert.Equal(count, finding.Count);
        Assert.Contains(text, finding.Message, StringComparison.Ordinal);
        Assert.Contains("phantom center", finding.Message, StringComparison.Ordinal);
    }

    // The 3/1 allocations that keep a phantom center (0, 3 and 4) are the fixture's last three frames.
    [Fact]
    public void ThePhantomAllocationsOf31ThatKeepTheCenterAreClean()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "mpeg-audio/layer2/mc-forbidden-phantom-3-1.mp2"));
        Report report = Analyzer.CreateDefault().Analyze("clean.mp2", new MemoryStream(data[(3 * 1152)..]));

        Assert.DoesNotContain(report.Findings, f => f.Code == Code);
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

    // The first frame's lfe_allocation 15 reads 16-bit LFE samples and is no finding.
    [Fact]
    public void AnLfeAllocationOf15IsValid()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "mpeg-audio/layer2/mc-forbidden-values.mp2"));
        const int length = 1152;
        byte[] kept = [.. data[..length], .. data[(4 * length)..]];
        Report report = Analyzer.CreateDefault().Analyze("one.mp2", new MemoryStream(kept));

        Assert.DoesNotContain(report.Findings, f => f.Code == Code);
    }
}
