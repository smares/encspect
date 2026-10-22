using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Host;

namespace Encspect.Gui.Tests;

public class GraphTests
{
    [Fact]
    public void Decimating_100000_points_keeps_every_columns_extremes()
    {
        const int count = 100_000;
        var random = new Random(7);
        double[] values = new double[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = 32 + (random.Next(15) * 21.0);
        }

        values[54_321] = 999;
        values[12_345] = -5;

        GraphColumn[] columns = GraphColumns.Decimate(values, 800);

        Assert.Equal(800, columns.Length);
        Assert.Equal(0, columns[0].First);
        Assert.Equal(count, columns[^1].End);
        for (int c = 0; c < columns.Length; c++)
        {
            GraphColumn column = columns[c];
            if (c > 0)
            {
                Assert.Equal(columns[c - 1].End, column.First);
            }

            Assert.InRange(column.End - column.First, 124, 126);
            double[] slice = values[column.First..column.End];
            Assert.Equal(slice.Min(), column.Min);
            Assert.Equal(slice.Max(), column.Max);
            Assert.Equal(column.Min, values[column.MinIndex]);
            Assert.Equal(column.Max, values[column.MaxIndex]);
            Assert.InRange(column.MinIndex, column.First, column.End - 1);
            Assert.InRange(column.MaxIndex, column.First, column.End - 1);
        }

        Assert.Equal(999, columns.Max(c => c.Max));
        Assert.Equal(-5, columns.Min(c => c.Min));
        Assert.Contains(columns, c => c.MaxIndex == 54_321);

        // Fewer points than columns: a column each.
        GraphColumn[] few = GraphColumns.Decimate(values.AsSpan(0, 10), 800);
        Assert.Equal(10, few.Length);
        Assert.All(few, c => Assert.Equal(1, c.End - c.First));
    }

    [Fact]
    public void Graphs_come_from_the_streams_series_alone()
    {
        StreamInfo stream = Stream(frames: 100_000, out double[] bitrates, out _);

        IReadOnlyList<GraphChoice> entries = GraphsViewModel.For(stream);

        // One entry for each quantity: the per-frame and per-second series in the same unit are its forms.
        Assert.Equal(["Bitrate", "Reservoir Fill"], entries.Select(e => e.Title));
        Assert.Equal([GraphForm.Frames, GraphForm.OverTime, GraphForm.Summary], entries[0].Forms.Select(f => f.Kind));
        Assert.Equal(["Per Frame", "Over Time", "Distribution"], entries[0].Forms.Select(f => f.Title));
        Assert.Equal([GraphForm.Frames], entries[1].Forms.Select(f => f.Kind));
        Assert.False(entries[1].HasForms);
        Graph[] graphs = [entries[0].Forms[0].Graph, entries[0].Forms[2].Graph, entries[0].Forms[1].Graph, entries[1].Graph];
        var line = (LineGraph)graphs[0];
        Assert.Equal(100_000, line.Count);
        Assert.Equal("kbps", line.Unit);
        Assert.Equal(bitrates.Max(), line.Maximum);
        Assert.Equal("Frame 3 · 0x1A2 · 160 kbps", line.Describe(2));
        Assert.Equal(418, line.FrameAt(2, null));
        Assert.Equal(2, line.IndexOfFrame(418));
        Assert.Null(line.IndexOfFrame(419));
        Assert.Equal("bytes", graphs[3].Unit);
        Assert.Equal("0:05 · 140 kbps", graphs[2].Describe(5));
    }

