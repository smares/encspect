using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Encspect.Core;

// The nodes, findings and figures of a WavPack file of format version 1 to 3 follow libwavpack 5.6.0's src/unpack3_open.c and src/unpack3.c (BSD 3-Clause, Copyright (c)
// 1998 - 2022 David Bryant, all rights reserved; the notice is in THIRD-PARTY-NOTICES.md) and the encoder and decoder of WavPack 3.97 (wavpack.c, wvunpack.c, BSD).
// Nothing is decoded here: the bitstream is one node and its end is where a trailer begins, or the end of the data.
namespace Encspect.Formats.WavPack;

internal static partial class Legacy
{
    private const int RawRate = 44100;
    private const long MaxChunkBeforeData = 1 << 20;

    // The streams of the WAV sniff step back to class 70 for a file like this one, so a legacy file is never named WAV by its first chunks alone.
    public static SniffResult? Sniff(ByteReader reader)
    {
        LegacyLayout? layout = LegacyLayout.Locate(reader);
        if (layout is null)
        {
            return null;
        }

        int length = LegacyLayout.HeaderLength(layout.Version);
        bool fits = length > 0 && layout.HeaderAt + length <= reader.End;
        if (layout.IsRaw)
        {
            LegacyHeader header = LegacyHeader.Read(reader, layout.HeaderAt);
            bool isCertain = layout.Version == 3 && fits && (header.Flags & LegacyHeader.RawFlag) != 0 && header.Size == 28;
            return new SniffResult(isCertain ? 100 : 80, reader.Start, 1, End: reader.End);
        }

        return new SniffResult(fits && layout.Version is >= 1 and <= 3 ? 100 : 80, reader.Start, Math.Min(SniffResult.MaxFrames, layout.Chunks.Count + 1), End: reader.End);
    }

    private static Node Field(long offset, long length, string name, NodeValue? value, string description) => Nodes.Field(offset, length, name, value, description, LegacyExplain.Topic);

    private static readonly (int Bit, string Name, string Description)[] FlagList =
    [
        (LegacyHeader.MonoFlag, "IsMono", LegacyExplain.FlagMono),
        (LegacyHeader.FastFlag, "IsFast", LegacyExplain.FlagFast),
        (LegacyHeader.RawFlag, "IsRaw", LegacyExplain.FlagRaw),
        (LegacyHeader.CalcNoise, "IsNoiseCalculated", LegacyExplain.FlagCalcNoise),
        (LegacyHeader.HighFlag, "IsHigh", LegacyExplain.FlagHigh),
        (LegacyHeader.Bytes3Flag, "HasThreeByteSamples", LegacyExplain.FlagBytes3),
        (LegacyHeader.Over20Flag, "IsOver20Bits", LegacyExplain.FlagOver20),
        (LegacyHeader.WvcFlag, "HasCorrectionFile", LegacyExplain.FlagWvc),
        (LegacyHeader.LossyShapeFlag, "IsNoiseShaped", LegacyExplain.FlagLossyShape),
        (LegacyHeader.VeryFastFlag, "IsVeryFast", LegacyExplain.FlagVeryFast),
        (LegacyHeader.NewHighFlag, "IsNewHigh", LegacyExplain.FlagNewHigh),
        (LegacyHeader.CancelExtremeFlag, "IsExtremeCanceled", LegacyExplain.FlagCancelExtreme),
        (LegacyHeader.CrossDecorrFlag, "IsCrossChannel", LegacyExplain.FlagCrossDecorr),
        (LegacyHeader.NewDecorrFlag, "HasNewDecorrelator", LegacyExplain.FlagNewDecorr),
        (LegacyHeader.JointStereoFlag, "IsJointStereo", LegacyExplain.FlagJointStereo),
        (LegacyHeader.ExtremeDecorrFlag, "IsExtreme", LegacyExplain.FlagExtreme),
    ];

