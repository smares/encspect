using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.WavPack;

// The nodes of the report, built from the entries when something reads them: a block's header, its
// sub-blocks and what the decoded ones hold. Functions only of the state, so the findings can build
// the node they point to.
internal static partial class Nodes
{
    public static Node Skipped(Records s, int entry)
    {
        Entry e = s.Entries[entry];
        bool isCut = (e.Status & BlockStatus.CutHeader) != 0;
        return new Node("Skipped Data", e.Offset, e.Length, NodeValue.Integer(e.Length, "bytes", "byte"))
        {
            Description = isCut ? Explain.CutHeader : Explain.SkippedData,
            Topic = Topics.Blocks,
        };
    }

    public static Node Block(Records s, int entry)
    {
        Entry e = s.Entries[entry];
        if (e.Kind == EntryKind.Run)
        {
            return new Node("Undetailed Blocks", e.Offset, e.Length, NodeValue.Integer(e.SubBlocks, "blocks", "block")) { Description = Explain.UndetailedBlocks, Topic = Topics.Blocks };
        }

        BlockHeader h = e.Header;
        bool hasAudio = h.HasAudio;
        return new Node(hasAudio ? "Block" : "Metadata Block", e.Offset, e.Length,
            hasAudio ? NodeValue.Integer(h.Samples, "samples", "sample") : "no audio", () => BlockChildren(s, entry))
        {
            Kind = hasAudio ? NodeKind.Frame : NodeKind.Detail,
            Description = hasAudio ? Explain.Block : Explain.MetadataBlock,
            Topic = Topics.Blocks,
        };
    }

    public static Node HeaderField(Records s, int entry, string name) =>
        s.Entries[entry].Kind == EntryKind.Run ? Block(s, entry) : HeaderNode(s, entry).Children.First(c => c.Name == name);

    public static Node SubBlock(Records s, int entry, int index)
    {
        Entry e = s.Entries[entry];
        if (e.Detail < 0 || index >= s.Details[e.Detail].SubBlocks.Count)
        {
            return Block(s, entry);
        }

        return SubBlockNode(s, e, s.Details[e.Detail], index);
    }

    private static IReadOnlyList<Node> BlockChildren(Records s, int entry)
    {
        Entry e = s.Entries[entry];
        var children = new List<Node> { HeaderNode(s, entry) };
        if (e.Detail < 0)
        {
            if (e.Length > BlockHeader.Length)
            {
                children.Add(new Node("Sub-blocks", e.Offset + BlockHeader.Length, e.Length - BlockHeader.Length, NodeValue.Integer(e.SubBlocks, "sub-blocks", "sub-block"))
                {
                    Description = Explain.UndetailedSubBlocks,
                    Topic = Topics.SubBlocks,
                });
            }

            return children;
        }

        Detail detail = s.Details[e.Detail];
        for (int i = 0; i < detail.SubBlocks.Count; i++)
        {
            children.Add(SubBlockNode(s, e, detail, i));
        }

        if (detail.Further > 0)
        {
            children.Add(new Node("Further Sub-blocks", detail.FurtherOffset, detail.FurtherLength, NodeValue.Integer(detail.Further, "sub-blocks", "sub-block"))
            {
                Description = Explain.FurtherSubBlocks,
                Topic = Topics.SubBlocks,
            });
        }

        if (detail.Trailing > 0)
        {
            children.Add(new Node("Trailing Bytes", detail.TrailingOffset, detail.Trailing, NodeValue.Integer(detail.Trailing, "bytes", "byte")) { Description = Explain.TrailingBytes });
        }

        return children;
    }

