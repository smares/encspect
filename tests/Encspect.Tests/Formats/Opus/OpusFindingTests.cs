using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The two findings of the entropy pass: a CELT frame that needs more bits than it holds, which libopus refuses with an internal error (opus.frame-overrun), and a hybrid frame that
// states a redundant frame longer than its data, which libopus ignores while it conceals the CELT part (opus.redundancy-overrun).
public class OpusFindingTests
{
    [Fact]
    public void ACeltFrameThatNeedsMoreBitsThanItHoldsIsAFindingAndNotDecoded()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", "opus-extreme-frames.bin"));

        Report report = Analyzer.CreateDefault().Analyze("overrun.opus", new MemoryStream(data), new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } });

        Finding finding = Assert.Single(report.Findings, f => f.Code == "opus.frame-overrun");
        Assert.Contains("libopus refuses", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }

    [Fact]
    public void AHybridFrameWhoseRedundancyReachesPastItsDataIsAFindingThatSaysWhatLibopusDoes()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", "opus-hybrid-redundancy-overrun.bin"));

        Report report = Analyzer.CreateDefault().Analyze("redundancy.opus", new MemoryStream(data), new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } });

        Finding finding = Assert.Single(report.Findings, f => f.Code == "opus.redundancy-overrun");
        Assert.Contains("libopus ignores the redundancy", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Code is "opus.frame-overrun" or "parse.failed" or "decode.failed");
    }
}
