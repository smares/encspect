using System.Buffers.Binary;

namespace Encspect.Formats.Asf;

// What is wrong with a packet, as the parsing of its bytes finds out. The bits are kept in the
// packet's record, so the findings and the nodes can be made from it later.
[Flags]
internal enum PacketIssues : ushort
{
    None = 0,

    // The packet's bytes end before its header or a payload does.
    Truncated = 1,

    // The padding length is more than what the packet holds after its header.
    PaddingTooLong = 2,

    // A payload extends past the packet's end (or its padding).
    PayloadOverrun = 4,

    // A packet of multiple payloads that says it holds none.
    NoPayloads = 8,

    // A packet of multiple payloads whose payload length type is 0, which cannot hold a length.
    PayloadLengthType = 16,

    // A stream number length type other than 1 (a byte), which no reader supports.
    StreamNumberType = 32,

    // A replicated data length of 2 to 7, which cannot hold the media object's size and time.
    ReplicatedLength = 64,

    // An error correction length type other than 0, which the specification reserves.
    EccLengthType = 128,

    // The packet length field differs from the size every packet has.
    PacketLengthMismatch = 256,

    // The padding length differs from the padding the packet's layout leaves.
    PaddingMismatch = 512,

    // A payload for stream 0, which is not a stream.
    ZeroStream = 1024,

    // A payload of length 0, or a compressed payload with a sub-payload that overruns it.
    EmptyPayload = 2048,

    // A compressed payload (replicated data length 1) whose layout cannot hold its fields.
    CompressedPayload = 4096,

    // A packet length field of 0 where the size of the packet is not otherwise known.
    NoPacketSize = 16384,
}

[Flags]
internal enum PayloadFlags : byte
{
    None = 0,
    Compressed = 1,
    KeyFrame = 2,
}

// One payload as the packet's bytes give it. Positions are in the packet.
internal struct PayloadView
{
    public int Position;
    public int HeaderLength;
    public byte StreamByte;
    public PayloadFlags Flags;
    public byte TimeDelta;
    public uint ObjectNumber;

    // The offset into the media object; for a compressed payload, its presentation time (section 5.2.3.2).
    public uint OffsetOrTime;
    public uint ReplicatedLength;
    public uint ObjectSize;
    public uint PresentationTime;
    public int DataPosition;
    public int DataLength;
    public int SubPayloads;

    public readonly int StreamNumber => StreamByte & 0x7F;

    public readonly bool IsKeyFrame => (StreamByte & 0x80) != 0;

    public readonly bool IsCompressed => (Flags & PayloadFlags.Compressed) != 0;
}

// The header of a packet and its payloads, read from the packet's bytes (ASF specification, section
// 5.2). One parser serves every pass: the first builds the records the nodes come from, the second
// puts media objects together from the payloads.
internal sealed class ParsedPacket
{
    public byte EccFlags { get; set; }

    public int EccLength { get; set; }

    public byte EccByte0 { get; set; }

    public byte EccByte1 { get; set; }

    public byte LengthTypeFlags { get; set; }

    public byte PropertyFlags { get; set; }

    public uint PacketLengthField { get; set; }

    public uint Sequence { get; set; }

    public uint PaddingLength { get; set; }

    public uint SendTime { get; set; }

    public ushort Duration { get; set; }

    public byte PayloadFlagsByte { get; set; }

    public bool IsMultiple => (LengthTypeFlags & 1) != 0;

    public bool HasEcc => (EccFlags & 0x80) != 0;

    // Where the first payload (or the payload flags of a packet of multiple payloads) starts.
    public int HeaderLength { get; set; }

    // The packet's size, as far as its fields say: the packet length field, else the size every
    // packet has.
    public int PacketLength { get; set; }

    // Where the payloads end.
    public int PayloadsEnd { get; set; }

    // The padding the layout leaves between the last payload and the packet's end.
    public int ActualPadding { get; set; }

    public PacketIssues Issues { get; set; }

    public List<PayloadView> Payloads { get; } = [];

    public void Clear()
    {
        EccFlags = 0;
        EccLength = 0;
        LengthTypeFlags = 0;
        PropertyFlags = 0;
        PacketLengthField = 0;
        Sequence = 0;
        PaddingLength = 0;
        SendTime = 0;
        Duration = 0;
        PayloadFlagsByte = 0;
        HeaderLength = 0;
        PacketLength = 0;
        PayloadsEnd = 0;
        ActualPadding = 0;
        Issues = PacketIssues.None;
        Payloads.Clear();
    }
}

