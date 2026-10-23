using System.Globalization;
using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// The coding series of an AC-3 or E-AC-3 stream (docs/design/coding-graphs.md): the block switching, coupling, rematrixing and spectral
// extension of every audio block read, and the coded bandwidth of every program frame (an AC-3 syncframe, or an E-AC-3 independent frame with
// the dependent frames that follow it). Each unit's values are held equal to what its frames' own nodes state, which are built on a second
// pass from the kept bits and which the series do not use, so a value in the wrong place of a unit fails here and not only in a total. The
// hand-built coding fixtures carry the expected values in their manifests, computed in Python from the options each frame was built with
// (make_ac3_fixtures.py), and the liba52 trace in crosscheck_ac3_fixtures.py holds the AC-3 series to an independent decoder.
public class Ac3CodingSeriesTests
{
    private static readonly string[][] Channels =
    [
        ["channel 1", "channel 2"], ["center"], ["left", "right"], ["left", "center", "right"], ["left", "right", "surround"],
        ["left", "center", "right", "surround"], ["left", "right", "left surround", "right surround"], ["left", "center", "right", "left surround", "right surround"],
    ];

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (string name in DescriptionTests.FixtureNames().Where(n => n.StartsWith("ac3/", StringComparison.Ordinal) || n.Contains("ac3", StringComparison.Ordinal)))
        {
            data.Add(name);
        }

