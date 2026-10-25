using Encspect.Core;

namespace Encspect.Formats.Asf;

// Advanced Systems Format (ASF), the container of Windows Media files (.wma, .wmv, .asf): the
// Advanced Systems Format Specification, revision 01.20.03, with FFmpeg's asfdec_f.c as the
// reference implementation. A file is a run of objects, each a 128-bit GUID, a 64-bit size and its
// data: the Header Object (which holds the File Properties, Stream Properties, Header Extension and
// the other header objects), the Data Object (fixed-size packets that carry the payloads of the
// streams) and the index objects. Every object becomes a node with its fields at their bytes. The
// media objects of each audio stream are put together from their payloads and go to the codec
// parser of their coding, WMA through the "wma", "wma-pro", "wma-lossless" and "wma-voice" routes
// with the stream's WAVEFORMATEX and codec-specific data; the codec reports the stream.
public sealed partial class AsfParser : IFormatParser
{
    // The codec parsers' IDs by the format tag of an audio stream (ASF specification, section 11.1).
    private static readonly IReadOnlyDictionary<int, string> DefaultRoutes = new Dictionary<int, string>
    {
        [0x0160] = "wma",
        [0x0161] = "wma",
        [0x0162] = "wma-pro",
        [0x0163] = "wma-lossless",
        [0x000A] = "wma-voice",
    };

    private readonly IReadOnlyDictionary<int, string> _routes;

    public AsfParser()
        : this(DefaultRoutes)
    {
    }

    // For tests: which codec parser gets the streams of which format tags.
    internal AsfParser(IReadOnlyDictionary<int, string> routes)
    {
        _routes = routes;
    }

    // A header object is at least this big: the 16-byte GUID, the 8-byte size, the object count and
    // the two reserved bytes (section 3.1).
    internal const int HeaderObjectMinimum = 30;

    internal const int ObjectHeaderSize = 24;

    public string Name => "ASF";

    // The Header Object's GUID at the start is certain: 128 bits that no other file starts with.
    // The Header Object must be first in every ASF file (section 2.2), and FFmpeg's probe accepts
    // the GUID alone.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        return reader.Length >= ObjectHeaderSize && StartsWithHeader(reader) ? new SniffResult(100, reader.Start, 1) : SniffResult.None;
    }

    // The end of the file by its own account: the File Properties Object's File Size, which the
    // specification gives as the size of the entire file (section 3.2); where that is unset (a
    // broadcast file) or cannot be right, the end of the run of top-level objects. A tag after it is
    // the file's, not an object's.
    public long? DeclaredEnd(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.Length < ObjectHeaderSize || !StartsWithHeader(reader))
        {
            return null;
        }

        try
        {
            return DeclaredEnd(reader);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    // Tags an ID3v2.4 SEEK frame points to inside the data are already in the report. Such a tag breaks the object
    // structure, so the objects are read up to the first one, and the bytes between and after the tags are reported
    // as not read. (The host passes them only where the file declares no end of its own, a Header Object smaller than
    // 30 bytes; within a declared end the objects own the bytes.)
    public void Parse(ParseContext context)
    {
        ByteReader window = context.Reader;
        List<Node> tags = [.. context.EmbeddedTags.Where(t => t.Offset >= window.Start && t.Offset < window.End).OrderBy(t => t.Offset)];
        ByteReader reader = tags.Count > 0 ? window.Slice(window.Start, tags[0].Offset - window.Start) : window;
        ParseFile(context, reader, _routes);
        long position = reader.End;
        foreach (Node? tag in tags.Append<Node?>(null))
        {
            long end = Math.Min(tag?.Offset ?? window.End, window.End);
            if (end > position)
            {
                Node data = context.Root.Add(new Node("Data", position, end - position, NodeValue.Integer(end - position, "bytes")) { Description = Explain.AfterEmbeddedTag });
                context.AddFinding(Severity.Warning, "asf.data-after-tag",
                    $"{Counts.Of(end - position, "byte", "bytes")} at 0x{position:X} {Counts.Agree(end - position, "follows", "follow")} an ID3v2 tag inside the ASF data, which breaks its objects; they are not read.", data);
            }

            position = Math.Max(position, tag?.End ?? position);
        }
    }

    private static bool StartsWithHeader(ByteReader reader)
    {
        Span<byte> guid = stackalloc byte[16];
        reader.Position = reader.Start;
        reader.Read(guid);
        return new Guid(guid) == AsfGuids.Header;
    }

    private static long? DeclaredEnd(ByteReader reader)
    {
        long start = reader.Start;
        if (ReadObjectHeader(reader, start, reader.End) is not { } header || header.Size < HeaderObjectMinimum)
        {
            return null;
        }

        long position = start + ObjectHeaderSize + 6;
        long headerEnd = Math.Min(header.End, reader.End);
        int seen = 0;
        while (position + ObjectHeaderSize <= headerEnd && seen++ < 4096)
        {
            reader.CancellationToken.ThrowIfCancellationRequested();
            if (ReadObjectHeader(reader, position, headerEnd) is not { } child || child.Size < ObjectHeaderSize)
            {
                break;
            }

            if (child.Id == AsfGuids.FileProperties && child.Size >= 104)
            {
                reader.Position = position + ObjectHeaderSize + 16;
                ulong fileSize = reader.ReadUInt64LittleEndian();
                reader.Position = position + ObjectHeaderSize + 16 + 8 + 8 + 8 + 8 + 8 + 8;
                uint flags = reader.ReadUInt32LittleEndian();

                // A file at least as big as its header and the Data Object's header, and not a
                // broadcast, where the field is invalid (section 3.2).
                if ((flags & 1) == 0 && fileSize >= (ulong)(header.Size + ObjectHeaderSize) && fileSize <= (ulong)long.MaxValue - (ulong)start)
                {
                    return start + (long)fileSize;
                }

                break;
            }

            position = child.End;
        }

        // The run of top-level objects from the start, each fitting in the file.
        position = start;
        long last = start;
        for (int count = 0; count < 100_000 && position + ObjectHeaderSize <= reader.End; count++)
        {
            reader.CancellationToken.ThrowIfCancellationRequested();
            if (ReadObjectHeader(reader, position, long.MaxValue) is { } open && open.Id == AsfGuids.Data && open.Size == 0)
            {
                // A Data Object of size 0 (a broadcast file) runs to the end of the file, and so owns what is at its end.
                return reader.End;
            }

            if (ReadObjectHeader(reader, position, long.MaxValue) is not { } top || top.Size < ObjectHeaderSize)
            {
                break;
            }

            last = top.End;
            if (top.End >= reader.End)
            {
                break;
            }

            position = top.End;
        }

        return last > start ? last : null;
    }

    internal readonly record struct ObjectHeader(Guid Id, ulong Size, long Offset)
    {
        // May lie past the end of the file or overflow; callers clamp.
        public long End => Size > (ulong)(long.MaxValue - Offset) ? long.MaxValue : Offset + (long)Size;
    }

    // The GUID and size at position, or null where fewer than 24 bytes remain before limit.
    internal static ObjectHeader? ReadObjectHeader(ByteReader reader, long position, long limit)
    {
        if (limit - position < ObjectHeaderSize || reader.End - position < ObjectHeaderSize)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[ObjectHeaderSize];
        reader.Position = position;
        reader.Read(bytes);
        return new ObjectHeader(new Guid(bytes[..16]), System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), position);
    }
}
