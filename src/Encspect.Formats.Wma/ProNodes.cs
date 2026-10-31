using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Builds the nodes of the packets and frames of a stream from what the reader kept: a packet from its record, a frame by replaying its
// trace. Both lists are built in blocks when first read.
internal sealed class ProNodes(StreamData data, ProParameters parameters)
{
    private readonly ProParameters _p = parameters;
    private readonly PacketTable _table = data.Table;

    private sealed class Subframe
    {
        public uint Mask;
        public int Size;
        public int Start;
        public long From;
        public long To;
        public List<Node> Nodes = [];
        public int[] Members = [];
        public int Slot;
    }

    public Node Frame(int index)
    {
        FrameRecord record = data.Frames[index];
        FramePart[] parts = data.PartsOf(record);
        long total = 0;
        foreach (FramePart part in parts)
        {
            total += part.Bits;
        }

        Node At(string name, long start, long bits, NodeValue value, string description, string? topic = null, bool isPrecise = true) =>
            BitNodes.Make(_table, parts, start, start + bits, name, value, description, topic, NodeKind.Detail, isPrecise);

        var trace = new TraceReader(data.Traces.Span(record.Trace, record.TraceLength));
        var children = new List<Node>();
        var subframes = new List<Subframe>();
        long cursor = 0;
        long tileStart = 0;
        long tileBits = 0;
        int tileSlot = -1;
        while (!trace.IsEnd)
        {
            int tag = trace.GetInt();
            if (tag == Tag.End)
            {
                break;
            }

            switch (tag)
            {
                case Tag.FrameLength:
                    long length = trace.Get();
                    children.Add(At("Frame Length", cursor, _p.CountWidth, NodeValue.Integer(length, "bits"), ProExplain.FrameLength));
                    cursor += _p.CountWidth;
                    break;
                case Tag.TileHeader:
                    tileBits = trace.Get();
                    tileStart = cursor;
                    cursor += tileBits;
                    tileSlot = children.Count;
                    children.Add(null!);
                    break;
                case Tag.Postprocessing:
                    long postBits = trace.Get();
                    bool first = trace.Get() != 0;
                    children.Add(At("Postprocessing Transform", cursor, postBits, NodeValue.Boolean(first), ProExplain.PostprocessingTransform));
                    cursor += postBits;
                    break;
                case Tag.DynamicRange:
                    children.Add(At("Dynamic Range Gain", cursor, 8, NodeValue.Integer(trace.Get()), ProExplain.DynamicRangeGain));
                    cursor += 8;
                    break;
                case Tag.Trim:
                    long trimBits = trace.Get();
                    bool hasTrim = trace.Get() != 0;
                    long trimStart = trace.Get();
                    long trimEnd = trace.Get();
                    string trimText = hasTrim ? $"{Counts.Of(trimStart, "sample", "samples")} at the start, {trimEnd} at the end" : "none";
                    children.Add(At("Trim", cursor, trimBits, NodeValue.Text(trimText), ProExplain.Trim));
                    cursor += trimBits;
                    break;
                case Tag.Subframe:
                    var sub = new Subframe { Mask = (uint)trace.Get(), Size = trace.GetInt(), Start = trace.GetInt(), From = cursor };
                    cursor = ReadSubframe(ref trace, sub, cursor, At);
                    sub.Slot = children.Count;
                    children.Add(null!);
                    subframes.Add(sub);
                    break;
                case Tag.Padding:
                    long padding = trace.Get();
                    children.Add(At("Frame Padding", cursor, padding, NodeValue.Integer(padding, "bits"), ProExplain.FramePadding));
                    cursor += padding;
                    break;
                case Tag.Trailer:
                    children.Add(At("More Frames", cursor, 1, NodeValue.Boolean(trace.Get() != 0), ProExplain.MoreFrames));
                    cursor += 1;
                    break;
                default:
                    break;
            }
        }

        for (int i = 0; i < subframes.Count; i++)
        {
            children[subframes[i].Slot] = SubframeNode(subframes[i], parts, record.Fault != 0 && i == subframes.Count - 1);
        }

        if (tileSlot >= 0)
        {
            Node tile = At("Tile Header", tileStart, tileBits, NodeValue.Text(Counts.Of(subframes.Count, "subframe", "subframes")), ProExplain.TileHeader, Topics.WmaSubframes);
            if (subframes.Count > 0)
            {
                for (int c = 0; c < _p.Channels; c++)
                {
                    var lengths = new List<string>();
                    foreach (Subframe sub in subframes)
                    {
                        if ((sub.Mask & (1u << c)) != 0)
                        {
                            lengths.Add((_p.FrameLength >> sub.Size).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                    }

                    // A zero-bit precise node is 0 bytes long, an imprecise one takes a byte: an empty tile's children must be precise to stay inside it.
                    tile.Add(At($"Channel {c + 1} Subframes", tileStart, tileBits, NodeValue.Text(string.Join(", ", lengths)), ProExplain.ChannelSubframes, null, tileBits == 0));
                }
            }

            children[tileSlot] = tile;
        }

        string value = record.Fault == 0
            ? $"{Counts.Of(subframes.Count, "subframe", "subframes")}, {Counts.Of(record.Bits, "bit", "bits")}"
            : $"invalid: {ProExplain.Reason((ProFault)record.Fault)}";
        var frame = BitNodes.Make(_table, parts, 0, total, "Frame", NodeValue.Text(value), ProExplain.Frame, Topics.WmaPro, NodeKind.Frame, false);
        foreach (Node child in children)
        {
            frame.Add(child);
        }

        return frame;
    }

    private Node SubframeNode(Subframe sub, FramePart[] parts, bool isInvalid)
    {
        int length = _p.FrameLength >> sub.Size;
        var channels = new List<int>();
        for (int c = 0; c < _p.Channels; c++)
        {
            if ((sub.Mask & (1u << c)) != 0)
            {
                channels.Add(c + 1);
            }
        }

        string text = $"{length} samples at {sub.Start}, {(channels.Count == 1 ? "channel" : "channels")} {string.Join(' ', channels)}{(isInvalid ? ", invalid" : string.Empty)}";
        Node node = BitNodes.Make(_table, parts, sub.From, sub.To, "Subframe", NodeValue.Text(text), ProExplain.Subframe, Topics.WmaSubframes, NodeKind.Detail, false);
        foreach (Node child in sub.Nodes)
        {
            node.Add(child);
        }

        return node;
    }

    private long ReadSubframe(ref TraceReader trace, Subframe sub, long cursor, Func<string, long, long, NodeValue, string, string?, bool, Node> place)
    {
        var members = new List<int>();
        for (int c = 0; c < _p.Channels; c++)
        {
            if ((sub.Mask & (1u << c)) != 0)
            {
                members.Add(c);
            }
        }

        sub.Members = [.. members];
        var scaleFactors = new Dictionary<int, Node>();
        var coefficients = new Dictionary<int, Node>();
        uint sent = 0;
        var nodes = new List<Node>();
        Node N(string name, long start, long bits, NodeValue value, string description, string? topic = null) => place(name, start, bits, value, description, topic, true);
        while (!trace.IsEnd)
        {
            int tag = trace.GetInt();
            if (tag == Tag.End)
            {
                break;
            }

            switch (tag)
            {
                case Tag.ExtendedHeader:
                    long bits = trace.Get();
                    long fill = trace.Get();
                    nodes.Add(N("Extended Header", cursor, bits, NodeValue.Text(fill == 0 ? "none" : $"{Counts.Of(fill, "fill bit", "fill bits")}"), ProExplain.ExtendedHeader, Topics.WmaSubframes));
                    cursor += bits;
                    break;
                case Tag.ReservedBit:
                    nodes.Add(N("Reserved Bit", cursor, 1, NodeValue.Boolean(trace.Get() != 0), ProExplain.ReservedBit));
                    cursor += 1;
                    break;
                case Tag.ChannelTransform:
                    long transformBits = trace.Get();
                    long groupCount = trace.Get();
                    Node transform = N("Channel Transform", cursor, transformBits,
                        NodeValue.Text(Counts.Of(Math.Max(groupCount - 1, 0), "group", "groups")), ProExplain.ChannelTransform, Topics.WmaChannelTransforms);
                    long at = cursor + 1;
                    for (long g = 1; g < groupCount; g++)
                    {
                        uint mask = (uint)trace.Get();
                        long groupBits = trace.Get();
                        long bands = trace.Get() - 1;
                        var names = new List<int>();
                        for (int i = 0; i < members.Count; i++)
                        {
                            if ((mask & (1u << i)) != 0)
                            {
                                names.Add(members[i] + 1);
                            }
                        }

                        string text = $"channels {string.Join(' ', names)}, {(bands < 0 ? "no transform" : $"transform in {Counts.Of(bands, "band", "bands")}")}";
                        transform.Add(N("Channel Group", at, groupBits, NodeValue.Text(text), ProExplain.ChannelGroup, Topics.WmaChannelTransforms));
                        at += groupBits;
                    }

                    nodes.Add(transform);
                    cursor += transformBits;
                    break;
                case Tag.CoefficientsSent:
                    long sentBits = trace.Get();
                    sent = (uint)trace.Get();
                    var yes = new List<string>();
                    for (int i = 0; i < members.Count; i++)
                    {
                        yes.Add((sent & (1u << i)) != 0 ? "yes" : "no");
                    }

                    nodes.Add(N("Coefficients Sent", cursor, sentBits, NodeValue.Text(string.Join(", ", yes)), ProExplain.CoefficientsSent));
                    cursor += sentBits;
                    break;
                case Tag.VectorCounts:
                    long vectorBits = trace.Get();
                    bool hasCounts = trace.Get() != 0;
                    string vectorText = "all";
                    if (hasCounts)
                    {
                        var counts = new List<long>();
                        for (int i = 0; i < members.Count; i++)
                        {
                            counts.Add(trace.Get());
                        }

                        vectorText = string.Join(", ", counts);
                    }

                    nodes.Add(N("Vector Coefficients", cursor, vectorBits, NodeValue.Text(vectorText), ProExplain.VectorCoefficients));
                    cursor += vectorBits;
                    break;
                case Tag.QuantizationStep:
                    long stepBits = trace.Get();
                    nodes.Add(N("Quantization Step", cursor, stepBits, NodeValue.Integer(trace.GetSigned()), ProExplain.QuantizationStep, Topics.WmaScaleFactors));
                    cursor += stepBits;
                    break;
                case Tag.ScaleFactors:
                    int member = trace.GetInt();
                    long sfBits = trace.Get();
                    long mode = trace.Get();
                    long step = trace.Get();
                    if ((sent & (1u << member)) != 0)
                    {
                        string text = mode switch
                        {
                            0 => $"a new set, step {step}",
                            1 => "differences from the last set",
                            _ => "as before",
                        };
                        scaleFactors[member] = N($"Scale Factors (channel {members[member] + 1})", cursor, sfBits, NodeValue.Text(text), ProExplain.ScaleFactors, Topics.WmaScaleFactors);
                    }

                    cursor += sfBits;
                    break;
                case Tag.Coefficients:
                    int owner = trace.GetInt();
                    long codeBits = trace.Get();
                    long nonZero = trace.Get();
                    long escapes = trace.Get();
                    long codebook = trace.Get();
                    bool isDamaged = trace.Get() != 0;
                    string codeText = $"{nonZero} non-zero, {Counts.Of(escapes, "escape", "escapes")}, codebook {codebook}{(isDamaged ? ", damaged" : string.Empty)}";
                    coefficients[owner] = N($"Coefficients (channel {members[owner] + 1})", cursor, codeBits, NodeValue.Text(codeText), ProExplain.Coefficients, Topics.WmaCoefficients);
                    cursor += codeBits;
                    break;
                default:
                    break;
            }
        }

        for (int i = 0; i < members.Count; i++)
        {
            if (scaleFactors.TryGetValue(i, out Node? sf))
            {
                nodes.Add(sf);
            }

            if (coefficients.TryGetValue(i, out Node? coded))
            {
                nodes.Add(coded);
            }
        }

        sub.Nodes = nodes;
        sub.To = cursor;
        return cursor;
    }
}
