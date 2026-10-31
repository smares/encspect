using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// The file bytes of one media object: one run, or the ranges of the packets it was put together from.
internal sealed class ByteMap
{
    private readonly long _offset;
    private readonly ByteRange[]? _ranges;
    private readonly int[] _starts = [];
    private readonly int _length;
    private readonly bool _isOpaque;

    public ByteMap(in SuperframeRecord record, ByteStore store)
    {
        _offset = record.Offset;
        _length = record.Length;
        if (record.RangesRef == uint.MaxValue)
        {
            return;
        }

        ReadOnlySpan<byte> bytes = store.Span(record.RangesRef, record.RangeCount * 12);
        _ranges = new ByteRange[record.RangeCount];
        _starts = new int[record.RangeCount];
        long total = 0;
        for (int i = 0; i < _ranges.Length; i++)
        {
            _ranges[i] = new ByteRange(BinaryPrimitives.ReadInt64LittleEndian(bytes[(i * 12)..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[((i * 12) + 8)..]));
            _starts[i] = (int)total;
            total += _ranges[i].Length;
        }

        _isOpaque = total != _length;
    }

    // The file offset of the byte at index; _length maps to the end.
    public long OffsetOf(int index)
    {
        if (_ranges is null)
        {
            return _offset + index;
        }

        if (_isOpaque)
        {
            return _ranges[0].Offset;
        }

        int segment = SegmentOf(Math.Min(index, _length - 1));
        return _ranges[segment].Offset + (index - _starts[segment]);
    }

    // The file ranges that hold bytes [from, to).
    public IReadOnlyList<ByteRange> Ranges(int from, int to)
    {
        if (_ranges is null)
        {
            return to > from ? [new ByteRange(_offset + from, to - from)] : [];
        }

        if (_isOpaque)
        {
            return [new ByteRange(_ranges[0].Offset, _ranges[0].Length)];
        }

        var result = new List<ByteRange>();
        for (int segment = to > from ? SegmentOf(from) : _ranges.Length; segment < _ranges.Length && _starts[segment] < to; segment++)
        {
            int begin = Math.Max(from, _starts[segment]);
            int end = Math.Min(to, _starts[segment] + (int)_ranges[segment].Length);
            if (end > begin)
            {
                result.Add(new ByteRange(_ranges[segment].Offset + (begin - _starts[segment]), end - begin));
            }
        }

        return result;
    }

    private int SegmentOf(int index)
    {
        int low = 0;
        int high = _starts.Length;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            (low, high) = _starts[middle] <= index ? (middle, high) : (low, middle);
        }

        return low;
    }
}

// Where the bits of a frame's buffer lie in the file: runs of the buffer, each a run of bits of one superframe.
internal sealed class FrameLayout
{
    private readonly List<(int Start, int End, int Superframe, int SuperframeBit)> _segments = [];
    private readonly Dictionary<int, ByteMap> _maps = [];
    private readonly StdStream _stream;

    public FrameLayout(StdStream stream, in FrameRecord frame)
    {
        _stream = stream;
        int align = stream.Parameters.BlockAlign;
        if (!frame.IsSpanning)
        {
            _segments.Add((0, 8 * align, frame.Superframe, 0));
            return;
        }

        int first = frame.StartBit >> 3;
        int at = 8 * (align - first);
        _segments.Add((0, at, frame.StartSuperframe, 8 * first));
        for (int i = frame.StartSuperframe + 1; i < frame.Superframe; i++)
        {
            if (stream.Superframes[i].State == SuperframeState.Middle)
            {
                _segments.Add((at, at + (8 * (align - 1)), i, 8));
                at += 8 * (align - 1);
            }
        }

        _segments.Add((at, at + stream.Superframes[frame.Superframe].BitOffset, frame.Superframe, 8 + stream.Parameters.BitOffsetBits));
    }

    public int Total => _segments[^1].End;

    private ByteMap Map(int superframe)
    {
        if (!_maps.TryGetValue(superframe, out ByteMap? map))
        {
            map = new ByteMap(_stream.Superframes[superframe], _stream.Store);
            _maps[superframe] = map;
        }

        return map;
    }

