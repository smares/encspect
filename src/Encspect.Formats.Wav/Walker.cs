using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Wav;

// Reads the chunks of the RIFF chunk and of its LIST chunks (Multimedia Programming Interface and Data Specifications 1.0, "Chunks" and
// "LIST Chunk"): a four-character ID, a 32-bit size that does not count the ID, the size field or the pad byte, the data, and a pad byte of
// zero when the size is odd.
internal static class Walker
{
    // The chunks listed as nodes at one level; the rest are not read.
    public const int MaxListed = 2048;

    // How many LIST chunks may nest, so that a crafted file cannot grow the tree without bound.
    public const int MaxNesting = 8;

    // The nodes the tree of the chunks holds in all (as MP4 and Matroska budget theirs): the caps on one level and on one table multiply
    // through nested lists and repeated table chunks, so a file of a few megabytes could otherwise ask for millions. Past it the rest of the
    // level is one node and is not read. A chunk that crosses the budget is read whole, which adds at most the nodes of one table.
    public const int MaxNodes = 250_000;

    public delegate void ChunkReader(State s, Fields f, Node chunk, long length);

    // An ID is four printable ASCII characters, padded on the right with blanks: the first is not a blank. (The specification limits IDs
    // to alphanumeric characters, but files hold others, Adobe's "_PMX" among them.)
    public static bool IsFourCc(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] == 0x20)
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if (bytes[i] is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    // The nodes of the subtree under node, itself included; children built on demand (a tag parser's frames) are one node, so counting builds none.
    internal static int Count(Node node)
    {
        int count = 0;
        var pending = new Stack<Node>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            Node current = pending.Pop();
            count++;
            if (!current.AreChildrenBuiltOnDemand)
            {
                foreach (Node child in current.Children)
                {
                    pending.Push(child);
                }
            }
        }

        return count;
    }

    private static bool IsChunkHeaderAt(State s, long position, long end)
    {
        if (position + 8 > end)
        {
            return false;
        }

        return IsFourCc(s.Reader.ReadBytesAt(position, 4));
    }

    // Reads the chunks in [position, end) and adds a node for each to parent. Returns the offset where the chunks end.
    public static long Chunks(State s, Node parent, long position, long end, string? listType, int depth)
    {
        ByteReader reader = s.Reader;
        bool isTop = depth == 0;
        int listed = 0;
        while (position + 8 <= end)
        {
            if (s.Nodes >= MaxNodes)
            {
                Node rest = parent.Add(new Node("Chunks Not Shown", position, end - position, NodeValue.Integer(end - position, "bytes", "byte")) { Description = Explain.ChunksNotShown, Topic = Topics.RiffChunks });
                s.IsNodeLimitReached = true;
                long from = position;
                s.Findings.Add(Severity.Warning, "wav.node-limit", rest, _ =>
                    $"The tree holds {MaxNodes} nodes, the most the report builds; the chunks from 0x{from:X} on are not read.");
                return end;
            }

            if (listed >= MaxListed)
            {
                Node more = parent.Add(new Node("Further Chunks", position, end - position, NodeValue.Integer(end - position, "bytes", "byte")) { Description = Explain.FurtherChunks, Topic = Topics.RiffChunks });
                s.Findings.Add(Severity.Info, "wav.chunk-limit", more, _ => $"The chunks from 0x{more.Offset:X} on are not read: the report lists at most {MaxListed} chunks at one level.");
                return end;
            }

            byte[] header = reader.ReadBytesAt(position, 8);
            if (!IsFourCc(header))
            {
                Node unread = parent.Add(new Node("Unread Data", position, end - position, NodeValue.Integer(end - position, "bytes", "byte")) { Description = Explain.UnreadData });
                long at = position;
                s.Findings.Add(Severity.Warning, "wav.chunk-id-invalid", unread, _ =>
                    $"The bytes at 0x{at:X} should start a chunk, but their first four are not an ID of printable characters; the remaining {Counts.Of(end - at, "byte", "bytes")} are not read.");
                return end;
            }

            string id = Encoding.Latin1.GetString(header, 0, 4);
            uint size32 = Fields.Dword(header.AsSpan(4), s.IsBigEndian);
            long bodyStart = position + 8;
            long remaining = end - bodyStart;
            ulong stated = size32;
            bool isDs64Size = false;
            bool isUnresolved = false;
            if (isTop && s.Is64Bit && size32 == uint.MaxValue)
            {
                if (s.Ds64 is { } ds64 && id == "data")
                {
                    stated = ds64.DataSize;
                    isDs64Size = true;
                }
                else if (s.Ds64 is { } table && table.Table.TryGetValue(id, out ulong big))
                {
                    stated = big;
                    isDs64Size = true;
                }
                else
                {
                    isUnresolved = true;
                }
            }

            // A data chunk whose size is a placeholder (0xFFFFFFFF, or 0 with no chunk after it) runs to the end of what holds it: a writer that
            // could not go back and fill in the size left it.
            bool isData = id == "data";
            bool isPlaceholder = isData && !isDs64Size && !isUnresolved && (size32 == uint.MaxValue || (size32 == 0 && remaining > 0 && !IsChunkHeaderAt(s, bodyStart, end)));
            long length;
            bool isPastEnd = false;
            if (isPlaceholder || isUnresolved)
            {
                length = remaining;
            }
            else if (stated > (ulong)remaining)
            {
                length = remaining;
                isPastEnd = true;
            }
            else
            {
                length = (long)stated;
            }

            long bodyEnd = bodyStart + length;
            bool hasPad = false;
            bool isPadMissing = false;
            long next = bodyEnd;
            if (length % 2 == 1 && !isPastEnd && !isPlaceholder && !isUnresolved)
            {
                if (bodyEnd >= end)
                {
                    isPadMissing = true;
                }
                else if (!IsChunkHeaderAt(s, bodyEnd + 1, end) && IsChunkHeaderAt(s, bodyEnd, end))
                {
                    isPadMissing = true;
                }
                else
                {
                    hasPad = true;
                    next = bodyEnd + 1;
                }
            }

            ChunkSpec spec = ChunkSpec.Of(id, listType);
            if (isTop && id == "ds64" && s.IsBw64)
            {
                spec = spec with { Description = Explain.Ds64ChunkBw64, Topic = Topics.Bw64 };
            }

            string name = spec.Name ?? $"'{id}' Chunk";
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
            chunk.Add("Chunk ID", position, 4, id, Explain.ChunkId, Topics.RiffChunks);
            chunk.Add("Chunk Size", position + 4, 4, NodeValue.Integer(size32, "bytes", "byte"), isTop && s.IsBw64 ? Explain.ChunkSizeBw64 : isTop && s.IsRf64 ? Explain.ChunkSizeRf64 : Explain.ChunkSize, Topics.RiffChunks);

            if (isUnresolved)
            {
                Node at = chunk.Children[1];
                s.Findings.Add(Severity.Warning, "wav.chunk-size-unresolved", at, _ =>
                    $"The {id.TrimEnd()} chunk's size is 0xFFFFFFFF, which in {(s.IsBw64 ? "a BW64" : "an RF64")} file stands for a size in the ds64 chunk, but the ds64 chunk {(s.Ds64 is null ? "is missing or too short" : "does not list this chunk")}; the chunk is read to the end of the RIFF chunk.");
            }

            if (isPlaceholder)
            {
                Node at = chunk.Children[1];
                long held = length;
                s.Findings.Add(Severity.Warning, "wav.data-size-placeholder", at, _ =>
                    $"The data chunk's size is {(size32 == 0 ? "0" : "0xFFFFFFFF")}, a placeholder a writer leaves when it cannot fill in the size; the chunk is taken to run to the end of the {(listType is null ? "RIFF chunk" : "list")}, {Counts.Of(held, "byte", "bytes")}.");
            }

            if (isPastEnd)
            {
                Node at = chunk.Children[1];
                ulong claimed = stated;
                long held = length;
                s.Findings.Add(Severity.Warning, "wav.chunk-past-end", at, _ =>
                    $"The {id.TrimEnd()} chunk states {Counts.Of(claimed, "byte", "bytes")}, but only {Counts.Of(held, "byte", "bytes")} are left in the {(listType is null ? (s.ContainerEnd < s.Reader.End ? s.ContainerLabel : "file") : "list")}; the chunk is cut short.");
            }

            long chunkStart = position;
            s.ChunkStated = stated;
            s.ChunkSize32 = size32;
            s.ChunkSizeIsUnknown = isPlaceholder || isUnresolved;
            if (length >= 0 && spec.Read is { } read)
            {
                try
                {
                    ByteReader body = reader.Slice(bodyStart, length);
                    var fields = new Fields(body, chunk, s.IsBigEndian);
                    read(s, fields, chunk, length);
                }
                catch (EndOfStreamException)
                {
                    Node target = chunk;
                    s.Findings.Add(Severity.Warning, "wav.chunk-too-short", target, _ =>
                        $"The {id.TrimEnd()} chunk at 0x{chunkStart:X} holds {Counts.Of(length, "byte", "bytes")}, too few for its fields.");
                }
            }

            s.Nodes += id == "LIST" ? 1 : Count(chunk) - 3;

            if (hasPad)
            {
                byte pad = reader.ReadBytesAt(bodyEnd, 1)[0];
                Node padNode = chunk.Add("Pad Byte", bodyEnd, 1, NodeValue.Hex(pad, 2), Explain.PadByte, Topics.RiffChunks);
                s.Nodes++;
                if (pad != 0)
                {
                    s.Findings.Add(Severity.Info, "wav.pad-byte-not-zero", padNode, _ => "The pad byte after a chunk of odd size should be zero.");
                }
            }

            if (isPadMissing)
            {
                Node at = chunk;
                s.Findings.Add(Severity.Warning, "wav.pad-byte-missing", at, _ =>
                    $"The {id.TrimEnd()} chunk's size is odd, but no pad byte follows it{(next >= end ? " before the end" : $"; the next chunk starts right after the data at 0x{next:X}")}.");
            }

            if (isPastEnd || isPlaceholder || isUnresolved)
            {
                return end;
            }

            position = next;
        }

        return position;
    }
}
