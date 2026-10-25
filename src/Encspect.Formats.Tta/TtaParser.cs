using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Tta;

// TTA (the TTA format description, "TTA1"): a 22-byte header with a CRC-32, a seek table with one 4-byte entry for each frame and a CRC-32, then
// the frames, each followed by a CRC-32 over its coded bytes. The seek table gives the length of every frame, so the frames are walked by it and
// each CRC-32 is compared; the coded samples are not read. Tags after the frames (APEv2, ID3v1) are the host's, which DeclaredEnd lets it tell
// from bytes inside the stream; an ID3v2 tag before it too.
public sealed class TtaParser : IFormatParser
{
    public const string FormatName = "TTA";

    private const int ReadChunk = 1 << 16;

    // The header of the second version is 36 bytes: the signature, channels, bits per sample, sample rate, the channel mask, two 8-byte sizes and
    // a CRC-32 (the format description's "TTA2 headers (in development)").
    private const int HeaderLengthV2 = 36;

    // DeclaredEnd adds up the lengths the seek table gives, which a crafted table can make exceed any long; past this the sum stops growing.
    private const long LongestDeclared = 1L << 60;

    public string Name => FormatName;

    // The header's signature and its CRC-32 are 8 bytes of evidence: a header whose CRC-32 holds is certain (class 100); one whose CRC-32 fails
    // but whose fields are possible is likely (80); the signature with fields that cannot be a header, or cut short, is possible (70). The
    // signature "TTA2" of the second version is likely only with the 36 bytes of its header there and at most 64 channels, 64 bits per sample
    // and a sample rate of at most 1,000,000 Hz, none of them zero; four bytes of ASCII alone are possible. The analyzer does not compute that header's CRC-32: the description
    // lists one ("CRC32 block footer") and no file of Encspect's tests shows what it covers. Only the start of the data counts: a header
    // further in is a Matroska track's CodecPrivate, among other places, and no start of a TTA file.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.Length < 4)
        {
            return SniffResult.None;
        }