        return data;
    }

    public static TheoryData<string> CodingFixtures()
    {
        var data = new TheoryData<string>();
        foreach (string name in DescriptionTests.FixtureNames().Where(n => n.StartsWith("ac3/coding-", StringComparison.Ordinal)))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_values_of_every_unit_equal_what_its_frames_nodes_state(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        foreach (StreamInfo stream in report.Streams)
        {
            Series? bitrate = stream.Series.FirstOrDefault(s => s.Name == "Bitrate");
            if (bitrate is null)
            {
                Assert.Empty(stream.CategorySeries);
                continue;
            }

            // A series exists where its tally node does.
            Node statistics = StatisticsOf(report, stream);
            Node? read = Child(statistics, "Audio Blocks Read");
            string[] expected = [.. new[] { "Blocks With Block Switching", "Blocks With Coupling", "Blocks With Rematrixing", "Blocks With Spectral Extension" }.Where(n => Child(statistics, n) is not null)];
            Assert.Equal(expected, stream.CategorySeries.Select(s => s.Name));
            if (read is not null && CountOf(read) == 0)
            {
                Assert.True(stream.CategorySeries.All(s => s.Total == 0));
            }

            Unit[] units = Units(report, stream, bitrate);
            Assert.Equal(bitrate.X.Length, units.Length);
            Series? bandwidth = stream.Series.FirstOrDefault(s => s.Name == "Coded Bandwidth");
            int point = 0;
            for (int index = 0; index < units.Length; index++)
            {
                List<Block> blocks = [.. units[index].Frames.SelectMany(f => Blocks(f))];
                bool hasPoint = bandwidth is not null && point < bandwidth.X.Length && bandwidth.X.Span[point] == units[index].Offset;
                Assert.Equal(blocks.Count > 0, hasPoint);
                foreach (CategorySeries series in stream.CategorySeries)
                {
                    Func<Block, bool> flag = series.Name switch
                    {
                        "Blocks With Block Switching" => b => b.Switched,
                        "Blocks With Coupling" => b => b.Coupled,
                        "Blocks With Rematrixing" => b => b.Rematrixed,
                        _ => b => b.SpectralExtension,
                    };
                    List<int> codes = [.. blocks.Select(b => flag(b) ? 0 : 1)];
                    int layout = series.ValuesPerUnit;
                    if (layout > 0)
                    {
                        Assert.True(codes.Count <= layout, $"{name}, unit {index}: {codes.Count} blocks in a layout of {layout}");
                        codes.AddRange(Enumerable.Repeat((int)CategorySeries.None, layout - codes.Count));
                    }

                    List<int> actual = [.. Enumerable.Range(0, series.ValueCount(index)).Select(v => (int)series[index, v])];
                    Assert.True(codes.SequenceEqual(actual), $"{name}, unit {index}: {series.Name} {string.Join(",", actual)}, the nodes give {string.Join(",", codes)}");
                }

                if (hasPoint)
                {
                    int rate = stream.SampleRate;
                    double top = Math.Round(blocks.Max(b => b.End) * (double)rate / 512, MidpointRounding.ToEven);
                    Assert.True(top == bandwidth!.Values.Span[point], $"{name}, unit {index}: {bandwidth.Values.Span[point]} Hz, the nodes give {top} Hz");
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
                Assert.True(bandwidth.X.Span.SequenceEqual(bitrate.X.Span) || bandwidth.X.Length < bitrate.X.Length);
            }

            // Each series equals its tally: the first category counts the node's value and the total is the blocks read.
            foreach (CategorySeries series in stream.CategorySeries)
            {
                Assert.Equal(series.Name, series.Anchor!.Name);
                Assert.Equal(CountOf(series.Anchor), series.Counts[0]);
                Assert.Equal(CountOf(read!), series.Total);
                Assert.Equal("Bitrate", series.XSeries!.Name);
                Assert.NotNull(series.Time);
                Assert.Equal(stream.Codec == "AC-3" ? 6 : 0, series.ValuesPerUnit);
            }
        }
    }

    [Theory]
    [MemberData(nameof(CodingFixtures))]
    public void The_hand_built_fixtures_hold_the_values_their_model_computes(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        JsonElement coding = Fixtures.LoadMpegManifest(name).GetProperty("coding");
        Assert.Equal(coding.GetArrayLength(), report.Streams.Count);
        for (int s = 0; s < report.Streams.Count; s++)
        {
            StreamInfo stream = report.Streams[s];
            JsonElement[] units = [.. coding[s].EnumerateArray()];
            Assert.Equal(units.Length, stream.CategorySeries[0].Count);
            Series bandwidth = stream.Series.Single(x => x.Name == "Coded Bandwidth");
            Assert.Equal(units.Length, bandwidth.Values.Length);
            for (int u = 0; u < units.Length; u++)
            {
                foreach ((string key, string series) in new[] { ("switching", "Blocks With Block Switching"), ("coupling", "Blocks With Coupling"),
                    ("rematrixing", "Blocks With Rematrixing"), ("spectral", "Blocks With Spectral Extension") })
                {
                    CategorySeries? found = stream.CategorySeries.FirstOrDefault(c => c.Name == series);
                    if (found is null || !units[u].TryGetProperty(key, out JsonElement expected))
                    {
                        // A series the stream's tallies do not have is one the manifest's blocks never use.
                        Assert.True(found is not null || !units[u].TryGetProperty(key, out JsonElement flags) || key is "coupling" or "rematrixing" or "spectral" && flags.EnumerateArray().All(f => !f.GetBoolean()),
                            $"{name}, unit {u}: the manifest has {key} values and the stream no series");
                        continue;
                    }

                    int[] want = [.. expected.EnumerateArray().Select(f => f.GetBoolean() ? 0 : 1)];
                    int[] got = [.. Enumerable.Range(0, found.ValueCount(u)).Select(v => (int)found[u, v]).Where(c => c != CategorySeries.None)];
                    Assert.True(want.SequenceEqual(got), $"{name}, unit {u}: {series} {string.Join(",", got)}, the model gives {string.Join(",", want)}");
                }

                Assert.Equal(units[u].GetProperty("bandwidth").GetInt32(), bandwidth.Values.Span[u]);
            }
        }
    }

    [Fact]
    public void An_ac3_stream_has_its_series_under_the_shared_concepts_and_the_names_of_its_tally_nodes()
    {
        Report report = Fixtures.AnalyzeMpeg("ac3/external-ffmpeg-stereo-192k.ac3");
        StreamInfo stream = Assert.Single(report.Streams);

        Assert.Equal(["Blocks With Block Switching", "Blocks With Coupling", "Blocks With Rematrixing"], stream.CategorySeries.Select(s => s.Name));
        Assert.Equal([GraphConcept.BlockSwitching, GraphConcept.StereoCoding, GraphConcept.StereoCoding], stream.CategorySeries.Select(s => s.Concept));
        Assert.Equal(["With block switching", "Without block switching"], stream.CategorySeries[0].Categories.Select(c => c.Name));
        Assert.Equal(["With coupling", "Without coupling"], stream.CategorySeries[1].Categories.Select(c => c.Name));
        Assert.Equal(["With rematrixing", "Without rematrixing"], stream.CategorySeries[2].Categories.Select(c => c.Name));
        Assert.All(stream.CategorySeries, s => Assert.Equal(6, s.ValuesPerUnit));
        Assert.All(stream.CategorySeries, s => Assert.Equal(96, s.Total));
        Assert.Equal([18L, 78L], stream.CategorySeries[2].Counts);

        Series bitrate = stream.Series.Single(s => s.Name == "Bitrate");
        Series perSecond = stream.Series.Single(s => s.Name == "Bitrate Over Time");
        Assert.Equal([GraphConcept.BitDistribution, GraphConcept.BitDistribution], [bitrate.Concept, perSecond.Concept]);
        Assert.Equal("Bitrate", bitrate.Quantity);
        Assert.Equal("Bitrate", perSecond.Quantity);
        Series bandwidth = stream.Series.Single(s => s.Name == "Coded Bandwidth");
        Series overTime = stream.Series.Single(s => s.Name == "Coded Bandwidth Over Time");
        Assert.Equal(["Hz", "Hz"], [bandwidth.Unit, overTime.Unit]);
        Assert.Equal([GraphConcept.CodedBandwidth, GraphConcept.CodedBandwidth], [bandwidth.Concept, overTime.Concept]);
        Assert.Equal("Coded Bandwidth", overTime.Anchor!.Name);
        Assert.Equal(["Lowest", "Most Common", "Highest"], overTime.Anchor.Children.Select(c => c.Name));
    }

    [Fact]
    public void A_mono_stream_has_block_switching_only_and_a_1_plus_1_stream_has_no_coupling_or_rematrixing()
    {
        StreamInfo mono = Assert.Single(Fixtures.AnalyzeMpeg("ac3/external-ffmpeg-mono-96k.ac3").Streams);
        Assert.Equal(["Blocks With Block Switching"], mono.CategorySeries.Select(s => s.Name));

        StreamInfo dual = Assert.Single(Fixtures.AnalyzeMpeg("ac3/coding-ac3-dual-mono.ac3").Streams);
        Assert.Equal(["Blocks With Block Switching"], dual.CategorySeries.Select(s => s.Name));
        Assert.Equal([3L, 9L], dual.CategorySeries[0].Counts);
    }

    [Fact]
    public void An_eac3_stream_has_spectral_extension_and_a_variable_layout()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg("ac3/coding-eac3-blocks.eac3").Streams);

        Assert.Equal(["Blocks With Block Switching", "Blocks With Coupling", "Blocks With Rematrixing", "Blocks With Spectral Extension"], stream.CategorySeries.Select(s => s.Name));
        Assert.Equal(GraphConcept.CodedBandwidth, stream.CategorySeries[3].Concept);
        Assert.All(stream.CategorySeries, s => Assert.Equal(0, s.ValuesPerUnit));
        Assert.Equal([6, 6, 3, 2, 1, 6], Enumerable.Range(0, 6).Select(stream.CategorySeries[0].ValueCount));
    }

    // A program frame of an independent frame and a dependent mono frame, in a 2/0 program: the mono blocks have no rematrixing and count as
    // blocks without it, so the series equals its tally, which counts only 2/0 blocks.
    [Fact]
    public void The_blocks_of_a_dependent_frame_join_the_program_frame_and_a_block_that_is_not_2_0_has_no_rematrixing()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg("ac3/coding-eac3-dependent.eac3").Streams);
        CategorySeries rematrixing = stream.CategorySeries.Single(s => s.Name == "Blocks With Rematrixing");

        Assert.Equal([12, 6, 1, 12], Enumerable.Range(0, 4).Select(rematrixing.ValueCount));
        Assert.Equal(31, rematrixing.Total);
        Assert.Equal([1, 1, 1], Enumerable.Range(0, 3).Select(v => (int)rematrixing[1, 3 + v]));
    }

    // A frame that stops before its end reports the blocks read to their end only, in the tally and in the series alike.
    [Fact]
    public void A_block_that_breaks_is_in_neither_the_tally_nor_the_series()
    {
        Report report = Fixtures.AnalyzeMpeg("ac3/bad-blocks.ac3");
        StreamInfo stream = Assert.Single(report.Streams);
        Node statistics = StatisticsOf(report, stream);
        Node read = Child(statistics, "Audio Blocks Read")!;

        Assert.True(CountOf(read) < CountOf(Child(statistics, "Audio Blocks")!));
        Assert.All(stream.CategorySeries, s => Assert.Equal(CountOf(read), s.Total));
        Assert.Equal(6, stream.CategorySeries[0].ValueCount(0));
        Assert.Contains(Enumerable.Range(0, stream.CategorySeries[0].Count).SelectMany(u => Enumerable.Range(0, 6).Select(v => (int)stream.CategorySeries[0][u, v])), c => c == CategorySeries.None);
    }

    private sealed record Block(bool Switched, bool Coupled, bool Rematrixed, bool SpectralExtension, int End);

    private sealed record Unit(long Offset, List<Node> Frames);

    // The program frames of the stream, from the frames' order: an independent frame at one of the series' offsets starts a unit, the dependent
    // frames after it join it once for each substream ID, and the one that repeats an ID ends it (the rest are left out, Annex E 2.3.1.2).
    private static Unit[] Units(Report report, StreamInfo stream, Series bitrate)
    {
        var frames = new List<Node>();
        foreach (Node node in report.Streams.SelectMany(s => s.Nodes).Distinct())
        {
            Collect(node, frames, 0);
        }

        var starts = new Dictionary<long, Unit>();
        foreach (double x in bitrate.X.Span)
        {
            starts[(long)x] = new Unit((long)x, []);
        }

        Unit? current = null;
        int seen = 0;
        foreach (Node frame in frames.OrderBy(f => f.Offset).DistinctBy(f => f.Offset))
        {
            bool dependent = Text(frame, "Bit Stream Information", "Stream Type")?.StartsWith('1') == true;
            if (!dependent)
            {
                current = starts.GetValueOrDefault(frame.Offset);
                seen = 0;
                current?.Frames.Add(frame);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            int id = Number(Text(frame, "Bit Stream Information", "Substream ID"));
            if ((seen & (1 << id)) != 0)
            {
                current = null;
                continue;
            }

            seen |= 1 << id;
            current.Frames.Add(frame);
        }

        return [.. starts.Values.OrderBy(u => u.Offset)];
    }

    private static void Collect(Node node, List<Node> frames, int depth)
    {
        if (node.Kind == NodeKind.Frame)
        {
            if (node.Name == "Frame")
            {
                frames.Add(node);
            }

            return;
        }

        if (depth < 12 && node.HasChildren)
        {
            foreach (Node child in node.Children)
            {
                Collect(child, frames, depth + 1);
            }
        }
    }

    // The audio blocks of a frame read to their end, with what each chooses, from the nodes alone (ATSC A/52:2018 5.4.3; Annex E 2.3.3): the
    // coupling, spectral extension, bandwidth and rematrixing a block states stay in force until the next that does, within the frame.
    private static List<Block> Blocks(Node frame)
    {
        var result = new List<Block>();
        Node info = frame.Children.Single(c => c.Name == "Bit Stream Information");
        int acmod = Number(Text(info, "Audio Coding Mode"));
        bool eac3 = Number(Text(info, "Bit Stream ID")) > 10;
        string[] labels = Channels[acmod];
        Node[] blocks = [.. frame.Children.Where(c => c.Name == "Audio Block")];

        // E-AC-3 states the coupling strategy of every block in the audio frame.
        bool[] frameCoupling = eac3 ? CouplingByBlock(frame, blocks.Length) : [];

        bool coupling = false;
        bool spectral = false;
        int beginCode = 0, endCode = 0, spxBegin = 0;
        var coupled = new bool[labels.Length];
        var inSpectral = new bool[labels.Length];
        var widths = new int[labels.Length];
        bool flags = false;
        for (int index = 0; index < blocks.Length; index++)
        {
            Node block = blocks[index];
            Node[] fields = [.. block.Children];
            if (eac3)
            {
                coupling = frameCoupling[index];
            }
            else if (fields.FirstOrDefault(c => c.Name == "HasCouplingStrategy") is { } strategy && strategy.Value!.AsBoolean() == true)
            {
                coupling = fields.First(c => c.Name == "IsCouplingInUse").Value!.AsBoolean() == true;
            }

            if (coupling)
            {
                if (fields.Any(c => c.Name.StartsWith("IsInCoupling", StringComparison.Ordinal)))
                {
                    for (int ch = 0; ch < labels.Length; ch++)
                    {
                        coupled[ch] = fields.First(c => c.Name == $"IsInCoupling ({labels[ch]})").Value!.AsBoolean() == true;
                    }
                }
                else if (eac3 && acmod == 2)
                {
                    Array.Fill(coupled, true);
                }

                if (fields.FirstOrDefault(c => c.Name == "Coupling Begin Code") is { } begin)
                {
                    beginCode = Number(begin.Value!.ToString());
                }

                if (fields.FirstOrDefault(c => c.Name == "Coupling End Code") is { } end)
                {
                    endCode = Number(end.Value!.ToString());
                }
            }
            else
            {
                Array.Clear(coupled);
            }

            if (eac3)
            {
                if (fields.FirstOrDefault(c => c.Name == "IsSpectralExtensionInUse") is { } use)
                {
                    spectral = use.Value!.AsBoolean() == true;
                    if (spectral)
                    {
                        for (int ch = 0; ch < labels.Length; ch++)
                        {
                            inSpectral[ch] = acmod == 1 || fields.First(c => c.Name == $"IsInSpectralExtension ({labels[ch]})").Value!.AsBoolean() == true;
                        }

                        int code = Number(fields.First(c => c.Name == "Spectral Extension Begin Code").Value!.ToString());
                        spxBegin = code < 6 ? code + 2 : (code * 2) - 3;
                    }
                    else
                    {
                        Array.Clear(inSpectral);
                    }
                }
            }

            for (int ch = 0; ch < labels.Length; ch++)
            {
                if (fields.FirstOrDefault(c => c.Name == $"Bandwidth Code ({labels[ch]})") is { } code)
                {
                    widths[ch] = Number(code.Value!.ToString());
                }
            }

            if (acmod == 2 && fields.FirstOrDefault(c => c.Name == "Rematrixing Flags") is { } rematrixing)
            {
                flags = rematrixing.Value!.ToString()!.Contains("sum/difference", StringComparison.Ordinal);
            }

            // A block is read to its end when its mantissas lie in the frame: the node of a block whose mantissas run past the frame ends
            // before the bits its value states.
            Node? mantissas = block.Children.FirstOrDefault(c => c.Name == "Mantissas");
            if (mantissas is null || (mantissas.BitLength ?? ((mantissas.Length * 8L) - (mantissas.BitOffset ?? 0))) < mantissas.Value!.AsInteger())
            {
                continue;
            }

            int top = 0;
            for (int ch = 0; ch < labels.Length; ch++)
            {
                top = Math.Max(top, coupled[ch] ? 37 + (12 * beginCode) : eac3 && inSpectral[ch] ? 25 + (12 * spxBegin) : 37 + (3 * (widths[ch] + 12)));
            }

            if (coupling)
            {
                top = Math.Max(top, 37 + (12 * (endCode + 3)));
            }

            result.Add(new Block(fields.Any(c => c.Name.StartsWith("IsBlockSwitched", StringComparison.Ordinal) && c.Value!.AsBoolean() == true),
                coupling, acmod == 2 && flags, eac3 && spectral, top));
        }

        return result;
    }

    // E-AC-3: cplinu of each block, from the audio frame's fields: block 0's cplinu, then for each further block cplstre and, where it is set,
    // cplinu; a block without a strategy keeps the last (Annex E 2.2.3).
    private static bool[] CouplingByBlock(Node frame, int blocks)
    {
        Node audio = frame.Children.Single(c => c.Name == "Audio Frame");
        Node[] fields = [.. audio.Children.Where(c => c.Name is "IsCouplingInUse" or "HasCouplingStrategy")];
        var use = new bool[blocks];
        if (fields.Length == 0 || blocks == 0)
        {
            return use;
        }

        use[0] = fields[0].Value!.AsBoolean() == true;
        int at = 1;
        for (int block = 1; block < blocks; block++)
        {
            bool states = fields[at++].Value!.AsBoolean() == true;
            use[block] = states ? fields[at++].Value!.AsBoolean() == true : use[block - 1];
        }

        return use;
    }

    // The statistics node of a stream: the one that holds the node its bitrate series is anchored to.
    private static Node StatisticsOf(Report report, StreamInfo stream)
    {
        Node anchor = stream.Series.Single(s => s.Name == "Bitrate Over Time").Anchor!;
        Node? Search(Node node, int depth) => node.Kind == NodeKind.Statistics && node.Children.Any(c => ReferenceEquals(c, anchor)) ? node
            : depth < 12 && node.HasChildren ? node.Children.Where(c => c.Kind != NodeKind.Frame).Select(c => Search(c, depth + 1)).FirstOrDefault(n => n is not null) : null;
        return report.Streams.SelectMany(s => s.Nodes).Distinct().Select(n => Search(n, 0)).First(n => n is not null)!;
    }

    private static Node? Child(Node statistics, string name) => statistics.Children.FirstOrDefault(c => c.Name == name);

    private static long CountOf(Node node) => node.Value?.AsInteger() ?? CategorySummary.Count(node.Value) ?? -1;

    private static string? Text(Node node, params string[] path)
    {
        Node? at = node;
        foreach (string name in path)
        {
            at = at?.Children.FirstOrDefault(c => c.Name == name);
        }

        return at?.Value?.ToString();
    }

    private static int Number(string? text) => int.Parse(text!.Split(' ', ':')[0], CultureInfo.InvariantCulture);
}
