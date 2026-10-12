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

    // A magic number is certain. A stream of version 4 to 6 has none, so it needs its frames to end
    // exactly at the end of the data, and ranks below MPEG audio's chain of frames (90).
    public int Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.MatchesAt(reader.Start, "MPCK"u8))
        {
            return Sv8Parser.Sniff(reader) ? 100 : 0;
        }

        if (reader.MatchesAt(reader.Start, "MP+"u8))
        {
            return 100;
        }

        return OldStreamParser.Sniff(reader) ? 60 : 0;
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

    public void Parse(ParseContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.MatchesAt(reader.Start, "MPCK"u8))
        {
            Sv8Parser.Parse(reader, context.Root, context, context.EncoderClaims);
        }
        else if (reader.MatchesAt(reader.Start, "MP+"u8))
        {
            Sv7Parser.Parse(reader, context.Root, context, context.EncoderClaims);
        }
        else
        {
            OldStreamParser.Parse(reader, context.Root, context);
        }
    }
}
