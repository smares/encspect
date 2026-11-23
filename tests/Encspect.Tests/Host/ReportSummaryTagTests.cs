using Encspect.Host;
using Xunit;

namespace Encspect.Tests;

public class ReportSummaryTagTests
{
    // The summary's Tags values (the GUI's Tags column and the CSV's) carry the version of a tag the parser read.
    [Fact]
    public void Tags_name_the_version_of_each_parsed_tag()
    {
        ReportSummary summary = ReportSummary.From(Fixtures.AnalyzeMpeg("tags/stack-id3v2-lyrics3-ape-id3v1.mp3"));

        Assert.Equal(["APEv2", "ID3v1", "ID3v2", "Lyrics3 v2"], summary.Tags.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("APE", summary.Tags);
        Assert.DoesNotContain("Lyrics3", summary.Tags);
    }
}
