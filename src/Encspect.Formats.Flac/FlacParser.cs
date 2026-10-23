using Encspect.Core;

namespace Encspect.Formats.Flac;

// Native FLAC (RFC 9639): the marker "fLaC", metadata blocks, then frames, each a header with a CRC-8, a subframe for every channel and a
// CRC-16. The frames are walked to the end of their residual, which is how their length is known and how the CRC-16 is checked; with decoding
// on, the walk stores the samples for the decoder (docs/design/decoders.md, "The second entropy pass"). Tags after the stream (APEv2, ID3v1)
// are the host's; an ID3v2 tag before it too.
public sealed class FlacParser : IFormatParser
{
    private static readonly byte[] Marker = "fLaC"u8.ToArray();

    // How far into the data a first frame is looked for when the stream has no marker.
    private const long SearchLimit = 1 << 20;

    public string Name => "FLAC";

    // What a search for frames in damaged data may read in candidates that did not hold, beyond four times the data. A test lowers it.
    internal long ProbeAllowance { get; init; } = 64L << 20;

    // False makes the walk store nothing, so the decoder reads every frame again: the contract as first drafted, which the measurement
    // in docs/design/decoders.md compares the lending walk with.
    internal bool LendsBuffers { get; init; } = true;

    // The marker followed by a STREAMINFO block header is certain (class 100); the marker with another first block is likely (80). Without
    // the marker, a chain of frames whose headers, CRC-8 and CRC-16 hold: 85 from the start of the data, 50 further in, as the other formats'
    // chains are.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.Length < 4)
        {
            return SniffResult.None;
        }

        var source = new Source(reader);
        long start = reader.Start;
        if (source.Window(start, 4).StartsWith(Marker))
        {
            return SniffMarker(source, start);
        }

        var scanner = new FrameScanner(source, null, 16L << 20);
        long found = scanner.FindNext(start, Math.Min(reader.End, start + SearchLimit));
        if (found < 0)
        {
            return SniffResult.None;
        }

        int frames = 0;
        long position = found;
        while (frames < SniffResult.MaxFrames && position < reader.End && scanner.IsChained(position))
        {
            Attempt attempt = scanner.Read(position, null);
            position += attempt.Walk.Length;
            frames++;
        }

        return new SniffResult(found == start ? 85 : 50, found, frames, frames, End: position);
    }

    private static SniffResult SniffMarker(Source source, long start)
    {
        long end = source.End;
        long position = start + 4;
        ReadOnlySpan<byte> first = source.Window(position, 4);
        bool isStreamInfo = first.Length == 4 && (first[0] & 0x7F) == BlockTypes.StreamInfo && ((first[1] << 16) | (first[2] << 8) | first[3]) == StreamInfoBlock.Length;
        int blocks = 0;
        bool isLast = false;
        while (blocks < SniffResult.MaxFrames && !isLast && position + 4 <= end)
        {
            ReadOnlySpan<byte> header = source.Window(position, 4);
            long bodyEnd = position + 4 + ((header[1] << 16) | (header[2] << 8) | header[3]);
            if ((header[0] & 0x7F) == BlockTypes.Forbidden || bodyEnd > end)
            {
                break;
            }

            blocks++;
            isLast = (header[0] & 0x80) != 0;
            position = bodyEnd;
        }

        int verified = 0;
        if (isLast && position + FrameHeader.MinLength <= end && FrameHeader.TryRead(source.Window(position, FrameHeader.MaxLength), out _) == HeaderFault.None)
        {
            verified = 1;
        }

        return new SniffResult(isStreamInfo ? 100 : 80, start, blocks + verified, verified, End: position);
    }

    public void Parse(ParseContext context)
    {
        ByteReader window = context.Reader;
        List<Node> tags = [.. context.EmbeddedTags.Where(t => t.Offset >= window.Start && t.Offset < window.End).OrderBy(t => t.Offset)];
        ByteReader reader = tags.Count > 0 ? window.Slice(window.Start, tags[0].Offset - window.Start) : window;
        var state = new State(context, isContained: false);
        Node stream = Summary.StreamNode(reader.Start, reader.End - reader.Start, state.Leading, state.Records);
        state.Stream = stream;
        context.Root.Add(stream);
        RawReader.Run(state, new Source(reader), ProbeAllowance, LendsBuffers);
        Summary.Finish(state, reader.Start, reader.End);

        long position = reader.End;
        foreach (Node? tag in tags.Append<Node?>(null))
        {
            long end = Math.Min(tag?.Offset ?? window.End, window.End);
            if (end > position)
            {
                Node data = context.Root.Add(new Node("Data", position, end - position, NodeValue.Integer(end - position, "bytes", "byte")) { Description = Explain.AfterEmbeddedTag });
                context.AddFinding(Severity.Warning, "flac.data-after-tag", $"{Counts.Of(end - position, "byte", "bytes")} at 0x{position:X} {Counts.Agree(end - position, "follows", "follow")} an ID3v2 tag inside the FLAC data, which breaks the chain of frames; they are not read.", data);
            }

            position = Math.Max(position, tag?.End ?? position);
        }
    }
}
