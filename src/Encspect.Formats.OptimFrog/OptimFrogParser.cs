using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.OptimFrog;

// OptimFROG (OptimFROG 5.100 SDK, format.txt): a file is a run of blocks, each a 4-byte ID, a 4-byte length and the data: the main block, the stored
// WAV header, data blocks with the compressed audio and a CRC-32 each, the stored footer and optional blocks. The analyzer walks the blocks, compares
// every data block's CRC-32 and reads the main block's fields; the compressed audio itself is not read, since only OptimFROG's own library decodes it.
// Tags after the blocks (APEv2, ID3v1, Lyrics3) are the host's, which DeclaredEnd lets it tell from bytes inside the file; an ID3v2 tag before it too.
public sealed class OptimFrogParser : IFormatParser
{
    public const string FormatName = "OptimFROG";

    private static ReadOnlySpan<byte> Signature => "OFR "u8;

    private static ReadOnlySpan<byte> FloatSignature => "OFRX"u8;

    public string Name => FormatName;

    // The signature, the main block's fields and the block after it are the evidence: a main block with a length of at least 15, a sample type of 0 to
    // 10, a channel configuration of 0 or 1, a sample rate above 0 and a known block after it is certain (class 100); the main block alone with valid
    // fields is likely (80); the four bytes alone, or fields that cannot be a main block's, are possible (70). Only the start of the data counts: the
    // specification puts the main block first (after an ID3v2 tag), and a self-extracting file's executable stub is not matched.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.Length < 4)
        {
            return SniffResult.None;
        }

        long start = reader.Start;
        Span<byte> bytes = stackalloc byte[Layout.BlockHeaderLength + Layout.MainFields];
        int available = (int)Math.Min(bytes.Length, reader.Length);
        reader.Position = start;
        reader.Read(bytes[..available]);
        if (!bytes[..Math.Min(4, available)].SequenceEqual(Signature) && !bytes[..Math.Min(4, available)].SequenceEqual(FloatSignature))
        {
            return SniffResult.None;
        }

        if (available < bytes.Length)
        {
            return new SniffResult(70, start, 0, 0, "main block cut short", start + available);
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        MainBlock main = MainBlock.Read(bytes[Layout.BlockHeaderLength..], size);
        bool areFieldsValid = size >= Layout.MainFields && main.SampleType <= 10 && main.ChannelConfiguration <= 1 && main.SampleRate > 0;
        if (!areFieldsValid)
        {
            return new SniffResult(70, start, 0, 0, "main block fields not valid", start + Math.Min(reader.Length, Layout.BlockHeaderLength + size));
        }

        long mainEnd = start + Layout.BlockHeaderLength + size;
        bool hasKnownNext = mainEnd < reader.End && Walk.TryReadHeader(reader, mainEnd, out uint id, out _) && Layout.IsKnown(id);
        long end = Math.Min(Walk.End(reader, SniffResult.MaxFrames), reader.End);
        return new SniffResult(hasKnownNext ? 100 : 80, start, 1, 0, hasKnownNext ? null : "no known block after the main block", end);
    }

    // The end of the last block the walk accepts: where the host looks for trailing tags. It lies past the end of a file cut in a block.
    public long? DeclaredEnd(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.Length < Layout.BlockHeaderLength + Layout.MainFields || !Walk.Accepts(reader, reader.Start, out uint id, out uint length, out _) || !Layout.IsMain(id) || length < Layout.MainFields)
        {
            return null;
        }

        return Walk.End(reader);
    }

    public void Parse(ParseContext context)
    {
        ByteReader reader = context.Reader;
        var state = new State(context);
        Walk.Run(state, reader);
        Records records = state.Records;
        state.Stream = Summary.StreamNode(state, state.Source);
        context.Root.Add(state.Stream);
        Summary.Finish(state, state.Source);
        long end = records.End;
        if (end < reader.End)
        {
            long length = reader.End - end;
            Node data = context.Root.Add(new Node("Trailing Data", end, length, NodeValue.Integer(length, "bytes", "byte")) { Description = Explain.Trailing });
            context.AddFinding(Severity.Warning, "optimfrog.data-after-blocks",
                $"{Counts.Of(length, "byte", "bytes")} at 0x{end:X} {Counts.Agree(length, "follows", "follow")} the last block and {Counts.Agree(length, "is", "are")} no tag; {Counts.Agree(length, "it is", "they are")} not read.", data);
        }
    }
}
