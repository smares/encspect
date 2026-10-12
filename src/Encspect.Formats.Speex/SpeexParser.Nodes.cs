using Encspect.Core;

namespace Encspect.Formats.Speex;

public sealed partial class SpeexParser
{
    // An audio packet's node, whose frames are built from the records when first read.
    private static Node PacketNode(Records records, int index)
    {
        PacketRecord packet = records.Packets[index];
        records.Ranges.TryGetValue(index, out ByteRange[]? ranges);
        long length = ranges is null ? packet.Length : ranges[^1].End - ranges[0].Offset;
        return new Node("Packet", records.OffsetOf(index), length, Summary(records, index), () => PacketFields(records, index))
        {
            Kind = NodeKind.Frame,
            Ranges = ranges,
            Description = Explain.Packet,
        };
    }

    private static string Summary(Records records, int index)
    {
        PacketRecord packet = records.Packets[index];
        int frames = records.FrameCount(index);
        string text = packet.Length == 1 ? "1 byte" : $"{packet.Length} bytes";
        if (frames == 0)
        {
            return text + ", no frames";
        }

        FrameRecord first = records.Frames[packet.FirstFrame];
        bool same = true;
        for (int f = packet.FirstFrame + 1; f < packet.FirstFrame + frames; f++)
        {
            same &= records.Frames[f].Narrowband == first.Narrowband && records.Frames[f].High == first.High;
        }

        string modes = same ? ", " + Modes(first, records.Mode) : string.Empty;
        return frames == 1 ? text + modes : $"{text}, {frames} frames{modes}";
    }

    private static string Modes(FrameRecord frame, int mode)
    {
        string text = $"narrowband submode {frame.Narrowband}";
        if (mode >= 1)
        {
            text += frame.Wideband == FrameRecord.Absent ? ", no wideband layer" : $", wideband submode {frame.Wideband}";
        }

        if (mode >= 2)
        {
            text += frame.UltraWideband == FrameRecord.Absent ? ", no ultra-wideband layer" : $", ultra-wideband submode {frame.UltraWideband}";
        }

        return text;
    }

    // The frames as their bits lie in the packet, then what follows the last one: the terminator
    // and the bits that fill the last byte, or what the decoder does not read.
    private static List<Node> PacketFields(Records records, int index)
    {
        PacketRecord packet = records.Packets[index];
        records.Ranges.TryGetValue(index, out ByteRange[]? ranges);
        var at = new BitPlace(records.OffsetOf(index), ranges is null ? null : new MappedBytes(new byte[packet.Length], ranges));
        var nodes = new List<Node>();
        long position = 0;
        int frames = records.FrameCount(index);
        for (int f = 0; f < frames; f++)
        {
            FrameRecord frame = records.Frames[packet.FirstFrame + f];
            long end = position + frame.Bits;
            Node node = at.Node("Frame", position, end, Modes(frame, records.Mode), Explain.Frame);
            long p = position;
            if (frame.PrefixBits > 0)
            {
                node.Add(at.Node("In-band Data", p, p + frame.PrefixBits, NodeValue.Integer(frame.PrefixBits, "bits", "bit"), Explain.InBandData));
                p += frame.PrefixBits;
            }

            p = AddLayer(node, at, Narrowband, p, frame.Narrowband, SpeexFrames.NarrowbandBits[frame.Narrowband]);
            if (frame.Wideband != FrameRecord.Absent)
            {
                p = AddLayer(node, at, Wideband, p, frame.Wideband, SpeexFrames.WidebandBits[frame.Wideband]);
            }

            if (frame.UltraWideband != FrameRecord.Absent)
            {
                AddLayer(node, at, UltraWideband, p, frame.UltraWideband, SpeexFrames.UltraWidebandBits[frame.UltraWideband]);
            }

            nodes.Add(node);
            position = end;
        }

        long total = packet.Length * 8L;
        if (position < total)
        {
            if (records.Irregular.TryGetValue(index, out PacketWalk walk))
            {
                bool ignored = walk.End == WalkEnd.FrameLimit;
                string value = ignored
                    ? $"{total - position} bits after the frames the header gives, which decoders ignore"
                    : $"{total - position} bits: {walk.Error}";
                nodes.Add(at.Node(ignored ? "Ignored Data" : "Unparsed", position, total, value, ignored ? Explain.IgnoredData : Explain.Unparsed));
            }
            else
            {
                nodes.Add(at.Node("Padding", position, total, NodeValue.Integer(total - position, "bits", "bit"), Explain.Padding));
            }
        }

        return nodes;
    }

