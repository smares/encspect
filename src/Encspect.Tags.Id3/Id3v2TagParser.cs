using System.Buffers.Binary;
using System.Globalization;
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

    public TagProbe? ProbeAt(ByteReader reader, long start)
    {
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

    // ID3v2.4 section 3.4: a tag appended at the end of a file has a footer, "3DI" followed by
    // the same version, flags and size as the header, so it can be found from the end.
    public TagProbe? ProbeBefore(ByteReader reader, long end)
    {
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
        var tag = new Node(Name, start, end - start, $"2.{major}.{revision}");
        var state = new ParseState(tag, major);

        state.Evidence.Add(new Evidence($"Signature \"ID3\" with version 2.{major}.{revision}", start, 5));
        state.Evidence.Add(new Evidence($"Syncsafe size of {size} bytes", start + 6, 4));

        AddHeaderNode(tag, "Header", "ID3", start, header);
        Node headerNode = tag.Children[^1];

        if (revision == 0xFF)
        {
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.revision-invalid", "The header revision is $FF, which the specification rules out.", headerNode));
            state.HasProblems = true;
        }

        int undefined = flags & ~DefinedHeaderFlags(major);
        if (undefined != 0)
        {
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.header-flags-unknown", $"The header sets undefined flag bits ${undefined:X2}.", headerNode));
            state.HasProblems = true;
        }
        else
        {
            state.Evidence.Add(new Evidence("Only defined header flags are set", start + 5, 1));
        }

        bool truncated = declaredEnd > reader.End;
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
            tag.Add("Compressed Data", bodyStart, length, $"{length} bytes");
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.compressed-tag", "The ID3v2.2 tag is marked as compressed, which no specification defines; its content is not parsed.", tag));
            state.HasProblems = true;
        }
        else
        {
            // 2.4 unsynchronizes frame by frame instead (ID3v2.4 section 6.1).
            var body = new Id3v2BodyReader(reader, bodyStart, bodyEnd, unsync && major < 4);
            long? declaredPadding = null;
            if (major >= 3 && (flags & 0x40) != 0)
            {
                declaredPadding = major == 3 ? ParseExtendedHeader23(body, state) : ParseExtendedHeader24(body, state);
            }

            long padding = ParseFrames(body, state, unsync);

            if (declaredPadding is { } expected && expected != padding)
            {
                state.Findings.Add(new Finding(
                    Severity.Info,
                    "id3v2.padding-size-mismatch",
                    $"The extended header declares {expected} bytes of padding, but the tag has {padding}.",
                    tag));
            }

            if (hasFooter && padding > 0)
            {
                state.Findings.Add(new Finding(Severity.Warning, "id3v2.padding-with-footer", "The tag has both padding and a footer; ID3v2.4 section 3.3 forbids padding when a footer is present.", tag));
                state.HasProblems = true;
            }
        }

        if (hasFooter)
        {
            ParseFooter(reader, start + HeaderSize + size, header, state);
        }

        Confidence confidence = truncated ? Confidence.Low : state.HasProblems ? Confidence.Medium : Confidence.High;
        return TagProbe.Accept(tag, signatureOffset, confidence, state.Evidence, state.Findings);
    }

    private static void AddHeaderNode(Node tag, string name, string identifier, long offset, byte[] header)
    {
        int major = header[3];
        int flags = header[5];

        Node node = tag.Add(name, offset, HeaderSize);
        node.Add("Identifier", offset, 3, identifier);
        node.Add("Version", offset + 3, 2, $"2.{major}.{header[4]}");

        Node flagsNode = node.Add("Flags", offset + 5, 1, $"0x{flags:X2}");
        flagsNode.Add("IsUnsynchronized", offset + 5, 1, Bool(flags & 0x80));
        if (major == 2)
        {
            flagsNode.Add("IsCompressed", offset + 5, 1, Bool(flags & 0x40));
        }
        else
        {
            flagsNode.Add("HasExtendedHeader", offset + 5, 1, Bool(flags & 0x40));
            flagsNode.Add("IsExperimental", offset + 5, 1, Bool(flags & 0x20));
            if (major == 4)
            {
                flagsNode.Add("HasFooter", offset + 5, 1, Bool(flags & 0x10));
            }
        }

        node.Add("Size", offset + 6, 4, Syncsafe(header, 6).ToString(CultureInfo.InvariantCulture));
    }

    // ID3v2.3 section 3.2: size (excluding itself, 6 or 10), flags, padding size, optional CRC.
    private static long? ParseExtendedHeader23(Id3v2BodyReader body, ParseState state)
    {
        long start = body.FilePosition;
        DecodedBytes sizeBytes = body.Read(4);
        if (sizeBytes.Length < 4)
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, "The extended header is cut off by the end of the tag.");
            return null;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(sizeBytes.Data);
        if (size is not (6 or 10))
        {
            body.Skip(size);
            AddExtendedHeaderProblem(state, start, body.FilePosition, $"The extended header size is {size}; ID3v2.3 allows only 6 or 10.");
            return null;
        }

        DecodedBytes data = body.Read((int)size);
        Node ext = state.Tag.Add("Extended Header", start, body.FilePosition - start);
        ext.Add("Size", sizeBytes.OffsetOf(0), sizeBytes.FileLength(0, 4), size.ToString(CultureInfo.InvariantCulture));
        if (data.Length < size)
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, "The extended header is cut off by the end of the tag.");
            return null;
        }

        int flags = BinaryPrimitives.ReadUInt16BigEndian(data.Data);
        Node flagsNode = ext.Add("Flags", data.OffsetOf(0), data.FileLength(0, 2), $"0x{flags:X4}");
        flagsNode.Add("HasCrc", data.OffsetOf(0), data.FileLength(0, 2), Bool(flags & 0x8000));

        uint padding = BinaryPrimitives.ReadUInt32BigEndian(data.Data.AsSpan(2));
        ext.Add("Padding Size", data.OffsetOf(2), data.FileLength(2, 6), padding.ToString(CultureInfo.InvariantCulture));

        if ((flags & 0x8000) != 0)
        {
            if (size < 10)
            {
                AddExtendedHeaderProblem(state, start, body.FilePosition, "The extended header has the CRC flag but no room for a CRC.");
            }
            else
            {
                uint crc = BinaryPrimitives.ReadUInt32BigEndian(data.Data.AsSpan(6));
                ext.Add("CRC", data.OffsetOf(6), data.FileLength(6, 10), $"0x{crc:X8}");
            }
        }

        return padding;
    }

    // ID3v2.4 section 3.2: syncsafe size including itself, number of flag bytes (1), flags, then
    // for each set flag a length byte and its data, in flag order.
    private static long? ParseExtendedHeader24(Id3v2BodyReader body, ParseState state)
    {
        long start = body.FilePosition;
        DecodedBytes sizeBytes = body.Read(4);
        if (sizeBytes.Length < 4 || sizeBytes.Data.Any(b => b >= 0x80))
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, "The extended header size is missing or not syncsafe.");
            return null;
        }

        long size = Syncsafe(sizeBytes.Data, 0);
        if (size < 6)
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, $"The extended header size is {size}; ID3v2.4 requires at least 6.");
            return null;
        }

        DecodedBytes data = body.Read((int)Math.Min(size - 4, MaxDecodedFrameBytes));
        Node ext = state.Tag.Add("Extended Header", start, body.FilePosition - start);
        ext.Add("Size", sizeBytes.OffsetOf(0), 4, size.ToString(CultureInfo.InvariantCulture));
        if (data.Length < 2)
        {
            AddExtendedHeaderProblem(state, start, body.FilePosition, "The extended header is cut off by the end of the tag.");
            return null;
        }

        ext.Add("Flag Bytes", data.OffsetOf(0), 1, data.Data[0].ToString(CultureInfo.InvariantCulture));
        int flags = data.Data[1];
        Node flagsNode = ext.Add("Flags", data.OffsetOf(1), 1, $"0x{flags:X2}");
        flagsNode.Add("IsUpdate", data.OffsetOf(1), 1, Bool(flags & 0x40));
        flagsNode.Add("HasCrc", data.OffsetOf(1), 1, Bool(flags & 0x20));
        flagsNode.Add("HasRestrictions", data.OffsetOf(1), 1, Bool(flags & 0x10));

        int i = 2;
        foreach ((int flag, string name) in new[] { (0x40, "Update"), (0x20, "CRC"), (0x10, "Restrictions") })
        {
            if ((flags & flag) == 0)
            {
                continue;
            }

            if (i >= data.Length || i + 1 + data.Data[i] > data.Length)
            {
                AddExtendedHeaderProblem(state, start, body.FilePosition, $"The extended header ends before its {name.ToLowerInvariant()} data.");
                return null;
            }

            int length = data.Data[i];
            ReadOnlySpan<byte> value = data.Data.AsSpan(i + 1, length);
            Node node = ext.Add(name, data.OffsetOf(i), data.FileLength(i, i + 1 + length), FormatExtendedValue(name, value));
            if (name == "Restrictions" && length == 1)
            {
                AddRestrictions(node, value[0]);
            }

            i += 1 + length;
        }

        return null;
    }

    private static string FormatExtendedValue(string name, ReadOnlySpan<byte> value)
    {
        switch (name)
        {
            case "CRC" when value.Length == 5:
                // A 35-bit CRC-32 stored as five syncsafe bytes.
                ulong crc = 0;
                foreach (byte b in value)
                {
                    crc = (crc << 7) | (uint)(b & 0x7F);
                }

                return $"0x{crc:X}";
            case "Restrictions" when value.Length == 1:
                return $"0x{value[0]:X2}";
            default:
                return $"{value.Length} bytes";
        }
    }

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
        node.Add("Tag Size", offset, 1, tagSize[value >> 6]);
        node.Add("Text Encoding", offset, 1, (value & 0x20) != 0 ? "ISO-8859-1 or UTF-8 only" : "No restrictions");
        node.Add("Text Size", offset, 1, textSize[(value >> 3) & 0x03]);
        node.Add("Image Encoding", offset, 1, (value & 0x04) != 0 ? "PNG or JPEG only" : "No restrictions");
        node.Add("Image Size", offset, 1, imageSize[value & 0x03]);
    }

    private static void AddExtendedHeaderProblem(ParseState state, long start, long end, string message)
    {
        Node node = state.Tag.Children.LastOrDefault(c => c.Name == "Extended Header") ?? state.Tag.Add("Extended Header", start, end - start);
        state.Findings.Add(new Finding(Severity.Warning, "id3v2.extended-header-invalid", message, node));
        state.HasProblems = true;
    }

    // Returns the number of padding bytes after the last frame.
    private static long ParseFrames(Id3v2BodyReader body, ParseState state, bool tagUnsync)
    {
        int major = state.Major;
        int idLength = major == 2 ? 3 : 4;
        int headerLength = major == 2 ? 6 : 10;
        long framesStart = body.FilePosition;

        while (!body.AtEnd)
        {
            long frameStart = body.FilePosition;
            DecodedBytes header = body.Read(headerLength);
            if (header.Length == 0)
            {
                break;
            }

            if (header.Data[0] == 0)
            {
                return ParsePadding(body, state, frameStart, header, framesStart);
            }

            if (header.Length < headerLength)
            {
                AddUnparsed(state, frameStart, body.FilePosition, "id3v2.frame-header-truncated", $"The {header.Length} bytes at 0x{frameStart:X} are too short for a frame header.");
                return 0;
            }

            string id = Encoding.Latin1.GetString(header.Data, 0, idLength);
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

    private static void ParseFrame(Id3v2BodyReader body, ParseState state, long frameStart, DecodedBytes header, string id, bool tagUnsync)
    {
        int major = state.Major;
        int idLength = major == 2 ? 3 : 4;
        int sizeLength = major == 2 ? 3 : 4;
        List<Finding> findings = state.Findings;

        long frameSize;
        bool plainSize = false;
        if (major == 2)
        {
            frameSize = (header.Data[3] << 16) | (header.Data[4] << 8) | header.Data[5];
        }
        else if (major == 3 || header.Data.AsSpan(4, 4).ContainsAnyInRange((byte)0x80, (byte)0xFF))
        {
            // 2.3 sizes are plain integers. A 2.4 size with a high bit set cannot be syncsafe,
            // which is what some writers (old iTunes versions) produced; read it as plain too.
            frameSize = BinaryPrimitives.ReadUInt32BigEndian(header.Data.AsSpan(4));
            plainSize = major == 4;
        }
        else
        {
            frameSize = Syncsafe(header.Data, 4);
        }

        int flags = major == 2 ? 0 : BinaryPrimitives.ReadUInt16BigEndian(header.Data.AsSpan(8));

        DecodedBytes raw = body.Read((int)Math.Min(frameSize, MaxDecodedFrameBytes));
        long readTotal = raw.Length + body.Skip(frameSize - raw.Length);
        long frameEnd = body.FilePosition;

        var frame = new Node(id, frameStart, frameEnd - frameStart);
        state.Tag.Add(frame);

        FrameExtras extras = major switch
        {
            3 => FrameExtras.For23(flags),
            4 => FrameExtras.For24(flags),
            _ => new FrameExtras(),
        };

        // The extra header data counts as part of the frame header.
        int extrasLength = extras.Fields.Sum(f => f.Length);
        long headerEnd = raw.OffsetOf(Math.Min(extrasLength, raw.Length));
        Node headerNode = frame.Add("Header", frameStart, headerEnd - frameStart);
        headerNode.Add("ID", header.OffsetOf(0), header.FileLength(0, idLength), id);
        headerNode.Add("Size", header.OffsetOf(idLength), header.FileLength(idLength, idLength + sizeLength), frameSize.ToString(CultureInfo.InvariantCulture));

        if (plainSize)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "id3v2.frame-size-not-syncsafe",
                $"Frame {id} at 0x{frameStart:X} stores its size as a plain integer instead of a syncsafe one, as old iTunes versions did.",
                frame));
            state.HasProblems = true;
        }

        if (readTotal < frameSize)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "id3v2.frame-too-large",
                $"Frame {id} at 0x{frameStart:X} declares {frameSize} bytes, but only {readTotal} remain in the tag.",
                frame));
            state.HasProblems = true;
        }

        if (major >= 3)
        {
            AddFrameFlags(headerNode, header, flags, major, id, findings, frame);
        }

        // Extra header data follows the frame header in flag order (2.3 section 3.3.1, 2.4
        // section 4.1.2) and counts toward the frame size.
        int pos = 0;
        long? dataLength = null;
        foreach ((string name, int length) in extras.Fields)
        {
            if (raw.Length < pos + length)
            {
                findings.Add(new Finding(Severity.Warning, "id3v2.frame-content-truncated", $"Frame {id} ends before its {name.ToLowerInvariant()}.", frame));
                state.HasProblems = true;
                return;
            }

            string value;
            if (length == 4)
            {
                long number = major == 4 ? Syncsafe(raw.Data, pos) : BinaryPrimitives.ReadUInt32BigEndian(raw.Data.AsSpan(pos));
                dataLength = number;
                value = number.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                value = raw.Data[pos].ToString(CultureInfo.InvariantCulture);
            }

            headerNode.Add(name, raw.OffsetOf(pos), raw.FileLength(pos, pos + length), value);
            pos += length;
        }

        if (frameSize == 0)
        {
            findings.Add(new Finding(Severity.Warning, "id3v2.frame-empty", $"Frame {id} at 0x{frameStart:X} is empty; a frame must hold at least one byte.", frame));
            state.HasProblems = true;
            return;
        }

        DecodedBytes content = raw.Slice(pos);
        long decodedSize = frameSize - pos;
        if (major == 4 && (extras.IsUnsynchronized || tagUnsync))
        {
            content = content.Resynchronize();
            decodedSize = dataLength ?? (raw.Length == frameSize ? content.Length : decodedSize);
        }

        if (extras.IsCompressed || extras.IsEncrypted)
        {
            string what = extras.IsCompressed ? "Compressed Data" : "Encrypted Data";
            long offset = raw.OffsetOf(pos);
            frame.Add(what, offset, frameEnd - offset, $"{frameEnd - offset} bytes");
            findings.Add(new Finding(
                Severity.Info,
                extras.IsCompressed ? "id3v2.frame-compressed" : "id3v2.frame-encrypted",
                $"Frame {id} is {(extras.IsCompressed ? "zlib-compressed" : "encrypted")}; its content is not decoded.",
                frame));
            return;
        }

        frame.Value = Id3v2FrameDecoder.Decode(major, id, frame, content, decodedSize, frameEnd, findings);
    }

    private static void AddFrameFlags(Node headerNode, DecodedBytes header, int flags, int major, string id, List<Finding> findings, Node frame)
    {
        long offset = header.OffsetOf(8);
        long length = header.FileLength(8, 10);
        Node node = headerNode.Add("Flags", offset, length, $"0x{flags:X4}");

        (string Name, int Bit)[] names = major == 3
            ? [("IsDiscardedOnTagAlter", 0x8000), ("IsDiscardedOnFileAlter", 0x4000), ("IsReadOnly", 0x2000), ("IsCompressed", 0x0080), ("IsEncrypted", 0x0040), ("HasGroupId", 0x0020)]
            : [("IsDiscardedOnTagAlter", 0x4000), ("IsDiscardedOnFileAlter", 0x2000), ("IsReadOnly", 0x1000), ("HasGroupId", 0x0040), ("IsCompressed", 0x0008), ("IsEncrypted", 0x0004), ("IsUnsynchronized", 0x0002), ("HasDataLengthIndicator", 0x0001)];

        int defined = 0;
        foreach ((string name, int bit) in names)
        {
            node.Add(name, offset, length, Bool(flags & bit));
            defined |= bit;
        }

        if ((flags & ~defined) != 0)
        {
            findings.Add(new Finding(Severity.Info, "id3v2.frame-flags-unknown", $"Frame {id} sets undefined flag bits 0x{flags & ~defined:X4}.", frame));
        }
    }

    private static long ParsePadding(Id3v2BodyReader body, ParseState state, long start, DecodedBytes first, long framesStart)
    {
        bool nonZero = first.Data.Any(b => b != 0);
        while (!body.AtEnd)
        {
            DecodedBytes chunk = body.Read(1 << 16);
            if (chunk.Length == 0)
            {
                break;
            }

            nonZero |= chunk.Data.Any(b => b != 0);
        }

        long length = body.FilePosition - start;
        Node padding = state.Tag.Add("Padding", start, length, $"{length} bytes");
        if (nonZero)
        {
            state.Findings.Add(new Finding(Severity.Warning, "id3v2.padding-not-zero", "Padding of the ID3v2 tag contains non-zero bytes.", padding));
            state.HasProblems = true;
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

    private static void AddUnparsed(ParseState state, long start, long end, string code, string message)
    {
        Node node = state.Tag.Add("Unparsed", start, end - start, $"{end - start} bytes");
        state.Findings.Add(new Finding(Severity.Warning, code, message, node));
        state.HasProblems = true;
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
            state.HasProblems = true;
        }
    }

    private static long Syncsafe(byte[] data, int offset) =>
        ((long)(data[offset] & 0x7F) << 21) | ((long)(data[offset + 1] & 0x7F) << 14) | ((long)(data[offset + 2] & 0x7F) << 7) | (long)(data[offset + 3] & 0x7F);

    private static string Bool(int value) => value != 0 ? "true" : "false";

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

        public bool HasProblems { get; set; }
    }

    private readonly struct FrameExtras
    {
        public FrameExtras()
        {
            Fields = [];
        }

        public List<(string Name, int Length)> Fields { get; init; }

        public bool IsCompressed { get; init; }

        public bool IsEncrypted { get; init; }

        public bool IsUnsynchronized { get; init; }

        // 2.3 section 3.3.1: decompressed size, encryption method, group ID.
        public static FrameExtras For23(int flags)
        {
            var fields = new List<(string, int)>();
            if ((flags & 0x0080) != 0)
            {
                fields.Add(("Decompressed Size", 4));
            }

            if ((flags & 0x0040) != 0)
            {
                fields.Add(("Encryption Method", 1));
            }

            if ((flags & 0x0020) != 0)
            {
                fields.Add(("Group ID", 1));
            }

            return new FrameExtras { Fields = fields, IsCompressed = (flags & 0x0080) != 0, IsEncrypted = (flags & 0x0040) != 0 };
        }

        // 2.4 section 4.1.2: group ID, encryption method, data length indicator.
        public static FrameExtras For24(int flags)
        {
            var fields = new List<(string, int)>();
            if ((flags & 0x0040) != 0)
            {
                fields.Add(("Group ID", 1));
            }

            if ((flags & 0x0004) != 0)
            {
                fields.Add(("Encryption Method", 1));
            }

            if ((flags & 0x0001) != 0)
            {
                fields.Add(("Data Length", 4));
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
