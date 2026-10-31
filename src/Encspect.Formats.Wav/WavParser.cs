using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Wav;

// WAVE files: a RIFF chunk (RIFX for big-endian integers, RF64 and BW64 for sizes of 64 bits) of the form type WAVE, holding the fmt chunk, the data
// chunk and other chunks (Multimedia Programming Interface and Data Specifications 1.0; EBU Tech 3285 and 3306; ITU-R BS.2088). The audio of PCM, floating-point
// and companded formats is described by the figures the fmt chunk states; MPEG audio, AC-3 and DTS go to their codec parsers. Tags after the
// RIFF chunk (APEv2, ID3v1) are the host's; an ID3v2 tag in an "id3 " or "ID3 " chunk is the chunk's, which hands it to the tag parser.
public sealed class WavParser : IFormatParser
{
    // The longest chain of chunks the sniff follows.
    private const int SniffChunks = SniffResult.MaxFrames;

    public string Name => "WAV";

    // The RIFF header with the form type WAVE is certain when a chunk header with an ID of printable characters follows it (class 100); a
    // header cut short or followed by bytes that are no chunk is likely (90 and 80). The chunks that fit in the data are counted as frames.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (RiffHeader.Read(reader) is not { } header)
        {
            return SniffResult.None;
        }

        long position = reader.Start + RiffHeader.Length;
        if (position + 8 > reader.End)
        {
            return new SniffResult(80, reader.Start, 0, End: reader.End);
        }

        bool isChunk = Walker.IsFourCc(reader.ReadBytesAt(position, 4));
        int frames = 0;
        while (isChunk && frames < SniffChunks && position + 8 <= reader.End && Walker.IsFourCc(reader.ReadBytesAt(position, 4)))
        {
            byte[] chunk = reader.ReadBytesAt(position, 8);
            ulong size = Fields.Dword(chunk.AsSpan(4), header.IsBigEndian);
            if (size == uint.MaxValue)
            {
                // A size the file states elsewhere or leaves open: the chain cannot go further.
                frames++;
                position = reader.End;
                break;
            }

            long next = position + 8 + (long)size + ((long)size & 1);
            if (next > reader.End)
            {
                break;
            }

            frames++;
            position = next;
        }

