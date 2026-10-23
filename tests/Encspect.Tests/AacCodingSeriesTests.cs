using System.Globalization;
using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Host;

namespace Encspect.Tests;

// The category series and the numeric series of an AAC stream's coding (docs/design/coding-graphs.md): each unit's values are held equal
// to what its raw data blocks' own nodes state, which the block reader builds on a second pass and the series do not use, so a code in
// the wrong place of a unit fails here and not only in a total. The faad2 trace in crosscheck_mp4_fixtures.py holds the same per unit
// against an independent decoder.
public class AacCodingSeriesTests
{
    public static TheoryData<string> Names() => Fixtures.Mp4Fixtures();

    [Theory]
    [MemberData(nameof(Names))]
    public void The_values_of_every_unit_equal_what_its_nodes_state(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        foreach (StreamInfo stream in report.Streams.Where(s => s.CategorySeries.Any(c => c.Name == "Window Sequences")))
        {
            CategorySeries windows = Series(stream, "Window Sequences")!;
            Unit[] units = Units(stream, windows);
            CategorySeries? pairs = Series(stream, "Stereo Coding");
            Series? bandwidth = stream.Series.FirstOrDefault(s => s.Name == "Coded Bandwidth");
            Assert.Equal(units.Length, windows.Count);
            Assert.Equal(units.Length, pairs?.Count ?? units.Length);
            int core = CoreRate(report, units);
            bool is960 = FrameLength(report) == 960;
            int point = 0;
            for (int unit = 0; unit < units.Length; unit++)
            {
                Node[] blocks = units[unit].Blocks;
                bool hasPoint = bandwidth is not null && point < bandwidth.X.Length && bandwidth.X.Span[point] == units[unit].Offset;

                // A block that does not reach its END has nodes up to where it broke, which count a channel stream the tally leaves out.
                if (blocks.Any(b => b.Children.All(c => c.Name != "END")))
                {
                    point += hasPoint ? 1 : 0;
                    continue;
                }

                var expectedWindows = new List<int>();
                var expectedPairs = new List<int>();
                int highest = -1;
                foreach (Node element in blocks.SelectMany(b => b.Children).Where(c => c.Name is "SCE" or "LFE" or "CPE"))
                {
                    Node? shared = element.Children.FirstOrDefault(c => c.Name == "ICS Info");
                    foreach (Node channel in element.Children.Where(c => c.Name.StartsWith("Channel", StringComparison.Ordinal)))
                    {
                        Node info = channel.Children.FirstOrDefault(c => c.Name == "ICS Info") ?? shared!;
                        int sequence = LeadingNumber(Value(info, "Window Sequence"));
                        expectedWindows.Add(sequence);
                        highest = Math.Max(highest, Hertz(core, is960, sequence == 2, LeadingNumber(Value(info, "Max SFB"))));
                    }

                    if (element.Name == "CPE")
                    {
                        expectedPairs.Add(PairCode(element, shared));
                    }
                }

                Assert.True(expectedWindows.SequenceEqual(Values(windows, unit)), $"{name}, unit {unit}: window sequences {string.Join(",", Values(windows, unit))}, the nodes give {string.Join(",", expectedWindows)}");
                List<int> actualPairs = pairs is null ? [] : Values(pairs, unit);
                Assert.True(expectedPairs.SequenceEqual(actualPairs), $"{name}, unit {unit}: stereo coding {string.Join(",", actualPairs)}, the nodes give {string.Join(",", expectedPairs)}");
                Assert.Equal(highest >= 0, hasPoint);
                if (hasPoint)
                {
                    Assert.True(highest == bandwidth!.Values.Span[point], $"{name}, unit {unit}: {bandwidth.Values.Span[point]} Hz, the nodes give {highest} Hz");
                    point++;
                }
            }

            Assert.Equal(point, bandwidth?.Values.Length ?? 0);
            if (bandwidth is not null)
            {
                // The node's lowest, most common (the lowest of equally common) and highest come from another accumulator than the series.
                double[] values = bandwidth.Values.ToArray();
                Node node = stream.Series.Single(s => s.Name == "Coded Bandwidth Over Time").Anchor!;
                double common = values.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
                Assert.Equal([$"{values.Min():0} Hz", $"{common:0} Hz", $"{values.Max():0} Hz"], node.Children.Select(c => c.Value!.ToString()!));
            }
        }
    }

