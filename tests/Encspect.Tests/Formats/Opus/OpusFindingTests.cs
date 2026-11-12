using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The findings of the entropy pass for frames that need more bits than they hold. A hybrid frame that states a redundant frame longer than its data, which libopus ignores while it
// conceals the CELT part, is opus.redundancy-overrun. A CELT frame whose range decoder runs past its data, which libopus refuses with an internal error (celt_decoder.c, "ec_tell > 8*len"),
// is opus.frame-overrun: no input is known to raise it (4,800 random packets of every configuration and the vectors never reach the check, and libopus never returns the error for
// them either), so the code is the same check as libopus's and has no test file.
public class OpusFindingTests
{
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
