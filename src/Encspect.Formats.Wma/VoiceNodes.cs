using Encspect.Core;

namespace Encspect.Formats.Wma;

// Builds the nodes of the packets and the superframes from the records the walk kept: one node at a time, with its children built on demand,
// so a long stream's tree is never held whole. A node reaches the bytes through the file ranges of the packets, never through their data.
internal sealed class VoiceNodes
{
    private static byte[] s_zeros = new byte[4096];

    private readonly VoiceStream _stream;
    private readonly VoiceParameters _p;

    public VoiceNodes(VoiceStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _p = stream.Parameters;
    }

    // The file offset of the first byte of a superframe, for the series.
    public long SuperframeOffset(int index) => SuperframeNode(index).Offset;

    private readonly record struct Piece(int Start, int End, int Packet, int PacketBit);

    private sealed class Maps(VoiceNodes owner)
    {
        private readonly Dictionary<int, MappedBytes> _maps = [];

        public MappedBytes Packet(int index)
        {
            if (!_maps.TryGetValue(index, out MappedBytes? map))
            {
                map = owner.MapOf(index);
                _maps[index] = map;
            }

            return map;
        }
    }

    private MappedBytes MapOf(int index)
    {
        VoicePacket packet = _stream.PacketRecords[index];
        var ranges = new ByteRange[packet.RangeCount];
        for (int i = 0; i < ranges.Length; i++)
        {
            ranges[i] = _stream.Ranges[packet.RangeStart + i];
        }

        byte[] zeros = Volatile.Read(ref s_zeros);
        if (zeros.Length < packet.Length)
        {
            zeros = new byte[Math.Max(packet.Length, zeros.Length * 2)];
            Volatile.Write(ref s_zeros, zeros);
        }

        return new MappedBytes(zeros.AsMemory(0, packet.Length), ranges);
    }

    // ---- packets ----

    public Node PacketNode(int index)
    {
        VoicePacket packet = _stream.PacketRecords[index];
        ByteRange first = _stream.Ranges[packet.RangeStart];
        ByteRange last = _stream.Ranges[packet.RangeStart + packet.RangeCount - 1];
        NodeValue value = packet.Kind switch
        {
            VoicePacketKind.Header => Counts.Of(packet.OwnCount + (packet.TailSuperframe >= 0 ? 1 : 0), "superframe", "superframes"),
            VoicePacketKind.Headerless => "no header, " + Counts.Of(packet.OwnCount + (packet.TailSuperframe >= 0 ? 1 : 0), "superframe", "superframes"),
            VoicePacketKind.InvalidHeader => "invalid header",
            _ => "not read",
        };
        IReadOnlyList<ByteRange>? ranges = packet.RangeCount > 1 ? [.. Enumerable.Range(0, packet.RangeCount).Select(i => _stream.Ranges[packet.RangeStart + i])] : null;
        return new Node("Packet", first.Offset, last.End - first.Offset, value, () => PacketChildren(index))
        {
            Kind = NodeKind.Frame,
            Ranges = ranges,
            Description = VoiceExplain.Packet,
            Topic = Topics.WmaVoice,
        };
    }

