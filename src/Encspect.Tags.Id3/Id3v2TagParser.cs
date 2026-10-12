using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Encspect.Core;

namespace Encspect.Tags.Id3;

// ID3v2.2, 2.3 and 2.4 (id3.org "ID3 tag version 2", "ID3 tag version 2.3.0", "ID3 tag version
// 2.4.0 - Main Structure"). A tag is found at the start of the file or after a previous tag,
// or through the footer that only 2.4 defines.
public sealed class Id3v2TagParser : ITagParser
{
    private const int HeaderSize = 10;
    private const int MaxDecodedFrameBytes = 1 << 20;

    private static readonly byte[] HeaderId = "ID3"u8.ToArray();
    private static readonly byte[] FooterId = "3DI"u8.ToArray();

    public string Name => "ID3v2";

    public TagProbe? ProbeAt(TagProbeContext context, long start)
    {
        ByteReader reader = context.Reader;
        if (!reader.MatchesAt(start, HeaderId))
        {
            return null;
        }

        if (reader.End - start < HeaderSize)
        {
            return TagProbe.Reject(start, "the file ends inside the 10-byte header");
        }

        byte[] header = reader.ReadBytesAt(start, HeaderSize);
        string? problem = ValidateHeader(header, "header");
        return problem is null ? Parse(reader, start, header, start) : TagProbe.Reject(start, problem);
    }

    // ID3v2.4 section 5: "If a SEEK frame was found, use its values to guide further searching."
    // The frame only gives a minimum offset, so the tag is searched for from there.
    public TagProbe? FindFrom(TagProbeContext context, long from, long limit)
    {
        ByteReader reader = context.Reader;
        const int Chunk = 1 << 16;
        limit = Math.Min(limit, reader.End);
        int probed = 0;
        for (long chunkStart = from; chunkStart < limit; chunkStart += Chunk)
        {
            int length = (int)Math.Min(Chunk + HeaderId.Length - 1, limit - chunkStart);
            byte[] window = reader.ReadBytesAt(chunkStart, length);
            int index = 0;
            while (true)
            {
                int found = window.AsSpan(index).IndexOf(HeaderId);
                long candidate = chunkStart + index + found;
                if (found < 0 || candidate >= Math.Min(chunkStart + Chunk, limit))
                {
                    break;
                }

                // Only a complete tag with a valid header can be found with high confidence, so only
                // those are parsed, and at most MaxSearchProbes of them: a crafted window full of
                // "ID3" would otherwise parse the rest of the file once per match.
                if (IsCompleteCandidate(reader, candidate) && ++probed <= MaxSearchProbes
                    && ProbeAt(context, candidate) is { IsAccepted: true, Confidence: Confidence.High } probe)
                {
                    return probe;
                }

                index += found + 1;
            }
        }

        return null;
    }

    private const int MaxSearchProbes = 64;

    private static bool IsCompleteCandidate(ByteReader reader, long start)
    {
        if (reader.End - start < HeaderSize)
        {
            return false;
        }

        byte[] header = reader.ReadBytesAt(start, HeaderSize);
        if (ValidateHeader(header, "header") is not null)
        {
            return false;
        }

        bool hasFooter = header[3] == 4 && (header[5] & 0x10) != 0;
        return start + HeaderSize + Syncsafe(header, 6) + (hasFooter ? HeaderSize : 0) <= reader.End;
    }

    // ID3v2.4 section 3.4: a tag appended at the end of a file has a footer, "3DI" followed by
    // the same version, flags and size as the header, so it can be found from the end.
    public TagProbe? ProbeBefore(TagProbeContext context, long end)
    {
        ByteReader reader = context.Reader;
        long footerStart = end - HeaderSize;
        if (!reader.MatchesAt(footerStart, FooterId))
        {
            return null;
        }

        byte[] footer = reader.ReadBytesAt(footerStart, HeaderSize);
        string? problem = ValidateHeader(footer, "footer");
        if (problem is not null)
        {
            return TagProbe.Reject(footerStart, problem);
        }

        if (footer[3] != 4 || (footer[5] & 0x10) == 0)
        {
            return TagProbe.Reject(footerStart, "only an ID3v2.4 tag with the footer flag set has a footer");
        }

        long size = Syncsafe(footer, 6);
        long start = end - (2 * HeaderSize) - size;
        if (start < reader.Start)
        {
            return TagProbe.Reject(footerStart, $"the footer declares {size} bytes, more than precede it");
        }

        byte[] header = reader.ReadBytesAt(start, HeaderSize);
        if (!header.AsSpan(0, 3).SequenceEqual(HeaderId) || !header.AsSpan(3).SequenceEqual(footer.AsSpan(3)))
        {
            return TagProbe.Reject(footerStart, $"the footer does not match a header at 0x{start:X}");
        }

        return Parse(reader, start, header, footerStart);
    }

    // Only problems that leave the version or the extent of the tag unknown reject it.
    private static string? ValidateHeader(byte[] header, string what)
    {
        int major = header[3];
        int revision = header[4];
        if (major is < 2 or > 4)
        {
            return $"version 2.{major}.{revision} in the {what} is not 2.2, 2.3 or 2.4";
        }

        for (int i = 6; i < 10; i++)
        {
            if (header[i] >= 0x80)
            {
                return $"size byte {i - 6} in the {what} (${header[i]:X2}) is not syncsafe";
            }
        }

        return null;
    }

    private static int DefinedHeaderFlags(int major) => major switch
    {
        2 => 0xC0,
        3 => 0xE0,
        _ => 0xF0,
    };