    [Fact]
    public void The_distribution_counts_the_per_frame_bitrates()
    {
        StreamInfo stream = Stream(frames: 100_000, out double[] bitrates, out double[] offsets);
        var distribution = (DistributionGraph)GraphsViewModel.For(stream)[0].Forms.Single(f => f.Kind == GraphForm.Summary).Graph;

        var expected = bitrates.GroupBy(b => b).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())).ToList();
        Assert.Equal(expected, distribution.Bars.Select(b => (b.Low, b.Count)));
        Assert.Equal(expected.Max(e => e.Item2), distribution.Maximum);
        int index = expected.FindIndex(e => e.Key == 160);
        int count = expected[index].Item2;
        Assert.Equal($"160 kbps · {count:N0} frames · {100.0 * count / bitrates.Length:0.0}%", distribution.Describe(index));
        Assert.Equal("160", distribution.Label(index));

        // A bar leads to its first frame, then to the next one after the frame selected.
        long first = (long)offsets[Array.IndexOf(bitrates, 160.0)];
        Assert.Equal(first, distribution.FrameAt(index, null));
        long second = distribution.FrameAt(index, first)!.Value;
        Assert.True(second > first);
        Assert.Equal(160, bitrates[Array.IndexOf(offsets, (double)second)]);
        Assert.Equal(index, distribution.IndexOfFrame(second));
    }

    [Fact]
    public void A_distribution_of_many_distinct_values_falls_into_ranges()
    {
        double[] values = Enumerable.Range(0, 1000).Select(i => 100 + (i * 0.37)).ToArray();
        double[] offsets = Enumerable.Range(0, 1000).Select(i => i * 10.0).ToArray();
        var distribution = new DistributionGraph(new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, values));

        Assert.Equal(DistributionGraph.RangeBars, distribution.Count);
        Assert.Equal(1000, distribution.Bars.Sum(b => b.Count));
        Assert.Equal(100, distribution.Bars[0].Low);
        Assert.Equal(values.Max(), distribution.Bars[^1].High);
        Assert.All(Enumerable.Range(0, 1000), i => Assert.NotNull(distribution.IndexOfFrame(i * 10)));
    }

    [Fact]
    public void A_second_leads_to_the_first_frame_that_starts_in_it()
    {
        // 1152 samples at 44.1 kHz: frame 39 starts at 1.0187 s, frame 38 at 0.9926 s.
        StreamInfo stream = Stream(frames: 400, out _, out double[] offsets);
        LineGraph perSecond = GraphsViewModel.For(stream).SelectMany(e => e.Forms).Select(f => f.Graph).OfType<LineGraph>().Single(g => g.Axis == SeriesAxis.Seconds && g.Unit == "kbps");

        Assert.Equal((long)offsets[0], perSecond.FrameAt(0, null));
        Assert.Equal((long)offsets[39], perSecond.FrameAt(1, null));
        Assert.Equal(1, perSecond.IndexOfFrame((long)offsets[39]));
        Assert.Equal(0, perSecond.IndexOfFrame((long)offsets[38]));
    }

    [Fact]
    public void Graphs_of_a_real_stream_and_the_decoder_note()
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(Fixtures.Stats("vbr-seconds.mp3"));
        var graphs = new GraphsViewModel();

        graphs.Load(report);

        Assert.True(graphs.HasGraphs);
        Assert.False(graphs.HasStreamChoice);
        Assert.Equal(["Bitrate", "Reservoir Fill"], graphs.Choices.Select(c => c.Title));
        Assert.Equal(["Per Frame", "Over Time", "Distribution"], graphs.Choices[0].Forms.Select(f => f.Title));
        Assert.Equal(["Per Frame", "Over Time"], graphs.Choices[1].Forms.Select(f => f.Title));
        Assert.Same(graphs.Choices[0], graphs.Selected);
        Assert.True(graphs.Choices[0].IsSelected);
        Assert.Equal("Waveform and spectrogram need a decoder, which MPEG Audio does not have yet.", graphs.DecoderNote);

        // The choice holds for the next file that has a graph of that title.
        graphs.Selected = graphs.Choices[1];
        graphs.Load(Analyzer.CreateDefault().AnalyzeFile(Fixtures.Mpeg("external-lame-vbr-v2.mp3")));
        Assert.Equal("Reservoir Fill", graphs.Selected!.Title);

        // A file without streams has neither graphs nor the note.
        graphs.Load(Analyzer.CreateDefault().Analyze("notes.txt", new MemoryStream("not audio"u8.ToArray())));
        Assert.False(graphs.HasGraphs);
        Assert.Null(graphs.Graph);
        Assert.Null(graphs.DecoderNote);
    }

    [Fact]
    public void Clicking_a_point_selects_its_frame_in_the_tree_and_hex_view()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Stats("vbr-seconds.mp3"));
        ui.Until(() => load.IsCompleted);
        var line = (LineGraph)model.Graphs.Graph!;

        model.Graphs.Activate(77);

        long offset = (long)line.Series.X.Span[77];
        TreeRow row = model.SelectedNode!;
        Assert.Equal(NodeKind.Frame, row.Kind);
        Assert.Equal(offset, row.Node.Offset);
        Assert.True(row.Parent!.IsExpanded);
        Assert.Equal(row, model.Tree![model.Tree.IndexOf(row)]);
        Assert.Equal(offset, model.Highlight!.Start);
        Assert.Equal(77, model.Graphs.MarkedIndex);

        // A bar selects a frame of its bitrate; the per-second graph the first frame of its second.
        model.Graphs.Selected = model.Graphs.Choices.Single(c => c.Title == "Bitrate");
        model.Graphs.SelectForm(GraphForm.Summary);
        var bars = (DistributionGraph)model.Graphs.Graph!;
        Assert.Equal(bars.IndexOfFrame(offset), model.Graphs.MarkedIndex);
        model.Graphs.Activate(0);
        Assert.Equal(bars.Bars[0].Low, line.ValueAt(line.IndexOfFrame(model.SelectedNode!.Node.Offset)!.Value));
        model.Graphs.Selected = model.Graphs.Choices.Single(c => c.Title == "Bitrate");
        model.Graphs.SelectForm(GraphForm.OverTime);
        model.Graphs.Activate(1);
        Assert.Equal(1, model.Graphs.MarkedIndex);
        Assert.Equal(NodeKind.Frame, model.SelectedNode!.Kind);
    }

    [Fact]
    public void Clicking_a_point_of_a_speex_stream_selects_its_packet()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Ogg("external-speexenc-wb-vbr.spx"));
        ui.Until(() => load.IsCompleted);
        var line = (LineGraph)model.Graphs.Graph!;
        Assert.Equal("Bitrate", line.Series.Name);

        model.Graphs.Activate(30);

        TreeRow row = model.SelectedNode!;
        Assert.Equal(NodeKind.Frame, row.Kind);
        Assert.Equal("Packet", row.Node.Name);
        Assert.Equal((long)line.Series.X.Span[30], row.Node.Offset);
        Assert.Equal(30, model.Graphs.MarkedIndex);
    }

    [Fact]
    public void A_file_of_two_streams_offers_a_choice_and_a_click_selects_a_frame_of_the_chosen_one()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Stats("sample-rate-change-statistics.mp3"));
        ui.Until(() => load.IsCompleted);
        GraphsViewModel graphs = model.Graphs;
        Assert.True(graphs.HasStreamChoice);
        Assert.Equal(2, graphs.Streams.Count);
        Assert.Equal("Stream 2: 48 kHz · 0:00.5", graphs.Streams[1].Text);

        graphs.Selected = graphs.Choices[1];
        graphs.SelectedStream = graphs.Streams[1];
        Assert.Equal(graphs.Choices[1].Title, graphs.Selected!.Title);
        var line = (LineGraph)graphs.Choices[0].Graph;
        Assert.Same(model.Report!.Streams[1].Series[0], line.Series);

        graphs.Selected = graphs.Choices[0];
        graphs.Activate(line.Count - 1);
        Assert.Equal(NodeKind.Frame, model.SelectedNode!.Kind);
        Assert.Equal((long)line.Series.X.Span[^1], model.SelectedNode.Node.Offset);
    }

    [Fact]
    public void The_frame_at_an_offset_is_found_without_building_other_frames()
    {
        var reads = new List<int>();
        var builds = new List<string>();
        var root = new Node("File", 0, 100_000 * 100L);
        var stream = new Node("Stream", 0, 100_000 * 100L, null, () => new CountingList(reads, Frames(builds)))
        {
            Kind = NodeKind.Stream,
        };
        root.Add(stream);

        List<int>? path = FrameLocator.PathFrom(root, 4_567_800);

        Assert.Equal([0, 45_678 + 2], path);
        Assert.InRange(reads.Count, 1, 40);
        Assert.Empty(builds);

        var tree = new StructureTree(root);
        TreeRow row = tree.Reveal(path!)!;
        Assert.Equal(4_567_800, row.Node.Offset);
        Assert.Empty(builds);

        // The leading details are skipped, and an offset past the frames leads nowhere, without
        // reading them all.
        Assert.Equal([0, 2], FrameLocator.PathFrom(root, 0));
        Assert.Equal([0, 100_001], FrameLocator.PathFrom(root, 9_999_998));
        reads.Clear();
        Assert.Null(FrameLocator.PathFrom(root, 9_999_999));
        Assert.InRange(reads.Count, 1, 40);
    }

    [Fact]
    public void Frames_out_of_file_order_are_still_found()
    {
        var root = new Node("File", 0, 6000);
        var stream = new Node("Stream", 0, 6000) { Kind = NodeKind.Stream };
        root.Add(stream);
        for (int i = 0; i < 60; i++)
        {
            int at = i switch { 30 => 31, 31 => 30, _ => i };
            stream.Add(new Node("Frame", at * 100L, 100) { Kind = NodeKind.Frame });
        }

        foreach (long offset in new long[] { 0, 2950, 3000, 3050, 3100, 3199, 5999 })
        {
            List<int>? path = FrameLocator.PathFrom(root, offset);
            Assert.NotNull(path);
            Node frame = stream.Children[path[^1]];
            Assert.InRange(offset, frame.Offset, frame.End - 1);
        }
    }

    [Fact]
    public void A_trailing_child_that_starts_before_the_frames_does_not_hide_them()
    {
        foreach (long statisticsOffset in new long[] { 0, 1000 })
        {
            var root = new Node("File", 0, 6000);
            var stream = new Node("Stream", 0, 6000) { Kind = NodeKind.Stream };
            root.Add(stream);
            stream.Add(new Node("Header", 0, 1000));
            for (int i = 0; i < 40; i++)
            {
                stream.Add(new Node("Frame", 1000 + (i * 100L), 100) { Kind = NodeKind.Frame });
            }

            stream.Add(new Node("Statistics", statisticsOffset, 5000 - statisticsOffset) { Kind = NodeKind.Statistics });

            Assert.Equal([0, 1 + 25], FrameLocator.PathFrom(root, 3505));
            Assert.Equal([0, 1], FrameLocator.PathFrom(root, 1000));
            Assert.Null(FrameLocator.PathFrom(root, 500));
        }
    }

    // A stream whose node names say nothing a graph could read; its series alone make the graphs.
    private static StreamInfo Stream(int frames, out double[] bitrates, out double[] offsets)
    {
        var random = new Random(3);
        double[] rates = [32, 64, 96, 128, 160, 192, 256, 320];
        bitrates = new double[frames];
        offsets = new double[frames];
        double[] fills = new double[frames];
        long offset = 0;
        for (int i = 0; i < frames; i++)
        {
            bitrates[i] = i < 3 ? 128 + (i * 16) : rates[random.Next(rates.Length)];
            offsets[i] = offset;
            fills[i] = random.Next(512);
            offset += 209;
        }

        double duration = frames * 1152 / 44100.0;
        int seconds = (int)Math.Ceiling(duration);
        double[] starts = Enumerable.Range(0, seconds).Select(s => (double)s).ToArray();
        double[] perSecond = Enumerable.Range(0, seconds).Select(s => 128.0 + (s % 3 * 6)).ToArray();
        return new StreamInfo
        {
            Nodes = [new Node("Zz", 0, offset)],
            Codec = "Test",
            Duration = duration,
            Series =
            [
                new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates),
                new Series("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, perSecond),
                new Series("Reservoir Fill", "bytes", SeriesAxis.FrameOffset, offsets, fills),
            ],
        };
    }

    // Two leading details (one covering the frames, as a stream's statistics do), then 100,000
    // frames of 100 bytes, whose own children record their builds.
    private static List<Node> Frames(List<string> builds)
    {
        var nodes = new List<Node>
        {
            new("Encoder", 156, 36),
            new("Statistics", 0, 100_000 * 100L) { Kind = NodeKind.Statistics },
        };
        for (int i = 0; i < 100_000; i++)
        {
            int frame = i;
            nodes.Add(new Node("Frame", i * 100L, i == 99_999 ? 99 : 100, null, () =>
            {
                builds.Add($"Frame {frame}");
                return [new Node("Header", frame * 100L, 4)];
            })
            {
                Kind = NodeKind.Frame,
            });
        }

        return nodes;
    }

    private sealed class CountingList(List<int> reads, List<Node> nodes) : IReadOnlyList<Node>
    {
        public int Count => nodes.Count;

        public Node this[int index]
        {
            get
            {
                reads.Add(index);
                return nodes[index];
            }
        }

        public IEnumerator<Node> GetEnumerator()
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
