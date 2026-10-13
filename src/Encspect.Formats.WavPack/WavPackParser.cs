using Encspect.Core;

namespace Encspect.Formats.WavPack;

// WavPack files, main files (.wv) and correction files (.wvc): blocks of a 32-byte header and
// metadata sub-blocks, back to back (WavPack 4 and 5 binary file/block format; libwavpack 5.6.0).
// There is no magic number at the start and no end marker, so a stream is recognized by a header
// that passes libwavpack's checks and is followed by another one, and it ends where its last block
// does. Tags after it (APEv2, ID3v1) are the host's. The audio bitstreams are not decoded.
public sealed class WavPackParser : IFormatParser
{
    // How far into the data a first header is looked for when none is at its start, as libwavpack
    // looks up to 1 MiB (read_next_header).
    private const long SearchLimit = 1 << 20;

    public string Name => "WavPack";

    // A header at the start of the data that is followed by another one, or by the end of the data,
    // is certain; one followed by bytes that are none, or by a block the data cuts short, is likely
    // (a cut stream, or damage); a chain of blocks further in ranks below MPEG audio, which a stream
    // that starts with garbage may also hold.
    public int Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.Length < BlockHeader.Length)
        {
            return 0;
        }

        var source = new Source(reader);
        if (source.TryHeader(reader.Start, out BlockHeader header))
        {
            long end = reader.Start + header.BlockSize;
            if (end == reader.End || end == context.FirstEmbeddedTag)
            {
                return 100;
            }

            if (end < reader.End)
            {
                return source.TryHeader(end, out _) ? 100 : 80;
            }

            return 90;
        }

        return source.FindChain(reader.Start + 1, Math.Min(reader.End, reader.Start + SearchLimit)) >= 0 ? 60 : 0;
    }

    // Where the blocks that follow one another from the start end. A block states its own size, so a
    // tag signature inside one is no tag.
    public long? DeclaredEnd(SniffContext context)
    {
        ByteReader reader = context.Reader;
        var source = new Source(reader);
        long position = reader.Start;
        if (!source.TryHeader(position, out _))
        {
            return null;
        }

        for (int count = 0; source.TryHeader(position, out BlockHeader header); count++)
        {
            if (count % 4096 == 0)
            {
                reader.CancellationToken.ThrowIfCancellationRequested();
            }

            position += header.BlockSize;
            if (position >= reader.End)
            {
                break;
            }
        }

        return position;
    }

    public void Parse(ParseContext context)
    {
        ByteReader window = context.Reader;
        var tags = context.EmbeddedTags.Where(t => t.Offset >= window.Start && t.Offset < window.End).Select(t => (t.Offset, Math.Min(t.End, window.End)));
        var state = new State(new Source(window, tags), context.CancellationToken);
        Reader.Run(state);
        Summary.Finish(state, context);
    }
}
