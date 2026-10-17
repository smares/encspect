using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Asf;

// A packet as the first pass found it: where it lies and the fields its header holds, 48 bytes.
// The nodes are built from it (and from the payload records after FirstPayload) when they are read.
internal struct PacketRecord
{
    public long Offset;
    public int Length;
    public int FirstPayload;
    public uint PacketLengthField;
    public uint Sequence;
    public uint PaddingLength;
    public uint SendTime;
    public int ActualPadding;
    public ushort Duration;
    public ushort Issues;
    public ushort PayloadCount;
    public byte EccFlags;
    public byte EccLength;
    public byte EccByte0;
    public byte EccByte1;
    public byte LengthTypeFlags;
    public byte PropertyFlags;
    public byte PayloadFlagsByte;

    public readonly bool IsMultiple => (LengthTypeFlags & 1) != 0;

    public readonly bool HasEcc => (EccFlags & 0x80) != 0;
}

// A payload of a packet, 32 bytes. Its place in the packet follows from the packet's length types.
internal struct PayloadRecord
{
    public uint ObjectNumber;
    public uint OffsetOrTime;
    public uint ReplicatedLength;
    public uint ObjectSize;
    public uint PresentationTime;
    public int DataLength;
    public ushort SubPayloads;
    public byte StreamByte;
    public byte Flags;
    public byte TimeDelta;

    public readonly bool IsCompressed => (Flags & (byte)PayloadFlags.Compressed) != 0;
}

internal sealed class StreamFigures
{
    public long Payloads { get; set; }

    public long PayloadBytes { get; set; }

    public long Objects { get; set; }

    public long Fragmented { get; set; }

    public long KeyFrames { get; set; }

    public long CompressedPayloads { get; set; }

    public long MaxObjectSize { get; set; }

    public long MinObjectSize { get; set; } = long.MaxValue;

    public long FirstTime { get; set; } = -1;

    public long LastTime { get; set; } = -1;
}

// What the scan of the packets found, for the statistics and the checks.
internal sealed class DataFigures
{
    public ChunkedList<PacketRecord> Packets { get; } = new();

    public ChunkedList<PayloadRecord> Payloads { get; } = new();

    // Packets read, including those past the records' limit.
    public long PacketCount { get; set; }

    public long PayloadCount { get; set; }

    public long PacketBytes { get; set; }

    public long PayloadBytes { get; set; }

    public long PaddingBytes { get; set; }

    public long MultiplePacketCount { get; set; }

    public long EccPackets { get; set; }

    public long CompressedPayloads { get; set; }

    public long SubPayloads { get; set; }

    public long FirstSendTime { get; set; } = -1;

    public long LastSendTime { get; set; } = -1;

    public long LastEnd { get; set; }

    public long DetailedEnd { get; set; }

    public long UnusedStart { get; set; } = -1;

    public Dictionary<int, StreamFigures> Streams { get; } = [];

    public Dictionary<PacketIssues, (long Count, long Offset)> Issues { get; } = [];

    public SortedDictionary<int, long> Sizes { get; } = [];
}

public sealed partial class AsfParser
{
    // Packets are described in records up to this count; a data object of more is still counted
    // and its media objects still go to the codec, but the packets after the limit are one node.
    internal const int MaxDetailedPackets = 1 << 16;

    // The largest packet read into memory.
    internal const int MaxPacketSize = 1 << 24;

    internal const int DataObjectHeader = 50;

    // ---- Data Object (section 5.1) ----

