using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Builds a Lossless frame's nodes by replaying its trace.
internal sealed class LosslessNodes(StreamData data, LosslessParameters parameters)
{
    private readonly LosslessParameters _p = parameters;
    private readonly PacketTable _table = data.Table;

    private sealed class Sub
    {
        public uint Mask;
        public int Length;
        public int Start;
        public long From;
        public long To;
        public int Slot;
        public List<Node> Nodes = [];
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
        var subframes = new List<Sub>();
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
                    children.Add(At("Frame Length", cursor, _p.CountWidth, NodeValue.Integer(trace.Get(), "bits"), ProExplain.FrameLength));
                    cursor += _p.CountWidth;
                    break;
                case Tag.TileHeader:
                    tileBits = trace.Get();
                    tileStart = cursor;
                    cursor += tileBits;
                    tileSlot = children.Count;
                    children.Add(null!);
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
                    children.Add(At("Trim", cursor, trimBits, NodeValue.Text(trimText), LosslessExplain.Trim));
                    cursor += trimBits;
                    break;
                case Tag.Subframe:
                    var sub = new Sub { Mask = (uint)trace.Get(), Length = trace.GetInt(), Start = trace.GetInt(), From = cursor };
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
                    children.Add(At("Has More Frames", cursor, 1, NodeValue.Boolean(trace.Get() != 0), ProExplain.MoreFrames));
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
                    foreach (Sub sub in subframes)
                    {
                        if ((sub.Mask & (1u << c)) != 0)
                        {
                            lengths.Add(sub.Length.ToString(CultureInfo.InvariantCulture));
                        }
                    }

