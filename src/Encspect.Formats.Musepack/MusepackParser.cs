using Encspect.Core;

namespace Encspect.Formats.Musepack;

// Musepack files: SV8 ("MPCK", SV8 specification), SV7 ("MP+", libmpcdec r495) and the stream
// versions 4 to 6 before them, which have no magic number (libmpcdec 1.2.6). The stream starts at
// the start of the data, after any ID3v2 tag, as libmpcdec and ffmpeg expect it; tags after it (APE,
// ID3v1) are the host's. SV7 and SV8 state where they end, by their frame count or their stream end
// packet (DeclaredEnd), so a tag signature inside the audio is not taken for a tag.
public sealed class MusepackParser : IFormatParser
{
    public string Name => "Musepack";

    // A magic number is certain, as long as the header after "MP+" is one an encoder writes
    // (Sv7Parser.IsConsistent); "MP+" at the start of other data ranks 60. A stream of version 4 to 6 has none, so it needs its units to end
    // where the encoders end them (OldStreamParser.Sniff). With three units or more, starting at the
    // first byte, that outranks MPEG audio (90), which also takes one or two frames that end at the
    // end of the data anywhere in it; with fewer it ranks below.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.MatchesAt(reader.Start, "MPCK"u8))
        {
            return Sv8Parser.Sniff(reader) ? new SniffResult(100, reader.Start, 1, Note: "MPCK") : SniffResult.None;
        }

        if (reader.MatchesAt(reader.Start, "MP+"u8))
        {
            return Sv7Parser.IsConsistent(reader) ? new SniffResult(100, reader.Start, 1, Note: "MP+") : new SniffResult(60, reader.Start, 1, Note: "MP+, inconsistent header");
        }

        // The parser reads up to the first tag inside the data, so the frames must end there.
        return OldStreamParser.Sniff(context.FirstEmbeddedTag is { } tag && tag > reader.Start && tag < reader.End ? reader.Slice(reader.Start, tag - reader.Start) : reader);
    }

    public long? DeclaredEnd(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.MatchesAt(reader.Start, "MPCK"u8))
        {
            return Sv8Parser.DeclaredEnd(reader);
        }

        return reader.MatchesAt(reader.Start, "MP+"u8) ? Sv7Parser.DeclaredEnd(reader) : null;
    }

    // Tags an ID3v2.4 SEEK frame points to inside the data are already in the report. A tag inside
    // the bitstream of SV7 or the packets of SV8 breaks it, so the stream is read up to the first
    // such tag, and the bytes between and after them are reported as not read.
    public void Parse(ParseContext context)
    {
        ByteReader window = context.Reader;
        List<Node> tags = [.. context.EmbeddedTags.Where(t => t.Offset >= window.Start && t.Offset < window.End).OrderBy(t => t.Offset)];
        ByteReader reader = tags.Count > 0 ? window.Slice(window.Start, tags[0].Offset - window.Start) : window;
        if (window.MatchesAt(window.Start, "MPCK"u8))
        {
            Sv8Parser.Parse(reader, context.Root, context, context.EncoderClaims);
        }
        else if (window.MatchesAt(window.Start, "MP+"u8))
        {
            Sv7Parser.Parse(reader, context.Root, context, context.EncoderClaims);
        }
        else
        {
            OldStreamParser.Parse(reader, context.Root, context);
        }

        long position = reader.End;
        foreach (Node? tag in tags.Append<Node?>(null))
        {
            long end = Math.Min(tag?.Offset ?? window.End, window.End);
            if (end > position)
            {
                Node data = context.Root.Add(new Node("Data", position, end - position, NodeValue.Integer(end - position, "bytes")) { Description = Explain.AfterEmbeddedTag });
                context.AddFinding(Severity.Warning, "musepack.data-after-tag",
                    $"{Counts.Of(end - position, "byte", "bytes")} at 0x{position:X} follow an ID3v2 tag inside the Musepack stream, which breaks it; they are not read.", data);
            }

            position = Math.Max(position, tag?.End ?? position);
        }
    }
}