    private TagProbe Parse(ByteReader reader, long start, byte[] header, long signatureOffset)
    {
        int major = header[3];
        int revision = header[4];
        int flags = header[5];
        long size = Syncsafe(header, 6);
        bool hasFooter = major == 4 && (flags & 0x10) != 0;

        long declaredEnd = start + HeaderSize + size + (hasFooter ? HeaderSize : 0);
        long end = Math.Min(declaredEnd, reader.End);
        var tag = new Node(Name, start, end - start, $"2.{major}.{revision}") { Kind = NodeKind.Tag, Description = Explain.Id3v2Tag, Topic = Topics.Id3v2 };
        bool truncated = declaredEnd > reader.End;
        var state = new ParseState(tag, major) { IsTruncated = truncated };

        state.Evidence.Add(new Evidence($"Signature \"ID3\" with version 2.{major}.{revision}", start, 5));
        state.Evidence.Add(new Evidence($"Syncsafe size of {size} bytes", start + 6, 4));

        AddHeaderNode(tag, "Header", "ID3", start, header);
        Node headerNode = tag.Children[^1];

        if (revision == 0xFF)
        {
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.revision-invalid", "The header revision is $FF, which the specification rules out.", headerNode));
        }

        int undefined = flags & ~DefinedHeaderFlags(major);
        if (undefined != 0)
        {
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.header-flags-unknown", $"The header sets undefined flag bits ${undefined:X2}.", headerNode));
        }
        else
        {
            state.Evidence.Add(new Evidence("Only defined header flags are set", start + 5, 1));
        }

        if (truncated)
        {
            state.Findings.Add(new Finding(
                Severity.Error,
                "id3v2.truncated",
                $"The ID3v2 header declares {size} bytes after the header, but the file ends {reader.End - start - HeaderSize} bytes into the tag.",
                tag));
        }

        long bodyStart = start + HeaderSize;
        long bodyEnd = Math.Min(bodyStart + size, reader.End);
        bool unsync = (flags & 0x80) != 0;

        if (major == 2 && (flags & 0x40) != 0)
        {
            // ID3v2.2 section 3.1: no compression scheme was ever defined, so the body is opaque.
            long length = bodyEnd - bodyStart;
            tag.Add("Compressed Data", bodyStart, length, NodeValue.Integer(length, "bytes"), Explain.CompressedTag);
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.compressed-tag", "The ID3v2.2 tag is marked as compressed, which no specification defines; its content is not parsed.", tag));
        }
        else
        {
            // 2.4 unsynchronizes frame by frame instead (ID3v2.4 section 6.1).
            var body = new Id3v2BodyReader(reader, bodyStart, bodyEnd, unsync && major < 4);
            long? declaredPadding = null;
            if (major >= 3 && (flags & 0x40) != 0 && FollowsDirectly(reader, bodyStart, bodyEnd))
            {
                // Some taggers set the flag without writing an extended header (mutagen, _tags.py).
                state.Findings.Add(new Finding(
                    Severity.Warning,
                    "id3v2.extended-header-missing",
                    "The header sets the extended header flag, but a frame follows it directly; the tag is read without an extended header.",
                    tag));
            }
            else if (major >= 3 && (flags & 0x40) != 0)
            {
                declaredPadding = major == 3 ? ParseExtendedHeader23(body, state) : ParseExtendedHeader24(body, state);
                state.ExtendedHeaderEnd = body.FilePosition;
            }

            long padding = ParseFrames(body, state, unsync);
            VerifyCrc(reader, state, unsync, bodyEnd, declaredPadding, truncated);
            CheckFrameSet(state);
            CheckUnsynchronization(state, body, unsync);
            CheckRestrictions(state, declaredEnd - start);

            if (declaredPadding is { } expected && expected != padding)
            {
                state.Findings.Add(new Finding(
                    Severity.Warning,
                    "id3v2.padding-size-mismatch",
                    $"The extended header declares {expected} bytes of padding, but the tag has {padding}.",
                    tag));
            }

            if (hasFooter && padding > 0)
            {
                state.Findings.Add(new Finding(Severity.Warning, "id3v2.padding-with-footer", "The tag has both padding and a footer; ID3v2.4 section 3.3 forbids padding when a footer is present.", tag));
            }
        }

        if (hasFooter)
        {
            ParseFooter(reader, start + HeaderSize + size, header, state);
        }

        Confidence confidence = truncated ? Confidence.Low : state.HasProblems ? Confidence.Medium : Confidence.High;
        long? next = state.SeekOffset is { } seek ? declaredEnd + seek : null;

        // A size that runs past the end of the file does not say where the tag ends, so it ends
        // where its frames and zero padding stop: what follows them is left to the other parsers
        // rather than claimed as unparsed tag bytes or padding (plan, rule 2).
        Node? unparsed = truncated ? state.Unparsed : null;
        if ((unparsed?.Offset ?? (truncated ? state.PaddingEnd : null)) is { } stop)
        {
            state.Findings.RemoveAll(f => unparsed is not null && f.Node == unparsed);
            var cut = new Node(tag.Name, tag.Offset, stop - tag.Offset, tag.Value) { Kind = NodeKind.Tag, Description = tag.Description, Topic = tag.Topic };
            foreach (Node child in tag.Children.Where(c => c != unparsed))
            {
                cut.Add(child);
            }

            string what = unparsed is null ? "frames and zero padding" : "frames";
            state.Findings.Add(new Finding(
                Severity.Warning,
                "id3v2.ends-before-size",
                $"The {what} of the ID3v2 tag stop at 0x{stop:X}, where its declared size cannot end; the tag is taken to end there.",
                cut));
            return TagProbe.Accept(cut, signatureOffset, confidence, state.Evidence, state.Findings.Select(f => f.Node == tag ? f with { Node = cut } : f).ToList(), next,
                EncoderClaims(state));
        }

        return TagProbe.Accept(tag, signatureOffset, confidence, state.Evidence, state.Findings, next, EncoderClaims(state));
    }

    // TSSE (TSS in ID3v2.2): "the used audio encoder and its settings when the file was encoded"
    // (ID3v2.3 section 4.2.1), as the software that wrote the tag fills it in.
    private static List<EncoderClaim> EncoderClaims(ParseState state) =>
        state.Frames.Where(f => f.Name is "TSSE" or "TSS" && !string.IsNullOrWhiteSpace(f.Value?.AsText()))
            .Select(f => new EncoderClaim(f.Value!.ToString(), f, $"ID3v2 {f.Name} frame"))
            .ToList();

    private static void VerifyCrc(ByteReader reader, ParseState state, bool unsync, long bodyEnd, long? declaredPadding, bool truncated)
    {
        if (state.StoredCrc is not { } stored)
        {
            return;
        }

        if (truncated)
        {
            state.Findings.Add(new Finding(Severity.Info, "id3v2.crc-not-verified", "The tag is truncated, so its CRC cannot be verified.", stored.Node));
            return;
        }

        long start = state.ExtendedHeaderEnd;
        uint crc = Crc32.Initial;
        long end;
        if (state.Major == 3)
        {
            // ID3v2.3 section 3.2: "The CRC should be calculated before unsynchronisation on the
            // data between the extended header and the padding, i.e. the frames and only the
            // frames." The padding size is the one the extended header declares.
            long decoded = new Id3v2BodyReader(reader, start, bodyEnd, unsync).Skip(long.MaxValue);
            long remaining = Math.Max(0, decoded - (declaredPadding ?? 0));
            var body = new Id3v2BodyReader(reader, start, bodyEnd, unsync);
            while (remaining > 0)
            {
                MappedBytes chunk = body.Read((int)Math.Min(remaining, 1 << 16));
                if (chunk.Length == 0)
                {
                    break;
                }

                crc = Crc32.Append(crc, chunk.Span);
                remaining -= chunk.Length;
            }

            end = body.FilePosition;
        }
        else
        {
            // ID3v2.4 section 3.2: "calculated on all the data between the header and footer as
            // indicated by the header's tag length field, minus the extended header", padding
            // included, as stored.
            reader.Position = start;
            while (reader.Position < bodyEnd)
            {
                crc = Crc32.Append(crc, reader.ReadBytes((int)Math.Min(bodyEnd - reader.Position, 1 << 16)));
            }

            end = bodyEnd;
        }

        uint computed = Crc32.Finish(crc);
        NodeValue text = NodeValue.Hex(computed, state.Major == 3 ? 8 : 0);
        stored.Node.Add("Computed", stored.Node.Offset, stored.Node.Length, text, Explain.ComputedCrc);
        string covered = $"0x{start:X} to 0x{end:X}";
        if (stored.Value == computed)
        {
            state.Evidence.Add(new Evidence($"CRC-32 in the extended header matches the data from {covered}", start, end - start));
        }
        else
        {
            state.Findings.Add(new Finding(
                Severity.Warning,
                "id3v2.crc-mismatch",
                $"The extended header stores CRC-32 0x{stored.Value:X}, but the data from {covered} gives {text}.",
                stored.Node));
        }
    }

