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

    // Bits flipped in the parametric stereo extension's IPD and OPD, its size and its coded values: whatever they come to, the stream is analyzed and decoded without an unhandled exception.
    [Fact]
    public void DamagedPhaseDifferencesNeverFailTheAnalysisOrTheDecoder()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "adts", "ps-phase.aac"));
        var random = new Random(20261127);
        for (int round = 0; round < 150; round++)
        {
            byte[] damaged = (byte[])file.Clone();
            for (int flip = 0; flip < 1 + (round % 4); flip++)
            {
                // The access units' bodies, after the ADTS header of the first frame.
                int at = 7 + random.Next(damaged.Length - 7);
                damaged[at] ^= (byte)(1 << random.Next(8));
            }

            Report report = Analyzer.CreateDefault().Analyze("damaged.aac", new MemoryStream(damaged), new AnalysisOptions { Decoding = new DecodeOptions { MaxWork = long.MaxValue, Output = _ => new DecoderWorkTests.NullSink() } });
            Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        }
    }

    private static StreamInfo Measured(string name, string folder, out Report report)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, folder, name));
        report = Analyzer.CreateDefault().Analyze(name, new MemoryStream(file), new AnalysisOptions { Decoding = new DecodeOptions { MaxWork = 1 << 24, Output = _ => new DecoderWorkTests.NullSink(), Measure = new MeasureOptions() } });
        return report.Streams.Single();
    }

    // A block whose channel elements differ from the last block's (a single channel element where a pair was, in a stream whose frames all hold SBR data) is a loss: the SBR state of the
    // elements no longer fits it, so it is decoded from reset state and withheld, as the one after a gap (the second test counts the withheld samples; this input ends inside its mdat). The buffers of an element serve a pair or a single channel, whichever the block has
    // (the first version indexed the second channel of a one-channel buffer: an IndexOutOfRangeException).
    [Fact]
    public void ABlockWhoseElementLayoutChangesIsDecodedFromResetStateAndWithheld()
    {
        _ = Measured("aac-sbr-element-width-changes.bin", "fuzz", out Report report);

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed" or "measure.failed");
        Assert.Contains(report.Findings, f => f.Code == "aac.layout-changed");
    }

    // The same counts in another order (channel configuration 3) change the layout too: every block but the first is withheld, and the parser says why.
    [Fact]
    public void ElementsInAnotherOrderAreReportedAndTheirBlocksWithheld()
    {
        StreamInfo stream = Measured("elements-order-changes.aac", Path.Combine("aac", "adts"), out Report report);

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed" or "measure.failed");
        Assert.Contains(report.Findings, f => f.Code == "aac.layout-changed");
        Assert.Equal(5 * 1024, stream.Measurements!.WithheldSamples);
    }
}
