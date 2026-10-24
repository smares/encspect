using Encspect.Core;

namespace Encspect.Formats.Tta;

// The nodes of the report built from the records when something reads them: the header, the seek table and a frame with its CRC-32. Functions
// only of the records, so the findings can build the node they point to.
internal static class Nodes
{
    // isConfiguration: the header is the one a container stores for the track, not the start of a file.
    public static Node Header(FieldMap map, int length, TtaHeader h, bool isConfiguration = false)
    {
        Node header = map.Bytes("Header", 0, length, null, isConfiguration ? Explain.ConfigurationHeader : Explain.Header, isConfiguration ? Topics.InMatroska : Topics.Stream);
        header.Add(map.Bytes("Signature", 0, 4, "TTA1", Explain.Signature, Topics.Stream));
        header.Add(map.Bytes("Audio Format", 4, 2, NodeValue.Integer(h.AudioFormat), h.AudioFormat switch
        {
            TtaHeader.FormatSimple => Explain.AudioFormatSimple,
            TtaHeader.FormatEncrypted => Explain.AudioFormatEncrypted,
            _ => Explain.AudioFormatOther,
        }, Topics.Stream));
        header.Add(map.Bytes("Channels", 6, 2, NodeValue.Integer(h.Channels, "channels", "channel"), Explain.Channels, Topics.Stream));
        header.Add(map.Bytes("Bits Per Sample", 8, 2, NodeValue.Integer(h.BitsPerSample, "bits", "bit"), Explain.BitsPerSample, Topics.Stream));
        header.Add(map.Bytes("Sample Rate", 10, 4, NodeValue.Integer(h.SampleRate, "Hz"), Explain.SampleRate, Topics.Stream));
        header.Add(map.Bytes("Samples", 14, 4, NodeValue.Integer(h.Samples, "samples", "sample"), Explain.HeaderSamples, Topics.Stream));
        header.Add(map.Bytes("CRC-32", 18, 4, NodeValue.Hex(h.StoredCrc, 8), Explain.HeaderCrc, Topics.Crc));
        header.Add(map.Bytes("Computed CRC-32", 18, 4, NodeValue.Hex(h.ComputedCrc, 8), Explain.HeaderComputedCrc, Topics.Crc));
        header.Add(map.Bytes("IsCrc32Valid", 18, 4, NodeValue.Boolean(h.IsCrcValid), Explain.HeaderIsCrcValid, Topics.Crc));
        return header;
    }

    // The seek table as far as the data holds it: its entries (one per frame the table lists) and its CRC-32 where the data reaches it.
    public static Node SeekTable(Records r)
    {
        int count = r.Entries.Count;
        long length = (4L * count) + (r.TableStoredCrc is null ? 0 : 4);
        return new Node("Seek Table", r.TableOffset, length, NodeValue.Integer(count, "entries", "entry"), () => SeekTableChildren(r))
        {
            Description = Explain.SeekTable,
            Topic = Topics.SeekTable,
        };
    }

    private static IReadOnlyList<Node> SeekTableChildren(Records r)
    {
        int count = r.Entries.Count;
        long end = r.TableOffset + (4L * count);
        var children = new List<Node>
        {
            new("Seek Points", r.TableOffset, 4L * count, NodeValue.Integer(count, "entries", "entry"), () => new BlockedChildren([], count, i => SeekPoint(r, i)))
            {
                Description = Explain.SeekPoints,
                Topic = Topics.SeekTable,
            },
        };
        if (r.TableStoredCrc is { } stored)
        {
            children.Add(new Node("CRC-32", end, 4, NodeValue.Hex(stored, 8)) { Description = Explain.TableCrc, Topic = Topics.Crc });
            children.Add(new Node("Computed CRC-32", end, 4, NodeValue.Hex(r.TableComputedCrc, 8)) { Description = Explain.TableComputedCrc, Topic = Topics.Crc });
            children.Add(new Node("IsCrc32Valid", end, 4, NodeValue.Boolean(stored == r.TableComputedCrc)) { Description = Explain.TableIsCrcValid, Topic = Topics.Crc });
        }

        return children;
    }

    public static Node SeekPoint(Records r, int index) =>
        new("Seek Point", r.TableOffset + (4L * index), 4, NodeValue.Integer(r.Entries[index].Size, "bytes", "byte")) { Description = Explain.SeekPoint, Topic = Topics.SeekTable };

    // The node of the frame at an index; entries whose frame lies past the end of the data have none.
    public static Node Frame(Records r, int index)
    {
        Entry e = r.Entries[index];
        long samples = r.SamplesOf(index);
        long length = Math.Min(e.Size, Math.Max(0, r.DataEnd - e.Offset));
        if (r.Ranges.TryGetValue(index, out IReadOnlyList<ByteRange>? ranges))
        {
            length = ranges[^1].End - e.Offset;
        }

        return new Node("Frame", e.Offset, length, samples >= 0 ? NodeValue.Integer(samples, "samples", "sample") : NodeValue.Integer(e.Size, "bytes", "byte"), () => FrameChildren(r, index))
        {
            Kind = NodeKind.Frame,
            Description = Explain.Frame,
            Topic = Topics.Stream,
            Ranges = ranges,
        };
    }

    public static Node CrcField(Records r, int index) => Frame(r, index).Children.First(c => c.Name == "CRC-32");

    private static IReadOnlyList<Node> FrameChildren(Records r, int index)
    {
        Entry e = r.Entries[index];
        bool hasRanges = r.Ranges.TryGetValue(index, out IReadOnlyList<ByteRange>? ranges);
        int size = hasRanges ? (int)ranges!.Sum(x => x.Length) : (int)Math.Min(e.Size, Math.Max(0, r.DataEnd - e.Offset));
        FieldMap map = hasRanges ? FieldMap.Of(ranges!, size) : FieldMap.At(e.Offset);
        if ((e.Status & EntryStatus.TooSmall) != 0)
        {
            return size == 0 ? [] : [map.Bytes("Frame Data", 0, size, NodeValue.Integer(size, "bytes", "byte"), Explain.SmallFrameData, Topics.Coding)];
        }

        if ((e.Status & EntryStatus.Cut) != 0)
        {
            return size == 0 ? [] : [map.Bytes("Frame Data", 0, size, NodeValue.Integer(size, "bytes", "byte"), Explain.CutFrameData, Topics.Coding)];
        }

        var children = new List<Node>(4);
        if (size > 4)
        {
            children.Add(map.Bytes("Frame Data", 0, size - 4, NodeValue.Integer(size - 4, "bytes", "byte"), Explain.FrameData, Topics.Coding));
        }

        children.Add(map.Bytes("CRC-32", size - 4, 4, NodeValue.Hex(e.StoredCrc, 8), Explain.FrameCrc, Topics.Crc));
        children.Add(map.Bytes("Computed CRC-32", size - 4, 4, NodeValue.Hex(e.ComputedCrc, 8), Explain.FrameComputedCrc, Topics.Crc));
        children.Add(map.Bytes("IsCrc32Valid", size - 4, 4, NodeValue.Boolean(e.IsCrcValid), Explain.FrameIsCrcValid, Topics.Crc));
        return children;
    }
}