    private static void ParseDataObject(State state, ObjectHeader header, long end, bool sizeUnknown)
    {
        (Node node, Fields f) = Begin(state, state.File, header, end, "Data Object", Explain.DataObject, Topics.AsfPackets);
        state.DataObjects++;
        state.DataNode ??= node;
        if (state.DataObjects > 1)
        {
            state.Findings.AddNow(Severity.Warning, "asf.duplicate-data", $"A second Data Object starts at 0x{header.Offset:X}; the specification allows one, and only the first is analyzed.", node);
        }

        Guid fileId = default;
        ulong total = 0;
        Node? totalNode = null;
        Body(state, node, f, DataObjectHeader, () =>
        {
            fileId = f.Guid("File ID", Explain.DataFileId);
            total = f.Qword("Total Data Packets", Explain.TotalDataPackets);
            totalNode = node.Children[^1];
            ushort reserved = f.Word("Reserved", Explain.DataReserved, v => NodeValue.Hex(v, 4));
            if (reserved != 0x0101)
            {
                state.Findings.AddNow(Severity.Info, "asf.data-reserved", $"The Data Object's reserved field is 0x{reserved:X4}; the specification sets it to 0x0101.", node.Children[^1]);
            }
        }, coversRest: false);

        if (sizeUnknown)
        {
            if (state.FilePropertiesNode is null || !state.IsBroadcast)
            {
                state.Findings.AddNow(Severity.Warning, "asf.data-size-zero", "The Data Object states a size of 0, which is only valid for a file in the process of being created (the broadcast flag, section 5.1).", node);
            }
        }

        if (state.DataObjects > 1 || node.Length < DataObjectHeader)
        {
            if (node.Length > DataObjectHeader)
            {
                node.Add(new Node("Data Packets", header.Offset + DataObjectHeader, node.Length - DataObjectHeader, NodeValue.Integer(node.Length - DataObjectHeader, "bytes"))
                {
                    Description = Explain.UnreadPackets,
                    Topic = Topics.AsfPackets,
                });
            }

            return;
        }

        if (state.FilePropertiesNode is not null && state.FileId != fileId)
        {
            state.Findings.AddNow(Severity.Warning, "asf.file-id",
                $"The Data Object's file ID {AsfGuids.Text(fileId)} differs from the File Properties Object's {AsfGuids.Text(state.FileId)}; the specification makes them identical (section 5.1).", node.Children[2]);
        }

        long start = header.Offset + DataObjectHeader;
        state.PacketsStart = start;
        state.PacketsEnd = end;
        state.PacketSize = PacketSizeOf(state);
        state.PacketSizeIsFixed = state.PacketSize > 0 && state.MinPacketSize == state.MaxPacketSize;
        DataFigures figures = ScanPackets(state, node, start, end);
        state.Figures = figures;
        AddPacketNodes(state, node, figures, start, end);
        node.Value = Plural(figures.PacketCount, "packet", "packets");

        if (state.FilePropertiesNode is not null && !state.IsBroadcast && (long)state.PacketCount != figures.PacketCount)
        {
            state.Findings.AddNow(Severity.Warning, "asf.packet-count",
                $"The File Properties Object counts {state.PacketCount} data packets, but the Data Object holds {figures.PacketCount}.", state.PacketCountNode!);
        }

        Node? counted = totalNode;
        if (!state.IsBroadcast && (long)total != figures.PacketCount && counted is not null)
        {
            state.Findings.AddNow(Severity.Warning, "asf.packet-count", $"The Data Object counts {total} packets, but it holds {figures.PacketCount}.", counted);
        }
    }

    // The size every packet has: the File Properties Object's, equal as a minimum and a maximum
    // (section 3.2), else the maximum.
    private static int PacketSizeOf(State state)
    {
        if (state.FilePropertiesNode is null)
        {
            return 0;
        }

        uint size = state.MaxPacketSize != 0 ? state.MaxPacketSize : state.MinPacketSize;
        return size > MaxPacketSize ? 0 : (int)size;
    }

    // The first pass over the Data Object: every packet's header and payload headers, as records, and
    // the figures for the statistics. Packets are read one at a time; nothing of the file is kept but
    // the records.
    private static DataFigures ScanPackets(State state, Node dataNode, long start, long end)
    {
        ByteReader reader = state.Reader;
        var figures = new DataFigures();
        int packetSize = state.PacketSize;
        if (state.FilePropertiesNode is { } && (state.MaxPacketSize > MaxPacketSize || state.MinPacketSize > MaxPacketSize))
        {
            state.Findings.AddNow(Severity.Warning, "asf.packet-size", $"The File Properties Object states a packet size above {MaxPacketSize} bytes, which is not read.", state.PacketSizeNode!);
        }

        byte[] buffer = new byte[Math.Max(packetSize, 1024)];
        var packet = new ParsedPacket();
        long position = start;
        if (packetSize == 0 && state.FilePropertiesNode is not null && end > start)
        {
            state.Findings.AddNow(Severity.Warning, "asf.packet-size", "The File Properties Object gives no packet size, so the packets can only be told apart by their own length fields.", state.PacketSizeNode ?? dataNode);
        }

        int readSize = packetSize > 0 ? packetSize : 1 << 16;
        while (position < end)
        {
            reader.CancellationToken.ThrowIfCancellationRequested();
            long remaining = end - position;

            // A packet needs at least its length type and property flags to be one.
            if (remaining < 2)
            {
                figures.UnusedStart = position;
                break;
            }

            int count = (int)Math.Min(readSize, remaining);
            if (buffer.Length < count)
            {
                buffer = new byte[count];
            }

            reader.Position = position;
            reader.Read(buffer.AsSpan(0, count));
            PacketParser.Parse(buffer.AsSpan(0, count), packetSize, packet, state.PacketSizeIsFixed);

            // A packet that states more bytes than the size every packet has (variable-size packets).
            if (!state.PacketSizeIsFixed && packet.PacketLength > count && packet.PacketLength <= MaxPacketSize && packet.PacketLength <= remaining)
            {
                count = packet.PacketLength;
                if (buffer.Length < count)
                {
                    buffer = new byte[count];
                }

                reader.Position = position;
                reader.Read(buffer.AsSpan(0, count));
                PacketParser.Parse(buffer.AsSpan(0, count), packetSize, packet, state.PacketSizeIsFixed);
            }

            int length = StepOf(state, packet, count, remaining);

            Record(state, figures, packet, position, length, remaining);
            position += length;
        }

        figures.LastEnd = position;
        return figures;
    }

