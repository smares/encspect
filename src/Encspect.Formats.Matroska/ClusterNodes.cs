using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Matroska;

// The nodes of a Cluster's children, built from the records when they are read (a report outlives the stream it was read from): a
// SimpleBlock or a Block with its header fields, lacing header and frames, and the elements around them.
internal static class ClusterNodes
{
    public static Node Cluster(Records records, int index)
    {
        ClusterRecord cluster = records.Clusters[index];
        int first = cluster.FirstItem;
        int count = cluster.ItemCount;
        ElementDefinition definition = Schema.Find(Schema.Cluster)!;
        return new Node("Cluster", cluster.Offset, cluster.HeaderLength + cluster.DataLength, NodeValue.Integer(cluster.Blocks, "blocks", "block"),
            () => new BlockedChildren([], count, i => ItemNode(records, first + i)))
        {
            Description = definition.Description,
            Topic = Topics.Container,
        };
    }

    private static Node ItemNode(Records records, int index)
    {
        Item item = records.Items[index];
        long length = item.HeaderLength + item.DataLength;
        switch (item.Kind)
        {
            case ItemKind.SimpleBlock:
                return BlockNode(records, item.Index, "SimpleBlock");
            case ItemKind.BlockGroup:
                return GroupNode(records, item);
            case ItemKind.Unparsed:
                return new Node("Unparsed Data", item.Offset, item.DataLength, NodeValue.Integer(item.DataLength, "bytes", "byte")) { Description = Explain.UnparsedData };
            case ItemKind.Crc:
                {
                    var crc = new Node("CRC-32", item.Offset, length, NodeValue.Hex(item.Value, 8)) { Description = Schema.Find(Schema.Crc32)!.Description, Topic = Topics.Crc };
                    if (records.ComputedCrcs.TryGetValue(index, out uint computed))
                    {
                        crc.Add(new Node("Computed", item.Offset + item.HeaderLength, item.DataLength, NodeValue.Hex(computed, 8)) { Description = Explain.Computed, Topic = Topics.Crc });
                    }

                    return crc;
                }

            default:
                return ElementNode(item.Id, item.Offset, item.HeaderLength, item.DataLength, item.Value);
        }
    }

    // A leaf the Cluster or a BlockGroup holds besides the blocks.
    private static Node ElementNode(uint id, long offset, int headerLength, long dataLength, long value)
    {
        ElementDefinition? definition = Schema.Find(id);
        long length = headerLength + dataLength;
        if (definition is null)
        {
            return new Node("Unknown Element", offset, length, $"ID 0x{id:X}, {(dataLength == 1 ? "1 byte" : $"{dataLength} bytes")}") { Description = Explain.UnknownElement };
        }

        NodeValue? shown = definition.Type switch
        {
            ElementType.Unsigned => Values.Of(definition, (ulong)value, 0, 0, null, null, dataLength),
            ElementType.Signed => Values.Of(definition, 0, value, 0, null, null, dataLength),
            ElementType.Float => Values.Of(definition, 0, 0, BitConverter.Int64BitsToDouble(value), null, null, dataLength),
            ElementType.Master => null,
            _ => Values.Of(definition, 0, 0, 0, null, null, dataLength),
        };
        return new Node(definition.Name, offset, length, shown)
        {
            Description = definition.Description,
            Topic = definition.Id == Schema.Timestamp ? Topics.Timestamps : null,
        };
    }

    private static Node GroupNode(Records records, Item item)
    {
        ElementDefinition definition = Schema.Find(Schema.BlockGroup)!;
        int first = item.Index;
        int count = item.Count;
        return new Node("BlockGroup", item.Offset, item.HeaderLength + item.DataLength, null, () => GroupChildren(records, first, count))
        {
            Description = definition.Description,
            Topic = Topics.Container,
        };
    }

    private static List<Node> GroupChildren(Records records, int first, int count)
    {
        var top = new List<Node>();
        var stack = new List<Node>();
        for (int i = first; i < first + count; i++)
        {
            Child child = records.Children[i];
            Node node = child.Block >= 0 ? BlockNode(records, child.Block, "Block") : ElementNode(child.Id, child.Offset, child.HeaderLength, child.DataLength, child.Value);
            if (child.Depth == 0 || stack.Count == 0)
            {
                top.Add(node);
                stack.Clear();
            }
            else
            {
                while (stack.Count > child.Depth)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                if (stack.Count == 0)
                {
                    top.Add(node);
                }
                else
                {
                    stack[^1].Add(node);
                }
            }

            stack.Add(node);
        }

        return top;
    }

    // A SimpleBlock or Block with its header, its lacing header and its frames (RFC 9559 sections 10.1 to 10.3).
    private static Node BlockNode(Records records, int index, string name)
    {
        BlockRecord block = records.Blocks[index];
        ElementDefinition definition = Schema.Find(name == "Block" ? Schema.Block : Schema.SimpleBlock)!;
        NodeValue? value = (block.Status & (BlockStatus.Short | BlockStatus.TooLarge)) != 0 ? NodeValue.Integer(0, "frames", "frame") : NodeValue.Integer(block.Frames, "frames", "frame");
        return new Node(name, block.Offset, block.End - block.Offset, value, () => BlockChildren(records, index, name == "Block"))
        {
            Description = definition.Description,
            Topic = Topics.Lacing,
        };
    }

