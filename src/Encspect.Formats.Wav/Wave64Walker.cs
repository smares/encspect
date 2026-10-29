using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Wav;

// Reads the chunks of a Wave64 file: a 16-byte GUID, a 64-bit size that counts the 24 bytes of this header, the data, and padding to the next
// multiple of 8 bytes from the start of the file (the size does not count it).
internal static class Wave64Walker
{
    private static readonly ChunkSpec Junk = new("Junk Chunk", Explain.JunkChunk, Topics.Wave64, Chunks.Filler);

    private static readonly ChunkSpec List = new("List Chunk", Explain.Wave64List, Topics.Wave64, null);

    private static ChunkSpec SpecOf(byte[] guid)
    {
        if (guid.AsSpan().SequenceEqual(Wave64.ListGuid))
        {
            return List;
        }

        string? id = Wave64.TextId(guid);
        return id switch
        {
            "fmt " or "fact" or "data" => ChunkSpec.Of(id, null) with { Topic = id == "data" ? Topics.PcmSamples : ChunkSpec.Of(id, null).Topic },
            "junk" => Junk,
            not null => new ChunkSpec($"'{id}' Chunk", Explain.UnknownChunk, Topics.Wave64, null),
            _ => new ChunkSpec(null, Explain.UnknownChunk, Topics.Wave64, null),
        };
    }

    public static long Walk(State s, Node parent, long position, long end)
    {
        ByteReader reader = s.Reader;
        int listed = 0;
        while (position + Wave64.ChunkHeaderLength <= end)
        {
            if (s.Nodes >= Walker.MaxNodes)
            {
                Node rest = parent.Add(new Node("Chunks Not Shown", position, end - position, NodeValue.Integer(end - position, "bytes", "byte")) { Description = Explain.ChunksNotShown, Topic = Topics.Wave64 });
                s.IsNodeLimitReached = true;
                long from = position;
                s.Findings.Add(Severity.Warning, "wav.node-limit", rest, _ =>
                    $"The tree holds {Walker.MaxNodes} nodes, the most the report builds; the chunks from 0x{from:X} on are not read.");
                return end;
            }

            if (listed >= Walker.MaxListed)
            {
                Node more = parent.Add(new Node("Further Chunks", position, end - position, NodeValue.Integer(end - position, "bytes", "byte")) { Description = Explain.FurtherChunks, Topic = Topics.Wave64 });
                s.Findings.Add(Severity.Info, "wav.chunk-limit", more, _ => $"The chunks from 0x{more.Offset:X} on are not read: the report lists at most {Walker.MaxListed} chunks at one level.");
                return end;
            }

            byte[] header = reader.ReadBytesAt(position, Wave64.ChunkHeaderLength);
            ulong stated = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
            if (stated < Wave64.ChunkHeaderLength)
            {
                Node unread = parent.Add(new Node("Unread Data", position, end - position, NodeValue.Integer(end - position, "bytes", "byte")) { Description = Explain.Wave64UnreadData });
                long at = position;
                s.Findings.Add(Severity.Warning, "wav.w64-chunk-size-invalid", unread, _ =>
                    $"The chunk at 0x{at:X} states {Counts.Of(stated, "byte", "bytes")}, but a size counts the 24 bytes of the chunk's own header, so none is under 24; the remaining {Counts.Of(end - at, "byte", "bytes")} are not read.");
                return end;
            }

            byte[] guid = header[..16];
            long bodyStart = position + Wave64.ChunkHeaderLength;
            long remaining = end - position;
            bool isPastEnd = stated > (ulong)remaining;
            long length = isPastEnd ? remaining - Wave64.ChunkHeaderLength : (long)stated - Wave64.ChunkHeaderLength;
            long bodyEnd = bodyStart + length;
            ulong padded = Wave64.Align(stated);
            long padLength = isPastEnd ? 0 : (long)(padded - stated);
            long padPresent = Math.Max(0, Math.Min(padLength, end - bodyEnd));
            long next = bodyEnd + padPresent;

            ChunkSpec spec = SpecOf(guid);
            string? id = Wave64.TextId(guid);
            string name = spec.Name ?? $"Chunk {new Guid(guid)}";
            bool isData = id == "data";
            bool isFirstData = isData && s.FirstDataNode is null && s.Format is not null;
            string? carried = isFirstData ? DataChunks.CarriedCodec(s, s.Format!, parent, reader.Slice(bodyStart, length)) : null;
            var chunk = new Node(name, position, next - position, NodeValue.Integer(length, "bytes", "byte"))
            {
                Description = spec.Description,
                Topic = spec.Topic,
                Kind = isFirstData && s.Format!.CodecId is null && carried is null ? NodeKind.Stream : NodeKind.Detail,
            };
            if (isFirstData)
            {
                s.CarriedCodec = carried;
            }

            parent.Add(chunk);
            listed++;
            s.Nodes += 3;
            chunk.Add("Chunk ID", position, 16, Wave64.Describe(guid), Explain.Wave64ChunkId, Topics.Wave64);
            chunk.Add("Chunk Size", position + 16, 8, stated <= long.MaxValue ? NodeValue.Integer((long)stated, "bytes", "byte") : NodeValue.Hex(stated, 16), Explain.Wave64ChunkSize, Topics.Wave64);

            if (isPastEnd)
            {
                Node at = chunk.Children[1];
                ulong claimed = stated;
                long held = remaining;
                s.Findings.Add(Severity.Warning, "wav.chunk-past-end", at, _ =>
                    $"The chunk states {Counts.Of(claimed, "byte", "bytes")} with its header, but only {Counts.Of(held, "byte", "bytes")} are left in the {(s.ContainerEnd < s.Reader.End ? s.ContainerLabel : "file")}; the chunk is cut short.");
            }

            long chunkStart = position;
            s.ChunkStated = (ulong)length;
            s.ChunkSize32 = 0;
            s.ChunkSizeIsUnknown = false;
            if (spec.Read is { } read)
            {
                try
                {
                    var fields = new Fields(reader.Slice(bodyStart, length), chunk, false);
                    read(s, fields, chunk, length);
                }
                catch (EndOfStreamException)
                {
                    Node target = chunk;
                    s.Findings.Add(Severity.Warning, "wav.chunk-too-short", target, _ =>
                        $"The {(id ?? "chunk").TrimEnd()} chunk at 0x{chunkStart:X} holds {Counts.Of(length, "byte", "bytes")}, too few for its fields.");
                }
            }

            s.Nodes += Walker.Count(chunk) - 3;

            if (padPresent > 0)
            {
                byte[] pad = reader.ReadBytesAt(bodyEnd, (int)padPresent);
                Node padNode = chunk.Add("Padding", bodyEnd, padPresent, NodeValue.Integer(padPresent, "bytes", "byte"), Explain.Wave64Padding, Topics.Wave64);
                s.Nodes++;
                if (pad.Any(b => b != 0))
                {
                    s.Findings.Add(Severity.Info, "wav.pad-byte-not-zero", padNode, _ => "The padding after a chunk should be zero.");
                }
            }

            if (padPresent < padLength)
            {
                Node at = chunk;
                long missing = padLength - padPresent;
                s.Findings.Add(Severity.Warning, "wav.pad-byte-missing", at, _ =>
                    $"The chunk is followed by {Counts.Of(missing, "byte", "bytes")} too few of the padding that takes it to a multiple of 8 bytes.");
            }

            if (isPastEnd)
            {
                return end;
            }

            position = next;
        }

        return position;
    }
}