internal static class PacketParser
{
    // The width of a field whose length type is 0 to 3 (sections 5.2.2 and 5.2.3).
    public static int Width(int type) => type switch { 1 => 1, 2 => 2, 3 => 4, _ => 0 };

    // Parses the packet at the start of bytes. packetSize is the size every packet has, from the File
    // Properties Object (fixedSize: the same for every packet); bytes holds the packet's bytes, or fewer where the data ends inside it.
    public static void Parse(ReadOnlySpan<byte> bytes, int packetSize, ParsedPacket packet, bool fixedSize = false)
    {
        packet.Clear();
        int position = 0;
        if (bytes.Length == 0)
        {
            packet.Issues |= PacketIssues.Truncated;
            return;
        }

        if ((bytes[0] & 0x80) != 0)
        {
            // Section 5.2.1: the flags byte, then the error correction data. Only length type 0 gives
            // its size in the flags; FFmpeg reads no data for the reserved types either.
            packet.EccFlags = bytes[0];
            if ((bytes[0] & 0x60) != 0)
            {
                packet.Issues |= PacketIssues.EccLengthType;
            }
            else
            {
                packet.EccLength = bytes[0] & 0x0F;
                packet.EccByte0 = packet.EccLength > 0 && bytes.Length > 1 ? bytes[1] : (byte)0;
                packet.EccByte1 = packet.EccLength > 1 && bytes.Length > 2 ? bytes[2] : (byte)0;
            }

            position = 1 + packet.EccLength;
        }

        if (position + 2 > bytes.Length)
        {
            packet.Issues |= PacketIssues.Truncated;
            packet.HeaderLength = Math.Min(position, bytes.Length);
            return;
        }

        packet.LengthTypeFlags = bytes[position++];
        packet.PropertyFlags = bytes[position++];

        int lengthWidth = Width((packet.LengthTypeFlags >> 5) & 3);
        int sequenceWidth = Width((packet.LengthTypeFlags >> 1) & 3);
        int paddingWidth = Width((packet.LengthTypeFlags >> 3) & 3);
        int fixedLength = lengthWidth + sequenceWidth + paddingWidth + 6;
        if (position + fixedLength > bytes.Length)
        {
            packet.Issues |= PacketIssues.Truncated;
            packet.HeaderLength = bytes.Length;
            return;
        }

        packet.PacketLengthField = ReadValue(bytes, ref position, lengthWidth);
        packet.Sequence = ReadValue(bytes, ref position, sequenceWidth);
        packet.PaddingLength = ReadValue(bytes, ref position, paddingWidth);
        packet.SendTime = BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]);
        packet.Duration = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(position + 4)..]);
        position += 6;

        int length = packet.PacketLengthField != 0 ? (int)Math.Min(packet.PacketLengthField, int.MaxValue) : packetSize;
        if (packet.PacketLengthField == 0 && packetSize <= 0)
        {
            packet.Issues |= PacketIssues.NoPacketSize;
            length = bytes.Length;
        }
        else if (packet.PacketLengthField != 0 && packetSize > 0 && packet.PacketLengthField != packetSize)
        {
            packet.Issues |= PacketIssues.PacketLengthMismatch;
        }

        if (fixedSize && packetSize > 0 && length > packetSize)
        {
            // Every packet has the file's packet size (section 8.2.13): a longer length field does not make this one longer,
            // and what it counts beyond the packet is payload that is not there.
            packet.Issues |= PacketIssues.PayloadOverrun;
            length = packetSize;
        }

        packet.PacketLength = length;
        if (length > bytes.Length)
        {
            packet.Issues |= PacketIssues.Truncated;
        }

        // The payloads end where the packet does, less the padding the packet declares.
        int available = Math.Min(length, bytes.Length);
        long declaredEnd = (long)length - packet.PaddingLength;
        if (declaredEnd < position)
        {
            packet.Issues |= PacketIssues.PaddingTooLong;
            declaredEnd = length;
        }

        int end = (int)Math.Min(declaredEnd, available);

        int payloadCount = 1;
        int payloadLengthWidth = 0;
        if (packet.IsMultiple)
        {
            if (position >= end)
            {
                packet.Issues |= PacketIssues.Truncated;
                packet.HeaderLength = position;
                return;
            }

            packet.PayloadFlagsByte = bytes[position++];
            payloadCount = packet.PayloadFlagsByte & 0x3F;
            int type = packet.PayloadFlagsByte >> 6;
            payloadLengthWidth = Width(type);
            if (payloadCount == 0)
            {
                packet.Issues |= PacketIssues.NoPayloads;
            }

            if (payloadLengthWidth == 0)
            {
                packet.Issues |= PacketIssues.PayloadLengthType;
            }
        }

        packet.HeaderLength = position;
        int offsetWidth = Width((packet.PropertyFlags >> 2) & 3);
        int numberWidth = Width((packet.PropertyFlags >> 4) & 3);
        int replicatedWidth = Width(packet.PropertyFlags & 3);
        if (((packet.PropertyFlags >> 6) & 3) != 1)
        {
            packet.Issues |= PacketIssues.StreamNumberType;
        }

        if (packet.IsMultiple && payloadLengthWidth == 0)
        {
            packet.PayloadsEnd = position;
            return;
        }

        for (int index = 0; index < payloadCount; index++)
        {
            int start = position;
            int headerSize = 1 + numberWidth + offsetWidth + replicatedWidth;
            if (position + headerSize > end)
            {
                packet.Issues |= PacketIssues.PayloadOverrun;
                break;
            }

            var payload = new PayloadView { Position = start, StreamByte = bytes[position++] };
            payload.ObjectNumber = ReadValue(bytes, ref position, numberWidth);
            payload.OffsetOrTime = ReadValue(bytes, ref position, offsetWidth);
            payload.ReplicatedLength = ReadValue(bytes, ref position, replicatedWidth);
            if (payload.StreamNumber == 0)
            {
                packet.Issues |= PacketIssues.ZeroStream;
            }

            if (payload.StreamByte >= 0x80)
            {
                payload.Flags |= PayloadFlags.KeyFrame;
            }

            if (payload.ReplicatedLength > end - position)
            {
                packet.Issues |= PacketIssues.PayloadOverrun;
                break;
            }

            int replicated = (int)payload.ReplicatedLength;
            if (payload.ReplicatedLength == 1)
            {
                // Section 5.2.3.2: a compressed payload keeps its presentation time delta where the
                // replicated data would be, and needs the fields a plain payload has too.
                payload.Flags |= PayloadFlags.Compressed;
                payload.TimeDelta = bytes[position];
                if (replicatedWidth == 0 || offsetWidth == 0)
                {
                    packet.Issues |= PacketIssues.CompressedPayload;
                }
            }
            else if (replicated >= 8)
            {
                payload.ObjectSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]);
                payload.PresentationTime = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 4)..]);
            }
            else if (replicated != 0)
            {
                packet.Issues |= PacketIssues.ReplicatedLength;
            }

            position += replicated;
            if (packet.IsMultiple)
            {
                if (position + payloadLengthWidth > end)
                {
                    packet.Issues |= PacketIssues.PayloadOverrun;
                    break;
                }

                long dataLength = ReadValue(bytes, ref position, payloadLengthWidth);
                payload.DataLength = (int)Math.Min(dataLength, int.MaxValue);
                if (dataLength > end - position)
                {
                    packet.Issues |= PacketIssues.PayloadOverrun;
                    payload.DataLength = Math.Max(0, end - position);
                }
            }
            else
            {
                payload.DataLength = end - position;
            }

            payload.HeaderLength = position - start;
            payload.DataPosition = position;
            if (payload.DataLength == 0)
            {
                packet.Issues |= PacketIssues.EmptyPayload;
            }

            if (payload.IsCompressed)
            {
                payload.SubPayloads = CountSubPayloads(bytes.Slice(position, payload.DataLength), packet);
            }

            packet.Payloads.Add(payload);
            position += payload.DataLength;
        }

        packet.PayloadsEnd = position;
        packet.ActualPadding = Math.Max(0, length - position);
        if (!packet.Issues.HasFlag(PacketIssues.Truncated))
        {
            if (packet.PaddingLength != 0 && packet.PaddingLength != packet.ActualPadding && !packet.Issues.HasFlag(PacketIssues.PaddingTooLong))
            {
                packet.Issues |= PacketIssues.PaddingMismatch;
            }
        }
    }

    // The sub-payloads of a compressed payload: each is a length byte and that many bytes, up to the
    // payload's end (section 5.2.3.2). A length that runs past the end ends the count.
    private static int CountSubPayloads(ReadOnlySpan<byte> data, ParsedPacket packet)
    {
        int count = 0;
        int position = 0;
        while (position < data.Length)
        {
            int length = data[position];
            position += 1 + length;
            if (position > data.Length)
            {
                packet.Issues |= PacketIssues.EmptyPayload;
                break;
            }

            count++;
        }

        return count;
    }

    private static uint ReadValue(ReadOnlySpan<byte> bytes, ref int position, int width)
    {
        uint value = width switch
        {
            1 => bytes[position],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]),
            _ => 0,
        };
        position += width;
        return value;
    }
}