    private IReadOnlyList<Node> PacketChildren(int index)
    {
        VoicePacket packet = _stream.PacketRecords[index];
        var children = new List<Node>();
        if (packet.Kind == VoicePacketKind.NotRead)
        {
            return children;
        }

        var maps = new Maps(this);
        MappedBytes map = maps.Packet(index);
        int bits = packet.Length * 8;
        int at = 0;

        void Part(string name, int start, int end, NodeValue? value, string description, string? topic = null)
        {
            end = Math.Min(end, bits);
            if (end > start && start < bits)
            {
                children.Add(Node.FromBits(name, map, start, end, value, NodeKind.Detail, description, topic));
            }
        }

        void Unread(int start, int end) => Part("Unread Bits", start, end, NodeValue.Integer(Math.Max(0, Math.Min(end, bits) - start), "bits"), VoiceExplain.UnreadBits);

        if (packet.Kind == VoicePacketKind.Headerless)
        {
            Unread(0, packet.BodyStart);
            at = packet.BodyStart;
        }
        else
        {
            Part("Packet Sequence Number", 0, 4, NodeValue.Integer(packet.Sequence), VoiceExplain.PacketSequenceNumber, Topics.WmaVoice);
            Part("Has Residual LSPs", 4, 5, NodeValue.Boolean(packet.HasResidualLsps), VoiceExplain.HasResidualLsps, Topics.WmaVoice);
            if (packet.Kind == VoicePacketKind.InvalidHeader)
            {
                Unread(packet.HeaderBits, bits);
                return children;
            }

            int valuesEnd = 5 + (6 * packet.ValueCount);
            Part("Superframes in Packet", 5, valuesEnd, NodeValue.Integer(packet.Count), VoiceExplain.SuperframesInPacket, Topics.WmaVoice);
            Part("Spillover Bits", valuesEnd, packet.HeaderBits, NodeValue.Integer(packet.Spillover, "bits"), VoiceExplain.SpilloverBits, Topics.WmaVoice);
            at = packet.HeaderBits;
            switch (packet.Lead)
            {
                case VoiceLead.Finished:
                    {
                        VoiceSuperframe sf = _stream.SuperframeRecords[packet.Finished];
                        int used = Math.Clamp(sf.Needed - sf.SpillAt, 0, packet.LeadBits);
                        Part("Superframe End", at, at + used, NodeValue.Integer(used, "bits"), VoiceExplain.SuperframeEnd, Topics.WmaVoiceSuperframes);
                        Part("Padding", at + used, at + packet.LeadBits, NodeValue.Integer(packet.LeadBits - used, "bits"), VoiceExplain.Padding, Topics.WmaVoiceSuperframes);
                        at += packet.LeadBits;
                        break;
                    }

                case VoiceLead.FinishedNotCopied:
                    Unread(at, at + packet.LeadBits);
                    at += packet.LeadBits;
                    break;
                case VoiceLead.Skipped:
                    Part("Skipped Spillover", at, at + packet.Spillover, NodeValue.Integer(packet.Spillover, "bits"), VoiceExplain.SkippedSpillover, Topics.WmaVoiceSuperframes);
                    at += packet.Spillover;
                    break;
            }

            if (packet.ResyncBits > 0)
            {
                Unread(at, at + packet.ResyncBits);
                at += packet.ResyncBits;
            }
        }

        for (int i = 0; i < packet.OwnCount; i++)
        {
            VoiceSuperframe sf = _stream.SuperframeRecords[packet.FirstOwn + i];
            if (sf.Start >= bits)
            {
                break;
            }

            if (sf.Start > at)
            {
                Unread(at, sf.Start);
            }

            int end = sf.Start + sf.Needed;
            Part("Superframe Data", sf.Start, end, sf.Outcome == VoiceOutcome.Read ? "3 frames" : "invalid", VoiceExplain.SuperframeData, Topics.WmaVoiceSuperframes);
            at = Math.Min(end, bits);
        }

        if (packet.Tail != VoiceTail.None)
        {
            int start = packet.TailStart;
            int end = packet.TailStart + packet.TailLength;
            if (start > at)
            {
                Unread(at, start);
            }

            if (packet.Tail == VoiceTail.Refused)
            {
                Unread(start, end);
            }
            else if (packet.TailSuperframe < 0)
            {
                Part("Unfinished Superframe", start, end, NodeValue.Integer(packet.TailLength, "bits"), VoiceExplain.UnfinishedSuperframe, Topics.WmaVoiceSuperframes);
            }
            else
            {
                VoiceSuperframe sf = _stream.SuperframeRecords[packet.TailSuperframe];
                if (sf.Outcome == VoiceOutcome.Read)
                {
                    int used = Math.Min(sf.Needed, packet.TailLength);
                    Part("Superframe Start", start, start + used, NodeValue.Integer(used, "bits"), VoiceExplain.SuperframeStart, Topics.WmaVoiceSuperframes);
                    Part("Padding", start + used, end, NodeValue.Integer(packet.TailLength - used, "bits"), VoiceExplain.Padding, Topics.WmaVoiceSuperframes);
                }
                else if (sf.Needed > packet.TailLength && sf.SpillBits > 0)
                {
                    Part("Superframe Start", start, end, NodeValue.Integer(packet.TailLength, "bits"), VoiceExplain.SuperframeStart, Topics.WmaVoiceSuperframes);
                }
                else if (sf.Needed > packet.TailLength)
                {
                    Part("Unfinished Superframe", start, end, NodeValue.Integer(packet.TailLength, "bits"), VoiceExplain.UnfinishedSuperframe, Topics.WmaVoiceSuperframes);
                }
                else
                {
                    int used = Math.Min(sf.Needed, packet.TailLength);
                    Part("Unfinished Superframe", start, start + used, NodeValue.Integer(used, "bits"), VoiceExplain.UnfinishedSuperframe, Topics.WmaVoiceSuperframes);
                    Part("Padding", start + used, end, NodeValue.Integer(packet.TailLength - used, "bits"), VoiceExplain.Padding, Topics.WmaVoiceSuperframes);
                }
            }

            at = Math.Min(end, bits);
        }

        if (at < bits)
        {
            Unread(at, bits);
        }

        return children;
    }

