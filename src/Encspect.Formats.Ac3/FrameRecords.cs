using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Ac3;

// The records frame nodes are built from once the analysis is over: the frames' records and the
// store their bits are kept in, and nothing else. Nodes are built from it again after a collection
// and on several threads, so it holds no reader, decoder or cursor that a build could consume; the
// analysis that wrote it keeps those. recordMismatch is called where a frame's nodes read another
// number of bits than the analysis kept (frame offset, kept, read): a check for tests.
internal sealed class FrameRecords(ChunkedList<Ac3Analysis.Frame> frames, ByteStore store, Action<long, int, int>? recordMismatch)
{
    public int Count => frames.Count;

    // A frame's node, with its fields built from the record when first read.
    public Node FrameNode(int index)
    {
        Ac3Analysis.Frame frame = frames[index];
        if (frame.IsGap)
        {
            return new Node("Gap", frame.Offset, frame.Store, NodeValue.Integer(frame.Store, "bytes")) { Description = Explain.Gap };
        }

        (SyncFrame header, int length, int prefix) = Prefix(store, frame);
        return new Node("Frame", frame.Offset, length, header.Summary(header.FrameLength), () => FrameChildren(frame, header, length, prefix))
        {
            Kind = NodeKind.Frame,
            Description = header.IsEac3 ? Explain.Eac3Frame : Explain.Ac3Frame,
            Topic = Topics.FramesAndBsi,
        };
    }

    // The header and length a record's prefix holds, or the kept bits give, and the prefix's size.
    private static (SyncFrame Header, int Length, int Prefix) Prefix(ByteStore store, Ac3Analysis.Frame frame)
    {
        int at = 0;
        int length = 0;
        if (frame.IsShortened)
        {
            length = BinaryPrimitives.ReadUInt16BigEndian(store.Span(frame.Store, 2));
            at = 2;
        }

        var header = new SyncFrame(BinaryPrimitives.ReadUInt64BigEndian(store.Span(frame.Store, at + SyncFrame.Length)[at..]));
        if (!frame.IsHeaderKept)
        {
            at += SyncFrame.Length;
        }

        return (header, frame.IsShortened ? length : header.FrameLength, at);
    }

    private List<Node> FrameChildren(Ac3Analysis.Frame frame, SyncFrame header, int length, int prefix)
    {
        byte[] record = store.Span(frame.Store + (uint)prefix, (frame.KeptBits + 7) >> 3).ToArray();
        var syntax = new FrameSyntax(FrameBits.ForNodes()) { RecordMismatch = recordMismatch };
        return syntax.Nodes(record, frame.KeptBits, frame.Offset, length, header, Verdict(frame.Crc1, frame.IsShortened), Verdict(frame.Crc2, frame.IsShortened));
    }

    private static string Verdict(Ac3Analysis.CrcState state, bool truncated) => state switch
    {
        Ac3Analysis.CrcState.Verified => "verified",
        Ac3Analysis.CrcState.Mismatch => "mismatch",
        _ => truncated ? "not checked: the frame is cut short" : "not checked",
    };
}