    // How far to the next packet: the fixed packet size where the file has one (FFmpeg treats the difference to a
    // shorter length field as padding, asfdec_f.c), else the packet's own length.
    internal static int StepOf(State state, ParsedPacket packet, int count, long remaining)
    {
        if (state.PacketSizeIsFixed)
        {
            return (int)Math.Min(state.PacketSize, remaining);
        }

        int length = packet.PacketLength > 0 ? (int)Math.Min(packet.PacketLength, remaining) : count;
        return length < 1 ? count : length;
    }

    private static readonly PacketIssues[] AllIssues = Enum.GetValues<PacketIssues>();

    private static void Record(State state, DataFigures figures, ParsedPacket packet, long position, int length, long remaining)
    {
        figures.PacketCount++;
        figures.PacketBytes += length;
        // The bytes of the packet after what was parsed are padding, whatever the length field says (asfdec_f.c adds
        // min_pktsize - packet_length to the padding), as the Padding node shows: only the bytes the file has count, and
        // a packet cut inside its header has none.
        int padding = Math.Max(0, length - Math.Max(packet.PayloadsEnd, packet.HeaderLength));
        figures.PaddingBytes += padding;
        figures.Sizes[length] = figures.Sizes.GetValueOrDefault(length) + 1;
        if (packet.IsMultiple)
        {
            figures.MultiplePacketCount++;
        }

        if (packet.HasEcc)
        {
            figures.EccPackets++;
        }

        if ((packet.Issues & PacketIssues.Truncated) != 0 || packet.PacketLength > remaining)
        {
            packet.Issues |= PacketIssues.Truncated;
        }

        foreach (PacketIssues issue in AllIssues)
        {
            if (issue != PacketIssues.None && (packet.Issues & issue) != 0)
            {
                (long count, long first) = figures.Issues.GetValueOrDefault(issue, (0, position));
                figures.Issues[issue] = (count + 1, first);
            }
        }

        if (figures.FirstSendTime < 0)
        {
            figures.FirstSendTime = packet.SendTime;
        }

        figures.LastSendTime = packet.SendTime;

        bool detailed = figures.Packets.Count < MaxDetailedPackets;
        int firstPayload = figures.Payloads.Count;
        foreach (PayloadView payload in packet.Payloads)
        {
            figures.PayloadCount++;
            figures.PayloadBytes += payload.DataLength;
            if (payload.IsCompressed)
            {
                figures.CompressedPayloads++;
                figures.SubPayloads += payload.SubPayloads;
            }

            if (!figures.Streams.TryGetValue(payload.StreamNumber, out StreamFigures? stream))
            {
                figures.Streams[payload.StreamNumber] = stream = new StreamFigures();
            }

            stream.Payloads++;
            stream.PayloadBytes += payload.DataLength;
            if (payload.IsKeyFrame)
            {
                stream.KeyFrames++;
            }

            if (payload.IsCompressed)
            {
                stream.CompressedPayloads++;
                stream.Objects += payload.SubPayloads;
                stream.FirstTime = stream.FirstTime < 0 ? payload.OffsetOrTime : stream.FirstTime;
                stream.LastTime = payload.OffsetOrTime + ((long)payload.TimeDelta * Math.Max(0, payload.SubPayloads - 1));
            }
            else if (payload.OffsetOrTime == 0 && payload.ReplicatedLength >= 8 || payload.ReplicatedLength == 0)
            {
                // The first payload of a media object (a payload at offset 0), whose replicated data
                // gives the object's size and presentation time.
                stream.Objects++;
                if (payload.ReplicatedLength >= 8)
                {
                    stream.MaxObjectSize = Math.Max(stream.MaxObjectSize, payload.ObjectSize);
                    stream.MinObjectSize = Math.Min(stream.MinObjectSize, payload.ObjectSize);
                    stream.FirstTime = stream.FirstTime < 0 ? payload.PresentationTime : stream.FirstTime;
                    stream.LastTime = payload.PresentationTime;
                    if (payload.DataLength < payload.ObjectSize)
                    {
                        stream.Fragmented++;
                    }
                }
            }

            if (detailed)
            {
                figures.Payloads.Add(new PayloadRecord
                {
                    ObjectNumber = payload.ObjectNumber,
                    OffsetOrTime = payload.OffsetOrTime,
                    ReplicatedLength = payload.ReplicatedLength,
                    ObjectSize = payload.ObjectSize,
                    PresentationTime = payload.PresentationTime,
                    DataLength = payload.DataLength,
                    SubPayloads = (ushort)Math.Min(payload.SubPayloads, ushort.MaxValue),
                    StreamByte = payload.StreamByte,
                    Flags = (byte)payload.Flags,
                    TimeDelta = payload.TimeDelta,
                });
            }
        }

        if (detailed)
        {
            figures.Packets.Add(new PacketRecord
            {
                Offset = position,
                Length = length,
                FirstPayload = firstPayload,
                PacketLengthField = packet.PacketLengthField,
                Sequence = packet.Sequence,
                PaddingLength = packet.PaddingLength,
                SendTime = packet.SendTime,
                ActualPadding = padding,
                Duration = packet.Duration,
                Issues = (ushort)packet.Issues,
                PayloadCount = (ushort)packet.Payloads.Count,
                EccFlags = packet.EccFlags,
                EccLength = (byte)packet.EccLength,
                EccByte0 = packet.EccByte0,
                EccByte1 = packet.EccByte1,
                LengthTypeFlags = packet.LengthTypeFlags,
                PropertyFlags = packet.PropertyFlags,
                PayloadFlagsByte = packet.PayloadFlagsByte,
            });
            figures.DetailedEnd = position + length;
        }
    }

