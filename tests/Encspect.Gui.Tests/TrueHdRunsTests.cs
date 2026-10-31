using Avalonia.Headless.XUnit;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Host;
using Encspect.Gui.Views;

namespace Encspect.Gui.Tests;

// The runs of access units of a TrueHD stream in the Audio panels: the frame a time leads to, the frame a click selects inside a run, and
// the mark that keeps the clicked column.
public class TrueHdRunsTests
{
    private static string LeadingUnits => Path.Combine(Fixtures.Directory, "truehd", "built-leading-units.thd");

    private static List<Node> FramesOf(StreamInfo stream)
    {
        var frames = new List<Node>();
        void Collect(Node node)
        {
            foreach (Node child in node.Children)
            {
                if (child.Kind == NodeKind.Frame)
                {
                    frames.Add(child);
                }
                else if (child.Kind is NodeKind.Stream or NodeKind.Detail)
                {
                    Collect(child);
                }
            }
        }

        foreach (Node node in stream.Nodes)
        {
            Collect(node);
        }

        return frames;
    }

    [Fact]
    public void A_time_leads_to_a_frame_that_is_never_earlier_than_the_true_one_though_units_lead_the_stream()
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(LeadingUnits);
        StreamInfo stream = report.Streams[0];
        FrameRuns runs = stream.Series.First(s => s.Name == "Bitrate").Runs!;
        SampleLocator locator = SampleLocator.For(stream)!;
        long leading = runs.FirstFrames.Span[0] - 1;
        Assert.True(leading > 0, "The file has no leading units.");
        long live = runs.FrameCount - leading;
        long unit = stream.Samples / live;

        for (long position = 0; position < stream.Samples; position += 13)
        {
            long found = locator.FrameIndexAt(position) + 1;
            long truth = leading + (position / unit) + 1;
            Assert.InRange(found - truth, 0, 1);
        }
    }

    [AvaloniaFact]
    public void A_click_inside_a_run_selects_the_frame_in_the_tree_and_keeps_the_clicked_column()
    {
        DetailWindow window = AudioPanelTests.Open(LeadingUnits);
        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == "Waveform");
        var graph = (WaveformGraph)window.Graph.Graph!;
        StreamInfo stream = window.Model.Report!.Streams[0];
        List<Node> frames = FramesOf(stream);
        SampleLocator locator = graph.Locator!;
        FrameRuns runs = stream.Series.First(s => s.Name == "Bitrate").Runs!;
        bool isInside = false;
        for (int column = 0; column < graph.Columns; column++)
        {
            long position = graph.PositionOfColumn(column);
            int after = locator.FramesIntoRunAt(position);
            window.Model.Graphs.Activate(column);
            long offset = window.Model.SelectedNode!.Node.Offset;
            int firstNumber = frames.FindIndex(f => f.Offset == locator.FrameOffsetAt(position));
            Assert.Equal(frames[firstNumber + after].Offset, offset);
            Assert.Equal(column, window.Graph.MarkedIndex);
            isInside |= after > 0;
        }

        Assert.True(isInside, $"No click fell inside a run: {graph.Columns} columns, {runs.FrameCount} frames.");
        window.Close();
    }

    [AvaloniaFact]
    public void Selecting_a_frame_after_another_walks_the_siblings_that_are_frames()
    {
        DetailWindow window = AudioPanelTests.Open(LeadingUnits, decode: false);
        List<Node> frames = FramesOf(window.Model.Report!.Streams[0]);
        for (int skip = 0; skip < 4; skip++)
        {
            Assert.Equal(frames[10 + skip].Offset, window.Model.SelectFrameAt(frames[10].Offset, skip)!.Node.Offset);
        }

        // Past the end of the list the last frame is taken.
        Assert.Equal(frames[^1].Offset, window.Model.SelectFrameAt(frames[^3].Offset, 50)!.Node.Offset);
        window.Close();
    }

    // Built-parity-mismatch had a run of 27 units of which 16 carried samples, over which a time was spread, so a click was up to 9 units early.
    [Fact]
    public void A_click_is_never_earlier_than_the_true_unit_though_units_that_code_nothing_trail_a_run()
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.Directory, "truehd", "built-parity-mismatch.thd"));
        StreamInfo stream = report.Streams[0];
        FrameRuns runs = stream.Series.First(s => s.Name == "Bitrate").Runs!;
        long unit = Enumerable.Range(0, runs.FirstFrames.Length).Max(r => runs.Samples.Span[r] / runs.FramesIn(r));
        // The units of a run that carry samples come first and carry `unit` each (the units that cannot be read trail the run), so the true
        // unit of a position is known from the runs alone, and the click is never before it.
        SampleLocator locator = SampleLocator.For(stream)!;
        long start = 0;
        for (int run = 0; run < runs.FirstFrames.Length; run++)
        {
            for (long position = start; position < start + runs.Samples.Span[run]; position += 7)
            {
                long truth = runs.FirstFrames.Span[run] + ((position - start) / unit);
                Assert.True(locator.FrameIndexAt(position) + 1 >= truth, $"Position {position}: unit {locator.FrameIndexAt(position) + 1} before unit {truth}.");
            }

            start += runs.Samples.Span[run];
        }
    }

    // A swatch of the Frame Map selects the frame the map names, not the frame a click on a panel had counted into its run.
    [AvaloniaFact]
    public void A_swatch_of_the_map_selects_the_first_frame_of_a_run_after_a_click_inside_one()
    {
        DetailWindow window = AudioPanelTests.Open(LeadingUnits);
        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == "Waveform");
        var graph = (WaveformGraph)window.Graph.Graph!;
        SampleLocator locator = graph.Locator!;
        int column = Enumerable.Range(0, graph.Columns).First(c => locator.FramesIntoRunAt(graph.PositionOfColumn(c)) > 0);
        window.Model.Graphs.Activate(column);

        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == "Bitrate");
        window.Model.Graphs.SelectForm(GraphForm.Map);
        var map = (FrameMapGraph)window.Graph.Graph!;
        long selected = window.Model.SelectedNode!.Node.Offset;
        long expected = map.NextFrameOf(0, selected)!.Value;
        window.Model.Graphs.ActivateCategory(0);

        Assert.Equal(expected, window.Model.SelectedNode!.Node.Offset);
        window.Close();
    }
}