        // The first chunk running past the data means a cut file or a wrong size.
        int rank = !isChunk ? 80 : frames > 0 ? 100 : 90;
        return new SniffResult(rank, reader.Start, frames, End: Math.Min(position, reader.End));
    }

    // Where the RIFF chunk ends by its own size. A size of 0 or 0xFFFFFFFF is the placeholder of a writer that could not fill it in, which the
    // host cannot trust to own the tags after it; in an RF64 or BW64 file the size is the one in the ds64 chunk.
    public long? DeclaredEnd(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (RiffHeader.Read(reader) is not { } header)
        {
            return null;
        }

        return header.DeclaredEnd(reader);
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
                context.AddFinding(Severity.Warning, "wav.data-after-tag", $"{Counts.Of(stop - position, "byte", "bytes")} at 0x{position:X} {Counts.Agree(stop - position, "follows", "follow")} an ID3v2 tag inside the RIFF chunk, which breaks the chain of chunks; they are not read.", data);
            }

            position = Math.Max(position, tag?.End ?? position);
        }
    }

    private static long ParseChunks(ParseContext context, ByteReader reader)
    {
        RiffHeader header = RiffHeader.Read(reader) ?? throw new InvalidDataException("The data does not start with a RIFF, RIFX, RF64 or BW64 header of the form type WAVE.");
        var state = new State(context) { Start = reader.Start, Magic = header.Magic };

        long declared = header.DeclaredEnd(reader) ?? reader.End;
        bool isPlaceholder = header.DeclaredEnd(reader) is null;
        long end = Math.Min(declared, reader.End);
        state.ContainerEnd = end;
        var container = new Node(header.Magic, reader.Start, end - reader.Start, "WAVE") { Description = Explain.RiffChunk(header.Magic), Topic = header.IsBw64 ? Topics.Bw64 : header.IsRf64 ? Topics.Rf64 : Topics.RiffChunks };
        state.Container = container;
        context.Root.Add(container);
        container.Add("Chunk ID", reader.Start, 4, header.Magic, Explain.RiffId, Topics.RiffChunks);
        Node size = container.Add("Chunk Size", reader.Start + 4, 4, NodeValue.Integer(header.Size, "bytes", "byte"), header.IsBw64 ? Explain.ChunkSizeBw64 : header.IsRf64 ? Explain.ChunkSizeRf64 : Explain.RiffSize, header.IsBw64 ? Topics.Bw64 : header.IsRf64 ? Topics.Rf64 : Topics.RiffChunks);
        container.Add("Form Type", reader.Start + 8, 4, "WAVE", Explain.FormType, Topics.RiffChunks);

        if (isPlaceholder)
        {
            long held = reader.End - reader.Start - 8;
            string form = header.IsBw64 ? "a BW64" : header.IsRf64 ? "an RF64" : $"a {header.Magic}";
            state.Findings.Add(Severity.Warning, "wav.riff-size-placeholder", size, _ =>
            {
                string stated = header.Size == 0 ? "0" : header.Size == uint.MaxValue ? "0xFFFFFFFF" : $"{header.Size}, too small to hold the form type";
                string why = header.Is64Bit
                    ? header.Size == uint.MaxValue ? $"which stands for the size in the ds64 chunk, {Ds64Unusable(state, held)}" : "which is no size an RF64 or BW64 chunk can have (0xFFFFFFFF stands for the size in the ds64 chunk)"
                    : $"which is no size {form} chunk can have; a writer leaves it when it cannot go back and fill it in";
                return $"The {header.Magic} chunk's size is {stated}, {why}. The file is read to its end, {Counts.Of(held, "byte", "bytes")} after the size field.";
            });
        }
        else if (declared > reader.End)
        {
            long missing = declared - reader.End;
            state.Findings.Add(Severity.Warning, "wav.truncated", size, _ =>
                $"The RIFF chunk states {Counts.Of(declared - reader.Start, "byte", "bytes")}, but the data ends {Counts.Of(missing, "byte", "bytes")} short of that: the file is cut off.");
        }

        if (header.IsBw64 && header.Size != uint.MaxValue)
        {
            uint stated = header.Size;
            state.Findings.Add(Severity.Warning, "wav.bw64-size-not-placeholder", size, _ =>
                $"The BW64 chunk's size is {stated}, but ITU-R BS.2088 section 3.2 sets it to 0xFFFFFFFF, since the ds64 chunk gives the size.");
        }

        long stop = Walker.Chunks(state, container, reader.Start + RiffHeader.Length, end, null, 0);
        if (stop < end)
        {
            long left = end - stop;
            Node extra = container.Add(new Node("Extra Data", stop, left, NodeValue.Integer(left, "bytes", "byte")) { Description = Explain.ExtraData });
            state.Findings.Add(Severity.Warning, "wav.trailing-bytes", extra, _ => $"The RIFF chunk holds {Counts.Of(left, "byte", "bytes")} after its last chunk, too few for another.");
        }

        Finish(state, header, size);

        if (!isPlaceholder && end < reader.End)
        {
            long extra = reader.End - end;
            Node trailing = context.Root.Add(new Node("Trailing Data", end, extra, NodeValue.Integer(extra, "bytes", "byte")) { Description = Explain.TrailingData });
            state.Findings.Add(Severity.Warning, "wav.data-after-riff", trailing, _ =>
                $"{Counts.Of(extra, "byte", "bytes")} follow the RIFF chunk's end at 0x{end:X}, and are no tag the analyzer recognizes.");
        }

        state.Findings.Flush(context);
        return end;
    }

    // Why the ds64 chunk gives no size to read the RIFF chunk by. A ds64 chunk that is not first is read all the same, and its size is compared
    // with what the file holds.
    private static string Ds64Unusable(State state, long held)
    {
        if (state.Ds64 is not { } ds64)
        {
            return "but the file holds no ds64 chunk with the three sizes right after the header";
        }

        string compared = ds64.RiffSize == (ulong)held ? "which is the number of bytes the file holds after the size field" : $"which is not the {Counts.Of(held, "byte", "bytes")} the file holds after the size field";
        bool isFirst = state.Container.Children.FirstOrDefault(c => c.Name is not ("Chunk ID" or "Chunk Size" or "Form Type"))?.Name == "Data Size 64 Chunk";
        return isFirst
            ? $"but the ds64 chunk states {ds64.RiffSize}, which no RIFF chunk's size can be"
            : $"but the ds64 chunk does not come first after the header, where the size is read; it states {ds64.RiffSize}, {compared}";
    }

    private static void Finish(State state, RiffHeader header, Node size)
    {
        if (header.Is64Bit)
        {
            if (state.Ds64 is not { } ds64)
            {
                // A ds64 chunk too short for its sizes is reported as the chunk that is too short.
                if (!state.Container.Children.Any(c => c.Name == "Data Size 64 Chunk"))
                {
                    state.Findings.Add(Severity.Warning, "wav.ds64-missing", state.Container, _ => header.IsBw64
                        ? "The file is a BW64 file, but it holds no ds64 chunk, which ITU-R BS.2088 section 2.4 requires first after the BW64 header."
                        : "The file is an RF64 file, but it holds no ds64 chunk, which Tech 3306-2009 requires first after the RF64 header.");
                }
            }
            else
            {
                Ds64Checks(state, header, size, ds64);
            }
        }

        foreach ((uint name, Node chunk) in state.LabelReferences)
        {
            if (state.CueIdentifiers?.Contains(name) != true && !state.IsCueNamesIncomplete)
            {
                state.Findings.Add(Severity.Warning, "wav.label-without-cue", chunk, n =>
                    $"{Counts.Of(n, "label, note, text or file chunk", "label, note, text or file chunks")} of an associated data list name{(n == 1 ? "s" : string.Empty)} a cue point that the cue chunk does not list.");
            }
        }

        if (state.AdmNode is { } adm && state.ChnaChunks.Count == 0)
        {
            state.Findings.Add(Severity.Warning, "wav.adm-without-chna", adm, _ =>
                "The chunk holds Audio Definition Model metadata, but the file has no chna chunk, which ITU-R BS.2088 section 9 requires to tie the tracks to it.");
        }

        if (state.Format is { Channels: > 0 } format)
        {
            foreach (ChnaChunk chna in state.ChnaChunks)
            {
                if (chna.Indexes.Any(i => i > format.Channels))
                {
                    int channels = format.Channels;
                    state.Findings.Add(Severity.Warning, "wav.chna-track-index-out-of-range", chna.Node, _ =>
                        $"The chna chunk names a track beyond the {Counts.Of(channels, "channel", "channels")} of the fmt chunk; track indexes count from 1 in the order of the interleaved channels (ITU-R BS.2088 section 8.2).");
                }
            }
        }

        DataChunks.Finish(state);
    }

    private static void Ds64Checks(State state, RiffHeader header, Node size, Ds64 ds64)
    {
        Node? first = state.Container.Children.FirstOrDefault(c => c.Name != "Chunk ID" && c.Name != "Chunk Size" && c.Name != "Form Type");
        if (first is not null && first.Name != "Data Size 64 Chunk")
        {
            Node at = state.Container.Children.First(c => c.Name == "Data Size 64 Chunk");
            state.Findings.Add(Severity.Warning, "wav.ds64-not-first", at, _ => header.IsBw64
                ? "The ds64 chunk is not the first chunk after the BW64 header, where ITU-R BS.2088 section 2.4 puts it."
                : "The ds64 chunk is not the first chunk after the RF64 header, where Tech 3306-2009 puts it.");
        }

        if (header.Size != uint.MaxValue && ds64.RiffSize != header.Size)
        {
            ulong stated = ds64.RiffSize;
            state.Findings.Add(Severity.Warning, "wav.ds64-riff-size-mismatch", size, _ =>
                $"The {header.Magic} chunk states {header.Size} bytes and the ds64 chunk {stated}.");
        }

        if (state.Data is { } data && state.DataSize32 is { } size32 and not uint.MaxValue && size32 != ds64.DataSize)
        {
            ulong stated = ds64.DataSize;
            Node field = data.Node.Children.First(c => c.Name == "Chunk Size");
            state.Findings.Add(Severity.Warning, "wav.ds64-data-size-mismatch", field, _ =>
                $"The data chunk states {Counts.Of(size32, "byte", "bytes")} and the ds64 chunk {stated}.");
        }
    }
}

