using Encspect.Core;

namespace Encspect.Formats.Tak;

// TAK (TAK 2.3.3, Sdk/Source/takStream.pas, version 2.2.0 of the module; see Layout.cs for its notice and the statement that this reader is not an official release): the stream
// ID "tBaK", metadata objects each ending in a CRC-24, then the audio frames, each with a header CRC-24 and a data CRC-24. The analyzer walks the metadata objects, compares their CRC-24s
// and reads the stream info, the encoder info, the wave data and the last frame info; the frames are one run of bytes in 0.2, since only a decoder reads their coding. Tags after the
// frames (APEv2) are the host's, which DeclaredEnd lets it tell from bytes inside the stream; an ID3v2 tag before it too.
public sealed class TakParser : IFormatParser
{
    public const string FormatName = "TAK";

    public string Name => FormatName;

    // The signature and the stream info are the evidence: "tBaK" followed by an object header with ID 1 and a plausible size whose CRC-24 holds is certain (class 100); the same with
    // a CRC-24 that fails is likely (80); the signature alone, or with something else after it, is possible (70). Only the start of the data counts.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        long start = reader.Start;
        if (reader.Length < Layout.SignatureLength || !reader.MatchesAt(start, Layout.Signature))
        {
            return SniffResult.None;
        }

        if (reader.Length < Layout.SignatureLength + Layout.ObjectHeaderLength)
        {
            return new SniffResult(70, start, 0, 0, "cut after the signature", reader.End);
        }

        reader.Position = start + Layout.SignatureLength;
        Layout.ReadObjectHeader(reader.ReadUInt32LittleEndian(), out int id, out bool isReserved, out int size);
        long end = start + Layout.SignatureLength + Layout.ObjectHeaderLength + size;
        if (id != Layout.StreamInfo || isReserved || size is < Layout.StreamInfoSizeMin or > Layout.StreamInfoSizeMax)
        {
            return new SniffResult(70, start, 0, 0, "no stream info object after the signature", Math.Min(start + Layout.SignatureLength, reader.End));
        }

        if (end > reader.End)
        {
            return new SniffResult(80, start, 1, 0, "stream info cut short", reader.End);
        }

        byte[] data = reader.ReadBytes(size);
        uint stored = (uint)(data[size - 3] | (data[size - 2] << 8) | (data[size - 1] << 16));
        return Layout.Crc24(data.AsSpan(0, size - Layout.CrcLength)) == stored
            ? new SniffResult(100, start, 1, 1, End: end)
            : new SniffResult(80, start, 1, 0, "stream info CRC-24 differs", end);
    }

    // The end of the last frame by the last frame info object (TAK 1.1.1 and later), when its CRC-24 holds; it lies past the end of a file cut in the audio. Null for a file without
    // the object or with a failing CRC-24: the trailing tags are then looked for as in a file with no declared size.
    public long? DeclaredEnd(SniffContext context)
    {
        ByteReader reader = context.Reader;
        var records = new Records();
        Walk.Run(records, reader, s: null);
        return records.DeclaredEnd;
    }

    public void Parse(ParseContext context)
    {
        ByteReader reader = context.Reader;
        var state = new State(context);
        Walk.Run(state.Records, reader, state);
        Records r = state.Records;
        if (!r.IsSignaturePresent)
        {
            Node unread = context.Root.Add(new Node("Data", reader.Start, reader.Length, NodeValue.Integer(reader.Length, "bytes", "byte")) { Description = Explain.Unread });
            context.AddFinding(Severity.Warning, "tak.no-signature", "The data does not begin with the stream ID \"tBaK\" of a TAK file, so it is not read as one.", unread);
            return;
        }

        long end = r.DeclaredEnd is { } declared ? Math.Min(declared, reader.End) : reader.End;
        Summary.Finish(state, reader.Start, end);
    }
}
