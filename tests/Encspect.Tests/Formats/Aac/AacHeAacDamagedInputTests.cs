using Encspect.Core;
using Encspect.Host;
using Encspect.Tests.Decoding;

namespace Encspect.Tests.Formats.Aac;

// HE-AAC streams whose SBR or parametric stereo data states what the specification does not allow: they are decoded to the end, and the damaged data is a finding of the parser or is carried
// as the standard's ranges say, never decode.failed.
public class AacHeAacDamagedInputTests
{
    private static Report Decode(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", name));
        return Analyzer.CreateDefault().Analyze(name, new MemoryStream(file), new AnalysisOptions { Decoding = new DecodeOptions { MaxWork = long.MaxValue, Output = _ => new DecoderWorkTests.NullSink() } });
    }

    // Envelope borders of a VARVAR frame (bs_var_bord_0 3, bs_num_rel_0 3 of 8 slots, bs_var_bord_1 0): 3, 11, 19, 27 and then 8 and 16 do not rise, and passed the 40 time slots.
    [Fact]
    public void SbrBordersOutsideTheFrameAreDamagedSbrData()
    {
        Report report = Decode("aac-sbr-varvar-borders-outside-the-frame.bin");

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Contains(report.Findings, f => f.Code == "aac.sbr-data-invalid" && f.Message.Contains("envelope borders", StringComparison.Ordinal));
    }

    // Fine IID values of 15 and then a coarse header in a frame without envelopes: the carried values are held to the coarse range.
    [Fact]
    public void ParametricStereoIidCarriedIntoACoarseFrameStaysInRange()
    {
        Report report = Decode("aac-ps-fine-to-coarse-iid-without-envelopes.bin");

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Equal(DecodeStatus.Decoded, report.Streams.Single().Verification!.Decoding);
    }

    // A block whose channel elements differ from the earlier blocks' (a single channel element where a channel pair was, in a stream whose frames all hold SBR data) gets an SBR
    // element of its own width: the buffers of the earlier one hold one channel, and reading the second was an IndexOutOfRangeException.
    [Fact]
    public void ABlockWhoseElementLayoutChangesIsDecodedWithAnElementOfItsOwnWidth()
    {
        Report report = Decode("aac-sbr-element-width-changes.bin");

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }
}