    // ---- superframes ----

    private Piece[] PiecesOf(in VoiceSuperframe sf)
    {
        if (!sf.IsCached)
        {
            return [new Piece(0, sf.Available, sf.Packet, sf.Start)];
        }

        var pieces = new List<Piece>(2);
        if (sf.TailBits > 0)
        {
            pieces.Add(new Piece(0, sf.TailBits, sf.Packet, sf.Start));
        }

        if (sf.SpillBits > 0)
        {
            pieces.Add(new Piece(sf.SpillAt, sf.SpillAt + sf.SpillBits, sf.SpillPacket, sf.SpillStart));
        }

        return [.. pieces];
    }

    // The file offset where a value that has no bytes lies: the byte of the last bit the superframe has from the file.
    private long AnchorOf(in VoiceSuperframe sf, Piece[] pieces, Maps maps)
    {
        if (pieces.Length > 0)
        {
            Piece last = pieces[^1];
            int bit = last.PacketBit + Math.Max(0, last.End - last.Start);
            return maps.Packet(last.Packet).OffsetOf(Math.Min(bit >> 3, maps.Packet(last.Packet).Length));
        }

        VoicePacket tail = sf.Packet >= 0 ? _stream.PacketRecords[sf.Packet] : default;
        if (sf.Packet >= 0)
        {
            return maps.Packet(sf.Packet).OffsetOf(Math.Min(sf.Start >> 3, Math.Max(0, tail.Length - 1)));
        }

        return 0;
    }

    // A field of the superframe, bits [start, end) counted from the superframe's first bit, wherever the bits lie in the file.
    private Node Field(in VoiceSuperframe sf, Piece[] pieces, Maps maps, string name, int start, int end, NodeValue? value, string description, NodeKind kind = NodeKind.Detail, string? topic = null)
    {
        var parts = new List<(int Packet, int Bit, int Count)>(2);
        foreach (Piece piece in pieces)
        {
            int s = Math.Max(start, piece.Start);
            int e = Math.Min(end, piece.End);
            if (s < e)
            {
                parts.Add((piece.Packet, piece.PacketBit + (s - piece.Start), e - s));
            }
        }

        if (parts.Count == 0 && start == end)
        {
            foreach (Piece piece in pieces)
            {
                if (start >= piece.Start && start <= piece.End)
                {
                    parts.Add((piece.Packet, piece.PacketBit + (start - piece.Start), 0));
                    break;
                }
            }
        }

        if (parts.Count == 0)
        {
            return new Node(name, AnchorOf(sf, pieces, maps), 0, value) { Kind = kind, Description = description, Topic = topic };
        }

        if (parts.Count == 1)
        {
            (int packet, int bit, int count) = parts[0];
            MappedBytes map = maps.Packet(packet);
            int limit = map.Length * 8;
            bit = Math.Min(bit, limit);
            return Node.FromBits(name, map, bit, Math.Min(bit + count, limit), value, kind, description, topic);
        }

        var ranges = new List<ByteRange>();
        foreach ((int packet, int bit, int count) in parts)
        {
            MappedBytes map = maps.Packet(packet);
            int first = bit >> 3;
            int endByte = Math.Min((bit + count + 7) >> 3, map.Length);
            foreach (ByteRange range in map.FileRanges(first, endByte))
            {
                if (ranges.Count > 0 && ranges[^1].End == range.Offset)
                {
                    ranges[^1] = new ByteRange(ranges[^1].Offset, ranges[^1].Length + range.Length);
                }
                else
                {
                    ranges.Add(range);
                }
            }
        }

        return new Node(name, ranges[0].Offset, ranges[^1].End - ranges[0].Offset, value)
        {
            Kind = kind,
            Ranges = ranges.Count > 1 ? ranges : null,
            Description = description,
            Topic = topic,
        };
    }

