using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Dependent substreams that replace channels, name more channels than are coded, are lost to a gap or disagree with their independent frame (A/52 Annex E 2.3.1.2, 3.8.2).
public class Eac3DependentTests
{
    private static Report Measure(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, "ac3", name));
        return Analyzer.CreateDefault().Analyze(name, stream, new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } });
    }

    private static string PcmHash(string name)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, "ac3", Path.ChangeExtension(name, ".decode.json"))));
        return document.RootElement.GetProperty("streams")[0].GetProperty("pcmSha256").GetString()!;
    }

    // The dependent frames' channel map replaces both channels of the independent substream, so what the independent substream holds (here enhanced coupling, whose last block of a frame
    // waits for the next frame) cannot show in the output: it equals the output over independent frames without enhanced coupling.
    [Fact]
    public void Channels_a_dependent_substream_replaces_carry_its_data_whatever_the_independent_substream_holds()
    {
        Assert.Equal(PcmHash("eac3-splice-plain.eac3"), PcmHash("eac3-splice-ecpl.eac3"));
        Assert.NotEqual(PcmHash("eac3-splice-plain.eac3"), PcmHash("eac3-signal-dependent.eac3"));
    }

    [Fact]
    public void A_channel_map_that_names_more_channels_than_are_coded_gives_a_finding_and_decodes_what_is_coded()
    {
        Report report = Measure("eac3-chanmap-more.eac3");

        Assert.Contains(report.Findings, f => f.Code == "ac3.channel-map-count");
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(MeasurementStatus.Complete, stream.Measurements!.Status);
        Assert.Equal(4, stream.Measurements.ChannelsMeasured);
    }

    // A dependent frame the data lost makes its program frame lost for the one stream; the frame before and after stay in it.
    [Fact]
    public void A_dependent_frame_lost_to_a_gap_counts_its_program_frame_lost_in_the_same_stream()
    {
        Report report = Measure("eac3-dependent-lost.eac3");

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(1536, stream.Measurements!.LostSamples);
        Assert.Equal(MeasurementStatus.Incomplete, stream.Measurements.Status);
        Assert.Equal(stream.Samples, stream.Measurements.Samples + stream.Measurements.LostSamples + stream.Measurements.WithheldSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "ac3.stream-changed" or "ac3.dependent-excess");
    }

    // The orphan dependent frame after a lost independent frame belongs to no program frame: it is not decoded into the one before, and no repeated ID is claimed.
    [Fact]
    public void A_dependent_frame_whose_independent_frame_is_lost_is_an_orphan()
    {
        Report report = Measure("eac3-independent-lost.eac3");

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Contains(report.Findings, f => f.Code == "ac3.dependent-without-independent");
        Assert.DoesNotContain(report.Findings, f => f.Code == "ac3.dependent-excess");
        Assert.Equal(stream.Samples, stream.Measurements!.Samples + stream.Measurements.LostSamples + stream.Measurements.WithheldSamples);
        Assert.Equal(11, stream.CategorySeries[0].Count);
    }

    [Fact]
    public void A_dependent_frame_of_another_block_count_than_its_independent_frame_is_a_finding_and_loses_the_program_frame()
    {
        Report report = Measure("eac3-dependent-blocks.eac3");

        Assert.Contains(report.Findings, f => f.Code == "ac3.dependent-format-differs");
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(1536, stream.Measurements!.LostSamples);
    }

    // A repeated dependent substream ID is left out of the decoding as of the figures: the stream decodes the same as with each ID once.
    [Fact]
    public void A_repeated_dependent_id_is_decoded_once()
    {
        Report report = Measure("dependent-repeated.eac3");

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(4, stream.Measurements!.ChannelsMeasured);
        Assert.Equal(stream.Samples, stream.Measurements.Samples + stream.Measurements.LostSamples + stream.Measurements.WithheldSamples);
    }
}