    // A node over bits [a, b) of the buffer, or over the bytes they touch where isWholeBytes. One run of one superframe keeps its bit
    // position; bits that continue in another run, or across a gap between the fragments of a media object, have ranges and none.
    public Node Field(string name, int a, int b, NodeValue? value, string description, string? topic, NodeKind kind = NodeKind.Detail, bool isWholeBytes = false, Func<IReadOnlyList<Node>>? build = null)
    {
        b = Math.Min(b, Total);
        a = Math.Min(a, b);
        var pieces = new List<(int Superframe, int From, int To)>();
        foreach ((int start, int end, int superframe, int superframeBit) in _segments)
        {
            int lo = Math.Max(a, start);
            int hi = Math.Min(b, end);
            if (hi > lo || (a == b && a >= start && a < end))
            {
                pieces.Add((superframe, superframeBit + (lo - start), superframeBit + (Math.Max(hi, lo) - start)));
            }
        }

        if (pieces.Count == 0)
        {
            // A field with no bits left in the buffer sits where the last bits end, in the last segment that holds any, so it stays
            // inside the block that ends there.
            int last = _segments.Count - 1;
            while (last > 0 && _segments[last].End <= _segments[last].Start)
            {
                last--;
            }

            (int start, int end, int superframe, int superframeBit) = _segments[last];
            int at = superframeBit + Math.Clamp(a - start, 0, Math.Max(0, end - start));
            pieces.Add((superframe, at, at));
        }

        var ranges = new List<ByteRange>();
        foreach ((int superframe, int from, int to) in pieces)
        {
            ByteMap map = Map(superframe);
            int first = from / 8;
            int end = to == from ? (from % 8 == 0 ? first : first + 1) : (to + 7) / 8;
            ranges.AddRange(map.Ranges(first, end));
        }

        if (pieces.Count == 1 && !isWholeBytes && ranges.Count <= 1 && build is null)
        {
            (int superframe, int from, int to) = pieces[0];
            return Node.FromBits(name, Map(superframe).OffsetOf(from / 8), from % 8, (from % 8) + (to - from), value, kind, description, topic);
        }

        if (ranges.Count == 0)
        {
            long at = Map(pieces[0].Superframe).OffsetOf(pieces[0].From / 8);
            return Make(name, at, 0, value, build, kind, description, topic, null);
        }

        return Cover(name, ranges, value, description, topic, kind, build);
    }

    private static Node Make(string name, long offset, long length, NodeValue? value, Func<IReadOnlyList<Node>>? build, NodeKind kind, string description, string? topic, IReadOnlyList<ByteRange>? ranges)
    {
        return build is null
            ? new Node(name, offset, length, value) { Kind = kind, Description = description, Topic = topic, Ranges = ranges }
            : new Node(name, offset, length, value, build) { Kind = kind, Description = description, Topic = topic, Ranges = ranges };
    }

    public static Node Cover(string name, IReadOnlyList<ByteRange> ranges, NodeValue? value, string description, string? topic, NodeKind kind = NodeKind.Detail, Func<IReadOnlyList<Node>>? build = null)
    {
        long offset = ranges[0].Offset;
        long length = ranges[^1].End - offset;
        return Make(name, offset, length, value, build, kind, description, topic, ranges.Count > 1 ? ranges : null);
    }
}

// Builds the nodes of the superframes and frames from their records.
internal sealed class StdNodes(StdParameters parameters, StdStream stream)
{
    private int FrameLength => parameters.FrameLength;

    public Node Superframes(Node parent, long count) => new("Superframes", parent.Offset, parent.Length, WmaStandardParser.Count(count, "superframe", "superframes"), BuildSuperframes)
    {
        Description = StdText.SuperframeList,
        Topic = Topics.WmaSuperframes,
    };

    public Node Frames(Node parent, long count) => new("Frames", parent.Offset, parent.Length, WmaStandardParser.Count(count, "frame", "frames"), BuildFrames)
    {
        Description = StdText.FrameList,
        Topic = Topics.WmaFramesAndBlocks,
    };

    private IReadOnlyList<Node> BuildSuperframes() => new BlockedChildren([], stream.Superframes.Count, Superframe);

    private IReadOnlyList<Node> BuildFrames() => new BlockedChildren([], stream.Frames.Count, Frame);

