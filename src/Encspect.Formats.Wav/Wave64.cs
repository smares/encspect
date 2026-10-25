using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Wav;

// Sony Wave64 files (.w64): the layout of a RIFF file with 128-bit GUIDs for chunk IDs, 64-bit sizes that count the 24-byte chunk header, and
// chunks aligned to 8 bytes. The fmt, fact and data chunks are those of a WAVE file, so they go through the same readers; the other chunks are
// named and sized. Sony's specification could not be read (docs/formats/wav.md says what the layout rests on).
public sealed class Wave64Parser : IFormatParser
{
    public string Name => "Wave64";

    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (!Wave64.HasHeader(reader))
        {
            return SniffResult.None;
        }

        long position = reader.Start + Wave64.HeaderLength;
        if (position + Wave64.ChunkHeaderLength > reader.End)
        {
            return new SniffResult(80, reader.Start, 0, End: reader.End);
        }

        bool isChunk = Wave64.ChunkSizeAt(reader, position) >= Wave64.ChunkHeaderLength;
        int frames = 0;
        while (isChunk && frames < SniffResult.MaxFrames && position + Wave64.ChunkHeaderLength <= reader.End && Wave64.ChunkSizeAt(reader, position) is { } size and >= Wave64.ChunkHeaderLength)
        {
            ulong next = (ulong)position + Wave64.Align(size);
            if (size > (ulong)(reader.End - position) || next > (ulong)reader.End + 7)
            {
                break;
            }

            frames++;
            position = (long)Math.Min(next, (ulong)reader.End);
        }

        int rank = !isChunk ? 80 : frames > 0 ? 100 : 90;
        return new SniffResult(rank, reader.Start, frames, End: Math.Min(position, reader.End));
    }

    // Where the riff chunk ends by its own size, which counts the whole file. A size too small to hold the header is no size.
    public long? DeclaredEnd(SniffContext context) => Wave64.DeclaredEnd(context.Reader);

    public void Parse(ParseContext context)
    {
        ByteReader window = context.Reader;
        List<Node> tags = [.. context.EmbeddedTags.Where(t => t.Offset >= window.Start && t.Offset < window.End).OrderBy(t => t.Offset)];
        ByteReader reader = tags.Count > 0 ? window.Slice(window.Start, tags[0].Offset - window.Start) : window;
        long end = ParseChunks(context, reader);

        long position = Math.Max(end, reader.End);
        foreach (Node? tag in tags.Append<Node?>(null))
        {
            long stop = Math.Min(tag?.Offset ?? window.End, window.End);
            if (stop > position)
            {
                Node data = context.Root.Add(new Node("Data", position, stop - position, NodeValue.Integer(stop - position, "bytes", "byte")) { Description = Explain.AfterEmbeddedTag });
                context.AddFinding(Severity.Warning, "wav.data-after-tag", $"{Counts.Of(stop - position, "byte", "bytes")} at 0x{position:X} {Counts.Agree(stop - position, "follows", "follow")} an ID3v2 tag inside the riff chunk, which breaks the chain of chunks; they are not read.", data);
            }

            position = Math.Max(position, tag?.End ?? position);
        }
    }

    private static long ParseChunks(ParseContext context, ByteReader reader)
    {
        if (!Wave64.HasHeader(reader))
        {
            throw new InvalidDataException("The data does not start with the riff and wave GUIDs of a Wave64 file.");
        }

        var state = new State(context) { Start = reader.Start, Magic = Wave64.Magic, ContainerLabel = "riff chunk" };
        long? declaredEnd = Wave64.DeclaredEnd(reader);
        bool isPlaceholder = declaredEnd is null;
        long declared = declaredEnd ?? reader.End;
        long end = Math.Min(declared, reader.End);
        state.ContainerEnd = end;
        ulong stated = Wave64.ChunkSizeAt(reader, reader.Start) ?? 0;
        var container = new Node(Wave64.Magic, reader.Start, end - reader.Start, "WAVE") { Description = Explain.Wave64Container, Topic = Topics.Wave64 };
        state.Container = container;
        context.Root.Add(container);
        container.Add("Chunk ID", reader.Start, 16, Wave64.Describe(reader.ReadBytesAt(reader.Start, 16)), Explain.Wave64Id, Topics.Wave64);
        Node size = container.Add("Chunk Size", reader.Start + 16, 8, stated <= long.MaxValue ? NodeValue.Integer((long)stated, "bytes", "byte") : NodeValue.Hex(stated, 16), Explain.Wave64Size, Topics.Wave64);
        container.Add("Form Type", reader.Start + 24, 16, Wave64.Describe(reader.ReadBytesAt(reader.Start + 24, 16)), Explain.Wave64FormType, Topics.Wave64);

        if (isPlaceholder)
        {
            long held = reader.End - reader.Start;
            state.Findings.Add(Severity.Warning, "wav.riff-size-placeholder", size, _ =>
                $"The riff chunk's size is {(stated <= long.MaxValue ? stated.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0x" + stated.ToString("X", System.Globalization.CultureInfo.InvariantCulture))}, too small to hold the 40 bytes of the header or too large to be a size; it counts the whole file, so a writer that cannot go back leaves a value that is none. The file is read to its end, {Counts.Of(held, "byte", "bytes")}.");
        }
        else if (declared > reader.End)
        {
            long missing = declared - reader.End;
            state.Findings.Add(Severity.Warning, "wav.truncated", size, _ =>
                $"The riff chunk states {Counts.Of(declared - reader.Start, "byte", "bytes")}, but the data ends {Counts.Of(missing, "byte", "bytes")} short of that: the file is cut off.");
        }

        long stop = Wave64Walker.Walk(state, container, reader.Start + Wave64.HeaderLength, end);
        if (stop < end)
        {
            long left = end - stop;
            Node extra = container.Add(new Node("Extra Data", stop, left, NodeValue.Integer(left, "bytes", "byte")) { Description = Explain.ExtraData });
            state.Findings.Add(Severity.Warning, "wav.trailing-bytes", extra, _ => $"The riff chunk holds {Counts.Of(left, "byte", "bytes")} after its last chunk, too few for another.");
        }

        DataChunks.Finish(state);

        if (!isPlaceholder && end < reader.End)
        {
            long extra = reader.End - end;
            Node trailing = context.Root.Add(new Node("Trailing Data", end, extra, NodeValue.Integer(extra, "bytes", "byte")) { Description = Explain.TrailingData });
            state.Findings.Add(Severity.Warning, "wav.data-after-riff", trailing, _ =>
                $"{Counts.Of(extra, "byte", "bytes")} follow the riff chunk's end at 0x{end:X}, and are no tag the analyzer recognizes.");
        }

        state.Findings.Flush(context);
        return end;
    }
}