    // The core's rate, which SBR may double for the output: the sample rate of an ADTS frame's header, or of the AudioSpecificConfig.
    private static int CoreRate(Report report, Unit[] units)
    {
        Node? holder = units.Length > 0 && units[0].Frame is { } frame ? frame
            : Find(report.Root, n => n.Name == "AudioSpecificConfig") ?? Find(report.Root, n => n.Name == "Program Config Element" && n.Children.Any(c => c.Name == "Sample Rate"));
        return (int)holder!.Children.First(c => c.Name == "Sample Rate").Value!.AsInteger()!.Value;
    }

    private static Node? Find(Node node, Func<Node, bool> test, int depth = 0)
    {
        if (test(node))
        {
            return node;
        }

        return depth < 24 && node.HasChildren ? node.Children.Where(c => c.Kind != NodeKind.Frame).Select(c => Find(c, test, depth + 1)).FirstOrDefault(n => n is not null) : null;
    }

    // The top of the max_sfb-th band of a window in Hz: the lines are spaced by half the sample rate over the lines of the window.
    private static int Hertz(int rate, bool is960, bool isShort, int maxSfb)
    {
        int index = StreamSetup.SampleRateIndex(rate);
        ushort[] table = isShort ? (is960 ? BandTables.Short120 : BandTables.Short)[index] : (is960 ? BandTables.Long960 : BandTables.Long)[index];
        int lines = isShort ? (is960 ? 120 : 128) : (is960 ? 960 : 1024);
        return (int)Math.Round(table[maxSfb] * (double)rate / (2 * lines), MidpointRounding.ToEven);
    }

    // The frame length the AudioSpecificConfig states, 1,024 where there is none (ADTS).
    private static int FrameLength(Report report) =>
        Find(report.Root, n => n.Name == "AudioSpecificConfig")?.Children.FirstOrDefault(c => c.Name == "Frame Length")?.Value?.AsInteger() is { } length ? (int)length : 1024;

    // The code of a channel pair from its nodes alone (ISO/IEC 14496-3 4.6.8): intensity stereo where the second channel names intensity
    // stereo bands, mid/side stereo where a band that is no intensity stereo and no noise substitution band has its ms_used flag set
    // (ms_mask_present 2 sets every band of the pair), and the reserved ms_mask_present 3 before either.
    private static int PairCode(Node pair, Node? shared)
    {
        bool common = Value(pair, "HasCommonWindow") == "true";
        int mask = common ? LeadingNumber(Value(pair, "Mid/Side Mask Present")) : 0;
        if (mask == 3)
        {
            return 4;
        }

        Node second = pair.Children.Single(c => c.Name == "Channel 1");
        Node info = second.Children.FirstOrDefault(c => c.Name == "ICS Info") ?? shared!;
        int maxSfb = LeadingNumber(Value(info, "Max SFB"));
        int groups = Value(info, "Scale Factor Grouping") is { } text && text.Contains(" groups", StringComparison.Ordinal) ? int.Parse(text.Split('(')[1].Split(' ')[0], CultureInfo.InvariantCulture) : 1;
        HashSet<(int, int)> intensity = Bands(Value(second, "Intensity Stereo Bands") ?? "none");

        // Where either channel codes noise substitution, ms_used signals something else and no decoder applies mid/side (FFmpeg's
        // apply_mid_side_stereo requires both band types below noise substitution).
        HashSet<(int, int)> noise = [.. Bands(Value(second, "Noise Substitution Bands") ?? "none"), .. Bands(Value(pair.Children.Single(c => c.Name == "Channel 0"), "Noise Substitution Bands") ?? "none")];
        HashSet<(int, int)> bands = [.. Enumerable.Range(0, groups).SelectMany(g => Enumerable.Range(0, maxSfb).Select(b => (g, b)))];
        HashSet<(int, int)> flagged = mask switch
        {
            1 => Bands(Value(pair, "Mid/Side Bands") ?? "none"),
            2 => bands,
            _ => [],
        };
        bool midSide = flagged.Any(b => !intensity.Contains(b) && !noise.Contains(b));
        return intensity.Count > 0 ? (midSide ? 3 : 2) : (midSide ? 1 : 0);
    }