// The 12 bytes that start the RIFF chunk: its ID, size and form type.
internal readonly record struct RiffHeader(string Magic, uint Size)
{
    public const int Length = 12;

    // The three sizes of the ds64 chunk, the least of it that gives the RF64 chunk its size (the chunk reader sets the sizes at the same point).
    private const int Ds64Sizes = 24;

    public bool IsBigEndian => Magic == "RIFX";

    public bool IsRf64 => Magic == "RF64";

    public bool IsBw64 => Magic == "BW64";

    // The two forms whose 32-bit sizes of 0xFFFFFFFF stand for sizes in the ds64 chunk.
    public bool Is64Bit => IsRf64 || IsBw64;

    // The header at the start of the reader, or null when the reader does not start with RIFF, RIFX, RF64 or BW64 and the form type WAVE.
    public static RiffHeader? Read(ByteReader reader)
    {
        if (reader.Length < Length)
        {
            return null;
        }

        byte[] bytes = reader.ReadBytesAt(reader.Start, Length);
        string magic = Encoding.Latin1.GetString(bytes, 0, 4);
        if (magic is not ("RIFF" or "RIFX" or "RF64" or "BW64") || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return null;
        }

        return new RiffHeader(magic, Fields.Dword(bytes.AsSpan(4), magic == "RIFX"));
    }

    // Where the RIFF chunk ends by its size, or null for a size that cannot be one: 0, 0xFFFFFFFF, one too small to hold the form type, or, in
    // an RF64 or BW64 file, a missing or unusable ds64 chunk.
    public long? DeclaredEnd(ByteReader reader)
    {
        ulong size = Size;
        if (Is64Bit && Size == uint.MaxValue)
        {
            long at = reader.Start + Length;
            if (at + 8 + 8 > reader.End)
            {
                return null;
            }

            byte[] chunk = reader.ReadBytesAt(at, 8);
            if (!chunk.AsSpan(0, 4).SequenceEqual("ds64"u8) || Fields.Dword(chunk.AsSpan(4), false) < Ds64Sizes || at + 8 + Ds64Sizes > reader.End)
            {
                return null;
            }

            reader.Position = at + 8;
            size = reader.ReadUInt64LittleEndian();
        }
        else if (Size is 0 or uint.MaxValue)
        {
            return null;
        }

        if (size < 4 || size > (ulong)(long.MaxValue / 4))
        {
            return null;
        }

        return reader.Start + 8 + (long)size;
    }
}
