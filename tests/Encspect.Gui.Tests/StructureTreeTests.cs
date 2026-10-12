using System.Collections.Specialized;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Host;

namespace Encspect.Gui.Tests;

public class StructureTreeTests
{
    [Fact]
    public void Children_are_not_built_until_their_row_is_expanded()
    {
        var builds = new List<string>();
        Node root = Synthetic(builds, frames: 100_000);
        var tree = new StructureTree(root);

        Assert.Equal(3, tree.Count);
        _ = tree.ToList();
        Assert.Empty(builds);

        TreeRow stream = tree[1];
        Assert.True(stream.HasChildren);
        tree.Expand(stream);
        Assert.Equal(["Stream"], builds);
        Assert.Equal(100_003, tree.Count);

        // Reading rows, as a list does for the ones on screen, builds none of the frames' children.
        for (int i = 0; i < 50; i++)
        {
            _ = tree[50_000 + i].Name;
        }

        TreeRow frame = tree[50_000];
        Assert.Equal("Frame", frame.Name);
        Assert.True(frame.HasChildren);
        Assert.Equal(["Stream"], builds);

        tree.Expand(frame);
        Assert.Equal(["Stream", "Frame 49998"], builds);
        Assert.Equal(100_005, tree.Count);
        Assert.Equal("Header", tree[50_001].Name);
        Assert.Equal("Side Info", tree[50_002].Name);
        Assert.Equal("Frame", tree[50_003].Name);
        Assert.Equal(50_000, tree.IndexOf(frame));
    }

    [Fact]
    public void An_mpeg_stream_shows_its_frames_without_building_them_all()
    {
        using var temp = new TempFolder();
        byte[] fixture = File.ReadAllBytes(Fixtures.Mpeg("external-blade-flags.mp3"));
        using (FileStream output = File.Create(temp["long.mp3"]))
        {
            for (int i = 0; i < 40; i++)
            {
                output.Write(fixture);
            }
        }

        Report report = Analyzer.CreateDefault().AnalyzeFile(temp["long.mp3"]);
        var tree = new StructureTree(report.Root);
        TreeRow stream = Enumerable.Range(0, tree.Count).Select(i => tree[i]).Single(r => r.Kind == NodeKind.Stream);
        int before = tree.Count;

        tree.Expand(stream);

        int frames = stream.Node.Children.Count(n => n.Kind == NodeKind.Frame);
        Assert.True(frames > 3000, $"{frames} frames");
        Assert.Equal(before + stream.Node.Children.Count, tree.Count);

        // Only the rows read were made.
        TreeRow last = tree[tree.Count - 1];
        Assert.Equal(NodeKind.Frame, last.Kind);
        Assert.Equal(tree.Count - 1, tree.IndexOf(last));
        Assert.Equal(1, stream.Rows!.Count(r => r is not null));
    }

    [Fact]
    public void Indexes_match_an_eager_flattening_through_any_expanding_and_collapsing()
    {
        var random = new Random(1234);
        Node root = Random(random, depth: 0);
        var tree = new StructureTree(root);
        for (int step = 0; step < 400; step++)
        {
            TreeRow row = tree[random.Next(tree.Count)];
            if (row.HasChildren)
            {
                tree.Toggle(row);
            }

            List<TreeRow> expected = Flatten(tree.Root).ToList();
            Assert.Equal(expected.Count, tree.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Same(expected[i], tree[i]);
                Assert.Equal(i, tree.IndexOf(expected[i]));
            }
        }
    }

    [Fact]
    public void Expanding_and_collapsing_announce_the_rows_that_come_and_go()
    {
        Node root = Synthetic([], frames: 10);
        var tree = new StructureTree(root);
        // The ranges resolve their rows when read, so they are read as the change is announced.
        var events = new List<(NotifyCollectionChangedAction Action, int Index, TreeRow[] Rows)>();
        tree.CollectionChanged += (_, e) => events.Add(e.Action == NotifyCollectionChangedAction.Add
            ? (e.Action, e.NewStartingIndex, e.NewItems!.Cast<TreeRow>().ToArray())
            : (e.Action, e.OldStartingIndex, e.OldItems!.Cast<TreeRow>().ToArray()));
        TreeRow stream = tree[1];

        tree.Expand(stream);
        tree.Expand(tree[3]);
        tree.Collapse(stream);

        Assert.Equal(NotifyCollectionChangedAction.Add, events[0].Action);
        Assert.Equal(2, events[0].Index);
        Assert.Equal(10, events[0].Rows.Length);
        Assert.Same(stream.RowAt(0), events[0].Rows[0]);
        Assert.Equal(4, events[1].Index);
        Assert.Equal(["Header", "Side Info"], events[1].Rows.Select(r => r.Name));
        Assert.Equal(NotifyCollectionChangedAction.Remove, events[2].Action);
        Assert.Equal(2, events[2].Index);
        Assert.Equal(12, events[2].Rows.Length);
        Assert.Equal(["Frame", "Frame", "Header", "Side Info", "Frame"], events[2].Rows.Take(5).Select(r => r.Name));
        Assert.Equal(3, tree.Count);
    }

    [Fact]
    public void A_path_reveals_its_node_with_every_row_on_the_way_expanded()
    {
        Node root = Synthetic([], frames: 5000);
        var tree = new StructureTree(root);

        TreeRow? row = tree.Reveal([0, 4321, 1]);

        Assert.NotNull(row);
        Assert.Equal("Side Info", row.Name);
        Assert.Same(root.Children[0].Children[4321].Children[1], row.Node);
        Assert.True(row.Parent!.IsExpanded);
        Assert.True(row.Parent.Parent!.IsExpanded);
        Assert.Same(row, tree[tree.IndexOf(row)]);
        Assert.Null(tree.Reveal([0, 5000]));
        Assert.Null(tree.Reveal([1, 0]));
    }

    // A file of one stream of frames, each with a header and side information; children built on
    // demand record their builds.
    internal static Node Synthetic(List<string> builds, int frames)
    {
        var root = new Node("File", 0, frames * 100L);
        var stream = new Node("Stream", 0, frames * 100L, null, () =>
        {
            builds.Add("Stream");
            return Enumerable.Range(0, frames).Select(i => new Node("Frame", i * 100L, 100, null, () =>
            {
                builds.Add($"Frame {i}");
                return [Node.FromBits("Header", i * 100L, 0, 32), new Node("Side Info", (i * 100L) + 4, 17)];
            })
            { Kind = NodeKind.Frame }).ToList();
        })
        { Kind = NodeKind.Stream };
        root.Add(stream);
        root.Add(new Node("Tag", frames * 100L, 0) { Kind = NodeKind.Tag });
        return root;
    }

    private static Node Random(Random random, int depth)
    {
        var node = new Node("N", 0, 0);
        int count = depth >= 4 ? 0 : random.Next(0, 5);
        for (int i = 0; i < count; i++)
        {
            node.Add(Random(random, depth + 1));
        }

        return node;
    }

    private static IEnumerable<TreeRow> Flatten(TreeRow row)
    {
        yield return row;
        if (!row.IsExpanded)
        {
            yield break;
        }

        for (int i = 0; i < row.Node.Children.Count; i++)
        {
            foreach (TreeRow below in Flatten(row.RowAt(i)))
            {
                yield return below;
            }
        }
    }
}
