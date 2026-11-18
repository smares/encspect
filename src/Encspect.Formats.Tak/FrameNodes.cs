using Encspect.Core;

namespace Encspect.Formats.Tak;

// The nodes of the frames, built from the records when something reads them.
internal static class FrameNodes
{
    public static Node Entry(Records r, int index) => r.Frames.Entries[index].IsSkipped ? Skipped(r, index) : Frame(r, index);

    public static Node Skipped(Records r, int index)
    {
        Entry e = r.Frames.Entries[index];
        return new Node("Skipped Data", e.Offset, e.Length, NodeValue.Integer(e.Length, "bytes", "byte")) { Description = Explain.SkippedData, Topic = Topics.Frames };
    }

    // The frame at an index of the records: its header, its coded data and the CRC-24 over the data.
    public static Node Frame(Records r, int index)
    {
        Entry e = r.Frames.Entries[index];
        long samples = SamplesOf(r, e);
        return new Node("Frame", e.Offset, e.Length, samples > 0 ? NodeValue.Integer(samples, "samples", "sample") : NodeValue.Integer(e.Length, "bytes", "byte"), () => Children(r, index))
        {
            Kind = NodeKind.Frame,
            Description = Explain.Frame,
            Topic = Topics.Frames,
        };
    }

    // The samples of a frame: a last frame states them, any other has the frame length of the stream info; 0 where neither is known.
    private static long SamplesOf(Records r, Entry e)
    {
        if ((e.Flags & EntryFlags.IsLast) != 0)
        {
            return e.Samples;
        }

        return r.Stream is { } info && Layout.IsFrameSizeValid(info.FrameSizeType, info.SampleRate) ? Layout.FrameSamples(info.FrameSizeType, info.SampleRate) : 0;
    }

    public static Node HeaderCrcField(Records r, int index) => Frame(r, index).Children.First(c => c.Name == "Header").Children.First(c => c.Name == "CRC-24");

    public static Node DataCrcField(Records r, int index) => Frame(r, index).Children.First(c => c.Name == "Data CRC-24");

    private static IReadOnlyList<Node> Children(Records r, int index)
    {
        Entry e = r.Frames.Entries[index];
        long at = e.Offset;
        int headerLength = e.HeaderLength;
        var children = new List<Node> { Header(r, e) };
        bool isCut = e.IsCut;
        long dataLength = e.Length - headerLength - (isCut ? 0 : Layout.CrcLength);
        if (dataLength > 0)
        {
            children.Add(Nodes.Bytes("Frame Data", at + headerLength, dataLength, NodeValue.Integer(dataLength, "bytes", "byte"), isCut ? Explain.CutFrameData : Explain.FrameData, Topics.Frames));
        }

        if (!isCut)
        {
            long crcAt = at + e.Length - Layout.CrcLength;
            children.Add(Nodes.Bytes("Data CRC-24", crcAt, Layout.CrcLength, NodeValue.Hex(e.DataStoredCrc, 6), Explain.DataCrc, Topics.Crc));
            children.Add(Nodes.Bytes("Computed Data CRC-24", crcAt, Layout.CrcLength, NodeValue.Hex(e.DataComputedCrc, 6), Explain.DataComputedCrc, Topics.Crc));
            children.Add(Nodes.Bytes("IsDataCrc24Valid", crcAt, Layout.CrcLength, NodeValue.Boolean(e.IsDataCrcValid), Explain.DataIsCrcValid, Topics.Crc));
        }

        return children;
    }

    private static Node Header(Records r, Entry e)
    {
        long at = e.Offset;
        int length = e.HeaderLength;
        bool isLast = (e.Flags & EntryFlags.IsLast) != 0;
        bool isInfo = (e.Flags & EntryFlags.IsInfo) != 0;
        Node header = Nodes.Bytes("Header", at, length, NodeValue.Integer(length, "bytes", "byte"), Explain.FrameHeader, Topics.Frames);
        header.Add(Nodes.Bytes("Sync Word", at, 2, NodeValue.Hex(0xA0FF, 4), Explain.SyncWord, Topics.Frames));
        header.Add(Nodes.Bits("IsLastFrame", at, 16, 17, NodeValue.Boolean(isLast), Explain.IsLastFrame, Topics.Frames));
        header.Add(Nodes.Bits("IsInfoFrame", at, 17, 18, NodeValue.Boolean(isInfo), Explain.IsInfoFrame, Topics.Frames));
        header.Add(Nodes.Bits("HasMetadata", at, 18, 19, NodeValue.Boolean(false), Explain.HasMetadata, Topics.Frames));
        header.Add(Nodes.Bits("Frame Number", at, 19, 40, NodeValue.Integer(e.Number), Explain.FrameNumber, Topics.Frames));
        if (isLast)
        {
            header.Add(Nodes.Bits("Samples", at, 40, 54, NodeValue.Integer(e.Samples, "samples", "sample"), Explain.LastFrameSamples, Topics.Frames));
            header.Add(Nodes.Bits("Reserved", at, 54, 56, NodeValue.Integer(0), Explain.FrameReserved, Topics.Frames));
        }

        if (isInfo && e.InfoSlot > 0 && FrameHeader.Read(r.Frames.InfoHeaders[e.InfoSlot - 1]) is { Info: { } info } parsed)
        {
            long infoAt = at + parsed.InfoOffset;
            Node stream = header.Add(Nodes.Bytes("Stream Info", infoAt, (info.BitLength + 7) / 8, NodeValue.Integer((info.BitLength + 7) / 8, "bytes", "byte"), Explain.FrameStreamInfo, Topics.Frames));
            Nodes.AddStreamInfo(stream, infoAt, info);
            int bit = (parsed.InfoOffset * 8) + info.BitLength;
            header.Add(Nodes.Bits("HasPreviousDistance", at, bit, bit + 1, NodeValue.Boolean(parsed.HasDistance), Explain.HasPreviousDistance, Topics.Frames));
            header.Add(Nodes.Bits("Reserved", at, bit + 1, bit + 6, NodeValue.Integer(0), Explain.FrameReserved, Topics.Frames));
            if (parsed.HasDistance)
            {
                header.Add(Nodes.Bits("Previous Distance", at, bit + 6, bit + 31, NodeValue.Integer(parsed.Distance, "bytes", "byte"), Explain.PreviousDistance, Topics.Frames));
            }
        }

        long crcAt = at + length - Layout.CrcLength;
        header.Add(Nodes.Bytes("CRC-24", crcAt, Layout.CrcLength, NodeValue.Hex(e.HeaderStoredCrc, 6), Explain.HeaderCrc, Topics.Crc));
        header.Add(Nodes.Bytes("Computed CRC-24", crcAt, Layout.CrcLength, NodeValue.Hex(e.HeaderComputedCrc, 6), Explain.HeaderComputedCrc, Topics.Crc));
        header.Add(Nodes.Bytes("IsCrc24Valid", crcAt, Layout.CrcLength, NodeValue.Boolean(e.IsHeaderCrcValid), Explain.HeaderIsCrcValid, Topics.Crc));
        return header;
    }
}