    // The audio's layout as the stored WAV header states it (the fields unpack3_open.c checks).
    private readonly record struct Format(int Tag, int Channels, long Rate, int BlockAlign, int Bits, long At);

    // The nodes of the header, with the fields findings point to.
    private sealed class HeaderNodes
    {
        public required Node Header { get; init; }

        public Node? Version { get; set; }

        public Node? Flags { get; set; }

        public Node? Samples { get; set; }

        public Node? Extra { get; set; }
    }

    // isCompanion: the file was opened as the correction file of a version 4 or 5 hybrid file, which is not its correction data (a version 4 file has blocks, this has none): its
    // tree is built as it is alone, with no stream of its own, no decoding and no correction file asked for.
    public static void Parse(ParseContext context, ByteReader reader, LegacyLayout layout, bool isCompanion = false)
    {
        var findings = new FindingCollector();
        long start = reader.Start;
        long end = reader.End;
        LegacyHeader header = LegacyHeader.Read(reader, layout.HeaderAt);
        var stream = new Node("WavPack", start, end - start) { Kind = NodeKind.Stream, Description = layout.IsRaw ? LegacyExplain.RawStream : LegacyExplain.Stream, Topic = LegacyExplain.Topic };
        context.Root.Add(stream);
        var parts = new List<Node>();

        Format? format = null;
        long dataSize = 0;
        FactText? fact = null;
        long factAt = 0;
        int factLength = 0;
        Node? wav = null;
        if (!layout.IsRaw)
        {
            wav = Wav(reader, layout, findings, out format, out dataSize, out fact, out factAt, out factLength);
            parts.Add(wav);
        }

        HeaderNodes nodes = HeaderNode(header, findings);
        parts.Add(nodes.Header);
        if (!header.IsComplete || header.Version is < 1 or > 3)
        {
            if (!header.IsComplete)
            {
                string needed = header.Version is >= 1 and <= 3 ? $"the WavPack header of version {header.Version}, which takes {header.Length}" : "the WavPack header, before its version field is complete";
                findings.Add(Severity.Warning, "wavpack.legacy-header-truncated", nodes.Header, _ => $"The data ends {Counts.Of(header.Held, "byte", "bytes")} into {needed}; the file holds no audio.");
            }
            else
            {
                findings.Add(Severity.Warning, "wavpack.version-unsupported", nodes.Version ?? nodes.Header, _ =>
                    $"The WavPack header behind the stored WAV header has version {header.Version}; versions 1 to 3 are read here, and a stream of version 4 or 5 has blocks, not this header.");
            }

            AddAll(stream, [], parts);
            findings.Flush(context);
            return;
        }

        long bitstreamAt = header.At + header.Length;
        LegacyTrailer.Found trailer = LegacyTrailer.Find(reader, bitstreamAt, end);
        long bitstreamEnd = trailer.At >= 0 ? trailer.At : end;
        var bitstream = new Node("Bitstream", bitstreamAt, bitstreamEnd - bitstreamAt, NodeValue.Integer(bitstreamEnd - bitstreamAt, "bytes", "byte")) { Description = LegacyExplain.Bitstream, Topic = LegacyExplain.Topic };
        parts.Add(bitstream);
        if (trailer.At >= 0)
        {
            parts.Add(TrailerNode(trailer, bitstreamEnd, end));
        }
        else if (trailer.FirstRun >= 0)
        {
            long run = trailer.FirstRun;
            findings.Add(Severity.Info, "wavpack.legacy-trailer-unrecognized", bitstream, _ =>
                $"A run of 0xFF bytes at 0x{run:X} is 16 bytes or longer, as a trailer's marker is, but no chunk headers follow it to the end of the data; it is left in the bitstream.");
        }

        if (bitstreamEnd == bitstreamAt)
        {
            findings.Add(Severity.Warning, "wavpack.legacy-bitstream-missing", nodes.Header, _ => "The data ends where the WavPack header ends, so the file holds no audio.");
        }

        bool isMono = layout.IsRaw ? (header.Flags & LegacyHeader.MonoFlag) != 0 : format is { Channels: 1 };
        Check(header, layout, format, dataSize, nodes, wav, findings, out long fromData);
        var leading = new List<Node>();
        var placement = new Placement(reader, bitstream, bitstreamAt, bitstreamEnd, end, trailer.At >= 0, Refusal(header, layout, format), nodes.Header, isCompanion);
        Summarize(context, header, layout, format, fromData, isMono, bitstreamEnd - bitstreamAt, stream, leading, findings, fact, factAt, factLength, placement);
        AddAll(stream, leading, parts);
        findings.Flush(context);
    }