    // "none", "0-3, 7" or "group 0: 0-3; group 1: 5", each optionally followed by " in phase" or " out of phase" and a semicolon.
    private static HashSet<(int, int)> Bands(string text)
    {
        var set = new HashSet<(int, int)>();
        if (text == "none")
        {
            return set;
        }

        foreach (string part in text.Replace(" in phase", string.Empty, StringComparison.Ordinal).Replace(" out of phase", string.Empty, StringComparison.Ordinal).Split("; "))
        {
            int group = 0;
            string ranges = part;
            if (part.StartsWith("group ", StringComparison.Ordinal))
            {
                string[] pieces = part.Split(": ");
                group = int.Parse(pieces[0]["group ".Length..], CultureInfo.InvariantCulture);
                ranges = pieces[1];
            }

            foreach (string range in ranges.Split(", "))
            {
                string[] ends = range.Split('-');
                int first = int.Parse(ends[0], CultureInfo.InvariantCulture);
                int last = ends.Length > 1 ? int.Parse(ends[1], CultureInfo.InvariantCulture) : first;
                for (int band = first; band <= last; band++)
                {
                    set.Add((group, band));
                }
            }
        }

        return set;
    }

    [Fact]
    public void A_stereo_stream_has_its_series_under_the_shared_concepts_and_the_names_of_its_tally_nodes()
    {
        Report report = Fixtures.AnalyzeMpeg("aac/ffmpeg-adts.aac");
        StreamInfo stream = Assert.Single(report.Streams);

        Assert.Equal(["Window Sequences", "Block Sizes", "Stereo Coding"], stream.CategorySeries.Select(s => s.Name));
        Assert.Equal([GraphConcept.BlockSwitching, GraphConcept.BlockSwitching, GraphConcept.StereoCoding], stream.CategorySeries.Select(s => s.Concept));
        Assert.All(stream.CategorySeries, s => Assert.Equal(s.Name, s.Anchor!.Name));
        Assert.All(stream.CategorySeries, s => Assert.Equal("Bitrate", s.XSeries!.Name));
        Assert.All(stream.CategorySeries, s => Assert.NotNull(s.Time));
        Assert.All(stream.CategorySeries.Where(s => s.Source is null), s => Assert.Equal(0, s.ValuesPerUnit));
        Assert.Equal(["Only Long", "Long Start", "Eight Short", "Long Stop"], Series(stream, "Window Sequences")!.Categories.Select(c => c.Name));
        Assert.Equal(["Left/right stereo", "Mid/side stereo", "Intensity stereo", "Mid/side and intensity stereo", "Reserved"], Series(stream, "Stereo Coding")!.Categories.Select(c => c.Name));
        Assert.True(Series(stream, "Stereo Coding")!.Categories[4].IsReserved);
        CategorySeries sizes = Series(stream, "Block Sizes")!;
        Assert.Equal("Window Sequences", sizes.Source!.Name);
        Assert.Equal(["Long, start and stop", "Short"], sizes.Categories.Select(c => c.Name));
        Assert.Equal([130L, 2L], sizes.Counts);
        Assert.Equal(132, Series(stream, "Window Sequences")!.Total);
        Assert.Equal(66, Series(stream, "Stereo Coding")!.Total);

        Series bitrate = stream.Series.Single(s => s.Name == "Bitrate");
        Series perSecond = stream.Series.Single(s => s.Name == "Bitrate Over Time");
        Assert.Equal([GraphConcept.BitDistribution, GraphConcept.BitDistribution], [bitrate.Concept, perSecond.Concept]);
        Assert.Equal("Bitrate", bitrate.Quantity);
        Assert.Equal("Bitrate", perSecond.Quantity);

        Series bandwidth = stream.Series.Single(s => s.Name == "Coded Bandwidth");
        Series bandwidthOverTime = stream.Series.Single(s => s.Name == "Coded Bandwidth Over Time");
        Assert.Equal(["Hz", "Hz"], [bandwidth.Unit, bandwidthOverTime.Unit]);
        Assert.Equal([SeriesAxis.FrameOffset, SeriesAxis.Seconds], [bandwidth.Axis, bandwidthOverTime.Axis]);
        Assert.Equal([GraphConcept.CodedBandwidth, GraphConcept.CodedBandwidth], [bandwidth.Concept, bandwidthOverTime.Concept]);
        Assert.Equal("Coded Bandwidth", bandwidth.Quantity);
        Assert.Equal("Coded Bandwidth", bandwidthOverTime.Quantity);
        Assert.Equal("Coded Bandwidth", bandwidthOverTime.Anchor!.Name);
        Assert.Equal(bandwidth.Values.Span.ToArray().Max(), bandwidthOverTime.Values.Span.ToArray().Max());
        Node anchor = bandwidthOverTime.Anchor;
        Assert.Equal(["Lowest", "Most Common", "Highest"], anchor.Children.Select(c => c.Name));
        Assert.Equal($"{bandwidth.Values.Span.ToArray().Min():0} Hz", anchor.Children[0].Value!.ToString());
        Assert.Equal($"{bandwidth.Values.Span.ToArray().Max():0} Hz", anchor.Children[2].Value!.ToString());
        Assert.True(bandwidth.X.Span.SequenceEqual(bitrate.X.Span));
    }

