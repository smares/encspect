using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.OptimFrog;

// The nodes of the report built from the records when something reads them. Functions only of the records and the reader, so a finding can build the
// node it points to.
internal static class Nodes
{
    public static string NameOf(uint id) => id switch
    {
        Layout.Main => "Main Block",
        Layout.Head => "Header Block",
        Layout.Comp => "Data Block",
        Layout.Tail => "Footer Block",
        Layout.Recovery => "Recovery Block",
        Layout.Md5Id => "MD5 Block",
        _ => "Unknown Block",
    };

    private static string DescriptionOf(uint id) => id switch
    {
        Layout.Main => Explain.MainBlock,
        Layout.Head => Explain.HeadBlock,
        Layout.Comp => Explain.CompBlock,
        Layout.Tail => Explain.TailBlock,
        Layout.Recovery => Explain.RecoveryBlock,
        Layout.Md5Id => Explain.Md5Block,
        _ => Explain.UnknownBlock,
    };

    // The node of the block at an index of the records. The bytes of the main, header and footer blocks are read again from the input when the node's
    // children are first asked for.
    public static Node Block(Source source, int index)
    {
        Records r = source.Records;
        BlockRecord b = r.Blocks[index];
        long length = Math.Min(b.End, r.DataEnd) - b.Offset;
        bool isComp = b.Id == Layout.Comp;
        return new Node(NameOf(b.Id), b.Offset, length, NodeValue.Integer(length, "bytes", "byte"), () => Children(source, index))
        {
            Kind = isComp ? NodeKind.Frame : NodeKind.Detail,
            Description = DescriptionOf(b.Id),
            Topic = Topics.Blocks,
        };
    }

    public static Node CrcField(Source source, int index) => Block(source, index).Children.First(c => c.Name == "CRC-32");

    private static IReadOnlyList<Node> Children(Source source, int index)
    {
        Records r = source.Records;
        BlockRecord b = r.Blocks[index];
        long held = Math.Min(b.End, r.DataEnd) - b.Offset - Layout.BlockHeaderLength;
        long at = b.Offset + Layout.BlockHeaderLength;
        var children = new List<Node>
        {
            new("Block ID", b.Offset, 4, Layout.Text(b.Id)) { Description = Explain.BlockId, Topic = Topics.Blocks },
            new("Block Length", b.Offset + 4, 4, NodeValue.Integer(b.Length, "bytes", "byte")) { Description = Explain.BlockLength, Topic = Topics.Blocks },
        };
        switch (b.Id)
        {
            case Layout.Main:
                AddMain(source, children, at, held, b);
                break;
            case Layout.Comp:
                AddComp(children, at, held, b);
                break;
            case Layout.Head:
                if (held > 0)
                {
                    children.Add(HeaderData.Node(source, "WAV Header Data", at, held, Explain.HeaderData, isHeader: true));
                }

                break;
            case Layout.Tail:
                if (held > 0)
                {
                    children.Add(HeaderData.Node(source, "WAV Terminating Data", at, held, Explain.TerminatingData, isHeader: false));
                }

                break;
            case Layout.Md5Id:
                if (b.Length == Layout.Md5Length && held == Layout.Md5Length && r.Md5 is { } md5 && index == r.Md5Index)
                {
                    children.Add(new Node("MD5", at, Layout.Md5Length, Convert.ToHexStringLower(md5)) { Description = Explain.Md5Digest, Topic = Topics.Blocks });
                }
                else if (held > 0)
                {
                    children.Add(new Node("MD5 Data", at, held, NodeValue.Integer(held, "bytes", "byte")) { Description = Explain.Md5Data, Topic = Topics.Blocks });
                }

                break;
            default:
                if (held > 0)
                {
                    children.Add(new Node(b.Id == Layout.Recovery ? "Recovery Data" : "Block Data", at, held, NodeValue.Integer(held, "bytes", "byte"))
                    {
                        Description = b.Id == Layout.Recovery ? Explain.RecoveryData : Explain.UnknownData,
                        Topic = Topics.Blocks,
                    });
                }

                break;
        }

        return children;
    }