    public Node Superframe(int index)
    {
        SuperframeRecord record = stream.Superframes[index];
        var map = new ByteMap(record, stream.Store);
        int align = parameters.BlockAlign;
        int bytes = record.Length;
        string value = record.State switch
        {
            SuperframeState.Short or SuperframeState.InvalidHeader => "not read",
            SuperframeState.Middle => "middle of a frame",
            _ => WmaStandardParser.Count(record.Frames, "frame", "frames"),
        };

        IReadOnlyList<ByteRange> ranges = map.Ranges(0, bytes);
        var node = new Node("Superframe", ranges.Count > 0 ? ranges[0].Offset : record.Offset, ranges.Count > 0 ? ranges[^1].End - ranges[0].Offset : 0, value)
        {
            Kind = NodeKind.Frame,
            Description = StdText.Superframe,
            Topic = Topics.WmaSuperframes,
            Ranges = ranges.Count > 1 ? ranges : null,
        };

        Node Bits(string name, int a, int b, NodeValue? v, string description, string? topic = null) => BitsOf(map, name, a, b, v, description, topic);
        Node Bytes(string name, int a, int b, NodeValue? v, string description, string? topic = null)
        {
            IReadOnlyList<ByteRange> r = map.Ranges(a, b);
            return r.Count == 0
                ? new Node(name, map.OffsetOf(a), 0, v) { Description = description, Topic = topic }
                : FrameLayout.Cover(name, r, v, description, topic);
        }

        int w = parameters.BitOffsetBits;
        if (parameters.UsesBitReservoir && bytes == align)
        {
            node.Add(Bits("Superframe Index", 0, 4, NodeValue.Integer(record.Index), StdText.SuperframeIndex));
            node.Add(Bits("Frames in Superframe", 4, 8, NodeValue.Integer(record.Count), StdText.FramesInSuperframe));
        }

        if (record.BitOffset >= 0)
        {
            node.Add(Bits("Last Frame Bit Offset", 8, 8 + w, NodeValue.Integer(record.BitOffset, "bits"), StdText.LastFrameBitOffset, Topics.WmaBitReservoir));
            if (record.State == SuperframeState.Read && record.BitOffset > 0)
            {
                node.Add(Bits("Frame End", 8 + w, 8 + w + record.BitOffset, NodeValue.Integer(record.BitOffset, "bits"), StdText.FrameEnd, Topics.WmaBitReservoir));
            }
        }

        if (record.State == SuperframeState.Middle)
        {
            node.Add(Bits("Frame Middle", 8, 8 * align, NodeValue.Integer(8L * (align - 1), "bits"), StdText.FrameMiddle, Topics.WmaBitReservoir));
        }

        for (int i = record.FirstFrame; i < record.FirstFrame + record.Frames; i++)
        {
            FrameRecord frame = stream.Frames[i];
            if (!frame.IsSpanning)
            {
                string frameValue = frame.IsValid ? WmaStandardParser.Count(frame.Blocks, "block", "blocks") : "invalid";
                node.Add(Bits("Frame Data", frame.StartBit, frame.StartBit + frame.Bits, frameValue, StdText.FrameData, Topics.WmaFramesAndBlocks));
            }
        }

        if (record.State == SuperframeState.Read)
        {
            if (parameters.UsesBitReservoir && record.Reservoir > 0 && record.EndBit >= 0)
            {
                node.Add(Bytes("Bit Reservoir", record.EndBit / 8, align, NodeValue.Integer(record.Reservoir, "bytes"), StdText.BitReservoir, Topics.WmaBitReservoir));
            }
            else if (!parameters.UsesBitReservoir && record.EndBit >= 0 && (record.EndBit + 7) / 8 < align)
            {
                int start = (record.EndBit + 7) / 8;
                node.Add(Bytes("Padding", start, align, NodeValue.Integer(align - start, "bytes"), StdText.Padding));
            }
        }

        return node;
    }

    // A node over bits [a, b) of one media object, with ranges and no bit position where the object's fragments are apart.
    private static Node BitsOf(ByteMap map, string name, int a, int b, NodeValue? value, string description, string? topic)
    {
        int first = a / 8;
        int end = b == a ? (a % 8 == 0 ? first : first + 1) : (b + 7) / 8;
        IReadOnlyList<ByteRange> ranges = map.Ranges(first, end);
        if (ranges.Count <= 1)
        {
            return Node.FromBits(name, map.OffsetOf(first), a % 8, (a % 8) + (b - a), value, NodeKind.Detail, description, topic);
        }

        return FrameLayout.Cover(name, ranges, value, description, topic);
    }

    public Node Frame(int index)
    {
        FrameRecord frame = stream.Frames[index];
        var layout = new FrameLayout(stream, frame);
        int start = frame.BufferStart;
        string value = frame.IsValid
            ? $"{WmaStandardParser.Count(frame.Blocks, "block", "blocks")}, {Counts.Of(frame.Bits, "bit", "bits")}"
            : $"invalid: {StdText.Reason(frame.Fault)}";
        return layout.Field("Frame", start, start + frame.Bits, value, StdText.Frame, Topics.WmaFramesAndBlocks, NodeKind.Frame, isWholeBytes: true, build: () => Blocks(frame, layout));
    }