    private static Node HeaderNode(Records s, int entry)
    {
        Entry e = s.Entries[entry];
        BlockHeader h = e.Header;
        long o = e.Offset;
        var header = new Node("Block Header", o, BlockHeader.Length) { Description = Explain.BlockHeader, Topic = Topics.Blocks };
        header.Add("Signature", o, 4, "wvpk", Explain.Signature, Topics.Blocks);
        header.Add("Block Size", o + 4, 4, NodeValue.Integer(h.BlockSize, "bytes", "byte"), Explain.BlockSize, Topics.Blocks);
        header.Add("Stream Version", o + 8, 2, NodeValue.Hex(h.Version, 4), Explain.StreamVersion, Topics.Blocks);
        header.Add("Total Samples", o + 11, 5, h.TotalSamples is { } total ? NodeValue.Integer(total, "samples", "sample") : "unknown",
            h.TotalSamples is null ? Explain.TotalSamplesUnknown : Explain.TotalSamples, Topics.Blocks);
        header.Add(new Node("Block Index", o + 10, 10, NodeValue.Integer(h.BlockIndex, "samples", "sample"))
        {
            Ranges = [new ByteRange(o + 10, 1), new ByteRange(o + 16, 4)],
            Description = Explain.BlockIndex,
            Topic = Topics.Blocks,
        });
        header.Add("Block Samples", o + 20, 4, NodeValue.Integer(h.Samples, "samples", "sample"), h.HasAudio ? Explain.BlockSamples : Explain.BlockSamplesZero, Topics.Blocks);
        header.Add(FlagsNode(h, o + 24));
        header.Add("CRC", o + 28, 4, NodeValue.Hex(h.Crc, 8), Explain.BlockCrc, Topics.Crc);
        return header;
    }

    private static Node FlagsNode(in BlockHeader h, long offset)
    {
        uint f = h.Flags;
        var flags = new Node("Flags", offset, 4, NodeValue.Hex(f, 8)) { Description = Explain.Flags, Topic = Topics.Blocks };
        Node Bit(string name, int bit, string description, string? topic = null) =>
            Bit2(name, bit, NodeValue.Boolean((f & (1u << bit)) != 0), description, topic);
        Node Bit2(string name, int bit, NodeValue value, string description, string? topic = null) =>
            Node.FromBits(name, offset, bit, bit + 1, value, NodeKind.Detail, description, topic, BitOrder.LeastSignificantFirst);
        Node Bits(string name, int first, int last, NodeValue value, string description, string? topic = null) =>
            Node.FromBits(name, offset, first, last, value, NodeKind.Detail, description, topic, BitOrder.LeastSignificantFirst);

        flags.Add(Bits("Bytes Per Sample", 0, 2, NodeValue.Integer(h.BytesPerSample, "bytes", "byte"), Explain.BytesPerSample));
        flags.Add(Bit("IsMono", 2, Explain.IsMono));
        flags.Add(Bit("IsHybrid", 3, Explain.IsHybrid, Topics.Hybrid));
        flags.Add(Bit("UsesJointStereo", 4, Explain.UsesJointStereo, Topics.MidSideStereo));
        flags.Add(Bit("UsesCrossChannelDecorrelation", 5, Explain.UsesCrossChannelDecorrelation, Topics.Decorrelation));
        flags.Add(Bit("UsesHybridNoiseShaping", 6, Explain.UsesHybridNoiseShaping, Topics.Hybrid));
        flags.Add(Bit("IsFloatingPoint", 7, Explain.IsFloatingPoint, Topics.FloatAndLargeIntegers));
        flags.Add(Bit("HasExtendedIntegers", 8, Explain.HasExtendedIntegers, Topics.FloatAndLargeIntegers));
        flags.Add(Bit("UsesHybridBitrateControl", 9, Explain.UsesHybridBitrateControl, Topics.Hybrid));
        flags.Add(Bit("UsesHybridBalance", 10, Explain.UsesHybridBalance, Topics.Hybrid));
        flags.Add(Bit("IsInitialBlock", 11, Explain.IsInitialBlock, Topics.Blocks));
        flags.Add(Bit("IsFinalBlock", 12, Explain.IsFinalBlock, Topics.Blocks));
        flags.Add(Bits("Left Shift", 13, 18, NodeValue.Integer(h.LeftShift, "bits", "bit"), Explain.LeftShift, Topics.FloatAndLargeIntegers));
        flags.Add(Bits("Maximum Magnitude", 18, 23, NodeValue.Integer(h.MaxMagnitude, "bits", "bit"), Explain.MaxMagnitude));
        flags.Add(Bits("Sample Rate", 23, 27, h.SampleRate != 0 ? NodeValue.Integer(h.SampleRate, "Hz") : "stored in a sub-block", h.SampleRate != 0 ? Explain.SampleRateIndex : Explain.SampleRateCustom));
        flags.Add(Bit("HasReservedFlag", 27, Explain.ReservedFlag));
        flags.Add(Bit("HasBlockChecksum", 28, Explain.HasBlockChecksum, Topics.Crc));
        flags.Add(Bit("UsesIirNoiseShaping", 29, Explain.UsesIirNoiseShaping, Topics.Hybrid));
        flags.Add(Bit("IsFalseStereo", 30, Explain.IsFalseStereo));
        flags.Add(Bit("IsDsd", 31, Explain.IsDsd, Topics.Dsd));
        return flags;
    }