    public Node SuperframeNode(int index)
    {
        VoiceSuperframe sf = _stream.SuperframeRecords[index];
        var maps = new Maps(this);
        Piece[] pieces = PiecesOf(sf);
        int end = sf.IsCached ? sf.Needed : Math.Min(sf.Needed, sf.Available);
        Node whole = Field(sf, pieces, maps, "Superframe", 0, Math.Max(end, 0), SuperframeValue(sf), VoiceExplain.Superframe, NodeKind.Frame, Topics.WmaVoiceSuperframes);
        // The superframe node itself is a range of bytes, with no bit position.
        return new Node("Superframe", whole.Offset, whole.Length, SuperframeValue(sf), () => SuperframeChildren(index))
        {
            Kind = NodeKind.Frame,
            Ranges = whole.Ranges,
            Description = VoiceExplain.Superframe,
            Topic = Topics.WmaVoiceSuperframes,
        };
    }

    private static NodeValue SuperframeValue(in VoiceSuperframe sf) => sf.Outcome switch
    {
        VoiceOutcome.Read => $"3 frames, {Counts.Of(sf.Samples, "sample", "samples")}",
        VoiceOutcome.Music => "invalid: music",
        VoiceOutcome.SampleCount => "invalid: more than 480 samples",
        VoiceOutcome.FrameType => "invalid: a frame type code with no type",
        _ => "invalid: it needs more bits than it has",
    };

    private IReadOnlyList<Node> SuperframeChildren(int index)
    {
        VoiceSuperframe sf = _stream.SuperframeRecords[index];
        var maps = new Maps(this);
        Piece[] pieces = PiecesOf(sf);
        var children = new List<Node>();
        int available = sf.Available;

        // A field the reading reached gets a node unless it began past the bits the superframe had (those of a cached one are values with no bytes).
        bool Reached(int start) => start < sf.Needed && start < available;

        void Add(string name, int start, int length, NodeValue value, string description, string? topic = Topics.WmaVoiceSuperframes)
        {
            if (Reached(start))
            {
                children.Add(Field(sf, pieces, maps, name, start, start + length, value, description, NodeKind.Detail, topic));
            }
        }

        int at = 0;
        Add("Is Speech", at, 1, NodeValue.Boolean(sf.Outcome != VoiceOutcome.Music), VoiceExplain.IsSpeech);
        if (sf.Outcome == VoiceOutcome.Music)
        {
            return children;
        }

        at = 1;
        Add("Has Sample Count", at, 1, NodeValue.Boolean(sf.HasSampleCount), VoiceExplain.HasSampleCount);
        at = 2;
        if (sf.HasSampleCount)
        {
            Add("Sample Count", at, 12, NodeValue.Integer(sf.SampleCount, "samples"), VoiceExplain.SampleCount);
            at += 12;
        }

        if (sf.Outcome == VoiceOutcome.SampleCount)
        {
            return children;
        }

        if (sf.HasResidualLsps)
        {
            Add("LSPs", at, _p.ResidualLspBits, $"{_p.LspCount} LSPs for the three frames", VoiceExplain.Lsps, Topics.WmaVoiceFrames);
            at += _p.ResidualLspBits;
        }

        for (int i = 0; i < sf.Frames; i++)
        {
            VoiceFrame frame = sf[i];
            if (Reached(at))
            {
                children.Add(FrameNode(sf, pieces, maps, frame, at));
            }

            at += frame.Bits;
        }

        if (sf.Outcome == VoiceOutcome.Read)
        {
            int length = sf.AdditionalGroups > 0 ? 5 + (10 * sf.AdditionalGroups) : 1;
            Add("Additional Data", at, length, sf.AdditionalGroups > 0 ? NodeValue.Integer(10 * sf.AdditionalGroups, "bits") : "none", VoiceExplain.AdditionalData);
        }
        else
        {
            int needed = sf.Needed - at;
            if (needed > 0 && at < available)
            {
                children.Add(Field(sf, pieces, maps, "Unread Bits", at, Math.Min(sf.Needed, available), NodeValue.Integer(needed, "bits"), VoiceExplain.UnreadBits, NodeKind.Detail, Topics.WmaVoiceSuperframes));
            }
        }

        return children;
    }