    private static void AddPacketNodes(State state, Node dataNode, DataFigures figures, long start, long end)
    {
        long packetsEnd = figures.DetailedEnd > start ? figures.DetailedEnd : start;
        int count = figures.Packets.Count;
        var children = new List<Node>();
        DataFigures captured = figures;
        Node packets = dataNode.Add(new Node("Data Packets", start, Math.Max(0, figures.LastEnd - start), NodeValue.Integer(figures.PacketCount, "packets", "packet"),
            () => new BlockedChildren(children, count, i => PacketNode(captured, i)))
        {
            Kind = NodeKind.Detail,
            Description = Explain.DataPackets,
            Topic = Topics.AsfPackets,
        });
        _ = packets;

        if (figures.PacketCount > count)
        {
            long undetailed = figures.PacketCount - count;
            var rest = new Node("Undetailed Packets", packetsEnd, figures.LastEnd - packetsEnd, NodeValue.Integer(undetailed, "packets", "packet"))
            {
                Description = Explain.UndetailedPackets,
                Topic = Topics.AsfPackets,
            };
            dataNode.Add(rest);
            state.Findings.AddNow(Severity.Info, "asf.packets-undetailed",
                $"The Data Object holds {figures.PacketCount} packets; the first {count} are described packet by packet, the {undetailed} after them are counted and their media objects still analyzed.", rest);
        }

        if (figures.UnusedStart >= 0 && figures.UnusedStart < end)
        {
            var unused = new Node("Unused Data", figures.UnusedStart, end - figures.UnusedStart, NodeValue.Integer(end - figures.UnusedStart, "bytes", "byte")) { Description = Explain.UnusedData };
            dataNode.Add(unused);
            state.Findings.AddNow(Severity.Warning, "asf.data-unused", $"{end - figures.UnusedStart} bytes at the end of the Data Object are too few for a packet.", unused);
        }

        AddDataFindings(state, dataNode, figures, start);
        AddStatistics(state, dataNode, figures, start, end);
    }