    private static List<Node> BlockChildren(Records records, int index, bool isBlock)
    {
        BlockRecord block = records.Blocks[index];
        var children = new List<Node>();
        if ((block.Status & BlockStatus.TooLarge) != 0)
        {
            return children;
        }

        long start = block.Offset + block.HeaderLength;
        long end = block.End;
        int track = block.TrackLength;
        if (track > 0 && start + track <= end)
        {
            children.Add(new Node("Track Number", start, track, NodeValue.Integer(block.Track)) { Description = Explain.TrackNumber });
        }

        if ((block.Status & BlockStatus.Short) != 0)
        {
            if (start + track < end)
            {
                children.Add(new Node("Unparsed Data", start + track, end - start - track, NodeValue.Integer(end - start - track, "bytes", "byte")) { Description = Explain.BlockData });
            }

            return children;
        }

        children.Add(new Node("Timestamp", start + track, 2, NodeValue.Integer(block.Timestamp, "ticks")) { Description = Explain.BlockTimestamp, Topic = Topics.Timestamps });
        children.Add(FlagsNode(block, start + track + 2, isBlock));

        long framesAt = block.FramesOffset;
        long laceStart = start + track + 3;
        if (block.Lacing != Lacing.None)
        {
            long laceLength = framesAt - laceStart;
            string lacing = $"{block.Lacing switch { Lacing.Xiph => "Xiph", Lacing.Ebml => "EBML", _ => "fixed-size" }} lacing, {block.Frames} {(block.Frames == 1 ? "frame" : "frames")}";
            var lace = new Node("Lacing", laceStart, laceLength, (block.Status & BlockStatus.LaceInvalid) != 0 ? "invalid lacing header" : lacing)
            {
                Description = Explain.LaceHeader,
                Topic = Topics.Lacing,
            };
            if (laceLength > 0)
            {
                lace.Add(new Node("Frame Count", laceStart, 1, NodeValue.Integer(block.Frames), null) { Description = Explain.LaceFrameCount });
                if (laceLength > 1 && block.Frames > 1)
                {
                    lace.Add(new Node("Frame Sizes", laceStart + 1, laceLength - 1, SizeList(records, block)) { Description = Explain.LaceSizes });
                }
            }

            children.Add(lace);
        }

        if ((block.Status & BlockStatus.LaceInvalid) != 0)
        {
            if (framesAt < end)
            {
                children.Add(new Node("Unparsed Data", framesAt, end - framesAt, NodeValue.Integer(end - framesAt, "bytes", "byte")) { Description = Explain.BlockData });
            }

            return children;
        }

        bool audio = records.AudioTracks.Contains(block.Track);
        long position = framesAt;
        for (int i = 0; i < block.Frames; i++)
        {
            long size = block.Frames > 1 ? records.LaceSizes[block.Lace + i] : end - framesAt;
            children.Add(new Node("Frame", position, size, NodeValue.Integer(size, "bytes", "byte"))
            {
                Kind = audio ? NodeKind.Frame : NodeKind.Detail,
                Description = audio ? Explain.FrameOfAudio : Explain.Frame,
            });
            position += size;
        }

        return children;
    }

    private static string SizeList(Records records, BlockRecord block)
    {
        int shown = Math.Min(block.Frames - 1, 16);
        string text = string.Join(", ", Enumerable.Range(0, shown).Select(i => records.LaceSizes[block.Lace + i].ToString(CultureInfo.InvariantCulture)));
        return block.Frames - 1 > shown ? $"{text}, and {block.Frames - 1 - shown} more" : text;
    }

    private static Node FlagsNode(BlockRecord block, long offset, bool isBlock)
    {
        var flags = new Node("Flags", offset, 1, NodeValue.Hex(block.Flags, 2)) { Description = Explain.BlockFlags };
        if (isBlock)
        {
            flags.Add(Node.FromBits("Reserved", offset, 0, 4, NodeValue.Hex(block.Flags >> 4, 1), description: Explain.FlagReserved));
        }
        else
        {
            flags.Add(Node.FromBits("Keyframe", offset, 0, 1, NodeValue.Boolean(block.IsKeyframe), description: Explain.FlagKeyframe));
            flags.Add(Node.FromBits("Reserved", offset, 1, 4, NodeValue.Hex((block.Flags >> 4) & 7, 1), description: Explain.FlagReserved));
        }

        flags.Add(Node.FromBits("Invisible", offset, 4, 5, NodeValue.Boolean(block.IsInvisible), description: Explain.FlagInvisible));
        flags.Add(Node.FromBits("Lacing", offset, 5, 7, NodeValue.Text($"{(int)block.Lacing} ({(block.Lacing switch { Lacing.None => "none", Lacing.Xiph => "Xiph", Lacing.Ebml => "EBML", _ => "fixed-size" })})"),
            description: Explain.FlagLacing, topic: Topics.Lacing));
        flags.Add(isBlock
            ? Node.FromBits("Unused", offset, 7, 8, NodeValue.Hex(block.Flags & 1, 1), description: Explain.FlagUnused)
            : Node.FromBits("Discardable", offset, 7, 8, NodeValue.Boolean((block.Flags & 1) != 0), description: Explain.FlagDiscardable));
        return flags;
    }
}