        long start = reader.Start;
        Span<byte> bytes = stackalloc byte[HeaderLengthV2];
        int available = (int)Math.Min(bytes.Length, reader.Length);
        reader.Position = start;
        reader.Read(bytes[..available]);
        if (bytes[..4].SequenceEqual(TtaHeader.SignatureV2))
        {
            bool arePlausible = available == HeaderLengthV2 && BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) is > 0 and <= 64
                && BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) is > 0 and <= 64 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) is > 0 and <= 1_000_000;
            return new SniffResult(arePlausible ? 80 : 70, start, 0, 0, "TTA2");
        }

        if (!TtaHeader.HasSignature(bytes))
        {
            return SniffResult.None;
        }

        if (available < TtaHeader.Length)
        {
            return new SniffResult(70, start, 0, 0, "header cut short", start + available);
        }

        TtaHeader header = TtaHeader.Read(bytes);
        long end = start + TtaHeader.Length + (header.SampleRate == 0 ? 0 : (4 * header.FrameCount) + 4);
        end = Math.Min(end, reader.End);
        if (header.IsCrcValid)
        {
            return new SniffResult(100, start, 1, 1, End: end);
        }

        bool areFieldsPossible = header.AudioFormat is >= 1 and <= 2 && header.Channels > 0 && header.SampleRate > 0;
        return new SniffResult(areFieldsPossible ? 80 : 70, start, 0, 0, "header CRC-32 differs", end);
    }

    public bool HasDeclaredEnd => true;

    // The end of the last frame the seek table lists, when the header and the whole table are there and the table's CRC-32 holds (a damaged
    // table says nothing about where the frames end); it lies past the end of a truncated file.
    public long? DeclaredEnd(SniffContext context)
    {
        ByteReader reader = context.Reader;
        long start = reader.Start;
        if (reader.Length < TtaHeader.Length)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[TtaHeader.Length];
        reader.Position = start;
        reader.Read(bytes);
        if (!TtaHeader.HasSignature(bytes))
        {
            return null;
        }

        TtaHeader header = TtaHeader.Read(bytes);
        long count = header.FrameCount;
        long tableLength = (4 * count) + 4;
        if (header.SampleRate == 0 || reader.Length - TtaHeader.Length < tableLength)
        {
            return null;
        }

        uint crc = Crc32.Initial;
        long total = 0;
        byte[] buffer = new byte[ReadChunk];
        for (long left = 4 * count; left > 0;)
        {
            int n = (int)Math.Min(left, buffer.Length);
            reader.Read(buffer.AsSpan(0, n));
            crc = Crc32.Update(crc, buffer.AsSpan(0, n));
            for (int i = 0; i < n; i += 4)
            {
                total = Math.Min(total + BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)), LongestDeclared);
            }

            left -= n;
        }

        uint stored = reader.ReadUInt32LittleEndian();
        return stored == Crc32.Finish(crc) ? start + TtaHeader.Length + tableLength + total : null;
    }

    public void Parse(ParseContext context)
    {
        ByteReader window = context.Reader;
        List<Node> tags = [.. context.EmbeddedTags.Where(t => t.Offset >= window.Start && t.Offset < window.End).OrderBy(t => t.Offset)];
        ByteReader reader = tags.Count > 0 ? window.Slice(window.Start, tags[0].Offset - window.Start) : window;
        var state = new State(context, isContained: false);
        state.Records.DataEnd = reader.End;
        long? end = Walk(state, reader);
        if (end is { } streamEnd)
        {
            Summary.Finish(state, reader.Start, streamEnd);
        }
        else
        {
            state.Findings.Flush(context);
        }

        long position = reader.End;
        foreach (Node? tag in tags.Append<Node?>(null))
        {
            long stop = Math.Min(tag?.Offset ?? window.End, window.End);
            if (stop > position)
            {
                Node data = context.Root.Add(new Node("Data", position, stop - position, NodeValue.Integer(stop - position, "bytes", "byte")) { Description = Explain.AfterEmbeddedTag });
                context.AddFinding(Severity.Warning, "tta.data-after-tag", $"{Counts.Of(stop - position, "byte", "bytes")} at 0x{position:X} {Counts.Agree(stop - position, "follows", "follow")} an ID3v2 tag inside the TTA data, which breaks the stream; they are not read.", data);
            }

            position = Math.Max(position, tag?.End ?? position);
        }
    }

    // Reads the header, the seek table and the frames into the state and adds the stream node (and the nodes of bytes outside the stream) to the
    // root. Returns the offset where the stream's bytes end, or null when the data holds no stream.
    private static long? Walk(State s, ByteReader reader)
    {
        Records records = s.Records;
        Node root = ((ParseContext)s.Context).Root;
        long start = reader.Start;
        Span<byte> bytes = stackalloc byte[TtaHeader.Length];
        int available = (int)Math.Min(bytes.Length, reader.Length);
        reader.Position = start;
        reader.Read(bytes[..available]);
        if (available < 4 || !(TtaHeader.HasSignature(bytes) || bytes[..4].SequenceEqual(TtaHeader.SignatureV2)))
        {
            Node unread = root.Add(new Node("Data", start, reader.Length, NodeValue.Integer(reader.Length, "bytes", "byte")) { Description = Explain.UnreadData });
            s.Findings.AddNow(Severity.Warning, "tta.no-signature", "The data does not begin with the signature \"TTA1\" of a TTA stream, so it is not read as one.", unread);
            return null;
        }

        long length = reader.Length;
        if (!TtaHeader.HasSignature(bytes))
        {
            s.Stream = SignatureOnly(start, length);
            root.Add(s.Stream);
            s.Findings.AddNow(Severity.Info, "tta.version-2", "The stream begins with \"TTA2\", the second version of the format, which the format description lists as in development; the analyzer reads TTA1 only.", s.Stream);
            return reader.End;
        }

        if (available < TtaHeader.Length)
        {
            s.Stream = CutHeader(start, length);
            root.Add(s.Stream);
            s.Findings.AddNow(Severity.Warning, "tta.header-truncated", $"The data ends {Counts.Of(length, "byte", "bytes")} into the header, which has {TtaHeader.Length}.", s.Stream);
            return reader.End;
        }

        TtaHeader header = TtaHeader.Read(bytes);
        records.Header = header;
        records.HeaderOffset = start;
        s.SampleRate = header.SampleRate;
        s.Channels = header.Channels;
        s.BitsPerSample = header.BitsPerSample;
        s.HeaderBytes = new MappedBytes(bytes.ToArray(), start);
        s.HeaderNode = Nodes.Header(FieldMap.At(start), TtaHeader.Length, header);
        s.Leading.Add(s.HeaderNode);
        Summary.HeaderFindings(s, header);

        long tableOffset = start + TtaHeader.Length;
        records.TableOffset = tableOffset;
        if (header.SampleRate == 0)
        {
            return Unread(s, root, reader, tableOffset);
        }

        long expected = header.FrameCount;
        s.TableEntriesExpected = expected;
        long tableAvailable = reader.End - tableOffset;
        long entries = Math.Min(expected, tableAvailable / 4);
        long tableEnd = tableOffset + (4 * expected) + 4;
        s.IsTableComplete = entries == expected && tableAvailable >= (4 * expected) + 4;
        records.TableEntries = entries;

        // The table is read in chunks and each chunk's entries are walked at once, so no list of the lengths is kept: the frames lie after the
        // table, so the reader moves between the two. An entry whose frame starts at or past the end of the data is missing, and so is every
        // later one (the position only grows), which keeps the position from overflowing and the entries from needing a record.
        long position = tableEnd;
        uint crc = Crc32.Initial;
        byte[] table = new byte[ReadChunk];
        byte[] buffer = new byte[ReadChunk];
        for (long done = 0; done < entries;)
        {
            reader.CancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(entries - done, table.Length / 4);
            reader.Position = tableOffset + (4 * done);
            reader.Read(table.AsSpan(0, 4 * count));
            crc = Crc32.Update(crc, table.AsSpan(0, 4 * count));
            for (int i = 0; i < count; i++)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(4 * i));
                var entry = new Entry { Offset = position, Size = size };
                if (position >= reader.End)
                {
                    entry.Status = EntryStatus.Missing;
                }
                else if (size < 4)
                {
                    entry.Status = EntryStatus.TooSmall;
                }
                else if (reader.End - position < size)
                {
                    entry.Status = EntryStatus.Cut;
                }
                else
                {
                    reader.Position = position;
                    uint frameCrc = Crc32.Initial;
                    for (long left = size - 4; left > 0;)
                    {
                        int n = (int)Math.Min(left, buffer.Length);
                        reader.Read(buffer.AsSpan(0, n));
                        frameCrc = Crc32.Update(frameCrc, buffer.AsSpan(0, n));
                        left -= n;
                    }

                    entry.StoredCrc = reader.ReadUInt32LittleEndian();
                    entry.ComputedCrc = Crc32.Finish(frameCrc);
                }

                s.AddEntry(entry);
                if (position < reader.End)
                {
                    position += size;
                }
            }

            done += count;
        }

        if (s.IsTableComplete)
        {
            reader.Position = tableOffset + (4 * expected);
            records.TableStoredCrc = reader.ReadUInt32LittleEndian();
            records.TableComputedCrc = Crc32.Finish(crc);
        }
        else
        {
            long listed = entries;
            s.Findings.Add(Severity.Warning, "tta.seek-table-truncated", () => s.TableNode!, _ =>
                $"The data ends inside the seek table: it holds {listed} of the {Counts.Of(expected, "entry", "entries")} the header's sample count calls for{(listed == expected ? ", but not the CRC-32 after them" : string.Empty)}.");
        }

        s.TableNode = Nodes.SeekTable(records);
        s.Leading.Add(s.TableNode);
        long end = Math.Min(Math.Max(position, tableEnd), reader.End);
        s.Stream = Summary.StreamNode(start, end - start, s.Leading, records);
        root.Add(s.Stream);
        if (position < reader.End)
        {
            Node trailing = root.Add(new Node("Trailing Data", position, reader.End - position, NodeValue.Integer(reader.End - position, "bytes", "byte")) { Description = Explain.PacketTrailing });
            long extra = reader.End - position;
            s.Findings.AddNow(Severity.Warning, "tta.data-after-frames", $"{Counts.Of(extra, "byte", "bytes")} at 0x{position:X} {Counts.Agree(extra, "follows", "follow")} the last frame the seek table lists and {Counts.Agree(extra, "is", "are")} no tag; {Counts.Agree(extra, "it is", "they are")} not read.", trailing);
        }

        return end;
    }

    // The stream nodes of data that holds no readable header. They are built apart from Walk so that their builders capture values only: a closure
    // shared with the state would keep the context, and so the input, alive as long as the report.
    private static Node SignatureOnly(long start, long length) =>
        new(Summary.CodecName, start, length, null, () =>
        [
            new Node("Signature", start, 4, "TTA2") { Description = Explain.SignatureV2, Topic = Topics.Stream },
            new Node("Data", start + 4, length - 4, NodeValue.Integer(length - 4, "bytes", "byte")) { Description = Explain.UnreadData },
        ])
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.Stream,
        };

    private static Node CutHeader(long start, long length) =>
        new(Summary.CodecName, start, length, null, () => [new Node("Header", start, length, NodeValue.Integer(length, "bytes", "byte")) { Description = Explain.UnreadData }])
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.Stream,
        };

    // The rest of a stream whose header leaves the length of a frame undefined, as bytes the analyzer does not read.
    private static long Unread(State s, Node root, ByteReader reader, long from)
    {
        long rest = reader.End - from;
        List<Node> leading = s.Leading;
        s.Stream = new Node(Summary.CodecName, reader.Start, reader.Length, null, () => [.. leading, new Node("Data", from, rest, NodeValue.Integer(rest, "bytes", "byte")) { Description = Explain.UnreadData }])
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.Stream,
        };
        root.Add(s.Stream);
        return reader.End;
    }
}
