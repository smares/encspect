using Encspect.Core;

namespace Encspect.Formats.Dts;

// The records unit nodes are built from once the analysis is over: the units' records and the store
// their bytes are kept in, and nothing else. A node factory that captured the analysis instead would
// keep its context, and with it the input, alive as long as the report.
internal sealed class DtsRecords(ChunkedList<DtsAnalysis.Frame> frames, ByteStore store)
{
    public int Count => frames.Count;

    // A unit's node, with its fields built from the record when first read.
    public Node FrameNode(int index)
    {
        DtsAnalysis.Frame frame = frames[index];
        if (frame.IsGap)
        {
            return new Node("Gap", frame.Offset, frame.Store, NodeValue.Integer(frame.Store, "bytes")) { Description = Explain.Gap };
        }

        UnitRecord.Open open = UnitRecord.Read(store, frame);
        return new Node("Frame", frame.Offset, frame.Length, open.Summary(), () => FrameChildren(open))
        {
            Kind = NodeKind.Frame,
            Description = Explain.Frame,
            Topic = Topics.CoreFrames,
        };
    }

    private static List<Node> FrameChildren(UnitRecord.Open open)
    {
        var syntax = new UnitSyntax(open.View, open.Layout, open.NewVerdicts());
        syntax.Run();
        return syntax.Nodes;
    }
}