    [Fact]
    public void A_mono_stream_has_no_stereo_coding_and_keeps_nothing_for_it()
    {
        Report report = Fixtures.AnalyzeMpeg("aac/ffmpeg-mono-faststart.m4a");
        StreamInfo stream = Assert.Single(report.Streams);

        Assert.Equal(["Window Sequences", "Block Sizes"], stream.CategorySeries.Select(s => s.Name));
        Assert.DoesNotContain(Fixtures.Resolve(stream.Nodes[0], "Statistics").Single().Children, c => c.Name == "Stereo Coding");
    }

    // The pairs of the hand-built fixtures, whose codes the model computes in Python from the specification's rule (aac_block_fixtures.py).
    [Fact]
    public void Channel_pairs_with_and_without_a_common_window_have_the_coding_their_bands_state()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg("adts/stereo-coding.aac").Streams);
        CategorySeries pairs = Series(stream, "Stereo Coding")!;

        // Left/right (mask 0, and mask 1 with no flag), mid/side (mask 1 with flags, mask 2), intensity stereo (flags only on intensity stereo
        // bands, mask 2 over a pair of intensity stereo bands only, and two pairs without a common window), both (mask 1 and mask 2), and
        // the pairs whose ms_used flags sit on noise substitution bands: of the second or the first channel only (left/right), beside an
        // ordinary flagged band (mid/side), beside intensity stereo bands (intensity stereo), and in every band under mask 2 (mid/side).
        Assert.Equal([0, 0, 1, 1, 2, 3, 2, 3, 2, 0, 2, 0, 0, 1, 2, 1], Enumerable.Range(0, pairs.Count).Select(unit => (int)pairs[unit, 0]));
        Assert.Equal([5L, 4L, 5L, 2L, 0L], pairs.Counts);
    }

    [Fact]
    public void The_reserved_ms_mask_present_takes_precedence_over_intensity_stereo()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg("adts/stereo-coding-reserved.aac").Streams);
        CategorySeries pairs = Series(stream, "Stereo Coding")!;

        Assert.Equal([4, 4, 0], Enumerable.Range(0, pairs.Count).Select(unit => (int)pairs[unit, 0]));
        Assert.Equal([1L, 0L, 0L, 0L, 2L], pairs.Counts);
    }

    // A unit of six channel streams holds two pairs, whose values come in the order of the elements.
    [Fact]
    public void A_unit_with_two_pairs_has_a_value_for_each_in_element_order()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg("adts/stereo-coding-elements.aac").Streams);
        CategorySeries pairs = Series(stream, "Stereo Coding")!;
        CategorySeries windows = Series(stream, "Window Sequences")!;

        Assert.Equal([[1, 2], [0, 1], [1, 0]], Enumerable.Range(0, 3).Select(unit => Values(pairs, unit).ToArray()));
        Assert.Equal([[0, 0, 0, 0, 0, 0], [0, 0, 0, 1, 1, 0], [0, 2, 2, 0, 0, 0]], Enumerable.Range(0, 3).Select(unit => Values(windows, unit).ToArray()));
    }

    // An ADTS frame of several raw data blocks gives one unit with the channel streams of every block, in order.
    [Fact]
    public void An_adts_frame_of_several_blocks_is_one_unit()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg("adts/raw-blocks.aac").Streams);
        CategorySeries windows = Series(stream, "Window Sequences")!;
        Node[] frames = [.. stream.Nodes.SelectMany(n => n.Children).Where(c => c.Name == "Frame")];

        Assert.Equal(frames.Length, windows.Count);
        Assert.Contains(frames, f => f.Children.Count(c => c.Name == "Raw Data Block") > 1);
        int[] streams = [.. frames.Select(f => f.Children.Where(c => c.Name == "Raw Data Block").SelectMany(b => b.Children).Sum(e => e.Name == "CPE" ? 2 : e.Name is "SCE" or "LFE" ? 1 : 0))];
        Assert.Equal(streams, Enumerable.Range(0, windows.Count).Select(windows.ValueCount));
    }

    // A unit codes at most 254 of its values and counts the rest, so a series equals its tally whatever a crafted block holds
    // (docs/design/coding-graphs.md, "Units and values"): 300 of the smallest single channel elements, 29 bits each.
    [Fact]
    public void A_unit_of_more_channel_streams_than_a_unit_codes_counts_the_rest_without_a_code()
    {
        var bits = new List<bool>();
        void Put(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        for (int element = 0; element < 300; element++)
        {
            Put(0, 3);
            Put(0, 4);
            Put(100, 8);
            Put(0, 1);
            Put(0, 2);
            Put(1, 1);
            Put(0, 6);
            Put(0, 4);
        }

        Put(7, 3);
        while (bits.Count % 8 != 0)
        {
            bits.Add(false);
        }

        byte[] block = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            block[i / 8] |= (byte)(bits[i] ? 0x80 >> (i % 8) : 0);
        }

        int length = 7 + block.Length;
        byte[] header = [0xFF, 0xF1, 0x50, (byte)(0x80 | (length >> 11)), (byte)(length >> 3), (byte)(((length & 7) << 5) | 0x1F), 0xFC];
        byte[] frame = [.. header, .. block];
        byte[] data = [.. frame, .. frame, .. frame, .. frame];

        Report report = Analyzer.CreateDefault().Analyze("many.aac", new MemoryStream(data));
        StreamInfo stream = Assert.Single(report.Streams);
        CategorySeries windows = Series(stream, "Window Sequences")!;

        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.Equal(4, windows.Count);
        Assert.All(Enumerable.Range(0, 4), unit => Assert.Equal(CategorySeries.MaxValuesPerUnit, windows.ValueCount(unit)));
        Assert.Equal(4 * (300 - CategorySeries.MaxValuesPerUnit), windows.UncodedValues);
        Assert.Equal(1200, windows.Total);
        Assert.Equal(1200, windows.Counts[0]);
        Assert.Empty(CategorySummary.Load().Check(report));
        Assert.Equal(4, stream.Series.Single(s => s.Name == "Coded Bandwidth").Values.Length);
    }

    // A block that cannot be read to its end is no point of the bitrate series, but its channel streams are in the tally: it is a unit of
    // the series, which then have the blocks' own offsets for x.
    [Fact]
    public void An_adif_stream_that_breaks_in_its_last_block_keeps_that_block_as_a_unit_of_the_series()
    {
        byte[] whole = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "adts", "adif.aac"));
        Report intact = Analyzer.CreateDefault().Analyze("adif.aac", new MemoryStream(whole));
        Assert.NotNull(Series(Assert.Single(intact.Streams), "Window Sequences")!.XSeries);

        Report cut = Analyzer.CreateDefault().Analyze("cut.aac", new MemoryStream(whole[..^6]));
        StreamInfo stream = Assert.Single(cut.Streams);
        CategorySeries windows = Series(stream, "Window Sequences")!;
        Series bitrate = stream.Series.Single(s => s.Name == "Bitrate");

        Assert.DoesNotContain(cut.Findings, f => f.Code == "parse.failed");
        Assert.Equal(bitrate.X.Length + 1, windows.Count);
        Assert.Null(windows.XSeries);
        Assert.Empty(CategorySummary.Load().Check(cut));
    }

    // A configuration with frameLengthFlag 1 has 960 lines in a long window and 120 in a short one, and its own band tables: the coded
    // bandwidth of a long window with max_sfb 30 and of eight short windows with max_sfb 10 at 44,100 Hz.
    [Fact]
    public void A_960_sample_frame_gives_its_coded_bandwidth_from_the_960_line_tables()
    {
        var bits = new List<bool>();
        void Put(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        // A single channel element of one long window, all 30 of its bands coded with the zero codebook (no scalefactors, no spectrum).
        Put(0, 3);
        Put(0, 4);
        Put(100, 8);
        Put(0, 1);
        Put(0, 2);
        Put(0, 1);
        Put(30, 6);
        Put(0, 1);
        Put(0, 4);
        Put(30, 5);
        Put(0, 3);

        // One of eight short windows, each its own group, max_sfb 10 (a section length of 10 is the escape value 7 and 3 in 3 bits).
        Put(0, 3);
        Put(1, 4);
        Put(100, 8);
        Put(0, 1);
        Put(2, 2);
        Put(0, 1);
        Put(10, 4);
        Put(0, 7);
        for (int group = 0; group < 8; group++)
        {
            Put(0, 4);
            Put(7, 3);
            Put(3, 3);
        }

        Put(0, 3);
        Put(7, 3);
        while (bits.Count % 8 != 0)
        {
            bits.Add(false);
        }

        byte[] unit = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            unit[i / 8] |= (byte)(bits[i] ? 0x80 >> (i % 8) : 0);
        }

        byte[] entry = Mp4Builder.AacEntry();
        int config = entry.AsSpan().IndexOf<byte>([0x05, 2, 0x12, 0x10]);
        entry[config + 3] = 0x14;
        const int count = 6;
        byte[] data = Mp4Builder.FileWithMoov(at => Mp4Builder.Trak(1, entry, Mp4Builder.Stts(count, 960), Mp4Builder.Stsc(count), Mp4Builder.ConstantSizes(unit.Length, count),
            Mp4Builder.ChunkOffsets([at])), [.. Enumerable.Repeat(unit, count).SelectMany(u => u)]);

        Report report = Analyzer.CreateDefault().Analyze("960.m4a", new MemoryStream(data));
        StreamInfo stream = Assert.Single(report.Streams);
        Series bandwidth = stream.Series.Single(s => s.Name == "Coded Bandwidth");

        Assert.Equal(960, FrameLength(report));
        Assert.Equal(count * 960L, stream.Samples);
        Assert.Equal([0, 2], Values(Series(stream, "Window Sequences")!, 0));
        int expected = (int)Math.Max(Math.Round(BandTables.Long960[4][30] * 44100.0 / (2 * 960), MidpointRounding.ToEven), Math.Round(BandTables.Short120[4][10] * 44100.0 / (2 * 120), MidpointRounding.ToEven));
        Assert.All(bandwidth.Values.ToArray(), v => Assert.Equal(expected, v));
        Assert.Equal(count, bandwidth.Values.Length);

        // With the 1,024 line tables the same bands end elsewhere, so the test tells the tables apart.
        Assert.NotEqual(expected, (int)Math.Max(Math.Round(BandTables.Long[4][30] * 44100.0 / 2048, MidpointRounding.ToEven), Math.Round(BandTables.Short[4][10] * 44100.0 / 256, MidpointRounding.ToEven)));
    }

    // A unit of the stream: where it starts and the raw data blocks it holds (an access unit is its own block).
    private sealed record Unit(long Offset, Node[] Blocks, Node? Frame = null);

    // The units of the stream, in order: the access units of an MP4 or Matroska track, the frames of an ADTS stream and the blocks of an
    // ADIF one.
    private static Unit[] Units(StreamInfo stream, CategorySeries series)
    {
        var units = new List<Unit>();
        foreach (Node node in stream.Nodes)
        {
            Collect(node, units, 0);
        }

        // A file of several streams has the frames of all of them under its regions; the series knows its own.
        HashSet<double> own = [.. series.X.ToArray()];
        return [.. units.Where(u => own.Contains(u.Offset))];
    }

    private static void Collect(Node node, List<Unit> units, int depth)
    {
        if (node.Name == "ADIF")
        {
            units.AddRange(node.Children.Where(c => c.Name == "Raw Data Block").Select(c => new Unit(c.Offset, [c])));
            return;
        }

        if (node.Kind == NodeKind.Frame)
        {
            if (node.Name == "Access Unit")
            {
                units.Add(new Unit(node.Offset, [node]));
            }
            else if (node.Name == "Frame")
            {
                units.Add(new Unit(node.Offset, [.. node.Children.Where(c => c.Name == "Raw Data Block")], node));
            }

            return;
        }

        if (depth < 6 && node.HasChildren)
        {
            foreach (Node child in node.Children)
            {
                Collect(child, units, depth + 1);
            }
        }
    }

    private static List<int> Values(CategorySeries series, int unit) =>
        [.. Enumerable.Range(0, series.ValueCount(unit)).Select(v => (int)series[unit, v])];

    private static string? Value(Node node, string name) => node.Children.FirstOrDefault(c => c.Name == name)?.Value?.ToString();

    private static int LeadingNumber(string? text) => int.Parse(text!.Split(' ')[0], CultureInfo.InvariantCulture);

    private static CategorySeries? Series(StreamInfo stream, string name) => stream.CategorySeries.FirstOrDefault(s => s.Name == name);
}