                    tile.Add(At($"Channel {c + 1} Subframes", tileStart, tileBits, NodeValue.Text(string.Join(", ", lengths)), ProExplain.ChannelSubframes, null, false));
                }
            }

            children[tileSlot] = tile;
        }

        string value = record.Fault == 0
            ? $"{Counts.Of(subframes.Count, "subframe", "subframes")}, {Counts.Of(record.Bits, "bit", "bits")}"
            : $"invalid: {LosslessExplain.Reason((LosslessFault)record.Fault)}";
        Node frame = BitNodes.Make(_table, parts, 0, total, "Frame", NodeValue.Text(value), ProExplain.Frame, Topics.WmaLossless, NodeKind.Frame, false);
        foreach (Node child in children)
        {
            frame.Add(child);
        }

        return frame;
    }

    private Node SubframeNode(Sub sub, FramePart[] parts, bool isInvalid)
    {
        var channels = new List<int>();
        for (int c = 0; c < _p.Channels; c++)
        {
            if ((sub.Mask & (1u << c)) != 0)
            {
                channels.Add(c + 1);
            }
        }

        string text = $"{sub.Length} samples at {sub.Start}, {(channels.Count == 1 ? "channel" : "channels")} {string.Join(' ', channels)}{(isInvalid ? ", invalid" : string.Empty)}";
        Node node = BitNodes.Make(_table, parts, sub.From, sub.To, "Subframe", NodeValue.Text(text), ProExplain.Subframe, Topics.WmaSubframes, NodeKind.Detail, false);
        foreach (Node child in sub.Nodes)
        {
            node.Add(child);
        }

        return node;
    }

    private long ReadSubframe(ref TraceReader trace, Sub sub, long cursor, Func<string, long, long, NodeValue, string, string?, bool, Node> place)
    {
        Node N(string name, long start, long bits, NodeValue value, string description, string? topic = null) => place(name, start, bits, value, description, topic, true);
        const string Subframes = Topics.WmaLosslessSubframes;
        while (!trace.IsEnd)
        {
            int tag = trace.GetInt();
            if (tag == LosslessTag.End)
            {
                break;
            }

            switch (tag)
            {
                case LosslessTag.Seekable:
                    sub.Nodes.Add(N("Is Seekable Subframe", cursor, 1, NodeValue.Boolean(trace.Get() != 0), LosslessExplain.SeekableSubframe, Subframes));
                    cursor += 1;
                    break;
                case LosslessTag.ArithmeticCoding:
                    sub.Nodes.Add(N("Uses Arithmetic Coding", cursor, 1, NodeValue.Boolean(trace.Get() != 0), LosslessExplain.ArithmeticCoding, Subframes));
                    cursor += 1;
                    break;
                case LosslessTag.FilterFlags:
                    sub.Nodes.Add(N("Has Recursive Filter", cursor, 1, NodeValue.Boolean(trace.Get() != 0), LosslessExplain.RecursiveFilter, Subframes));
                    sub.Nodes.Add(N("Uses Mid/Side Stereo", cursor + 1, 1, NodeValue.Boolean(trace.Get() != 0), LosslessExplain.MidSide, Topics.MidSide));
                    sub.Nodes.Add(N("Has Multichannel Filter", cursor + 2, 1, NodeValue.Boolean(trace.Get() != 0), LosslessExplain.MultichannelFilter, Subframes));
                    cursor += 3;
                    break;
                case LosslessTag.RecursiveParameters:
                    long recursiveBits = trace.Get();
                    long recursiveOrder = trace.Get();
                    long recursiveScaling = trace.Get();
                    sub.Nodes.Add(N("Recursive Filter Parameters", cursor, recursiveBits, NodeValue.Text($"order {recursiveOrder}, scaling {recursiveScaling}"), LosslessExplain.RecursiveParameters, Subframes));
                    cursor += recursiveBits;
                    break;
                case LosslessTag.MultichannelParameters:
                    long multichannelBits = trace.Get();
                    long multichannelOrder = trace.Get();
                    long multichannelScaling = trace.Get();
                    sub.Nodes.Add(N("Multichannel Filter Parameters", cursor, multichannelBits, NodeValue.Text($"order {multichannelOrder}, scaling {multichannelScaling}"), LosslessExplain.MultichannelParameters, Subframes));
                    cursor += multichannelBits;
                    break;
                case LosslessTag.CascadedFilters:
                    long cascadedBits = trace.Get();
                    long first = trace.Get();
                    long filters = trace.Get();
                    sub.Nodes.Add(N("Cascaded Filters", cursor, cascadedBits, NodeValue.Text($"{Counts.Of(filters, "filter", "filters")}, {Counts.Of(first, "filter", "filters")} for channel 1"), LosslessExplain.CascadedFilters, Subframes));
                    cursor += cascadedBits;
                    break;
                case LosslessTag.MeanScaling:
                    sub.Nodes.Add(N("Mean Scaling", cursor, 3, NodeValue.Integer(trace.Get()), LosslessExplain.MeanScaling, Subframes));
                    cursor += 3;
                    break;
                case LosslessTag.QuantizationStep:
                    sub.Nodes.Add(N("Quantization Step", cursor, 8, NodeValue.Integer(trace.Get()), LosslessExplain.QuantizationStep, Subframes));
                    cursor += 8;
                    break;
                case LosslessTag.RawPcm:
                    sub.Nodes.Add(N("Is Raw PCM Subframe", cursor, 1, NodeValue.Boolean(trace.Get() != 0), LosslessExplain.RawPcm, Subframes));
                    cursor += 1;
                    break;
                case LosslessTag.CodedChannels:
                    long codedBits = trace.Get();
                    uint coded = (uint)trace.Get();
                    var yes = new List<string>();
                    for (int c = 0; c < _p.Channels; c++)
                    {
                        yes.Add((coded & (1u << c)) != 0 ? "yes" : "no");
                    }

                    sub.Nodes.Add(N("Coded Channels", cursor, codedBits, NodeValue.Text(string.Join(", ", yes)), LosslessExplain.CodedChannels, Subframes));
                    cursor += codedBits;
                    break;
                case LosslessTag.Lpc:
                    long lpcBits = trace.Get();
                    long lpcOrder = trace.Get();
                    sub.Nodes.Add(N("LPC Filter", cursor, lpcBits, NodeValue.Text(lpcOrder == 0 ? "none" : $"order {lpcOrder}"), LosslessExplain.LpcFilter, Subframes));
                    cursor += lpcBits;
                    break;
                case LosslessTag.ZeroLowBits:
                    long zeroBits = trace.Get();
                    long zeros = trace.Get();
                    sub.Nodes.Add(N("Zero Low Bits", cursor, zeroBits, NodeValue.Integer(zeros, "bits"), LosslessExplain.ZeroLowBits, Topics.WastedBits));
                    cursor += zeroBits;
                    break;
                case LosslessTag.Samples:
                    int rawChannel = trace.GetInt();
                    long rawBits = trace.Get();
                    long width = trace.Get();
                    sub.Nodes.Add(N($"Samples (channel {rawChannel + 1})", cursor, rawBits, NodeValue.Text($"{sub.Length} samples of {Counts.Of(width, "bit", "bits")}"), LosslessExplain.Samples, Subframes));
                    cursor += rawBits;
                    break;
                case LosslessTag.Residuals:
                    int channel = trace.GetInt();
                    long residualBits = trace.Get();
                    long count = trace.Get();
                    long escapes = trace.Get();
                    bool isTransient = trace.Get() != 0;
                    string text = $"{Counts.Of(count, "residual", "residuals")}, {Counts.Of(escapes, "escape", "escapes")}{(isTransient ? ", transient" : string.Empty)}";
                    sub.Nodes.Add(N($"Residuals (channel {channel + 1})", cursor, residualBits, NodeValue.Text(text), LosslessExplain.Residuals, Subframes));
                    cursor += residualBits;
                    break;
                default:
                    break;
            }
        }

        sub.To = cursor;
        return cursor;
    }
}