    private IReadOnlyList<Node> Blocks(FrameRecord frame, FrameLayout layout)
    {
        if (!stream.HasBytes(frame))
        {
            return [];
        }

        FrameBuffer buffer = stream.BuildBuffer(frame, stream.StoredBytes);
        var bits = new Bits(buffer.Data, buffer.Total) { Position = buffer.Start };
        FrameState state = FrameState.Unpack(frame.State);
        var trace = new List<TraceBlock>();
        StdFrameReader.Read(parameters, bits, ref state, null, trace, out _);
        var nodes = new List<Node>(trace.Count);
        foreach (TraceBlock block in trace)
        {
            nodes.Add(BlockNode(block, layout));
        }

        return nodes;
    }

    private Node BlockNode(TraceBlock block, FrameLayout layout)
    {
        string size = block.SizeIndex >= 0 ? WmaStandardParser.Count(FrameLength >> block.SizeIndex, "sample", "samples") : "1 sample";
        string value = size + (block.IsMidSide ? ", mid/side" : string.Empty) + (block.HasCodedChannel ? string.Empty : ", not coded") + (block.IsInvalid ? ", invalid" : string.Empty);
        Node node = layout.Field("Block", block.Start, Math.Max(block.Start, block.End), value, StdText.Block, Topics.WmaFramesAndBlocks, isWholeBytes: true);
        foreach (TraceField field in block.Fields)
        {
            node.Add(FieldNode(field, layout));
        }

        return node;
    }

    private Node FieldNode(TraceField f, FrameLayout layout)
    {
        string channel = (f.Channel + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        NodeValue SizeValue() => NodeValue.Integer(f.A >= 0 ? FrameLength >> f.A : 1, "samples");
        return f.Kind switch
        {
            FieldKind.PreviousSize => layout.Field("Previous Block Size", f.Start, f.End, SizeValue(), StdText.PreviousBlockSize, Topics.WmaFramesAndBlocks),
            FieldKind.Size => layout.Field("Block Size", f.Start, f.End, SizeValue(), StdText.BlockSize, Topics.WmaFramesAndBlocks),
            FieldKind.NextSize => layout.Field("Next Block Size", f.Start, f.End, SizeValue(), StdText.NextBlockSize, Topics.WmaFramesAndBlocks),
            FieldKind.MidSide => layout.Field("Mid/Side Stereo", f.Start, f.End, NodeValue.Boolean(f.A != 0), StdText.MidSide, Topics.MidSideStereo),
            FieldKind.Coded => layout.Field($"Channel {channel} Coded", f.Start, f.End, NodeValue.Boolean(f.A != 0), StdText.ChannelCoded, null),
            FieldKind.Gain => layout.Field("Total Gain", f.Start, f.End, NodeValue.Integer(f.A), StdText.TotalGain, null),
            FieldKind.NoiseFlags => layout.Field("Noise Substitution", f.Start, f.End, $"{f.A.ToString(System.Globalization.CultureInfo.InvariantCulture)} of {WmaStandardParser.Count(f.B, "high band", "high bands")}", StdText.NoiseSubstitution, Topics.NoiseSubstitution),
            FieldKind.NoiseLevels => layout.Field("Noise Levels", f.Start, f.End, NodeValue.Integer(f.A, "bands"), StdText.NoiseLevels, Topics.NoiseSubstitution),
            FieldKind.ExponentsSent => layout.Field("Exponents Sent", f.Start, f.End, NodeValue.Boolean(f.A != 0), StdText.ExponentsSent, null),
            FieldKind.Exponents => layout.Field($"Exponents (channel {channel})", f.Start, f.End,
                parameters.UsesScaleFactorExponents ? "scale factor codes" : "line spectral pairs",
                parameters.UsesScaleFactorExponents ? StdText.ExponentsScaleFactors : StdText.ExponentsLineSpectralPairs, null),
            _ => layout.Field($"Coefficients (channel {channel})", f.Start, f.End, $"{f.A.ToString(System.Globalization.CultureInfo.InvariantCulture)} non-zero, last at {f.B.ToString(System.Globalization.CultureInfo.InvariantCulture)}", StdText.Coefficients, null),
        };
    }
}
