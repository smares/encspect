using Encspect.Core;

namespace Encspect.Formats.Asf;

public sealed partial class AsfParser
{
    // The largest media object put together in memory; a stream that states more is not followed.
    private const int MaxMediaObject = 1 << 24;

    // The second pass over the Data Object, for the codec: one stream's media objects, each put
    // together from the payloads that carry it (a media object may be fragmented across packets and
    // shares them with other streams), in the order they complete. A compressed payload's
    // sub-payloads are media objects of their own (section 5.2.3.2). The packets are read again from
    // the file, one at a time, as the codec asks for objects.
    private static IEnumerable<MappedBytes> MediaObjects(State state, int streamNumber)
    {
        ByteReader reader = state.Reader;
        FindingCollector findings = state.Findings;
        Node anchor = state.DataNode!;
        int packetSize = state.PacketSize;
        byte[] buffer = new byte[Math.Max(packetSize, 1024)];
        var packet = new ParsedPacket();
        long position = state.PacketsStart;
        long end = state.PacketsEnd;
        int readSize = packetSize > 0 ? packetSize : 1 << 16;

        byte[]? data = null;
        var ranges = new List<ByteRange>();
        uint number = 0;
        int collected = 0;

        // The object's stated size; its buffer grows with the fragments that arrive, so a packet that states a
        // large object and never delivers it costs what it holds, not what it states.
        int total = 0;

        while (position < end)
        {
            reader.CancellationToken.ThrowIfCancellationRequested();
            long remaining = end - position;
            if (remaining < 2)
            {
                break;
            }

            int count = (int)Math.Min(readSize, remaining);
            if (buffer.Length < count)
            {
                buffer = new byte[count];
            }

            reader.Position = position;
            reader.Read(buffer.AsSpan(0, count));
            PacketParser.Parse(buffer.AsSpan(0, count), packetSize, packet);
            if (packet.PacketLength > count && packet.PacketLength <= MaxPacketSize && packet.PacketLength <= remaining)
            {
                count = packet.PacketLength;
                if (buffer.Length < count)
                {
                    buffer = new byte[count];
                }

                reader.Position = position;
                reader.Read(buffer.AsSpan(0, count));
                PacketParser.Parse(buffer.AsSpan(0, count), packetSize, packet);
            }

            int length = StepOf(state, packet, count, remaining);

            foreach (PayloadView payload in packet.Payloads)
            {
                if (payload.StreamNumber != streamNumber)
                {
                    continue;
                }

                if (payload.IsCompressed)
                {
                    foreach (MappedBytes sub in SubPayloads(buffer, position, payload))
                    {
                        yield return sub;
                    }

                    continue;
                }

                long dataOffset = position + payload.DataPosition;
                uint offset = payload.OffsetOrTime;
                uint size = payload.ReplicatedLength >= 8 ? payload.ObjectSize : (offset == 0 ? (uint)payload.DataLength : 0);
                if (offset == 0)
                {
                    if (data is not null)
                    {
                        findings.Add(Severity.Warning, "asf.object-incomplete", anchor, n =>
                            $"{(n == 1 ? "A media object of stream" : $"{n} media objects of stream")} {streamNumber} {(n == 1 ? "ends" : "end")} before all of its bytes arrive: the next one starts first. They are dropped.");
                        data = null;
                    }

                    if (size == 0 || size > MaxMediaObject)
                    {
                        findings.Add(Severity.Warning, "asf.object-size-invalid", anchor, n =>
                            $"{(n == 1 ? "A media object of stream" : $"{n} media objects of stream")} {streamNumber} state{(n == 1 ? "s" : string.Empty)} a size of 0 or more than {MaxMediaObject} bytes; {(n == 1 ? "it is" : "they are")} not followed.");
                        continue;
                    }

                    total = (int)size;
                    data = new byte[Math.Min(total, Math.Max(payload.DataLength, 1))];
                    ranges.Clear();
                    collected = 0;
                    number = payload.ObjectNumber;
                }
                else if (data is null || payload.ObjectNumber != number || offset != collected)
                {
                    findings.Add(Severity.Warning, "asf.fragment-order", anchor, n =>
                        $"{(n == 1 ? "A fragment of a media object of stream" : $"{n} fragments of media objects of stream")} {streamNumber} {(n == 1 ? "does" : "do")} not continue the object before {(n == 1 ? "it" : "them")}; {(n == 1 ? "it is" : "they are")} dropped.");
                    continue;
                }

                int take = Math.Min(payload.DataLength, total - collected);
                if (take < payload.DataLength)
                {
                    findings.Add(Severity.Warning, "asf.fragment-overrun", anchor, n =>
                        $"{(n == 1 ? "A fragment" : $"{n} fragments")} of stream {streamNumber} {(n == 1 ? "holds" : "hold")} more bytes than the media object's size leaves; the rest is dropped.");
                }

                if (data!.Length < collected + take)
                {
                    Array.Resize(ref data, (int)Math.Min(total, Math.Max(collected + take, data.Length * 2L)));
                }

                buffer.AsSpan(payload.DataPosition, take).CopyTo(data.AsSpan(collected));
                if (take > 0)
                {
                    ranges.Add(new ByteRange(dataOffset, take));
                }

                collected += take;
                if (collected == total)
                {
                    yield return new MappedBytes(data, ranges);
                    data = null;
                    ranges = [];
                }
            }

            position += length;
        }

        if (data is not null)
        {
            findings.Add(Severity.Warning, "asf.object-incomplete", anchor, n =>
                $"{(n == 1 ? "A media object of stream" : $"{n} media objects of stream")} {streamNumber} {(n == 1 ? "ends" : "end")} before all of its bytes arrive: the data ends first. They are dropped.");
        }
    }

    // The sub-payloads of a compressed payload: a length byte and that many bytes each.
    private static IEnumerable<MappedBytes> SubPayloads(byte[] packet, long packetOffset, PayloadView payload)
    {
        int at = payload.DataPosition;
        int end = payload.DataPosition + payload.DataLength;
        while (at < end)
        {
            int length = packet[at];
            at++;
            if (length == 0 || at + length > end)
            {
                yield break;
            }

            yield return new MappedBytes(packet.AsMemory(at, length).ToArray(), packetOffset + at);
            at += length;
        }
    }
}
