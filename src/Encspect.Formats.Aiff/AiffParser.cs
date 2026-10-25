using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Aiff;

// AIFF and AIFF-C files: a FORM chunk of the form type AIFF or AIFC, holding the Common Chunk with the parameters of the sound, the Sound Data
// Chunk with the samples, and other chunks (Apple, Audio Interchange File Format AIFF 1.3 and the AIFF-C draft of 8/26/91). The sample rate is
// an 80-bit extended number. The samples are described by the Common Chunk's figures. Tags after the FORM chunk (APEv2, ID3v1) are the host's;
// an ID3v2 tag in an "ID3 " chunk is the chunk's, which hands it to the tag parser.
public sealed class AiffParser : IFormatParser
{
    private const int SniffChunks = SniffResult.MaxFrames;

    public string Name => "AIFF";

    // The FORM header with the form type AIFF or AIFC is certain when a chunk header with an ID of printable characters follows it and its chunk
    // fits (class 100); a header cut short or followed by bytes that are no chunk is likely (90 and 80). The chunks that fit are counted as frames.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (FormHeader.Read(reader) is null)
        {
            return SniffResult.None;
        }

        long position = reader.Start + FormHeader.Length;
        if (position + 8 > reader.End)
        {
            return new SniffResult(80, reader.Start, 0, End: reader.End);
        }

        bool isChunk = Walker.IsId(reader.ReadBytesAt(position, 4));
        int frames = 0;
        while (isChunk && frames < SniffChunks && position + 8 <= reader.End && Walker.IsId(reader.ReadBytesAt(position, 4)))
        {
            uint size = Fields.Dword(reader.ReadBytesAt(position + 4, 4));
            if (size == uint.MaxValue)
            {
                frames++;
                position = reader.End;
                break;
            }

            long next = position + 8 + size + (size & 1);
            if (next > reader.End)
            {
                break;
            }

            frames++;
            position = next;
        }

        int rank = !isChunk ? 80 : frames > 0 ? 100 : 90;
        return new SniffResult(rank, reader.Start, frames, End: Math.Min(position, reader.End));
    }

    // Where the FORM chunk ends by its size. A size of 0 or 0xFFFFFFFF is the placeholder of a writer that could not fill it in, which the host
    // cannot trust to own the tags after it. An AIFF file's ID3 chunk, which taggers write last, lies inside the FORM chunk.
    public long? DeclaredEnd(SniffContext context)
    {
        ByteReader reader = context.Reader;
        return FormHeader.Read(reader)?.DeclaredEnd(reader.Start);
    }

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
                context.AddFinding(Severity.Warning, "aiff.data-after-tag", $"{Counts.Of(stop - position, "byte", "bytes")} at 0x{position:X} {Counts.Agree(stop - position, "follows", "follow")} an ID3v2 tag inside the FORM chunk, which breaks the chain of chunks; they are not read.", data);
            }

            position = Math.Max(position, tag?.End ?? position);
        }
    }

    private static long ParseChunks(ParseContext context, ByteReader reader)
    {
        FormHeader header = FormHeader.Read(reader) ?? throw new InvalidDataException("The data does not start with a FORM header of the form type AIFF or AIFC.");
        var state = new State(context) { Start = reader.Start, IsAifc = header.FormType == "AIFC" };

        long? declared = header.DeclaredEnd(reader.Start);
        long end = Math.Min(declared ?? reader.End, reader.End);
        state.ContainerEnd = end;
        var container = new Node("FORM", reader.Start, end - reader.Start, header.FormType) { Description = state.IsAifc ? Explain.FormAifc : Explain.FormAiff, Topic = Topics.Container };
        state.Container = container;
        context.Root.Add(container);
        container.Add("Chunk ID", reader.Start, 4, "FORM", Explain.FormId, Topics.Container);
        Node size = container.Add("Chunk Size", reader.Start + 4, 4, NodeValue.Integer(header.Size, "bytes", "byte"), Explain.FormSize, Topics.Container);
        container.Add("Form Type", reader.Start + 8, 4, header.FormType, Explain.FormType, Topics.Container);

        if (declared is null)
        {
            long held = reader.End - reader.Start - 8;
            string stated = header.Size == 0 ? "0" : header.Size == uint.MaxValue ? "0xFFFFFFFF" : $"{header.Size}, too small to hold the form type";
            state.Findings.Add(Severity.Warning, "aiff.form-size-placeholder", size, _ =>
                $"The FORM chunk's size is {stated}, which is no size a FORM chunk can have; a writer leaves it when it cannot go back and fill it in. The file is read to its end, {Counts.Of(held, "byte", "bytes")} after the size field.");
        }
        else if (declared > reader.End)
        {
            long missing = declared.Value - reader.End;
            long stated = declared.Value - reader.Start;
            state.Findings.Add(Severity.Warning, "aiff.truncated", size, _ =>
                $"The FORM chunk states {Counts.Of(stated, "byte", "bytes")}, but the data ends {Counts.Of(missing, "byte", "bytes")} short of that: the file is cut off.");
        }

        long stop = Walker.Chunks(state, container, reader.Start + FormHeader.Length, end);
        if (stop < end)
        {
            long left = end - stop;
            Node extra = container.Add(new Node("Extra Data", stop, left, NodeValue.Integer(left, "bytes", "byte")) { Description = Explain.ExtraData });
            state.Findings.Add(Severity.Warning, "aiff.trailing-bytes", extra, _ => $"The FORM chunk holds {Counts.Of(left, "byte", "bytes")} after its last chunk, too few for another.");
        }

        Summary.Finish(state);

        if (declared is not null && end < reader.End)
        {
            long extra = reader.End - end;
            Node trailing = context.Root.Add(new Node("Trailing Data", end, extra, NodeValue.Integer(extra, "bytes", "byte")) { Description = Explain.TrailingData });
            state.Findings.Add(Severity.Warning, "aiff.data-after-form", trailing, _ =>
                $"{Counts.Of(extra, "byte", "bytes")} follow the FORM chunk's end at 0x{end:X}, and are no tag the analyzer recognizes.");
        }

        state.Findings.Flush(context);
        return end;
    }
}

// The 12 bytes that start the FORM chunk: its ID, size and form type.
internal readonly record struct FormHeader(uint Size, string FormType)
{
    public const int Length = 12;

    // The header at the start of the reader, or null when the reader does not start with FORM and the form type AIFF or AIFC.
    public static FormHeader? Read(ByteReader reader)
    {
        if (reader.Length < Length)
        {
            return null;
        }

        byte[] bytes = reader.ReadBytesAt(reader.Start, Length);
        if (!bytes.AsSpan(0, 4).SequenceEqual("FORM"u8))
        {
            return null;
        }

        string form = Encoding.Latin1.GetString(bytes, 8, 4);
        return form is "AIFF" or "AIFC" ? new FormHeader(Fields.Dword(bytes.AsSpan(4)), form) : null;
    }

    // Where the FORM chunk ends by its size, or null for a size that cannot be one: 0, 0xFFFFFFFF, or one too small to hold the form type.
    public long? DeclaredEnd(long start) => Size is 0 or uint.MaxValue or < 4 ? null : start + 8 + Size;
}