    // What a packet issue is called; null for a flag no finding is written for (a test holds every flag to one).
    internal static (Severity Severity, string Code, string Text)? Describe(PacketIssues issue) => issue switch
    {
        PacketIssues.Truncated => (Severity.Warning, "asf.packet-truncated", "ends before its header or a payload does: the file is cut short or the packet is damaged"),
        PacketIssues.PaddingTooLong => (Severity.Warning, "asf.padding-length", "states more padding than the packet holds after its header"),
        PacketIssues.PayloadOverrun => (Severity.Warning, "asf.payload-overrun", "holds a payload that extends past the packet's end"),
        PacketIssues.NoPayloads => (Severity.Warning, "asf.no-payloads", "states that it holds no payloads (section 5.2.3.3)"),
        PacketIssues.PayloadLengthType => (Severity.Warning, "asf.payload-length-type", "has multiple payloads but a payload length type of 0, which cannot hold their lengths (section 5.2.3.3)"),
        PacketIssues.StreamNumberType => (Severity.Info, "asf.stream-number-type", "has a stream number length type other than 1; the specification fixes it to a byte (section 5.2.2), and readers read a byte"),
        PacketIssues.ReplicatedLength => (Severity.Warning, "asf.replicated-length", "holds a payload whose replicated data is 2 to 7 bytes, too few for the media object's size and time (section 5.2.3.1)"),
        PacketIssues.EccLengthType => (Severity.Warning, "asf.ecc-length-type", "has an error correction length type other than 0, which the specification reserves (section 5.2.1)"),
        PacketIssues.PacketLengthMismatch => (Severity.Warning, "asf.packet-length", "states a packet length that differs from the packet size in the File Properties Object (section 8.2.13)"),
        PacketIssues.PaddingMismatch => (Severity.Warning, "asf.padding-mismatch", "states a padding length that differs from the padding its layout leaves (section 8.2.15)"),
        PacketIssues.ZeroStream => (Severity.Warning, "asf.payload-stream-zero", "holds a payload for stream 0, which is not a stream"),
        PacketIssues.EmptyPayload => (Severity.Warning, "asf.payload-empty", "holds an empty payload, or a compressed payload whose sub-payloads overrun it (section 5.2.3)"),
        PacketIssues.CompressedPayload => (Severity.Warning, "asf.compressed-payload", "holds a compressed payload (replicated data length 1) in a packet without the fields it needs (section 5.2.3.2)"),
        PacketIssues.NoPacketSize => (Severity.Warning, "asf.no-packet-size", "has no packet length field, and the File Properties Object gives no packet size"),
        _ => null,
    };

    private static void AddDataFindings(State state, Node dataNode, DataFigures figures, long start)
    {
        foreach ((PacketIssues issue, (long count, long offset)) in figures.Issues.OrderBy(i => i.Value.Offset))
        {
            if (Describe(issue) is not var (severity, code, text))
            {
                continue;
            }

            Node? node = FindPacketNode(figures, offset);
            string which = count == 1 ? $"The packet at 0x{offset:X}" : $"{count} packets, the first at 0x{offset:X},";
            state.Findings.AddNow(severity, code, $"{which} {text}.", node ?? dataNode, (int)Math.Min(count, int.MaxValue));
        }

        _ = start;
    }

