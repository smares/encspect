using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio;

// Monkey's Audio files (.ape, .mac): a descriptor and header, a seek table, the WAV header data, frames of range-coded residuals and the WAV
// terminating data (Monkey's Audio SDK 13.42). Files before version 3.98 have a header of their own and no descriptor. Tags after the data (APEv2,
// ID3v1) are the host's; an ID3v2 tag before it too. The coded samples are decoded only with decoding on (docs/formats/monkeys-audio.md).
public sealed class MonkeysAudioParser : IFormatParser
{
    public string Name => "Monkey's Audio";

    // The signature at the start of the data with a header the SDK would take is certain (class 100); the signature with a header that holds
    // nothing plausible is likely (80); a signature further in, within the first MiB the SDK scans, with a plausible header is class 60, since four
    // characters can occur anywhere. The MD5 of the coded data needs the whole file, so no sniff counts a verified frame.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.Length < 6)
        {
            return SniffResult.None;
        }

        var source = new Source(reader);
        long found = Reader.FindSignature(source, reader.Start, reader.Start + Reader.SearchLimit + 4);
        if (found < 0)
        {
            return SniffResult.None;
        }

        bool isPlausible;
        ApeHeader header = default;
        try
        {
            header = Reader.ReadHeader(source, found, out _);
            isPlausible = Reader.IsPlausible(header);
        }
        catch (EndOfStreamException)
        {
            isPlausible = false;
        }

        if (found == reader.Start)
        {
            return new SniffResult(isPlausible ? 100 : 80, found, 1, End: Math.Min(reader.End, found + (isPlausible ? header.FixedLength : ApeHeader.DescriptorLength)));
        }

        return isPlausible ? new SniffResult(60, found, 1, End: Math.Min(reader.End, found + header.FixedLength)) : SniffResult.None;
    }

    public bool HasDeclaredEnd => true;

    // Where the file ends by its own account: the descriptor counts the frame data and the terminating data. Files before version 3.98 state
    // no size.
    public long? DeclaredEnd(SniffContext context)
    {
        ByteReader reader = context.Reader;
        var source = new Source(reader);
        long found = Reader.FindSignature(source, reader.Start, reader.Start + Reader.SearchLimit + 4);
        if (found < 0)
        {
            return null;
        }

        try
        {
            ApeHeader header = Reader.ReadHeader(source, found, out _);
            if (!header.HasDescriptor || !Reader.IsPlausible(header))
            {
                return null;
            }

            long end = header.DataOffset(found) + (long)Math.Min(header.FrameDataBytes, 1UL << 60) + header.TerminatingBytes;
            return end;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    public void Parse(ParseContext context)
    {
        ByteReader window = context.Reader;
        List<Node> tags = [.. context.EmbeddedTags.Where(t => t.Offset >= window.Start && t.Offset < window.End).OrderBy(t => t.Offset)];
        ByteReader reader = tags.Count > 0 ? window.Slice(window.Start, tags[0].Offset - window.Start) : window;
        var state = new State(context, reader);
        Reader.Run(state);

        long position = reader.End;
        foreach (Node? tag in tags.Append<Node?>(null))
        {
            long end = Math.Min(tag?.Offset ?? window.End, window.End);
            if (end > position)
            {
                Node data = context.Root.Add(new Node("Data", position, end - position, NodeValue.Integer(end - position, "bytes", "byte")) { Description = Explain.AfterEmbeddedTag });
                context.AddFinding(Severity.Warning, "monkeys-audio.data-after-tag", $"{Counts.Of(end - position, "byte", "bytes")} at 0x{position:X} {Counts.Agree(end - position, "follows", "follow")} an ID3v2 tag inside the Monkey's Audio data, which breaks it; they are not read.", data);
            }

            position = Math.Max(position, tag?.End ?? position);
        }
    }
}
