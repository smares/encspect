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
    // is certain (class 100); one followed by bytes that are none, or by a block the data cuts short,
    // is likely (80 and 90: a cut stream, or damage); a chain of blocks further in is class 60, below
    // MPEG audio, which a stream that starts with garbage may also hold. The blocks have no checksum
    // of the whole block that the sniff computes, so none counts as verified.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.Length < BlockHeader.Length)
        {
            return SniffResult.None;
        }

        var source = new Source(reader);
        if (source.TryHeader(reader.Start, out BlockHeader header))
        {
            long end = reader.Start + header.BlockSize;
            if (end == reader.End || end == context.FirstEmbeddedTag)
            {
                return new SniffResult(100, reader.Start, 1, End: Math.Min(end, reader.End));
            }

            if (end < reader.End)
            {
                return source.TryHeader(end, out BlockHeader next) ? new SniffResult(100, reader.Start, 2, End: Math.Min(end + next.BlockSize, reader.End)) : new SniffResult(80, reader.Start, 1, End: end);
            }

            return new SniffResult(90, reader.Start, 1, End: reader.End);
        }

        long found = source.FindChain(reader.Start + 1, Math.Min(reader.End, reader.Start + SearchLimit));
        return found >= 0 ? new SniffResult(60, found, 2, End: reader.End) : SniffResult.None;
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

    // Tags an ID3v2.4 SEEK frame found inside the data are already in the report. A tag between
    // blocks breaks the chain, so the blocks are read up to the first one, and the bytes between and
    // after the tags are reported as not read.
    public void Parse(ParseContext context)
    {
        ByteReader window = context.Reader;
        List<Node> tags = [.. context.EmbeddedTags.Where(t => t.Offset >= window.Start && t.Offset < window.End).OrderBy(t => t.Offset)];
        ByteReader reader = tags.Count > 0 ? window.Slice(window.Start, tags[0].Offset - window.Start) : window;
        var state = new State(new Source(reader), context.CancellationToken);
        Reader.Run(state);
        Summary.Finish(state, context);

        long position = reader.End;
        foreach (Node? tag in tags.Append<Node?>(null))
        {
            long end = Math.Min(tag?.Offset ?? window.End, window.End);
            if (end > position)
            {
                Node data = context.Root.Add(new Node("Data", position, end - position, NodeValue.Integer(end - position, "bytes", "byte")) { Description = Explain.AfterEmbeddedTag });
                context.AddFinding(Severity.Warning, "wavpack.data-after-tag",
                    $"{Counts.Of(end - position, "byte", "bytes")} at 0x{position:X} follow an ID3v2 tag inside the WavPack data, which breaks the chain of blocks; they are not read.", data);
            }

            position = Math.Max(position, tag?.End ?? position);
        }
    }
}