    private static void AddMain(Source source, List<Node> children, long at, long held, BlockRecord b)
    {
        if (held < Layout.MainFields)
        {
            if (held > 0)
            {
                children.Add(new Node("Main Data", at, held, NodeValue.Integer(held, "bytes", "byte")) { Description = Explain.MainData, Topic = Topics.Blocks });
            }

            return;
        }

        if (source.Records.Main is not { } m || b.Offset != source.Records.Offset)
        {
            return;
        }

        children.Add(new Node("Total Samples", at, 6, NodeValue.Integer(m.Samples, "samples", "sample")) { Description = Explain.TotalSamples, Topic = Topics.Blocks });
        children.Add(new Node("Sample Type", at + 6, 1, Layout.SampleType(m.SampleType) ?? $"undefined ({m.SampleType})") { Description = Explain.SampleType, Topic = Topics.Blocks });
        children.Add(new Node("Channel Configuration", at + 7, 1, Layout.ChannelConfiguration(m.ChannelConfiguration) ?? $"undefined ({m.ChannelConfiguration})")
        {
            Description = Explain.ChannelConfiguration,
            Topic = Topics.Channels,
        });
        children.Add(new Node("Sample Rate", at + 8, 4, NodeValue.Integer(m.SampleRate, "Hz")) { Description = Explain.SampleRate, Topic = Topics.Blocks });
        Node encoder = new("Encoder ID", at + 12, 2, NodeValue.Hex(m.EncoderId, 4)) { Description = Explain.EncoderId, Topic = Topics.Blocks };
        encoder.Add("Encoder Version", at + 12, 2, Layout.VersionText(m.Version), Explain.EncoderVersion, ClaimRoles.EncoderTopic);
        encoder.Add("Build System", at + 12, 2, Layout.BuildSystem(m.BuildSystem) ?? $"undefined ({m.BuildSystem})", Explain.BuildSystem, Topics.Blocks);
        children.Add(encoder);
        Node compression = new("Compression", at + 14, 1, NodeValue.Hex(m.Compression, 2)) { Description = Explain.Compression, Topic = Topics.Modes };
        compression.Add("Mode", at + 14, 1, Layout.Mode(m.Mode) ?? $"undefined ({m.Mode})", Explain.Mode, Topics.Modes);
        compression.Add("Speedup", at + 14, 1, Layout.Speedup(m.Speedup) ?? $"undefined ({m.Speedup})", Explain.Speedup, Topics.Modes);
        children.Add(compression);
        if (m.MinDecoderVersion is { } minimum)
        {
            children.Add(new Node("Minimum Decoder Version", at + Layout.MainFields, Layout.MinDecoderVersionLength, Layout.VersionText(minimum))
            {
                Description = Explain.MinDecoderVersion,
                Topic = Topics.Blocks,
            });
        }

        long known = m.MinDecoderVersion is null ? Layout.MainFields : Layout.MainFields + Layout.MinDecoderVersionLength;
        if (held > known)
        {
            children.Add(new Node("Additional Fields", at + known, held - known, NodeValue.Integer(held - known, "bytes", "byte")) { Description = Explain.AdditionalFields, Topic = Topics.Blocks });
        }
    }

    private static void AddComp(List<Node> children, long at, long held, BlockRecord b)
    {
        if (held < Layout.CompFields)
        {
            if (held > 0)
            {
                children.Add(new Node("Block Data", at, held, NodeValue.Integer(held, "bytes", "byte")) { Description = Explain.CompFieldsCut, Topic = Topics.Blocks });
            }

            return;
        }

        children.Add(new Node("CRC-32", at, 4, NodeValue.Hex(b.StoredCrc, 8)) { Description = Explain.BlockCrc, Topic = Topics.Crc });
        if (b.HasCrc)
        {
            children.Add(new Node("Computed CRC-32", at, 4, NodeValue.Hex(b.ComputedCrc, 8)) { Description = Explain.BlockComputedCrc, Topic = Topics.Crc });
            children.Add(new Node("IsCrc32Valid", at, 4, NodeValue.Boolean(b.IsCrcValid)) { Description = Explain.BlockIsCrcValid, Topic = Topics.Crc });
        }

        children.Add(new Node("Samples", at + 4, 4, NodeValue.Integer(b.Samples, "samples", "sample")) { Description = Explain.BlockSamples, Topic = Topics.Blocks });
        children.Add(new Node("Sample Type", at + 8, 1, Layout.SampleType(b.SampleType) ?? $"undefined ({b.SampleType})") { Description = Explain.BlockSampleType, Topic = Topics.Blocks });
        children.Add(new Node("Channel Configuration", at + 9, 1, Layout.ChannelConfiguration(b.ChannelConfiguration) ?? $"undefined ({b.ChannelConfiguration})")
        {
            Description = Explain.BlockChannelConfiguration,
            Topic = Topics.Channels,
        });
        children.Add(new Node("Reserved", at + 10, 2, NodeValue.Hex(b.Reserved, 4)) { Description = Explain.BlockReserved, Topic = Topics.Blocks });
        Node encoder = new("Encoder ID", at + 12, 2, NodeValue.Hex(b.EncoderId, 4)) { Description = Explain.BlockEncoderId, Topic = Topics.Blocks };
        encoder.Add("Encoder Version", at + 12, 2, Layout.VersionText((b.EncoderId >> 4) + 4500), Explain.EncoderVersion, ClaimRoles.EncoderTopic);
        children.Add(encoder);
        long data = held - Layout.CompFields;
        if (data > 0)
        {
            children.Add(new Node("Compressed Data", at + Layout.CompFields, data, NodeValue.Integer(data, "bytes", "byte")) { Description = Explain.CompressedData, Topic = Topics.Blocks });
        }
    }
}

// What the nodes need once the analysis is over: the records and what the walk read of the stored headers. It holds no reader, so a report does not
// keep the input alive.
internal sealed class Source(Records records)
{
    public Records Records { get; } = records;

    // The stored header and footer blocks the walk analyzed (the first of each), by the offset of their data.
    public Dictionary<long, HeaderInfo> Headers { get; } = [];
}