// The layout of Wave64: GUIDs as Windows stores them (the first three groups little-endian), the riff and wave GUIDs of the header, and the
// GUID of a chunk whose ID is four characters, which is those characters, then the same twelve bytes for every such chunk.
internal static class Wave64
{
    public const string Magic = "Wave64";

    // The riff GUID, 16 bytes, the size (8) and the wave GUID (16).
    public const int HeaderLength = 40;

    // A chunk's GUID and its size, which counts these 24 bytes.
    public const int ChunkHeaderLength = 24;

    public static readonly byte[] RiffGuid = Convert.FromHexString("726966662E91CF11A5D628DB04C10000");
    public static readonly byte[] WaveGuid = Convert.FromHexString("77617665F3ACD3118CD100C04F8EDB8A");
    public static readonly byte[] ListGuid = Convert.FromHexString("6C6973742F91CF11A5D628DB04C10000");

    // The twelve bytes after the four characters in the GUID of fmt, data, fact and the other chunks named by four characters.
    public static readonly byte[] TextTail = Convert.FromHexString("F3ACD3118CD100C04F8EDB8A");

    public static bool HasHeader(ByteReader reader) =>
        reader.Length >= HeaderLength && reader.ReadBytesAt(reader.Start, 16).AsSpan().SequenceEqual(RiffGuid) && reader.ReadBytesAt(reader.Start + 24, 16).AsSpan().SequenceEqual(WaveGuid);

    // The size field of the chunk header at position, or null when fewer than 24 bytes are there.
    public static ulong? ChunkSizeAt(ByteReader reader, long position) =>
        position + ChunkHeaderLength <= reader.End ? BinaryPrimitives.ReadUInt64LittleEndian(reader.ReadBytesAt(position + 16, 8)) : null;

    // The size rounded up to the 8 bytes every chunk starts at; a size over 2^63 stays as it is, since no file holds it.
    public static ulong Align(ulong size) => size > ulong.MaxValue - 7 ? size : (size + 7) & ~7UL;

    public static long? DeclaredEnd(ByteReader reader)
    {
        if (!HasHeader(reader) || ChunkSizeAt(reader, reader.Start) is not { } size || size < HeaderLength || size > long.MaxValue / 4)
        {
            return null;
        }

        return reader.Start + (long)size;
    }

    // The four characters of a GUID of the form <four printable characters><TextTail>, or null for any other GUID.
    public static string? TextId(ReadOnlySpan<byte> guid) =>
        guid.Length == 16 && guid[4..].SequenceEqual(TextTail) && Walker.IsFourCc(guid[..4]) ? System.Text.Encoding.Latin1.GetString(guid[..4]) : null;

    // The GUID in the form Windows writes it, with the name when it is one the layout gives.
    public static string Describe(ReadOnlySpan<byte> guid)
    {
        string text = new Guid(guid).ToString();
        if (guid.SequenceEqual(RiffGuid))
        {
            return $"{text} (riff)";
        }

        if (guid.SequenceEqual(WaveGuid))
        {
            return $"{text} (wave)";
        }

        if (guid.SequenceEqual(ListGuid))
        {
            return $"{text} (LIST)";
        }

        return TextId(guid) is { } id ? $"{text} ({id})" : text;
    }
}
