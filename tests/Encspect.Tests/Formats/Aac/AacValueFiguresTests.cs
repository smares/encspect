using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Host;

namespace Encspect.Tests.Formats.Aac;

// The figures of the quantized values (docs/formats/aac.md, "The quantized values"): the last nonzero coefficient, the nonzero and the escape-coded coefficients, which the
// walk of every analysis takes from each codebook symbol's nonzero mask, and the check of the window sequences of consecutive blocks. The values the hand-built files hold are
// counted again by the fixture script (aac_block_fixtures.py, value_fields) and held in each manifest.
public class AacValueFiguresTests
{
    public static TheoryData<string> Names() => Fixtures.Mp4Fixtures();

    [Theory]
    [MemberData(nameof(Names))]
    public void The_figures_of_the_values_lie_within_what_the_stream_states(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        foreach (StreamInfo stream in report.Streams)
        {
            Series? last = stream.Series.FirstOrDefault(s => s.Name == "Last Non-Zero Coefficient");
            Series? escapes = stream.Series.FirstOrDefault(s => s.Name == "Escape-Coded Coefficients");
            if (last is null)
            {
                Assert.Null(escapes);
                continue;
            }

            Series bandwidth = stream.Series.Single(s => s.Name == "Coded Bandwidth");
            Series bitrate = stream.Series.Single(s => s.Name == "Bitrate");
            Series overTime = stream.Series.Single(s => s.Name == "Last Non-Zero Coefficient Over Time");
            Assert.Equal(["Hz", "Hz"], [last.Unit, overTime.Unit]);
            Assert.Equal(last.Quantity, overTime.Quantity);
            Assert.Equal(last.Concept, overTime.Concept);
            Assert.Equal(GraphConcept.CodedBandwidth, last.Concept);
            Assert.Equal("Last Non-Zero Coefficient", last.Quantity);
            Assert.False(string.IsNullOrEmpty(last.Description));
            Assert.Equal("Last Non-Zero Coefficient", overTime.Anchor!.Name);

            // A unit's last nonzero line lies at or below the top of its last band, which is the coded bandwidth at the same x.
            for (int i = 0; i < last.X.Length; i++)
            {
                int at = bandwidth.X.Span.IndexOf(last.X.Span[i]);
                Assert.True(at >= 0, $"{name}: no coded bandwidth at {last.X.Span[i]}");
                Assert.True(last.Values.Span[i] <= bandwidth.Values.Span[at], $"{name}: {last.Values.Span[i]} above {bandwidth.Values.Span[at]} at {last.X.Span[i]}");
            }

            Assert.Equal($"{last.Values.Span.ToArray().Max():0} Hz", overTime.Anchor.Children.Single(c => c.Name == "Highest").Value!.ToString());
            Assert.Equal($"{last.Values.Span.ToArray().Min():0} Hz", overTime.Anchor.Children.Single(c => c.Name == "Lowest").Value!.ToString());
            Assert.True(bitrate.X.Length >= last.X.Length);

            Assert.NotNull(escapes);
            Assert.Equal("%", escapes.Unit);
            Assert.All(escapes.Values.ToArray(), v => Assert.InRange(v, 0, 100));
            Assert.Equal("Escape-Coded Coefficients", escapes.Anchor!.Name);
            Assert.False(string.IsNullOrEmpty(escapes.Description));
        }
    }

    [Fact]
    public void The_escapes_of_a_file_that_codes_codebook_11_with_escape_values_are_counted()
    {
        Report report = Fixtures.AnalyzeMpeg("aac/adts/windows.aac");
        StreamInfo stream = Assert.Single(report.Streams);
        Series escapes = stream.Series.Single(s => s.Name == "Escape-Coded Coefficients");
        Assert.Contains(escapes.Values.ToArray(), v => v > 0);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(0, 2, false)]
    [InlineData(0, 3, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, true)]
    [InlineData(1, 3, true)]
    [InlineData(2, 0, false)]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, 0, true)]
    [InlineData(3, 1, true)]
    [InlineData(3, 2, false)]
    [InlineData(3, 3, false)]
    public void The_slopes_of_two_windows_match_where_the_standard_calls_the_transition_meaningful(int previous, int next, bool allowed) =>
        Assert.Equal(allowed, BlockReader.SlopesMatch(previous, next));

    [Fact]
    public void Window_sequences_whose_slopes_do_not_match_are_reported_once_for_each_block_with_the_first_one_located()
    {
        Report report = Fixtures.AnalyzeMpeg("aac/adts/window-sequences-invalid.aac");
        Finding finding = Assert.Single(report.Findings, f => f.Code == "aac.window-slopes-differ");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.StartsWith("5 raw data blocks hold a window sequence", finding.Message);
        Assert.Equal("Window Sequence", finding.Node!.Name);
    }

    [Fact]
    public void A_gap_in_the_stream_forgets_the_window_sequence_before_it()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "adts", "window-sequences-invalid.aac"));
        int first = ((file[3] & 3) << 11) | (file[4] << 3) | (file[5] >> 5);

        // Junk between the first frame (only long) and the second (eight short windows, whose left slope does not match the right slope of a long window) is a gap, so the second follows no window.
        byte[] cut = [.. file[..first], .. new byte[] { 1, 2, 3, 4, 5 }, .. file[first..]];
        using var stream = new MemoryStream(cut);
        Report report = Analyzer.CreateDefault().Analyze("cut.aac", stream);
        Finding finding = Assert.Single(report.Findings, f => f.Code == "aac.window-slopes-differ");
        Assert.StartsWith("4 raw data blocks hold a window sequence", finding.Message);
    }
}