    private static void AddHeaderNode(Node tag, string name, string identifier, long offset, byte[] header)
    {
        int major = header[3];
        int flags = header[5];

        Node node = tag.Add(name, offset, HeaderSize, null, name == "Footer" ? Explain.Footer : Explain.Header);
        node.Add("Identifier", offset, 3, identifier, Explain.HeaderIdentifier);
        node.Add("Version", offset + 3, 2, $"2.{major}.{header[4]}", Explain.HeaderVersion);

        Node flagsNode = node.Add("Flags", offset + 5, 1, NodeValue.Hex(flags, 2), Explain.HeaderFlags);
        flagsNode.Add("IsUnsynchronized", offset + 5, 1, Bool(flags & 0x80), Explain.IsUnsynchronized, Topics.Unsynchronization);
        if (major == 2)
        {
            flagsNode.Add("IsCompressed", offset + 5, 1, Bool(flags & 0x40), Explain.IsCompressedTag);
        }
        else
        {
            flagsNode.Add("HasExtendedHeader", offset + 5, 1, Bool(flags & 0x40), Explain.HasExtendedHeader);
            flagsNode.Add("IsExperimental", offset + 5, 1, Bool(flags & 0x20), Explain.IsExperimental);
            if (major == 4)
            {
                flagsNode.Add("HasFooter", offset + 5, 1, Bool(flags & 0x10), Explain.HasFooter);
            }
        }

        node.Add("Size", offset + 6, 4, NodeValue.Integer(Syncsafe(header, 6)), Explain.Size, Topics.Unsynchronization);
    }

    // ID3v2.3 section 3.2: size (excluding itself, 6 or 10), flags, padding size, optional CRC.
    private static long? ParseExtendedHeader23(Id3v2BodyReader body, ParseState state)
    {
        long start = body.FilePosition;
        MappedBytes sizeBytes = body.Read(4);
        if (sizeBytes.Length < 4)
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, "The extended header is cut off by the end of the tag.");
            return null;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(sizeBytes.Span);
        if (size is not (6 or 10))
        {
            body.Skip(size);
            AddExtendedHeaderProblem(state, start, body.FilePosition, $"The extended header size is {size}; ID3v2.3 allows only 6 or 10.");
            return null;
        }