    // Where the parts of the stream lie, for the decoder's session.
    private sealed record Placement(ByteReader Reader, Node Bitstream, long BitstreamAt, long BitstreamEnd, long DataEnd, bool HasTrailer, string? Refusal, Node Header, bool IsCompanion);

    // The bytes libwavpack's open skips for a chunk (unpack3_open.c): what follows the 16 common fields of the format chunk, or the whole chunk, rounded up to even.
    private static long Skipped(string id, long size) => id == "fmt " ? (size >= 16 ? (size + 1 - 16) & ~1L : 0) : (size + 1) & ~1L;

    // Why the file is not decoded, or null where it is: the checks of libwavpack's open (unpack3_open.c) that the findings above state, in the order the open
    // makes them, and two of Encspect's own for files libwavpack opens (a bit limit beyond a signed 16-bit field, the mono flag against the channel count).
    // The decoder of 3.97 reads the raw files libwavpack does not, so a raw file is refused only for what its header says.
    private static string? Refusal(LegacyHeader header, LegacyLayout layout, Format? format)
    {
        if (!layout.IsRaw)
        {
            if (format is not { } f)
            {
                return "The stored WAV header has no format chunk before the data, so the audio's layout is not stated, and libwavpack's open refuses the file";
            }

            if (f.Tag != 1 || f.Channels is 0 or > 2 || f.Rate == 0 || f.Bits is < 16 or > 24 || f.BlockAlign / f.Channels > 3 || f.BlockAlign % f.Channels != 0 || f.BlockAlign / f.Channels < (f.Bits + 7) / 8)
            {
                return "The stored WAV header states audio that libwavpack's open refuses (PCM of one or two channels, 16 to 24 bits of at most 3 bytes a sample)";
            }

            if (layout.Chunks.Any(c => c.Id != "data" && Skipped(c.Id, c.Size) > MaxChunkBeforeData))
            {
                return "A chunk of the stored WAV header is larger than the 1 MiB that libwavpack's open skips, so it refuses the file";
            }
        }
        else if (!header.IsVersion3)
        {
            return $"A raw file has a header of version 3; this one has version {header.Version}";
        }

        if (header.IsVersion3 && (header.Flags & LegacyHeader.ExtremeDecorrFlag) != 0)
        {
            if ((header.Flags & LegacyHeader.NotStoredFlags) != 0)
            {
                return "The flags have a bit that is never stored together with the extreme flag, so libwavpack's open refuses the file";
            }

            if (header.Bits != 0 && ((header.Flags & LegacyHeader.NewHighFlag) != 0 && (header.Flags & (LegacyHeader.FastFlag | LegacyHeader.HighFlag)) != 0 || (header.Flags & LegacyHeader.CrossDecorrFlag) != 0))
            {
                return "The flags combine a bit limit or hybrid target with flags libwavpack's open refuses with the extreme flag, so it refuses the file";
            }
        }

        if (header.Bits > short.MaxValue)
        {
            return $"The bit limit or hybrid target of {header.Bits} is more than a signed 16-bit field holds; Encspect does not decode it";
        }

        if (header.IsVersion3 && !layout.IsRaw && format is { } fmt && ((header.Flags & LegacyHeader.MonoFlag) != 0) != (fmt.Channels == 1))
        {
            return "The mono flag and the channel count of the stored WAV header disagree; libwavpack opens the file, but Encspect does not guess which of the two is right";
        }

        return null;
    }