    // The node of the packet at a file offset, if its record is kept.
    private static Node? FindPacketNode(DataFigures figures, long offset)
    {
        int low = 0;
        int high = figures.Packets.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            long at = figures.Packets[middle].Offset;
            if (at == offset)
            {
                return PacketNode(figures, middle);
            }

            if (at < offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return null;
    }

    // ---- The packet nodes (section 5.2) ----

    private static Node PacketNode(DataFigures figures, int index)
    {
        PacketRecord packet = figures.Packets[index];
        string value = packet.PayloadCount == 1 ? "1 payload" : $"{packet.PayloadCount} payloads";
        return new Node("Packet", packet.Offset, packet.Length, value, () => PacketChildren(figures, index))
        {
            Kind = NodeKind.Frame,
            Description = Explain.Packet,
            Topic = Topics.AsfPackets,
        };
    }

    private static List<Node> PacketChildren(DataFigures figures, int index)
    {
        PacketRecord packet = figures.Packets[index];
        var nodes = new List<Node>();
        long position = packet.Offset;
        Node Add(string name, long width, NodeValue? value, string description, string? topic = null)
        {
            var node = new Node(name, position, width, value) { Description = description, Topic = topic };
            nodes.Add(node);
            position += width;
            return node;
        }

        if (packet.HasEcc)
        {
            long flagsAt = position;
            Node ecc = Add("Error Correction Flags", 1, NodeValue.Hex(packet.EccFlags, 2), Explain.EccFlags);
            Fields.Bits(ecc, "Error Correction Data Length", flagsAt, 0, 4, NodeValue.Integer(packet.EccFlags & 0x0F, "bytes"), Explain.EccDataLength);
            Fields.Bits(ecc, "Opaque Data Present", flagsAt, 4, 1, NodeValue.Boolean((packet.EccFlags & 0x10) != 0), Explain.EccOpaque);
            Fields.Bits(ecc, "Error Correction Length Type", flagsAt, 5, 2, NodeValue.Integer((packet.EccFlags >> 5) & 3), Explain.EccLengthType);
            Fields.Bits(ecc, "Error Correction Present", flagsAt, 7, 1, NodeValue.Boolean(true), Explain.EccPresent);
            if (packet.EccLength > 0)
            {
                Node data = Add("Error Correction Data", packet.EccLength, NodeValue.Integer(packet.EccLength, "bytes", "byte"), Explain.EccData);
                if (packet.EccLength >= 2)
                {
                    data.Add("Type and Number", data.Offset, 1, NodeValue.Hex(packet.EccByte0, 2), Explain.EccTypeAndNumber);
                    data.Add("Cycle", data.Offset + 1, 1, NodeValue.Hex(packet.EccByte1, 2), Explain.EccCycle);
                }
            }
        }

        long lengthFlagsAt = position;
        Node lengthFlags = Add("Length Type Flags", 1, NodeValue.Hex(packet.LengthTypeFlags, 2), Explain.LengthTypeFlags);
        Fields.Bits(lengthFlags, "Multiple Payloads Present", lengthFlagsAt, 0, 1, NodeValue.Boolean(packet.IsMultiple), Explain.MultiplePayloadsPresent);
        Fields.Bits(lengthFlags, "Sequence Type", lengthFlagsAt, 1, 2, NodeValue.Integer((packet.LengthTypeFlags >> 1) & 3), Explain.SequenceType);
        Fields.Bits(lengthFlags, "Padding Length Type", lengthFlagsAt, 3, 2, NodeValue.Integer((packet.LengthTypeFlags >> 3) & 3), Explain.PaddingLengthType);
        Fields.Bits(lengthFlags, "Packet Length Type", lengthFlagsAt, 5, 2, NodeValue.Integer((packet.LengthTypeFlags >> 5) & 3), Explain.PacketLengthType);
        Fields.Bits(lengthFlags, "Error Correction Present", lengthFlagsAt, 7, 1, NodeValue.Boolean((packet.LengthTypeFlags & 0x80) != 0), Explain.LengthFlagsEccPresent);

        long propertyAt = position;
        Node property = Add("Property Flags", 1, NodeValue.Hex(packet.PropertyFlags, 2), Explain.PropertyFlags);
        Fields.Bits(property, "Replicated Data Length Type", propertyAt, 0, 2, NodeValue.Integer(packet.PropertyFlags & 3), Explain.ReplicatedDataLengthType);
        Fields.Bits(property, "Offset Into Media Object Length Type", propertyAt, 2, 2, NodeValue.Integer((packet.PropertyFlags >> 2) & 3), Explain.OffsetLengthType);
        Fields.Bits(property, "Media Object Number Length Type", propertyAt, 4, 2, NodeValue.Integer((packet.PropertyFlags >> 4) & 3), Explain.ObjectNumberLengthType);
        Fields.Bits(property, "Stream Number Length Type", propertyAt, 6, 2, NodeValue.Integer((packet.PropertyFlags >> 6) & 3), Explain.StreamNumberLengthType);

        int lengthWidth = PacketParser.Width((packet.LengthTypeFlags >> 5) & 3);
        int sequenceWidth = PacketParser.Width((packet.LengthTypeFlags >> 1) & 3);
        int paddingWidth = PacketParser.Width((packet.LengthTypeFlags >> 3) & 3);
        if (lengthWidth > 0)
        {
            Add("Packet Length", lengthWidth, NodeValue.Integer(packet.PacketLengthField, "bytes"), Explain.PacketLength);
        }

        if (sequenceWidth > 0)
        {
            Add("Sequence", sequenceWidth, NodeValue.Integer(packet.Sequence), Explain.Sequence);
        }

        if (paddingWidth > 0)
        {
            Add("Padding Length", paddingWidth, NodeValue.Integer(packet.PaddingLength, "bytes"), Explain.PaddingLength);
        }

        if ((packet.Issues & (ushort)PacketIssues.Truncated) != 0 && packet.Offset + packet.Length < position + 6)
        {
            nodes.RemoveAll(n => n.End > packet.Offset + packet.Length);
            return nodes;
        }

        Add("Send Time", 4, NodeValue.Integer(packet.SendTime, "ms"), Explain.SendTime);
        Add("Duration", 2, NodeValue.Integer(packet.Duration, "ms"), Explain.PacketDuration);

        int payloadLengthWidth = 0;
        if (packet.IsMultiple)
        {
            long flagsAt = position;
            Node flags = Add("Payload Flags", 1, NodeValue.Hex(packet.PayloadFlagsByte, 2), Explain.PayloadFlags);
            Fields.Bits(flags, "Number of Payloads", flagsAt, 0, 6, NodeValue.Integer(packet.PayloadFlagsByte & 0x3F), Explain.NumberOfPayloads);
            Fields.Bits(flags, "Payload Length Type", flagsAt, 6, 2, NodeValue.Integer(packet.PayloadFlagsByte >> 6), Explain.PayloadLengthType);
            payloadLengthWidth = PacketParser.Width(packet.PayloadFlagsByte >> 6);
        }

        int numberWidth = PacketParser.Width((packet.PropertyFlags >> 4) & 3);
        int offsetWidth = PacketParser.Width((packet.PropertyFlags >> 2) & 3);
        int replicatedWidth = PacketParser.Width(packet.PropertyFlags & 3);
        for (int i = 0; i < packet.PayloadCount; i++)
        {
            PayloadRecord payload = figures.Payloads[packet.FirstPayload + i];
            long start = position;
            int stream = payload.StreamByte & 0x7F;
            bool key = payload.StreamByte >= 0x80;
            var node = new Node("Payload", start, 0, $"stream {stream}{(payload.IsCompressed ? $", {Plural(payload.SubPayloads, "object", "objects")}" : string.Empty)}")
            {
                Description = payload.IsCompressed ? Explain.CompressedPayload : Explain.Payload,
                Topic = Topics.AsfPackets,
            };
            long at = start;
            Node Field(string name, long width, NodeValue? value, string description, string? topic = null)
            {
                Node field = node.Add(name, at, width, value, description, topic);
                at += width;
                return field;
            }

            Node streamByte = Field("Stream Number", 1, NodeValue.Hex(payload.StreamByte, 2), Explain.PayloadStreamNumber);
            Fields.Bits(streamByte, "Stream Number", start, 0, 7, NodeValue.Integer(stream), Explain.PayloadStreamNumberBits);
            Fields.Bits(streamByte, "Key Frame Bit", start, 7, 1, NodeValue.Boolean(key), Explain.KeyFrameBit);
            if (numberWidth > 0)
            {
                Field("Media Object Number", numberWidth, NodeValue.Integer(payload.ObjectNumber), payload.IsCompressed ? Explain.CompressedMediaObjectNumber : Explain.MediaObjectNumber);
            }

            if (offsetWidth > 0)
            {
                Field(payload.IsCompressed ? "Presentation Time" : "Offset Into Media Object", offsetWidth,
                    NodeValue.Integer(payload.OffsetOrTime, payload.IsCompressed ? "ms" : "bytes"), payload.IsCompressed ? Explain.CompressedPresentationTime : Explain.OffsetIntoMediaObject);
            }

            if (replicatedWidth > 0)
            {
                Field("Replicated Data Length", replicatedWidth, NodeValue.Integer(payload.ReplicatedLength, "bytes"), Explain.ReplicatedDataLength);
            }

            if (payload.ReplicatedLength > 0)
            {
                Node replicated = Field("Replicated Data", payload.ReplicatedLength, NodeValue.Integer(payload.ReplicatedLength, "bytes", "byte"),
                    payload.IsCompressed ? Explain.CompressedReplicatedData : Explain.ReplicatedData, Topics.AsfPackets);
                if (payload.IsCompressed)
                {
                    replicated.Add("Presentation Time Delta", replicated.Offset, 1, NodeValue.Integer(payload.TimeDelta, "ms"), Explain.PresentationTimeDelta);
                }
                else if (payload.ReplicatedLength >= 8)
                {
                    replicated.Add("Media Object Size", replicated.Offset, 4, NodeValue.Integer(payload.ObjectSize, "bytes"), Explain.MediaObjectSize);
                    replicated.Add("Presentation Time", replicated.Offset + 4, 4, NodeValue.Integer(payload.PresentationTime, "ms"), Explain.MediaObjectPresentationTime);
                    if (payload.ReplicatedLength > 8)
                    {
                        replicated.Add("Extension Data", replicated.Offset + 8, payload.ReplicatedLength - 8, NodeValue.Integer(payload.ReplicatedLength - 8, "bytes", "byte"), Explain.ExtensionData);
                    }
                }
            }

            if (payloadLengthWidth > 0)
            {
                Field("Payload Length", payloadLengthWidth, NodeValue.Integer(payload.DataLength, "bytes"), Explain.PayloadLength);
            }

            NodeValue dataValue = payload.IsCompressed
                ? NodeValue.Text($"{Plural(payload.SubPayloads, "sub-payload", "sub-payloads")}, {payload.DataLength} bytes")
                : NodeValue.Integer(payload.DataLength, "bytes", "byte");
            Field("Payload Data", payload.DataLength, dataValue, payload.IsCompressed ? Explain.SubPayloads : Explain.PayloadData);

            var shown = new Node("Payload", start, at - start, node.Value) { Description = node.Description, Topic = node.Topic };
            foreach (Node child in node.Children)
            {
                shown.Add(child);
            }

            nodes.Add(shown);
            position = at;
        }

        long packetEnd = packet.Offset + packet.Length;
        if (position < packetEnd)
        {
            Add("Padding", packetEnd - position, NodeValue.Integer(packetEnd - position, "bytes", "byte"), Explain.PacketPadding);
        }

        // A packet cut short or damaged has fewer bytes than its fields: only those that fit are shown.
        nodes.RemoveAll(n => n.End > packetEnd);
        return nodes;
    }

    // ---- Statistics ----

    private static void AddStatistics(State state, Node dataNode, DataFigures figures, long start, long end)
    {
        if (figures.PacketCount == 0)
        {
            return;
        }

        long length = figures.LastEnd - start;
        Node stats = dataNode.Add(new Node("Statistics", start, length, Plural(figures.PacketCount, "packet", "packets"))
        {
            Kind = NodeKind.Statistics,
            Description = Explain.DataStatistics,
            Topic = Topics.AsfPackets,
        });

        void Add(string name, NodeValue value, string description) => stats.Add(name, start, length, value, description);

        Add("Packets", NodeValue.Integer(figures.PacketCount), Explain.StatPackets);
        Add("Payloads", NodeValue.Integer(figures.PayloadCount), Explain.StatPayloads);
        if (figures.Sizes.Count == 1)
        {
            Add("Packet Size", NodeValue.Integer(figures.Sizes.First().Key, "bytes"), Explain.StatPacketSize);
        }
        else
        {
            Add("Packet Sizes", NodeValue.Integer(figures.Sizes.Count, "sizes", "size"), Explain.StatPacketSizes);
        }

        Add("Packets with Multiple Payloads", NodeValue.Integer(figures.MultiplePacketCount), Explain.StatMultiplePackets);
        Add("Packets with Error Correction", NodeValue.Integer(figures.EccPackets), Explain.StatEccPackets);
        Add("Compressed Payloads", NodeValue.Integer(figures.CompressedPayloads), Explain.StatCompressedPayloads);
        Add("Payload Bytes", NodeValue.Integer(figures.PayloadBytes, "bytes"), Explain.StatPayloadBytes);
        Add("Padding Bytes", NodeValue.Integer(figures.PaddingBytes, "bytes"), Explain.StatPaddingBytes);
        long overhead = figures.PacketBytes - figures.PayloadBytes - figures.PaddingBytes;
        Add("Packet Overhead", NodeValue.Integer(overhead, "bytes"), Explain.StatOverhead);
        if (figures.FirstSendTime >= 0)
        {
            Add("First Send Time", NodeValue.Integer(figures.FirstSendTime, "ms"), Explain.StatFirstSendTime);
            Add("Last Send Time", NodeValue.Integer(figures.LastSendTime, "ms"), Explain.StatLastSendTime);
        }

        foreach ((int number, StreamFigures stream) in figures.Streams.OrderBy(s => s.Key))
        {
            Node node = stats.Add($"Stream {number}", start, length, Plural(stream.Payloads, "payload", "payloads"), Explain.StatStream);
            void Child(string name, NodeValue value, string description) => node.Add(name, start, length, value, description);
            string kind = state.Stream(number) is { } entry ? entry.TypeName.ToLowerInvariant() : "undeclared";
            Child("Stream Type", kind, Explain.StatStreamType);
            Child("Payloads", NodeValue.Integer(stream.Payloads), Explain.StatPayloads);
            Child("Payload Bytes", NodeValue.Integer(stream.PayloadBytes, "bytes"), Explain.StatPayloadBytes);
            Child("Media Objects", NodeValue.Integer(stream.Objects), Explain.StatMediaObjects);
            Child("Fragmented Media Objects", NodeValue.Integer(stream.Fragmented), Explain.StatFragmented);
            Child("Key Frame Payloads", NodeValue.Integer(stream.KeyFrames), Explain.StatKeyFrames);
            if (stream.MaxObjectSize > 0)
            {
                Child("Smallest Media Object", NodeValue.Integer(stream.MinObjectSize, "bytes"), Explain.StatSmallestObject);
                Child("Largest Media Object", NodeValue.Integer(stream.MaxObjectSize, "bytes"), Explain.StatLargestObject);
            }

            if (stream.FirstTime >= 0)
            {
                Child("First Presentation Time", NodeValue.Integer(stream.FirstTime, "ms"), Explain.StatFirstPresentationTime);
                Child("Last Presentation Time", NodeValue.Integer(stream.LastTime, "ms"), Explain.StatLastPresentationTime);
            }
        }

        _ = end;
    }

    internal static string Percent(long part, long whole) =>
        whole == 0 ? "0.0%" : (part * 100.0 / whole).ToString("F1", CultureInfo.InvariantCulture) + "%";
}