    // The three layers a frame may have, with what their nodes say.
    private sealed record Layer(string Name, bool IsWideband, int SubmodeBits, string Description, string Submode, string Coded);

    private static readonly Layer Narrowband = new("Narrowband Layer", false, 4, Explain.NarrowbandLayer, Explain.NarrowbandSubmode, Explain.NarrowbandCoded);
    private static readonly Layer Wideband = new("Wideband Layer", true, 3, Explain.WidebandLayer, Explain.WidebandSubmode, Explain.HighBandCoded);
    private static readonly Layer UltraWideband = new("Ultra-Wideband Layer", true, 3, Explain.UltraWidebandLayer, Explain.UltraWidebandSubmode, Explain.HighBandCoded);

    // A layer: the bit that marks it (0 for the narrowband layer, 1 for a wideband one), its
    // submode, then its coded data.
    private static long AddLayer(Node frame, BitPlace at, Layer kind, long start, int submode, int bits)
    {
        int submodeBits = kind.SubmodeBits;
        Node layer = at.Node(kind.Name, start, start + bits, NodeValue.Integer(bits, "bits", "bit"), kind.Description);
        layer.Add(at.Node("IsWidebandLayer", start, start + 1, NodeValue.Boolean(kind.IsWideband), kind.IsWideband ? Explain.IsWidebandBit : Explain.IsNarrowbandBit));
        layer.Add(at.Node("Submode", start + 1, start + 1 + submodeBits, NodeValue.Integer(submode), kind.Submode));
        if (bits > 1 + submodeBits)
        {
            layer.Add(at.Node("Coded Data", start + 1 + submodeBits, start + bits, NodeValue.Integer(bits - 1 - submodeBits, "bits", "bit"), kind.Coded));
        }

        frame.Add(layer);
        return start + bits;
    }

    // Places bit fields of a packet in the file: at its offset when its bytes are contiguous, or
    // through its ranges when it spans pages.
    private sealed class BitPlace(long offset, MappedBytes? mapped)
    {
        public Node Node(string name, long start, long end, NodeValue? value, string description) => mapped is null
            ? Core.Node.FromBits(name, offset, start, end, value, description: description)
            : Core.Node.FromBits(name, mapped, start, end, value, description: description);
    }

    // The audio packets of a stream, built from the records in blocks when first read and held
    // weakly, so a stream of millions of packets never keeps all their nodes.
    private sealed class PacketList(Records records) : IReadOnlyList<Node>
    {
        private const int BlockSize = 1024;

        private readonly WeakReference<Node[]>?[] _blocks = new WeakReference<Node[]>?[(records.Packets.Count + BlockSize - 1) / BlockSize];
        private Block? _last;

        public int Count => records.Packets.Count;

        public Node this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
                return BlockOf(index / BlockSize)[index % BlockSize];
            }
        }

        public IEnumerator<Node> GetEnumerator()
        {
            for (int block = 0; block < _blocks.Length; block++)
            {
                foreach (Node node in BlockOf(block))
                {
                    yield return node;
                }
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private Node[] BlockOf(int block)
        {
            if (Volatile.Read(ref _last) is { } last && last.Index == block)
            {
                return last.Nodes;
            }

            Node[]? built = null;
            while (true)
            {
                WeakReference<Node[]>? current = Volatile.Read(ref _blocks[block]);
                if (current is not null && current.TryGetTarget(out Node[]? existing))
                {
                    Volatile.Write(ref _last, new Block(block, existing));
                    return existing;
                }

                built ??= Build(block);
                if (Interlocked.CompareExchange(ref _blocks[block], new WeakReference<Node[]>(built), current) == current)
                {
                    Volatile.Write(ref _last, new Block(block, built));
                    return built;
                }
            }
        }

        private Node[] Build(int block)
        {
            int start = block * BlockSize;
            var nodes = new Node[Math.Min(BlockSize, Count - start)];
            for (int i = 0; i < nodes.Length; i++)
            {
                nodes[i] = PacketNode(records, start + i);
            }

            return nodes;
        }

        private sealed record Block(int Index, Node[] Nodes);
    }
}