    private Node FrameNode(in VoiceSuperframe sf, Piece[] pieces, Maps maps, in VoiceFrame frame, int start)
    {
        int available = sf.Available;
        byte[] descriptor = VoiceTables.FrameDescriptors[frame.Type];
        int end = start + frame.Bits;
        VoiceSuperframe copy = sf;
        VoiceFrame frameCopy = frame;
        return BuildFrame();

        Node BuildFrame()
        {
            Node node = Field(copy, pieces, maps, "Frame", start, Math.Min(end, available), $"Type {frameCopy.Type}: {VoiceNames.Frame(frameCopy.Type)}", VoiceExplain.Frame, NodeKind.Detail, Topics.WmaVoiceFrames);
            var children = new List<Node>();
            void Add(string name, int s, int length, NodeValue value, string description)
            {
                if (s < available)
                {
                    children.Add(Field(copy, pieces, maps, name, s, Math.Min(s + length, available), value, description, NodeKind.Detail, Topics.WmaVoiceFrames));
                }
            }

            int at = start;
            if (!copy.HasResidualLsps)
            {
                Add("LSPs", at, _p.IndependentLspBits, $"{_p.LspCount} LSPs", VoiceExplain.Lsps);
                at += _p.IndependentLspBits;
            }

            int codeBits = VoiceTables.FrameTypeCodeLengths[_p.SlotOfType[frameCopy.Type]];
            Add("Frame Type", at, codeBits, NodeValue.Integer(frameCopy.Type), VoiceExplain.FrameType);
            at += codeBits;
            if (descriptor[2] == 1)
            {
                Add("Pitch", at, _p.PitchBits, NodeValue.Integer(frameCopy.Pitch, "samples"), VoiceExplain.Pitch);
                at += _p.PitchBits;
            }

            if (descriptor[3] == 0)
            {
                Add("Silence Gain", at, 8, NodeValue.Integer(frameCopy.Parameter), VoiceExplain.SilenceGain);
                at += 8;
            }

            if (descriptor[3] == 2)
            {
                int width = (frameCopy.Flags & 1) != 0 ? 8 : 6;
                Add("Window Index", at, width, NodeValue.Integer(frameCopy.Parameter), VoiceExplain.WindowIndex);
                at += width;
            }

            int[] widths = BlockWidths(frameCopy, descriptor);
            if (at < available)
            {
                Node blocks = Field(copy, pieces, maps, "Blocks", at, Math.Min(end, available), NodeValue.Integer(widths.Length, "blocks"), VoiceExplain.Blocks, NodeKind.Detail, Topics.WmaVoiceFrames);
                var list = new List<Node>();
                int blockAt = at;
                for (int b = 0; b < widths.Length; b++)
                {
                    if (blockAt < available || widths[b] == 0)
                    {
                        list.Add(Field(copy, pieces, maps, $"Block {b + 1}", blockAt, Math.Min(Math.Min(blockAt + widths[b], end), available), NodeValue.Integer(widths[b], "bits"), VoiceExplain.Block, NodeKind.Detail, Topics.WmaVoiceFrames));
                    }

                    blockAt += widths[b];
                }

                children.Add(WithChildren(blocks, list));
            }

            return WithChildren(node, children);
        }
    }

    private static Node WithChildren(Node node, List<Node> children)
    {
        if (children.Count == 0)
        {
            return node;
        }

        var result = new Node(node.Name, node.Offset, node.Length, node.Value)
        {
            Kind = node.Kind,
            Ranges = node.Ranges,
            Description = node.Description,
            Topic = node.Topic,
        };
        if (node.BitOffset is { } bit)
        {
            return CopyBits(node, children);
        }

        foreach (Node child in children)
        {
            result.Add(child);
        }

        return result;
    }

    // A node with bits keeps them: rebuilt through FromBits at the same place.
    private static Node CopyBits(Node node, List<Node> children)
    {
        Node result = Node.FromBits(node.Name, node.Offset, node.BitOffset!.Value, node.BitOffset.Value + node.BitLength!.Value, node.Value, node.Kind, node.Description, node.Topic);
        foreach (Node child in children)
        {
            result.Add(child);
        }

        return result;
    }

    // The width of each block of a frame, with its pitch field.
    private int[] BlockWidths(in VoiceFrame frame, byte[] descriptor)
    {
        int blocks = descriptor[0];
        int adaptive = descriptor[2];
        int fixedKind = descriptor[3];
        int[] widths = new int[blocks];
        if (fixedKind == 2)
        {
            bool extended = (frame.Flags & 1) != 0;
            bool lag = (frame.Flags & 2) != 0;
            widths[0] = (extended ? 10 : 12) + (lag ? 5 : 4) + 1 + 7;
            widths[1] = 12 + (lag ? 3 : 4) + 1 + 7;
            return widths;
        }

        int q = 5 - descriptor[1];
        for (int n = 0; n < blocks; n++)
        {
            int width = adaptive == 2 ? (n == 0 ? _p.BlockPitchBits : _p.BlockDeltaPitchBits) : 0;
            width += fixedKind switch
            {
                1 => 14,
                3 => 12 + ((5 + descriptor[4]) * q),
                _ => 0,
            };
            widths[n] = width;
        }

        return widths;
    }
}