        MappedBytes data = body.Read((int)size);
        Node ext = state.Tag.Add("Extended Header", start, body.FilePosition - start, null, Explain.ExtendedHeader);
        ext.Add("Size", sizeBytes.OffsetOf(0), sizeBytes.FileLength(0, 4), NodeValue.Integer(size), Explain.ExtendedSize);
        if (data.Length < size)
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, "The extended header is cut off by the end of the tag.");
            return null;
        }

        int flags = BinaryPrimitives.ReadUInt16BigEndian(data.Span);
        Node flagsNode = ext.Add("Flags", data.OffsetOf(0), data.FileLength(0, 2), NodeValue.Hex(flags, 4), Explain.ExtendedFlags23);
        flagsNode.Add("HasCrc", data.OffsetOf(0), data.FileLength(0, 2), Bool(flags & 0x8000), Explain.ExtendedHasCrc);

        uint padding = BinaryPrimitives.ReadUInt32BigEndian(data.Span[2..]);
        ext.Add("Padding Size", data.OffsetOf(2), data.FileLength(2, 6), NodeValue.Integer(padding), Explain.ExtendedPadding, Topics.Padding);

        // "%x0000000 00000000": the CRC flag is the only one defined.
        if ((flags & 0x7FFF) != 0)
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, $"The extended header sets undefined flags (0x{flags & 0x7FFF:X4}); ID3v2.3 defines only the CRC flag.");
        }

        if ((flags & 0x8000) == 0 && size == 10)
        {
            // "If this flag is set four bytes of CRC-32 data is appended", so 10 bytes need it.
            ext.Add("Data", data.OffsetOf(6), data.FileLength(6, 10), NodeValue.Integer(4, "bytes"), Explain.ExtendedData);
            AddExtendedHeaderProblem(state, start, body.FilePosition, "The extended header is 10 bytes, the size with a CRC, but its CRC flag is clear; its last 4 bytes are unexplained.");
        }
        else if ((flags & 0x8000) != 0)
        {
            if (size < 10)
            {
                AddExtendedHeaderProblem(state, start, body.FilePosition, "The extended header has the CRC flag but no room for a CRC.");
            }
            else
            {
                uint crc = BinaryPrimitives.ReadUInt32BigEndian(data.Span[6..]);
                Node crcNode = ext.Add("CRC", data.OffsetOf(6), data.FileLength(6, 10), NodeValue.Hex(crc, 8), Explain.ExtendedCrc);
                state.StoredCrc = (crc, crcNode);
            }
        }

        return padding;
    }

    // ID3v2.4 section 3.2: syncsafe size including itself, number of flag bytes (1), flags, then
    // for each set flag a length byte and its data, in flag order.
    private static long? ParseExtendedHeader24(Id3v2BodyReader body, ParseState state)
    {
        long start = body.FilePosition;
        MappedBytes sizeBytes = body.Read(4);
        if (sizeBytes.Length < 4 || sizeBytes.Span.ContainsAnyInRange((byte)0x80, (byte)0xFF))
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, "The extended header size is missing or not syncsafe.");
            return null;
        }

        long size = Syncsafe(sizeBytes.Span, 0);
        if (size < 6)
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, $"The extended header size is {size}; ID3v2.4 requires at least 6.");
            return null;
        }

        MappedBytes data = body.Read((int)Math.Min(size - 4, MaxDecodedFrameBytes));
        body.Skip(size - 4 - data.Length);
        Node ext = state.Tag.Add("Extended Header", start, body.FilePosition - start, null, Explain.ExtendedHeader);
        ext.Add("Size", sizeBytes.OffsetOf(0), 4, NodeValue.Integer(size), Explain.ExtendedSize);
        if (data.Length < 2)
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, "The extended header is cut off by the end of the tag.");
            return null;
        }

        int flagBytes = data.Span[0];
        ext.Add("Flag Bytes", data.OffsetOf(0), 1, NodeValue.Integer(flagBytes), Explain.FlagBytes);
        int flags = data.Span[1];
        Node flagsNode = ext.Add("Flags", data.OffsetOf(1), 1, NodeValue.Hex(flags, 2), Explain.ExtendedFlags24);
        flagsNode.Add("IsUpdate", data.OffsetOf(1), 1, Bool(flags & 0x40), Explain.IsUpdate);
        flagsNode.Add("HasCrc", data.OffsetOf(1), 1, Bool(flags & 0x20), Explain.ExtendedHasCrc);
        flagsNode.Add("HasRestrictions", data.OffsetOf(1), 1, Bool(flags & 0x10), Explain.HasRestrictions);

        // "Number of flag bytes $01"; the flag data follows all flag bytes.
        if (flagBytes != 1)
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, $"The extended header has {flagBytes} flag bytes; ID3v2.4 defines 1.");
        }

        // "%0bcd0000 ... All unknown flags MUST be unset".
        if ((flags & ~0x70) != 0)
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, $"The extended header sets undefined flags (0x{flags & ~0x70:X2}), which ID3v2.4 requires to be unset.");
        }

        int i = 1 + Math.Max(flagBytes, 1);
        foreach ((int flag, string name, int expected, string description) in new[]
        {
            (0x40, "Update", 0, Explain.UpdateData), (0x20, "CRC", 5, Explain.ExtendedCrc), (0x10, "Restrictions", 1, Explain.Restrictions),
        })
        {
            if ((flags & flag) == 0)
            {
                continue;
            }

            if (i >= data.Length || i + 1 + data.Span[i] > data.Length)
            {
                AddExtendedHeaderProblem(state, start, body.FilePosition, $"The extended header ends before its {name.ToLowerInvariant()} data.");
                return null;
            }

            int length = data.Span[i];
            ReadOnlySpan<byte> value = data.Span.Slice(i + 1, length);
            Node node = ext.Add(name, data.OffsetOf(i), data.FileLength(i, i + 1 + length), FormatExtendedValue(name, value), description);
            if (length != expected)
            {
                AddExtendedHeaderProblem(state, start, body.FilePosition, $"The extended header's {name.ToLowerInvariant()} data is {length} bytes; ID3v2.4 defines {expected}.");
            }

            if (name == "Restrictions" && length == 1)
            {
                AddRestrictions(node, value[0]);
                state.Restrictions = value[0];
            }
            else if (name == "CRC" && length == 5)
            {
                state.StoredCrc = (Syncsafe35(value), node);
            }

            i += 1 + length;
        }

        // "Extended header size ... is the size of the whole extended header".
        if (4 + i < size)
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, $"The extended header declares {size} bytes, but its flags and their data take {4 + i}.");
        }

        return null;
    }

    private static NodeValue FormatExtendedValue(string name, ReadOnlySpan<byte> value) => name switch
    {
        "CRC" when value.Length == 5 => NodeValue.Hex((long)Syncsafe35(value), 0),
        "Restrictions" when value.Length == 1 => NodeValue.Hex(value[0], 2),
        _ => NodeValue.Integer(value.Length, "bytes"),
    };

    // ID3v2.4 section 3.2, restrictions byte %ppqrrstt.
    private static void AddRestrictions(Node node, byte value)
    {
        string[] tagSize =
        [
            "No more than 128 frames and 1 MB",
            "No more than 64 frames and 128 KB",
            "No more than 32 frames and 40 KB",
            "No more than 32 frames and 4 KB",
        ];
        string[] textSize =
        [
            "No restrictions",
            "No more than 1024 characters",
            "No more than 128 characters",
            "No more than 30 characters",
        ];
        string[] imageSize =
        [
            "No restrictions",
            "256x256 pixels or less",
            "64x64 pixels or less",
            "Exactly 64x64 pixels, unless required otherwise",
        ];

        long offset = node.End - 1;
        node.Add("Tag Size", offset, 1, tagSize[value >> 6], Explain.RestrictTagSize);
        node.Add("Text Encoding", offset, 1, (value & 0x20) != 0 ? "ISO-8859-1 or UTF-8 only" : "No restrictions", Explain.RestrictTextEncoding, Topics.TextEncodings);
        node.Add("Text Size", offset, 1, textSize[(value >> 3) & 0x03], Explain.RestrictTextSize);
        node.Add("Image Encoding", offset, 1, (value & 0x04) != 0 ? "PNG or JPEG only" : "No restrictions", Explain.RestrictImageEncoding);
        node.Add("Image Size", offset, 1, imageSize[value & 0x03], Explain.RestrictImageSize);
    }

    private static void AddExtendedHeaderProblem(ParseState state, long start, long end, string message)
    {
        Node node = state.Tag.Children.LastOrDefault(c => c.Name == "Extended Header") ?? state.Tag.Add("Extended Header", start, end - start, null, Explain.ExtendedHeader);
        state.Findings.Add(new Finding(Severity.Warning, "id3v2.extended-header-invalid", message, node));
    }

    // "A tag MUST contain at least one frame" (ID3v2.4 section 4, 2.3 section 3.3), and of most
    // kinds a tag may hold only one frame (ID3v2.3 section 4, ID3v2.4 frames section 4): text and
    // URL frames of each kind except the user-defined ones and WCOM and WOAR, the frames listed
    // in SingleFrameIds, and a picture of type 1 or 2 each. SEEK has its own finding.
    private static void CheckFrameSet(ParseState state)
    {
        if (state.Frames.Count == 0 && state.Unparsed is null)
        {
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.no-frames", "The tag holds no frame; ID3v2 requires at least one.", state.Tag));
            return;
        }

        foreach (IGrouping<string, Node> group in state.Frames.GroupBy(UniqueKind).Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            Node second = group.ElementAt(1);
            state.Findings.Add(new Finding(
                Severity.Warning,
                "id3v2.frame-duplicate",
                $"The tag holds {group.Count()} {group.Key} frames, where ID3v2.{state.Major} allows one; the second at 0x{second.Offset:X}.",
                second));
        }

        foreach (var group in state.RepeatKeys.GroupBy(k => k.Key).Where(g => g.Count() > 1))
        {
            var second = group.ElementAt(1);
            string same = second.Shown is null ? $"the same {second.Field}" : $"the same {second.Field}, {second.Shown}";
            state.Findings.Add(new Finding(
                Severity.Warning,
                "id3v2.frame-duplicate",
                $"The tag holds {group.Count()} {second.Frame.Name} frames with {same}, where ID3v2.{state.Major} allows one; the second at 0x{second.Frame.Offset:X}.",
                second.Frame));
        }
    }

    private static void CheckUnsynchronization(ParseState state, Id3v2BodyReader body, bool unsync)
    {
        // ID3v2.3 section 5 (and ID3v2.2 section 5): the flag says every false sync was corrected.
        if (state.Major < 4 && unsync && body.FalseSync is { } falseSync)
        {
            state.Findings.Add(new Finding(
                Severity.Warning,
                "id3v2.false-sync",
                $"The tag is marked as unsynchronized, but holds a false synchronization at 0x{falseSync:X}, which unsynchronization removes.",
                state.Tag.Children.LastOrDefault(c => c.Offset <= falseSync && falseSync < c.End) ?? state.Tag));
        }

        // ID3v2.4 section 6.1: the header flag "MUST NOT be set if the tag has a frame which is not
        // unsynchronised". Such frames are still read as unsynchronized, as mutagen reads them.
        if (state.NotUnsynchronized.Count > 0)
        {
            Node first = state.NotUnsynchronized[0];
            string which = state.NotUnsynchronized.Count == 1
                ? $"frame {first.Name} at 0x{first.Offset:X} is not"
                : $"{state.NotUnsynchronized.Count} frames are not, the first {first.Name} at 0x{first.Offset:X}";
            state.Findings.Add(new Finding(
                Severity.Warning,
                "id3v2.unsync-flag-mismatch",
                $"The header sets the unsynchronization flag, but {which}; ID3v2.4 allows the flag only when every frame is unsynchronized.",
                first));
        }
    }

    // ID3v2.4 section 3.2, restrictions %ppqrrstt: "the tag is restricted as follows". What the
    // decoded frames show is checked; frames this tool does not decode are not.
    private static void CheckRestrictions(ParseState state, long tagLength)
    {
        if (state.Restrictions is not { } restrictions)
        {
            return;
        }

        void Violation(string message, Node node) =>
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.restriction-violated", $"The extended header restricts {message}.", node));

        (int Frames, int Bytes, string Text) size = (restrictions >> 6) switch
        {
            0 => (128, 1 << 20, "128 frames and 1 MB"),
            1 => (64, 128 << 10, "64 frames and 128 KB"),
            2 => (32, 40 << 10, "32 frames and 40 KB"),
            _ => (32, 4 << 10, "32 frames and 4 KB"),
        };
        if (state.Frames.Count > size.Frames)
        {
            Violation($"the tag to {size.Text}, but it holds {state.Frames.Count} frames", state.Tag);
        }

        if (tagLength > size.Bytes)
        {
            Violation($"the tag to {size.Text}, but it is {tagLength} bytes long", state.Tag);
        }

        bool latin1OrUtf8 = (restrictions & 0x20) != 0;
        int maxCharacters = ((restrictions >> 3) & 0x03) switch
        {
            0 => int.MaxValue,
            1 => 1024,
            2 => 128,
            _ => 30,
        };
        bool pngOrJpeg = (restrictions & 0x04) != 0;
        int imageSize = restrictions & 0x03;

        foreach (Node frame in state.Frames)
        {
            string? encoding = Child(frame, "Encoding");
            if (latin1OrUtf8 && encoding is not (null or "ISO-8859-1" or "UTF-8"))
            {
                Violation($"text to ISO-8859-1 and UTF-8, but frame {frame.Name} at 0x{frame.Offset:X} uses {encoding}", frame);
            }

            // "If a text frame consists of more than one string, the sum of the strungs is
            // restricted as stated."
            int text = frame.Children.Where(c => c.Name == "Text").Sum(c => Characters(c.Value?.ToString()));
            int longest = frame.Children.Where(c => c.Name == "Description").Select(c => Characters(c.Value?.ToString())).Append(text).Max();
            if (longest > maxCharacters)
            {
                Violation($"strings to {maxCharacters} characters, but frame {frame.Name} at 0x{frame.Offset:X} holds one of {longest}", frame);
            }

            // "-->" links to an image instead of holding one (ID3v2.4 frames section 4.14).
            if (frame.Name != "APIC" || Child(frame, "MIME Type") is not { } mime || mime == "-->")
            {
                continue;
            }

            ImageInfo? image = state.Images.TryGetValue(frame, out ImageInfo found) ? found : null;
            string? format = image?.Format ?? FormatOfMimeType(mime);
            if (pngOrJpeg && format is not ("PNG" or "JPEG"))
            {
                Violation($"images to PNG and JPEG, but frame APIC at 0x{frame.Offset:X} holds {(mime.Length > 0 ? mime : "an image of no stated type")}", frame);
            }

            // "Exactly 64x64 pixels, unless required otherwise": a picture of type 1 is 32x32.
            bool isIcon = Child(frame, "Picture Type")?.StartsWith("1 ", StringComparison.Ordinal) == true;
            if (image is { Width: int width, Height: int height } && imageSize != 0)
            {
                (bool fits, string limit) = imageSize switch
                {
                    1 => (width <= 256 && height <= 256, "256x256 pixels or less"),
                    2 => (width <= 64 && height <= 64, "64x64 pixels or less"),
                    _ => ((width == 64 && height == 64) || isIcon, "exactly 64x64 pixels"),
                };
                if (!fits)
                {
                    Violation($"images to {limit}, but frame APIC at 0x{frame.Offset:X} holds one of {width}x{height}", frame);
                }
            }
        }
    }

    private static string? Child(Node node, string name) => node.Children.FirstOrDefault(c => c.Name == name)?.Value?.ToString();

    private static int Characters(string? text) => text?.EnumerateRunes().Count() ?? 0;

    // ID3v2.4 frames section 4.14: "In the event that the MIME media type name is omitted,
    // "image/" will be implied."
    private static string? FormatOfMimeType(string mime) =>
        (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? mime[6..] : mime).ToLowerInvariant() switch
        {
            "png" => "PNG",
            "jpeg" or "jpg" => "JPEG",
            _ => null,
        };

    // What makes a frame one of a kind that may occur once, or empty where it may repeat.
    private static string UniqueKind(Node frame)
    {
        string id = frame.Name;
        if (id is "APIC" or "PIC")
        {
            string? type = frame.Children.FirstOrDefault(c => c.Name == "Picture Type")?.Value?.ToString();
            return type is not null && (type.StartsWith("1 ", StringComparison.Ordinal) || type.StartsWith("2 ", StringComparison.Ordinal))
                ? $"{id} picture type {type}"
                : string.Empty;
        }

        bool repeatable = id is "TXXX" or "TXX" or "WXXX" or "WXX" or "WCOM" or "WCM" or "WOAR" or "WAR";
        return !repeatable && (id[0] is 'T' or 'W' || SingleFrameIds.Contains(id)) ? id : string.Empty;
    }

    // Frames other than text and URL frames that ID3v2.2, 2.3 or 2.4 allow only once per tag.
    private static readonly HashSet<string> SingleFrameIds =
    [
        "MCDI", "ETCO", "MLLT", "SYTC", "RVAD", "EQUA", "RVRB", "PCNT", "RBUF", "POSS", "OWNE", "ASPI", "IPLS",
        "MCI", "ETC", "MLL", "STC", "RVA", "EQU", "REV", "CNT", "BUF", "IPL",
    ];

    // Returns the number of padding bytes after the last frame.
    private static long ParseFrames(Id3v2BodyReader body, ParseState state, bool tagUnsync)
    {
        int major = state.Major;
        int idLength = major == 2 ? 3 : 4;
        int headerLength = major == 2 ? 6 : 10;
        long framesStart = body.FilePosition;
        state.PlainSizes = major == 4 && HasPlainFrameSizes(body.Reader, framesStart, body.End);

        while (!body.AtEnd)
        {
            long frameStart = body.FilePosition;
            MappedBytes header = body.Read(headerLength);
            if (header.Length == 0)
            {
                break;
            }

            if (header.Span[0] == 0)
            {
                return ParsePadding(body, state, frameStart, header, framesStart);
            }

            if (header.Length < headerLength)
            {
                AddUnparsed(state, frameStart, body.FilePosition, "id3v2.frame-header-truncated", $"The {header.Length} bytes at 0x{frameStart:X} are too short for a frame header.");
                return 0;
            }

            string id = Encoding.Latin1.GetString(header.Span[..idLength]);
            if (!id.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
            {
                body.Skip(long.MaxValue);
                AddUnparsed(
                    state,
                    frameStart,
                    body.FilePosition,
                    "id3v2.frame-id-invalid",
                    $"Invalid frame ID \"{Escape(id)}\" at 0x{frameStart:X}; the remaining {body.FilePosition - frameStart} bytes of the tag are not parsed.");
                return 0;
            }

            ParseFrame(body, state, frameStart, header, id, tagUnsync);
        }

        if (!state.HasProblems)
        {
            state.Evidence.Add(new Evidence("Frames fill the tag exactly", framesStart, body.FilePosition - framesStart));
        }

        return 0;
    }

    private static void ParseFrame(Id3v2BodyReader body, ParseState state, long frameStart, MappedBytes header, string id, bool tagUnsync)
    {
        int major = state.Major;
        int idLength = major == 2 ? 3 : 4;
        int sizeLength = major == 2 ? 3 : 4;
        List<Finding> findings = state.Findings;

        long frameSize;
        bool plainSize = false;
        if (major == 2)
        {
            frameSize = (header.Span[3] << 16) | (header.Span[4] << 8) | header.Span[5];
        }
        else if (major == 3 || state.PlainSizes || header.Span.Slice(4, 4).ContainsAnyInRange((byte)0x80, (byte)0xFF))
        {
            // 2.3 sizes are plain integers. A 2.4 size with a high bit set cannot be syncsafe,
            // which is what some writers (old iTunes versions) produced; read it as plain too.
            frameSize = BinaryPrimitives.ReadUInt32BigEndian(header.Span[4..]);
            // Below 128 both readings agree, so only a size that differs shows the plain integer.
            plainSize = major == 4 && frameSize != Syncsafe(header.Span, 4);
        }
        else
        {
            frameSize = Syncsafe(header.Span, 4);
        }

        int flags = major == 2 ? 0 : BinaryPrimitives.ReadUInt16BigEndian(header.Span[8..]);

        MappedBytes raw = body.Read((int)Math.Min(frameSize, MaxDecodedFrameBytes));
        long readTotal = raw.Length + body.Skip(frameSize - raw.Length);
        long frameEnd = body.FilePosition;

        var frame = new Node(id, frameStart, frameEnd - frameStart) { Description = Id3v2FrameTexts.For(major, id), Topic = Id3v2FrameTexts.TopicFor(major, id) };
        state.Tag.Add(frame);
        state.Frames.Add(frame);

        FrameExtras extras = major switch
        {
            3 => FrameExtras.For23(flags),
            4 => FrameExtras.For24(flags),
            _ => new FrameExtras(),
        };

        // The extra header data counts as part of the frame header.
        int extrasLength = extras.Fields.Sum(f => f.Length);
        long headerEnd = raw.OffsetOf(Math.Min(extrasLength, raw.Length));
        Node headerNode = frame.Add("Header", frameStart, headerEnd - frameStart, null, Explain.FrameHeader);
        headerNode.Add("ID", header.OffsetOf(0), header.FileLength(0, idLength), id, Explain.FrameId);
        headerNode.Add("Size", header.OffsetOf(idLength), header.FileLength(idLength, idLength + sizeLength), NodeValue.Integer(frameSize), Explain.FrameSize);

        if (plainSize)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "id3v2.frame-size-not-syncsafe",
                $"Frame {id} at 0x{frameStart:X} stores its size as a plain integer instead of a syncsafe one, as old iTunes versions did.",
                frame));
        }

        if (readTotal < frameSize)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "id3v2.frame-too-large",
                $"Frame {id} at 0x{frameStart:X} declares {frameSize} bytes, but only {readTotal} remain in the tag.",
                frame));
        }

        if (major >= 3)
        {
            AddFrameFlags(headerNode, header, flags, major, id, findings, frame);
        }

        if (major == 4 && tagUnsync && !extras.IsUnsynchronized)
        {
            state.NotUnsynchronized.Add(frame);
        }

        // ID3v2.4 section 4.1.2 k: "If set, this requires the 'Data Length Indicator' bit to be
        // set as well."
        if (major == 4 && (flags & 0x0008) != 0 && (flags & 0x0001) == 0)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "id3v2.compressed-without-data-length",
                $"Frame {id} at 0x{frameStart:X} is compressed without a data length indicator, which ID3v2.4 requires.",
                frame));
        }

        // Extra header data follows the frame header in flag order (2.3 section 3.3.1, 2.4
        // section 4.1.2) and counts toward the frame size.
        int pos = 0;
        long? dataLength = null;
        foreach ((string name, int length, string description) in extras.Fields)
        {
            if (raw.Length < pos + length)
            {
                findings.Add(new Finding(Severity.Warning, "id3v2.frame-content-truncated", $"Frame {id} ends before its {name.ToLowerInvariant()}.", frame));
                return;
            }

            NodeValue value;
            if (length == 4)
            {
                long number = major == 4 ? Syncsafe(raw.Span, pos) : BinaryPrimitives.ReadUInt32BigEndian(raw.Span[pos..]);
                dataLength = number;
                value = NodeValue.Integer(number);
            }
            else
            {
                value = NodeValue.Integer(raw.Span[pos]);
            }

            headerNode.Add(name, raw.OffsetOf(pos), raw.FileLength(pos, pos + length), value, description);
            pos += length;
        }

        if (frameSize == 0)
        {
            findings.Add(new Finding(Severity.Warning, "id3v2.frame-empty", $"Frame {id} at 0x{frameStart:X} is empty; a frame must hold at least one byte.", frame));
            return;
        }

        // Nodes and values use the measured length; a data length indicator only states it.
        MappedBytes content = raw.Slice(pos);
        long decodedSize = readTotal - pos;
        if (major == 4 && (extras.IsUnsynchronized || tagUnsync))
        {
            content = Unsynchronization.Resynchronize(content, out long? falseSync);
            decodedSize = content.Length;
            if (raw.Length < readTotal)
            {
                (long rest, long? restFalseSync) = Unsynchronization.MeasureResynchronized(body.Reader, raw.OffsetOf(raw.Length), frameEnd, raw.Length > pos && raw.Span[^1] == 0xFF);
                decodedSize += rest;
                falseSync ??= restFalseSync;
            }

            // ID3v2.4 section 6.1: "A completely unsynchronised tag has no false synchonisations in it."
            if (falseSync is { } at)
            {
                findings.Add(new Finding(
                    Severity.Warning,
                    "id3v2.false-sync",
                    $"Frame {id} is unsynchronized, but holds a false synchronization at 0x{at:X}, which unsynchronization removes.",
                    frame));
            }
        }

        if (extras.IsEncrypted)
        {
            long offset = raw.OffsetOf(pos);
            frame.Add("Encrypted Data", offset, frameEnd - offset, NodeValue.Integer(frameEnd - offset, "bytes"), Explain.EncryptedData);
            findings.Add(new Finding(Severity.Info, "id3v2.frame-encrypted", $"Frame {id} is encrypted; its content is not decoded.", frame));
            return;
        }

        if (extras.IsCompressed)
        {
            long offset = raw.OffsetOf(pos);
            frame.Add("Compressed Data", offset, frameEnd - offset, NodeValue.Integer(frameEnd - offset, "bytes"), Explain.CompressedData);
            MappedBytes? inflated = Decompress(state, frame, content, raw.Length < readTotal, dataLength, offset, frameEnd);
            if (inflated is null)
            {
                return;
            }

            content = inflated;
            decodedSize = inflated.Length;
        }
        else if (major == 4 && dataLength is { } indicated && readTotal == frameSize && indicated != decodedSize)
        {
            // ID3v2.4 section 4.1.2 p: "the value one would write as the 'Frame length' if all of
            // the frame format flags were zeroed".
            findings.Add(new Finding(
                Severity.Warning,
                "id3v2.data-length-mismatch",
                $"Frame {id} at 0x{frameStart:X} has a data length indicator of {indicated} bytes, but its data is {decodedSize} bytes.",
                frame));
        }

        state.RepeatKeys.AddRange(Id3v2RepeatKeys.For(major, id, content.Data, isWhole: content.Length == decodedSize)
            .Select(k => (frame, k.Field, k.Shown, k.Key)));

        if (major == 4 && id == "SEEK")
        {
            ParseSeekFrame(state, frame, content);
            return;
        }

        frame.Value = Id3v2FrameDecoder.Decode(major, id, frame, content, decodedSize, frameEnd, findings, out ImageInfo? image);
        if (image is { } info)
        {
            state.Images[frame] = info;
        }
    }

    // ID3v2.3 section 3.3.1 i and ID3v2.4 section 4.1.2 k: zlib (RFC 1950). Decompression stops
    // one byte past the decode limit, so a small frame cannot expand into unbounded memory or time.
    private static MappedBytes? Decompress(ParseState state, Node frame, MappedBytes compressed, bool isCut, long? declared, long start, long end)
    {
        string id = frame.Name;
        string field = state.Major == 3 ? "decompressed size" : "data length indicator";
        if (isCut)
        {
            state.Findings.Add(new Finding(Severity.Info, "id3v2.frame-compressed", $"Frame {id} is zlib-compressed and larger than the decode limit for one frame; its content is not decoded.", frame));
            return null;
        }

        byte[]? output = Inflate(compressed.Data, MaxDecodedFrameBytes + 1);
        if (output is null)
        {
            state.Findings.Add(new Finding(
                Severity.Warning,
                "id3v2.frame-decompression-failed",
                $"Frame {id} at 0x{frame.Offset:X} is marked as zlib-compressed, but its data is not one complete zlib stream; its content is not decoded.",
                frame));
            return null;
        }

        if (output.Length > MaxDecodedFrameBytes)
        {
            state.Findings.Add(new Finding(Severity.Info, "id3v2.frame-compressed", $"Frame {id} is zlib-compressed and decompresses to more than the decode limit of {MaxDecodedFrameBytes} bytes; its content is not decoded.", frame));
            if (declared is { } small && small <= MaxDecodedFrameBytes)
            {
                state.Findings.Add(new Finding(Severity.Warning, "id3v2.decompressed-size-mismatch", $"Frame {id} at 0x{frame.Offset:X} declares {small} bytes in its {field}, but decompresses to more than {MaxDecodedFrameBytes}.", frame));
            }

            return null;
        }

        if (declared is { } size && size != output.Length)
        {
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.decompressed-size-mismatch", $"Frame {id} at 0x{frame.Offset:X} declares {size} bytes in its {field}, but decompresses to {output.Length}.", frame));
        }

        state.Findings.Add(new Finding(Severity.Info, "id3v2.frame-compressed", $"Frame {id} is zlib-compressed; its content is shown decompressed, with every field pointing at the compressed data.", frame));
        return MappedBytes.Opaque(output, start, end);
    }

    // Up to limit bytes of the zlib stream in data, or null when data is not one complete stream.
    private static byte[]? Inflate(ReadOnlyMemory<byte> data, int limit)
    {
        var output = new MemoryStream();
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
            byte[] buffer = new byte[1 << 16];
            int read;
            while (output.Length < limit && (read = zlib.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - output.Length))) > 0)
            {
                output.Write(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // Damaged data raises InvalidDataException or, from zlib itself, ZLibException.
            return null;
        }

        // ZLibStream returns what it has when the stream breaks off, so the Adler-32 that ends a
        // complete stream (RFC 1950 section 2.2) must be the frame's last four bytes.
        byte[] result = output.ToArray();
        if (result.Length < limit && (data.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(data.Span[^4..]) != Adler32(result)))
        {
            return null;
        }

        return result;
    }

    // RFC 1950 section 8.2.
    private static uint Adler32(byte[] data)
    {
        uint a = 1;
        uint b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    // ID3v2.4 frames section 4.29: "Minimum offset to next tag $xx xx xx xx", "calculated from
    // the end of this tag to the beginning of the next". "There may only be one 'seek frame' in
    // a tag."
    private static void ParseSeekFrame(ParseState state, Node frame, MappedBytes content)
    {
        if (content.Length != 4)
        {
            frame.Add("Data", content.OffsetOf(0), content.FileLength(0, content.Length), NodeValue.Integer(content.Length, "bytes"), Explain.SeekData);
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.seek-invalid", $"The SEEK frame holds {content.Length} bytes instead of 4.", frame));
            return;
        }

        uint offset = BinaryPrimitives.ReadUInt32BigEndian(content.Span);
        frame.Add("Minimum Offset", content.OffsetOf(0), content.FileLength(0, 4), NodeValue.Integer(offset), Explain.MinimumOffset);
        frame.Value = $"next tag at least {offset} bytes after this one";

        if (state.SeekOffset is not null)
        {
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.seek-duplicate", "The tag has more than one SEEK frame; the first one is used.", frame));
            return;
        }

        state.SeekOffset = offset;
    }

    private static void AddFrameFlags(Node headerNode, MappedBytes header, int flags, int major, string id, List<Finding> findings, Node frame)
    {
        long offset = header.OffsetOf(8);
        long length = header.FileLength(8, 10);
        Node node = headerNode.Add("Flags", offset, length, NodeValue.Hex(flags, 4), Explain.FrameFlags);

        (string Name, int Bit)[] names = major == 3
            ? [("IsDiscardedOnTagAlter", 0x8000), ("IsDiscardedOnFileAlter", 0x4000), ("IsReadOnly", 0x2000), ("IsCompressed", 0x0080), ("IsEncrypted", 0x0040), ("HasGroupId", 0x0020)]
            : [("IsDiscardedOnTagAlter", 0x4000), ("IsDiscardedOnFileAlter", 0x2000), ("IsReadOnly", 0x1000), ("HasGroupId", 0x0040), ("IsCompressed", 0x0008), ("IsEncrypted", 0x0004), ("IsUnsynchronized", 0x0002), ("HasDataLengthIndicator", 0x0001)];

        int defined = 0;
        foreach ((string name, int bit) in names)
        {
            node.Add(name, offset, length, Bool(flags & bit), FrameFlagText(name), name == "IsUnsynchronized" ? Topics.Unsynchronization : null);
            defined |= bit;
        }

        if ((flags & ~defined) != 0)
        {
            findings.Add(new Finding(Severity.Warning, "id3v2.frame-flags-unknown", $"Frame {id} sets undefined flag bits 0x{flags & ~defined:X4}.", frame));
        }
    }

    private static string FrameFlagText(string name) => name switch
    {
        "IsDiscardedOnTagAlter" => Explain.IsDiscardedOnTagAlter,
        "IsDiscardedOnFileAlter" => Explain.IsDiscardedOnFileAlter,
        "IsReadOnly" => Explain.IsReadOnly,
        "IsCompressed" => Explain.IsCompressed,
        "IsEncrypted" => Explain.IsEncrypted,
        "HasGroupId" => Explain.HasGroupId,
        "IsUnsynchronized" => Explain.IsFrameUnsynchronized,
        _ => Explain.HasDataLengthIndicator,
    };

    private static long ParsePadding(Id3v2BodyReader body, ParseState state, long start, MappedBytes first, long framesStart)
    {
        // Reading stops at the first non-zero byte, so a tag declaring a huge padding costs no more
        // than its zero bytes.
        long? nonZero = FirstNonZero(first);
        while (nonZero is null && !body.AtEnd)
        {
            MappedBytes chunk = body.Read(1 << 16);
            if (chunk.Length == 0)
            {
                break;
            }

            nonZero = FirstNonZero(chunk);
        }

        // Padding is zero bytes (ID3v2.3 section 3.0, ID3v2.4 section 3.3), so the padding of a tag
        // whose size runs past the end of the file stops at the first byte that is not zero.
        if (state.IsTruncated && nonZero is { } stop)
        {
            state.PaddingEnd = stop;
            nonZero = null;
        }

        long length = (state.PaddingEnd ?? body.End) - start;
        Node padding = state.Tag.Add("Padding", start, length, NodeValue.Integer(length, "bytes"), Explain.Padding, Topics.Padding);
        if (nonZero is not null)
        {
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.padding-not-zero", "Padding of the ID3v2 tag contains non-zero bytes.", padding));
        }
        else
        {
            state.Evidence.Add(new Evidence("Padding is all zero bytes", start, length));
        }

        if (!state.HasProblems)
        {
            state.Evidence.Add(new Evidence("Frames fill the tag up to the padding", framesStart, start - framesStart));
        }

        return length;
    }

    private static long? FirstNonZero(MappedBytes bytes)
    {
        int index = bytes.Span.IndexOfAnyExcept((byte)0);
        return index < 0 ? null : bytes.OffsetOf(index);
    }

    private static void AddUnparsed(ParseState state, long start, long end, string code, string message)
    {
        Node node = state.Tag.Add("Unparsed", start, end - start, NodeValue.Integer(end - start, "bytes"), Explain.Unparsed);
        state.Findings.Add(new Finding(Severity.Warning, code, message, node));
        state.Unparsed ??= node;
    }

    private static void ParseFooter(ByteReader reader, long offset, byte[] header, ParseState state)
    {
        if (offset + HeaderSize > reader.End)
        {
            return;
        }

        byte[] footer = reader.ReadBytesAt(offset, HeaderSize);
        AddHeaderNode(state.Tag, "Footer", Encoding.Latin1.GetString(footer, 0, 3), offset, footer);

        if (footer.AsSpan(0, 3).SequenceEqual(FooterId) && footer.AsSpan(3).SequenceEqual(header.AsSpan(3)))
        {
            state.Evidence.Add(new Evidence("Footer repeats the header", offset, HeaderSize));
        }
        else
        {
            Node node = state.Tag.Children[^1];
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.footer-mismatch", "The footer does not repeat the header's version, flags and size after \"3DI\".", node));
        }
    }

    // ID3v2.4 section 3.2: "The CRC-32 is stored as an 35 bit synchsafe integer, leaving the
    // upper four bits always zeroed."
    private static ulong Syncsafe35(ReadOnlySpan<byte> value)
    {
        ulong result = 0;
        foreach (byte b in value)
        {
            result = (result << 7) | (uint)(b & 0x7F);
        }

        return result;
    }

    private static long Syncsafe(ReadOnlySpan<byte> data, int offset) =>
        ((long)(data[offset] & 0x7F) << 21) | ((long)(data[offset + 1] & 0x7F) << 14) | ((long)(data[offset + 2] & 0x7F) << 7) | (long)(data[offset + 3] & 0x7F);

    private static NodeValue Bool(int value) => NodeValue.Boolean(value != 0);

    private static string Escape(string text)
    {
        var builder = new StringBuilder();
        foreach (char c in text)
        {
            builder.Append(c is >= ' ' and < '\x7F' ? c.ToString() : $"\\x{(int)c:X2}");
        }

        return builder.ToString();
    }

    private sealed class ParseState(Node tag, int major)
    {
        public Node Tag { get; } = tag;

        public int Major { get; } = major;

        public List<Finding> Findings { get; } = [];

        public List<Evidence> Evidence { get; } = [];

        // Whether the tag has any warning or error, the decoders' findings included.
        public bool HasProblems => Findings.Any(f => f.Severity >= Severity.Warning);

        public bool IsTruncated { get; init; }

        // Where the zero padding of a truncated tag stops, if data follows it.
        public long? PaddingEnd { get; set; }

        // The restrictions byte of an ID3v2.4 extended header.
        public byte? Restrictions { get; set; }

        public Dictionary<Node, ImageInfo> Images { get; } = [];

        public List<(Node Frame, string Field, string? Shown, string Key)> RepeatKeys { get; } = [];

        // ID3v2.4 frames without the unsynchronization flag in a tag that sets it in its header.
        public List<Node> NotUnsynchronized { get; } = [];

        public long ExtendedHeaderEnd { get; set; }

        public (ulong Value, Node Node)? StoredCrc { get; set; }

        public long? SeekOffset { get; set; }

        // ID3v2.4 frame sizes read as plain integers throughout, as old iTunes versions wrote them.
        public bool PlainSizes { get; set; }

        // The first bytes the frames could not be read from.
        public Node? Unparsed { get; set; }

        public List<Node> Frames { get; } = [];
    }

    // The frame IDs ID3v2.3 and 2.4 declare, and those common writers add (mutagen, _frames.py).
    private static readonly HashSet<string> FrameIds =
    [
        "AENC", "APIC", "ASPI", "CHAP", "COMM", "COMR", "CTOC", "ENCR", "EQU2", "EQUA", "ETCO", "GEOB", "GRID", "GRP1",
        "IPLS", "LINK", "MCDI", "MLLT", "MVIN", "MVNM", "OWNE", "PCNT", "PCST", "POPM", "POSS", "PRIV", "RBUF", "RVA2",
        "RVAD", "RVRB", "SEEK", "SIGN", "SYLT", "SYTC", "TALB", "TBPM", "TCAT", "TCMP", "TCOM", "TCON", "TCOP", "TDAT",
        "TDEN", "TDES", "TDLY", "TDOR", "TDRC", "TDRL", "TDTG", "TENC", "TEXT", "TFLT", "TGID", "TIME", "TIPL", "TIT1",
        "TIT2", "TIT3", "TKEY", "TKWD", "TLAN", "TLEN", "TMCL", "TMED", "TMOO", "TOAL", "TOFN", "TOLY", "TOPE", "TORY",
        "TOWN", "TPE1", "TPE2", "TPE3", "TPE4", "TPOS", "TPRO", "TPUB", "TRCK", "TRDA", "TRSN", "TRSO", "TSIZ", "TSO2",
        "TSOA", "TSOC", "TSOP", "TSOT", "TSRC", "TSSE", "TSST", "TXXX", "TYER", "UFID", "USER", "USLT", "WCOM", "WCOP",
        "WFED", "WOAF", "WOAR", "WOAS", "WORS", "WPAY", "WPUB", "WXXX",
    ];

    private static bool FollowsDirectly(ByteReader reader, long bodyStart, long bodyEnd) =>
        bodyEnd - bodyStart >= 4 && FrameIds.Contains(Encoding.Latin1.GetString(reader.ReadBytesAt(bodyStart, 4)));

    // Old iTunes versions wrote ID3v2.4 frame sizes as plain integers. The frames are walked with
    // both readings, counting the declared frame IDs each finds; plain wins with more of them, or
    // with as many when the syncsafe walk overshoots the tag and the plain one does not
    // (mutagen, _tags.py, determine_bpi).
    private static bool HasPlainFrameSizes(ByteReader reader, long start, long end)
    {
        (int syncsafeFound, long syncsafeOver) = Walk(syncsafe: true);
        (int plainFound, long plainOver) = Walk(syncsafe: false);
        return plainFound > syncsafeFound || (plainFound == syncsafeFound && syncsafeOver >= 1 && plainOver <= 1);

        (int Found, long Overshoot) Walk(bool syncsafe)
        {
            int found = 0;
            long at = start;
            while (at < end - 10)
            {
                byte[] header = reader.ReadBytesAt(at, 10);
                if (header.All(b => b == 0))
                {
                    return (found, -((end - at) % 10));
                }

                at += 10 + (syncsafe ? Syncsafe(header, 4) : BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)));
                found += FrameIds.Contains(Encoding.Latin1.GetString(header, 0, 4)) ? 1 : 0;
            }

            return (found, at - end);
        }
    }

    private readonly struct FrameExtras
    {
        public FrameExtras()
        {
            Fields = [];
        }

        public List<(string Name, int Length, string Description)> Fields { get; init; }

        public bool IsCompressed { get; init; }

        public bool IsEncrypted { get; init; }

        public bool IsUnsynchronized { get; init; }

        // 2.3 section 3.3.1: decompressed size, encryption method, group ID.
        public static FrameExtras For23(int flags)
        {
            var fields = new List<(string, int, string)>();
            if ((flags & 0x0080) != 0)
            {
                fields.Add(("Decompressed Size", 4, Explain.DecompressedSize));
            }

            if ((flags & 0x0040) != 0)
            {
                fields.Add(("Encryption Method", 1, Explain.EncryptionMethod));
            }

            if ((flags & 0x0020) != 0)
            {
                fields.Add(("Group ID", 1, Explain.GroupId));
            }

            return new FrameExtras { Fields = fields, IsCompressed = (flags & 0x0080) != 0, IsEncrypted = (flags & 0x0040) != 0 };
        }

        // 2.4 section 4.1.2: group ID, encryption method, data length indicator.
        public static FrameExtras For24(int flags)
        {
            var fields = new List<(string, int, string)>();
            if ((flags & 0x0040) != 0)
            {
                fields.Add(("Group ID", 1, Explain.GroupId));
            }

            if ((flags & 0x0004) != 0)
            {
                fields.Add(("Encryption Method", 1, Explain.EncryptionMethod));
            }

            if ((flags & 0x0001) != 0)
            {
                fields.Add(("Data Length", 4, Explain.DataLength));
            }

            return new FrameExtras
            {
                Fields = fields,
                IsCompressed = (flags & 0x0008) != 0,
                IsEncrypted = (flags & 0x0004) != 0,
                IsUnsynchronized = (flags & 0x0002) != 0,
            };
        }
    }
}