    private static Node SubBlockNode(Records s, Entry e, Detail detail, int index)
    {
        SubBlock sb = detail.SubBlocks[index];
        int function = sb.Function;
        string name = SubBlockIds.Name(function);
        return new Node(name, sb.Offset, sb.Length, NodeValue.Integer(sb.DataLength, "bytes", "byte"), () => SubBlockChildren(s, e, detail, index))
        {
            Description = Explain.SubBlock(function),
            Topic = TopicOf(function),
        };
    }

    private static string TopicOf(int function) => function switch
    {
        SubBlockIds.DecorrTerms or SubBlockIds.DecorrWeights or SubBlockIds.DecorrSamples or SubBlockIds.EntropyVars => Topics.Decorrelation,
        SubBlockIds.HybridProfile or SubBlockIds.ShapingWeights => Topics.Hybrid,
        SubBlockIds.WvcBitstream => Topics.Hybrid,
        SubBlockIds.FloatInfo or SubBlockIds.Int32Info or SubBlockIds.WvxBitstream => Topics.FloatAndLargeIntegers,
        SubBlockIds.DsdBlock => Topics.Dsd,
        SubBlockIds.RiffHeader or SubBlockIds.RiffTrailer or SubBlockIds.AltHeader or SubBlockIds.AltTrailer or SubBlockIds.Md5Checksum
            or SubBlockIds.AltMd5Checksum or SubBlockIds.AltExtension or SubBlockIds.NewConfigBlock => Topics.Wrapper,
        SubBlockIds.BlockChecksum => Topics.Crc,
        _ => Topics.SubBlocks,
    };

    private static IReadOnlyList<Node> SubBlockChildren(Records s, Entry e, Detail detail, int index)
    {
        SubBlock sb = detail.SubBlocks[index];
        long o = sb.Offset;
        var children = new List<Node>();
        var id = new Node("ID", o, 1, NodeValue.Hex(sb.Id, 2)) { Description = Explain.SubBlockId, Topic = Topics.SubBlocks };
        id.Add(Node.FromBits("Function", o, 0, 5, NodeValue.Hex(sb.Id & 0x1F, 2), NodeKind.Detail, Explain.SubBlockFunction, Topics.SubBlocks, BitOrder.LeastSignificantFirst));
        id.Add(Node.FromBits("IsOptional", o, 5, 6, NodeValue.Boolean((sb.Id & SubBlockIds.OptionalBit) != 0), NodeKind.Detail, Explain.IsOptional, Topics.SubBlocks, BitOrder.LeastSignificantFirst));
        id.Add(Node.FromBits("HasOddSize", o, 6, 7, NodeValue.Boolean(sb.IsOdd), NodeKind.Detail, Explain.HasOddSize, Topics.SubBlocks, BitOrder.LeastSignificantFirst));
        id.Add(Node.FromBits("IsLarge", o, 7, 8, NodeValue.Boolean(sb.IsLarge), NodeKind.Detail, Explain.IsLarge, Topics.SubBlocks, BitOrder.LeastSignificantFirst));
        children.Add(id);
        children.Add(new Node("Size", o + 1, sb.HeaderLength - 1, NodeValue.Integer(sb.Size / 2, "words", "word")) { Description = Explain.SubBlockSize, Topic = Topics.SubBlocks });
        long dataAt = o + sb.HeaderLength;
        if (sb.DataLength > 0)
        {
            var data = new Node("Data", dataAt, sb.DataLength, NodeValue.Integer(sb.DataLength, "bytes", "byte")) { Description = Explain.SubBlockData };
            ReadOnlySpan<byte> kept = detail.Data.AsSpan(sb.Stored, sb.StoredLength);
            DataChildren(s, e, detail, sb, kept, dataAt, data);
            children.Add(data);
        }

        if (sb.PadLength > 0)
        {
            children.Add(new Node("Padding", dataAt + sb.DataLength, 1, NodeValue.Integer(1, "bytes", "byte")) { Description = Explain.SubBlockPad });
        }

        return children;
    }

    internal static Node Field(long offset, long length, string name, NodeValue? value, string description, string? topic = null) =>
        new(name, offset, length, value) { Description = description, Topic = topic };

    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Printable(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder();
        foreach (byte b in bytes)
        {
            text.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return text.ToString();
    }

    internal static uint ReadUInt16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
}