    private static void AddAll(Node stream, List<Node> leading, List<Node> parts)
    {
        foreach (Node node in leading)
        {
            stream.Add(node);
        }

        foreach (Node node in parts)
        {
            stream.Add(node);
        }
    }

    private static Node Wav(ByteReader reader, LegacyLayout layout, FindingCollector findings, out Format? format, out long dataSize, out FactText? fact, out long factAt, out int factLength)
    {
        long start = layout.RiffAt;
        var wav = new Node("WAV Header", start, layout.HeaderAt - start) { Description = LegacyExplain.StoredHeader, Topic = LegacyExplain.Topic };
        var riff = new Node("RIFF Chunk", start, LegacyLayout.RiffLength) { Description = Explain.RiffChunk, Topic = LegacyExplain.Topic };
        riff.Add(Field(start, 4, "ID", "RIFF", Explain.ChunkId));
        riff.Add(Field(start + 4, 4, "Size", NodeValue.Integer(layout.RiffSize, "bytes", "byte"), LegacyExplain.Riff));
        riff.Add(Field(start + 8, 4, "Form Type", "WAVE", Explain.FormType));
        wav.Add(riff);
        format = null;
        dataSize = 0;
        fact = null;
        factAt = 0;
        factLength = 0;
        Node? firstLarge = null;
        foreach (StoredChunk chunk in layout.Chunks)
        {
            bool isData = chunk.Id == "data";
            bool isFormat = chunk.Id == "fmt ";
            long body = chunk.At + LegacyLayout.ChunkHeaderLength;
            long bodyLength = isData ? 0 : chunk.Size + (chunk.Size & 1);
            string name = chunk.Id == "fact" ? "Fact Chunk" : $"{chunk.Id.TrimEnd()} Chunk";
            var node = new Node(name, chunk.At, LegacyLayout.ChunkHeaderLength + bodyLength) { Description = isFormat ? Explain.FormatChunk : isData ? LegacyExplain.DataChunk : LegacyExplain.OtherChunk, Topic = LegacyExplain.Topic };
            node.Add(Field(chunk.At, 4, "ID", Nodes.Printable(Encoding.Latin1.GetBytes(chunk.Id)), Explain.ChunkId));
            node.Add(Field(chunk.At + 4, 4, "Size", NodeValue.Integer(chunk.Size, "bytes", "byte"), isData ? LegacyExplain.DataChunkSize : Explain.ChunkSize));
            if (isData)
            {
                dataSize = chunk.Size;
            }
            else if (Skipped(chunk.Id, chunk.Size) > MaxChunkBeforeData)
            {
                firstLarge ??= node;
            }

            if (isFormat && chunk.Size >= 16)
            {
                byte[] fields = reader.ReadBytesAt(body, (int)Math.Min(chunk.Size, 40));
                Nodes.WaveFormat(fields, fields.Length, body, node);
                format = new Format(BinaryPrimitives.ReadUInt16LittleEndian(fields), BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(2)), BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(4)),
                    BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(12)), BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(14)), body);
            }
            else if (isFormat)
            {
                findings.Add(Severity.Warning, "wavpack.legacy-wav-format-unsupported", node, _ =>
                    $"The format chunk states {Counts.Of(chunk.Size, "byte", "bytes")}, fewer than the 16 of the common fields; libwavpack refuses a file with one.", "short");
            }

            if (chunk.Id == "fact" && fact is null)
            {
                int held = (int)Math.Min(chunk.Size, FactText.MaxLength);
                if (FactText.Read(reader.ReadBytesAt(body, held)) is { } text)
                {
                    fact = text;
                    factAt = body;
                    factLength = held;
                    Node textNode = text.ToNode(body, held);
                    node.Add(textNode);
                    if (!text.IsRecognized)
                    {
                        findings.Add(Severity.Info, "wavpack.wrapper-fact-text-unrecognized", textNode, _ => FactText.UnrecognizedFinding);
                    }
                }
            }

            wav.Add(node);
        }

        if (firstLarge is not null)
        {
            Node large = firstLarge;
            findings.Add(Severity.Warning, "wavpack.legacy-wav-format-unsupported", large, _ =>
                $"A chunk before the data is more than 1 MiB to skip ({Counts.Of(Chunk(large), "byte", "bytes")}); libwavpack refuses to skip one that large.", "large chunk");
        }

        if (format is null && !layout.Chunks.Any(c => c.Id == "fmt " && c.Size < 16))
        {
            findings.Add(Severity.Warning, "wavpack.legacy-wav-format-unsupported", wav, _ => "The stored WAV header has no format chunk before the data, so the audio's layout is not stated; libwavpack refuses the file.", "no format");
        }

        return wav;
    }

    private static long Chunk(Node node) => node.Children.First(c => c.Name == "Size").Value?.AsInteger() ?? 0;

    private static HeaderNodes HeaderNode(LegacyHeader header, FindingCollector findings)
    {
        long at = header.At;
        long length = Math.Min(header.Held, Math.Max(header.Length, 10));
        var node = new Node("WavPack Header", at, length) { Description = LegacyExplain.Header, Topic = LegacyExplain.Topic };
        var result = new HeaderNodes { Header = node };
        if (header.Held >= 4)
        {
            node.Add(Field(at, 4, "ID", "wvpk", LegacyExplain.HeaderId));
        }

        if (header.Held >= 8)
        {
            node.Add(Field(at + 4, 4, "Size", NodeValue.Integer(header.Size, "bytes", "byte"), LegacyExplain.HeaderSize));
            if (header.Version is >= 1 and <= 3 && header.Size != LegacyLayout.HeaderLength(header.Version) - 8)
            {
                Node size = node.Children[^1];
                findings.Add(Severity.Info, "wavpack.legacy-header-size", size, _ =>
                    $"The header states a size of {header.Size}; the encoders write {LegacyLayout.HeaderLength(header.Version) - 8} for version {header.Version}, the header's size less its first 8 bytes. No reader uses it.");
            }
        }

        if (header.Held >= 10)
        {
            result.Version = node.Add(Field(at + 8, 2, "Version", NodeValue.Integer(Math.Max(header.Version, 0)), LegacyExplain.Version));
        }

        if (header.Version is < 2 or > 3 || header.Held < 12)
        {
            return result;
        }

        bool isHybrid = header.IsHybrid;
        node.Add(isHybrid
            ? Field(at + 10, 2, "Hybrid Target", NodeValue.Integer(header.Bits, "1/256 bits", "1/256 bit"), LegacyExplain.HybridTarget)
            : Field(at + 10, 2, "Lossy Bit Limit", NodeValue.Integer(header.Bits, "bits", "bit"), LegacyExplain.LossyBitLimit));
        if (!header.IsVersion3 || !header.IsComplete)
        {
            return result;
        }

        Node flags = new Node("Flags", at + 12, 2, NodeValue.Hex(header.Flags, 4)) { Description = LegacyExplain.Flags, Topic = LegacyExplain.Topic };
        foreach ((int bit, string name, string description) in FlagList)
        {
            int index = int.TrailingZeroCount(bit);
            flags.Add(Node.FromBits(name, at + 12, index, index + 1, NodeValue.Boolean((header.Flags & bit) != 0), NodeKind.Detail, description, LegacyExplain.Topic, BitOrder.LeastSignificantFirst));
        }

        result.Flags = node.Add(flags);
        node.Add(Field(at + 14, 2, "Shift", NodeValue.Integer(header.Shift, "bits", "bit"), LegacyExplain.Shift));
        result.Samples = node.Add(Field(at + 16, 4, "Total Samples", NodeValue.Integer(header.TotalSamples, "samples", "sample"), LegacyExplain.TotalSamples));
        node.Add(Field(at + 20, 4, "Audio CRC", NodeValue.Hex(header.Crc, 8), LegacyExplain.Crc));
        node.Add(Field(at + 24, 4, "Correction CRC", NodeValue.Hex(header.Crc2, 8), LegacyExplain.Crc2));
        node.Add(Field(at + 28, 4, "Source Extension", header.Extension, LegacyExplain.Extension));
        result.Extra = node.Add(Field(at + 32, 1, "Extra Byte Count", NodeValue.Integer(header.ExtraCount, "bytes", "byte"), LegacyExplain.ExtraCount));
        node.Add(Field(at + 33, 3, "Extra Bytes", NodeValue.Hex(header.Extras[0] << 16 | header.Extras[1] << 8 | header.Extras[2], 6), LegacyExplain.ExtraBytes));
        return result;
    }

    private static Node TrailerNode(LegacyTrailer.Found trailer, long at, long end)
    {
        var node = new Node("Trailer", at, end - at, NodeValue.Integer(end - at, "bytes", "byte")) { Description = LegacyExplain.Trailer, Topic = LegacyExplain.Topic };
        node.Add(Field(at, LegacyTrailer.MarkerLength, "Marker", NodeValue.Integer(LegacyTrailer.MarkerLength, "bytes", "byte"), LegacyExplain.TrailerMarker));
        foreach (LegacyTrailer.Chunk chunk in trailer.Chunks)
        {
            long length = Math.Min(8 + chunk.Size + (chunk.Size & 1), end - chunk.At);
            var chunkNode = new Node($"{chunk.Id.TrimEnd()} Chunk", chunk.At, length) { Description = LegacyExplain.TrailerChunk, Topic = LegacyExplain.Topic };
            chunkNode.Add(Field(chunk.At, 4, "ID", chunk.Id, Explain.ChunkId));
            chunkNode.Add(Field(chunk.At + 4, 4, "Size", NodeValue.Integer(chunk.Size, "bytes", "byte"), Explain.ChunkSize));
            node.Add(chunkNode);
        }

        if (trailer.Count > trailer.Chunks.Count)
        {
            long from = trailer.Chunks[^1].At + 8 + trailer.Chunks[^1].Size + (trailer.Chunks[^1].Size & 1);
            node.Add(Field(from, end - from, "Further Chunks", NodeValue.Integer(trailer.Count - trailer.Chunks.Count, "chunks", "chunk"), LegacyExplain.TrailerFurther));
        }

        return node;
    }

    // The checks of the stored WAV header that libwavpack makes (unpack3_open.c), and of the total samples against the data chunk's size.
    private static void Check(LegacyHeader header, LegacyLayout layout, Format? format, long dataSize, HeaderNodes nodes, Node? wav, FindingCollector findings, out long fromData)
    {
        fromData = -1;
        Node? formatChunk = wav?.Children.FirstOrDefault(c => c.Name == "fmt Chunk");
        Node At(string name) => formatChunk?.Children.FirstOrDefault(c => c.Name == name) ?? formatChunk ?? nodes.Header;
        if (format is { } f)
        {
            if (f.Tag != 1)
            {
                findings.Add(Severity.Warning, "wavpack.legacy-wav-format-unsupported", At("Format Tag"), _ => $"The format tag is 0x{f.Tag:X4}; libwavpack takes PCM (1) only.", "format tag");
            }

            if (f.Channels is 0 or > 2)
            {
                findings.Add(Severity.Warning, "wavpack.legacy-wav-format-unsupported", At("Channels"), _ => $"The stored WAV header has {Counts.Of(f.Channels, "channel", "channels")}; libwavpack takes one or two.", "channels");
            }

            if (f.Rate == 0)
            {
                findings.Add(Severity.Warning, "wavpack.legacy-wav-format-unsupported", At("Sample Rate"), _ => "The stored WAV header has a sample rate of 0; libwavpack refuses it.", "sample rate");
            }

            if (f.Bits is < 16 or > 24)
            {
                findings.Add(Severity.Warning, "wavpack.legacy-wav-format-unsupported", At("Bits Per Sample"), _ => $"The stored WAV header has {Counts.Of(f.Bits, "bit", "bits")} a sample; libwavpack takes 16 to 24.", "bits");
            }

            if (f.Channels is 1 or 2 && (f.BlockAlign / f.Channels > 3 || f.BlockAlign % f.Channels != 0 || f.BlockAlign / f.Channels < (f.Bits + 7) / 8))
            {
                findings.Add(Severity.Warning, "wavpack.legacy-wav-format-unsupported", At("Block Align"), _ =>
                    $"The block align of {f.BlockAlign} bytes is no whole number of samples of at most 3 bytes that hold {Counts.Of(f.Bits, "bit", "bits")}; libwavpack refuses it.", "block align");
            }

            if (f.Channels is 1 or 2)
            {
                fromData = dataSize / f.Channels / (f.Bits > 16 ? 3 : 2);
            }

            if (header.IsVersion3 && f.Channels is 1 or 2 && ((header.Flags & LegacyHeader.MonoFlag) != 0) != (f.Channels == 1) && nodes.Flags is { } monoFlags)
            {
                findings.Add(Severity.Warning, "wavpack.legacy-channels-mismatch", monoFlags, _ =>
                    $"The stored WAV header has {Counts.Of(f.Channels, "channel", "channels")}, but the mono flag is {((header.Flags & LegacyHeader.MonoFlag) != 0 ? "set" : "clear")}; a decoder takes the channel count from the WAV header and the coding from the flag.");
            }
        }

        if (header.IsVersion3 && !layout.IsRaw && fromData >= 0 && nodes.Samples is { } samples && header.TotalSamples != fromData)
        {
            long counted = fromData;
            findings.Add(Severity.Warning, "wavpack.total-samples-mismatch", samples, _ =>
                $"The header says the file holds {Counts.Of(header.TotalSamples, "sample", "samples")}, but the stored data chunk's size is that of {counted}; libwavpack takes the data chunk's count, 3.97's decoder the header's.");
        }

        if (header.IsVersion3 && (header.Flags & LegacyHeader.ExtremeDecorrFlag) != 0 && nodes.Flags is { } flags)
        {
            if ((header.Flags & LegacyHeader.NotStoredFlags) != 0)
            {
                findings.Add(Severity.Warning, "wavpack.flags-invalid", flags, _ =>
                    $"The flags 0x{header.Flags & LegacyHeader.NotStoredFlags:X4} are never stored, and the extreme flag is set; libwavpack refuses the file.", "not stored");
            }

            if (header.Bits != 0 && (header.Flags & LegacyHeader.NewHighFlag) != 0 && (header.Flags & (LegacyHeader.FastFlag | LegacyHeader.HighFlag)) != 0)
            {
                findings.Add(Severity.Warning, "wavpack.flags-invalid", flags, _ =>
                    "The header has a hybrid target, the new high flag and the fast or high flag, and the extreme flag is set; libwavpack refuses the file.", "hybrid with fast or high");
            }

            if (header.Bits != 0 && (header.Flags & LegacyHeader.CrossDecorrFlag) != 0)
            {
                findings.Add(Severity.Warning, "wavpack.flags-invalid", flags, _ =>
                    "The header has a bit limit or hybrid target and the cross-channel flag, and the extreme flag is set; libwavpack refuses the file.", "bits with cross-channel");
            }
        }

        if (layout.IsRaw && header.IsVersion3 && header.ExtraCount > 3 && nodes.Extra is { } extra)
        {
            findings.Add(Severity.Warning, "wavpack.legacy-extra-bytes-invalid", extra, _ =>
                $"The header says {header.ExtraCount} bytes follow the last whole sample of the raw source; it can hold 3 at most.");
        }
    }
}
