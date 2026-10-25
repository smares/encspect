using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Aiff;

// Reads the chunks of the FORM chunk (Audio Interchange File Format AIFF-C, section 2.0): a four-character ID, a 32-bit size that does not
// count the ID or the size field, the data, and a pad byte of zero when the size is odd.
internal static class Walker
{
    // The chunks listed as nodes; the rest are not read.
    public const int MaxListed = 2048;

    public delegate void ChunkReader(State s, Fields f, Node chunk, long length);

    // The chunks of which at most one may appear in a FORM (sections 3.0 to 13.0).
    private static readonly HashSet<string> Single = new(StringComparer.Ordinal) { "FVER", "COMM", "SSND", "MARK", "COMT", "INST", "AESD", "NAME", "AUTH", "(c) ", "CHAN" };

    // The nodes the tree of the chunks holds in all (as MP4 and Matroska budget theirs): the caps on the chunks and on a table multiply when a
    // chunk is repeated, so a file of a few megabytes could otherwise ask for millions. Past it the rest of the FORM is one node and is not
    // read. A chunk that crosses the budget is read whole, which adds at most the nodes of one table.
    public const int MaxNodes = 250_000;

    // An ID is four printable ASCII characters, from the space to the tilde, where no space precedes a printing character (section 1.0). One that
    // starts with a space is taken as none: blanks are padding.
    public static bool IsId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] == 0x20)
        {
            return false;
        }

        bool isTrailing = false;
        for (int i = 0; i < 4; i++)
        {
            if (bytes[i] is < 0x20 or > 0x7E || (isTrailing && bytes[i] != 0x20))
            {
                return false;
            }

            isTrailing |= bytes[i] == 0x20;
        }

        return true;
    }

    // The nodes of the subtree under node, itself included; children built on demand (a tag parser's frames) are one node, so counting builds none.
    private static int Count(Node node)
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

    private static bool IsChunkHeaderAt(State s, long position, long end) => position + 8 <= end && IsId(s.Reader.ReadBytesAt(position, 4));

    // Reads the chunks in [position, end) and adds a node for each to parent. Returns the offset where the chunks end.
    public static long Chunks(State s, Node parent, long position, long end)
    {
        ByteReader reader = s.Reader;
        int listed = 0;
        while (position + 8 <= end)
        {
            if (s.Nodes >= MaxNodes)
            {
                Node rest = parent.Add(new Node("Chunks Not Shown", position, end - position, NodeValue.Integer(end - position, "bytes", "byte")) { Description = Explain.ChunksNotShown, Topic = Topics.Container });
                long from = position;
                s.Findings.Add(Severity.Warning, "aiff.node-limit", rest, _ =>
                    $"The tree holds {MaxNodes} nodes, the most the report builds; the chunks from 0x{from:X} on are not read.");
                return end;
            }

            if (listed >= MaxListed)
            {
                Node more = parent.Add(new Node("Further Chunks", position, end - position, NodeValue.Integer(end - position, "bytes", "byte")) { Description = Explain.FurtherChunks, Topic = Topics.Container });
                s.Findings.Add(Severity.Info, "aiff.chunk-limit", more, _ => $"The chunks from 0x{more.Offset:X} on are not read: the report lists at most {MaxListed} chunks.");
                return end;
            }

            byte[] header = reader.ReadBytesAt(position, 8);
            if (!IsId(header))
            {
                Node unread = parent.Add(new Node("Unread Data", position, end - position, NodeValue.Integer(end - position, "bytes", "byte")) { Description = Explain.UnreadData });
                long at = position;
                s.Findings.Add(Severity.Warning, "aiff.chunk-id-invalid", unread, _ =>
                    $"The bytes at 0x{at:X} should start a chunk, but their first four are not an ID of printable characters; the remaining {Counts.Of(end - at, "byte", "bytes")} are not read.");
                return end;
            }

            string id = Encoding.Latin1.GetString(header, 0, 4);
            uint size32 = Fields.Dword(header.AsSpan(4));
            long bodyStart = position + 8;
            long remaining = end - bodyStart;

            // A sound data chunk whose size is 0xFFFFFFFF runs to the end of the FORM: a writer that could not fill in the size left it.
            bool isPlaceholder = id == "SSND" && size32 == uint.MaxValue;
            long length;
            bool isPastEnd = false;
            if (isPlaceholder)
            {
                length = remaining;
            }
            else if (size32 > remaining)
            {
                length = remaining;
                isPastEnd = true;
            }
            else
            {
                length = size32;
            }

            long bodyEnd = bodyStart + length;
            bool hasPad = false;
            bool isPadMissing = false;
            long next = bodyEnd;
            if (length % 2 == 1 && !isPastEnd && !isPlaceholder)
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

            ChunkSpec spec = ChunkSpec.Of(id);
            string name = spec.Name ?? $"'{id}' Chunk";
            var chunk = new Node(name, position, next - position, NodeValue.Integer(length, "bytes", "byte"))
            {
                Description = spec.Description,
                Topic = spec.Topic,
                Kind = id == "SSND" && s.FirstSoundNode is null ? NodeKind.Stream : NodeKind.Detail,
            };
            parent.Add(chunk);
            listed++;
            chunk.Add("Chunk ID", position, 4, id, Explain.ChunkId, Topics.Container);
            chunk.Add("Chunk Size", position + 4, 4, NodeValue.Integer(size32, "bytes", "byte"), Explain.ChunkSize, Topics.Container);

            s.Counts[id] = s.Counts.GetValueOrDefault(id) + 1;
            if (Single.Contains(id) && s.Counts[id] > 1)
            {
                s.Findings.Add(Severity.Warning, "aiff.chunk-repeated", chunk, n => $"The FORM holds {n + 1} {id.TrimEnd()} chunks, but the specification allows one; the first is used.", id);
            }

            if (isPlaceholder)
            {
                Node at = chunk.Children[1];
                long held = length;
                s.Findings.Add(Severity.Warning, "aiff.sound-size-placeholder", at, _ =>
                    $"The sound data chunk's size is 0xFFFFFFFF, a placeholder a writer leaves when it cannot fill in the size; the chunk is taken to run to the end of the FORM chunk, {Counts.Of(held, "byte", "bytes")}.");
            }

            if (isPastEnd)
            {
                Node at = chunk.Children[1];
                long held = length;
                s.Findings.Add(Severity.Warning, "aiff.chunk-past-end", at, _ =>
                    $"The {id.TrimEnd()} chunk states {Counts.Of(size32, "byte", "bytes")}, but only {Counts.Of(held, "byte", "bytes")} are left in the {(s.ContainerEnd < s.Reader.End ? "FORM chunk" : "file")}; the chunk is cut short.");
            }

            s.ChunkSize32 = size32;
            s.ChunkSizeIsUnknown = isPlaceholder;
            long chunkStart = position;
            if (spec.Read is { } read)
            {
                try
                {
                    ByteReader body = reader.Slice(bodyStart, length);
                    read(s, new Fields(body, chunk), chunk, length);
                }
                catch (EndOfStreamException)
                {
                    s.Findings.Add(Severity.Warning, "aiff.chunk-too-short", chunk, _ =>
                        $"The {id.TrimEnd()} chunk at 0x{chunkStart:X} holds {Counts.Of(length, "byte", "bytes")}, too few for its fields.");
                }
            }

            s.Nodes += Count(chunk);
            if (hasPad)
            {
                byte pad = reader.ReadBytesAt(bodyEnd, 1)[0];
                Node padNode = chunk.Add("Pad Byte", bodyEnd, 1, NodeValue.Hex(pad, 2), Explain.PadByte, Topics.Container);
                s.Nodes++;
                if (pad != 0)
                {
                    s.Findings.Add(Severity.Info, "aiff.pad-byte-not-zero", padNode, _ => "The pad byte after a chunk of odd size should be zero.");
                }
            }

            if (isPadMissing)
            {
                s.Findings.Add(Severity.Warning, "aiff.pad-byte-missing", chunk, _ =>
                    $"The {id.TrimEnd()} chunk's size is odd, but no pad byte follows it{(next >= end ? " before the end" : $"; the next chunk starts right after the data at 0x{next:X}")}.");
            }

            if (isPastEnd || isPlaceholder)
            {
                return end;
            }

            position = next;
        }

        return position;
    }
}
