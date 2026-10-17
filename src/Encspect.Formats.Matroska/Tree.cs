using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Matroska;

internal sealed partial class FileReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Reads the element whose header is given: its node, with its value or, for a Master Element, its children. The element ends where
    // it says or, if that is past its parent, at the end of the parent (a finding says so). next is where the parent's next child
    // starts. depth counts the levels below the file's node.
    private Node ParseElement(ElementHeader header, ElementDefinition? parentDefinition, long parentEnd, int depth, out long next)
    {
        ElementDefinition? definition = Schema.Find(header.Id);
        long declaredEnd = header.End ?? parentEnd;
        long end = Math.Min(declaredEnd, parentEnd);
        bool isComplete = declaredEnd <= parentEnd;
        string name = definition?.Name ?? "Unknown Element";
        _nodes++;
        var node = new Node(name, header.Offset, end - header.Offset)
        {
            Description = definition?.Description ?? Explain.UnknownElement,
            Topic = definition is null ? null : TopicOf(definition),

            // The Segment's Tags element is the file's own tags (RFC 9559, section 5.1.8), a tag of the container as MP4's item list is.
            Kind = definition?.Id == Schema.Tags && parentDefinition?.Id == Schema.Segment ? NodeKind.Tag : NodeKind.Detail,
        };
        next = end;

        if (header.IsUnknownSize)
        {
            if (definition is not { IsUnknownSizeAllowed: true })
            {
                _findings.Add(Severity.Warning, "matroska.unknown-size-not-allowed", node, count =>
                    $"{(count == 1 ? "An element" : $"{count} elements")} of unknown size where the schema allows it only for the Segment and the Cluster (RFC 8794, section 6.2); the first, {name} at 0x{header.Offset:X}, is taken to run to the end of its parent.");
            }
        }
        else if (declaredEnd > parentEnd)
        {
            long over = declaredEnd - parentEnd;
            if (declaredEnd > _reader.End)
            {
                _findings.Add(Severity.Warning, "matroska.element-truncated", node, count =>
                    $"{(count == 1 ? "An element states" : $"{count} elements state")} a size that runs past the end of the file, which is cut short: the {name} at 0x{header.Offset:X} states {Bytes((long)header.Size!.Value)}, {Bytes(declaredEnd - _reader.End)} more than the file holds.");
            }
            else
            {
                _findings.Add(Severity.Warning, "matroska.element-overruns-parent", node, count =>
                    $"{(count == 1 ? "An element states" : $"{count} elements state")} a size that runs past the end of {(count == 1 ? "its" : "their")} parent (RFC 8794, section 7.7); the {name} at 0x{header.Offset:X} states {Bytes((long)header.Size!.Value)}, {Bytes(over)} more than its parent has left, and is read only up to the parent's end.");
            }
        }

        if (header.IsLongId)
        {
            _findings.Add(Severity.Info, "matroska.element-id-long", node, count =>
                $"{(count == 1 ? "An Element ID is" : $"{count} Element IDs are")} not coded at their shortest length, which RFC 8794 requires (section 5); the first is the {name}'s at 0x{header.Offset:X}.");
        }

        if (header.IsLongSize)
        {
            _findings.Add(Severity.Warning, "matroska.size-too-long", node, count =>
                $"{(count == 1 ? "An Element Data Size is" : $"{count} Element Data Sizes are")} coded in more octets than the EBML Header's EBMLMaxSizeLength allows, which makes the element invalid (RFC 8794, section 11.2.5); the first is the {name}'s at 0x{header.Offset:X}.");
        }

        if (definition is null)
        {
            node.Value = $"ID {Ebml.IdText(header.Id)}, {Bytes(end - header.DataOffset)}";
            _findings.Add(Severity.Info, "matroska.unknown-element", node, count =>
                $"{(count == 1 ? "An element is" : $"{count} elements are")} none the Matroska schema lists, which readers skip by their size (RFC 9559, section 7; RFC 8794, section 16); the first has the ID {Ebml.IdText(header.Id)} and lies at 0x{header.Offset:X}, {ParentText(parentDefinition)}.");
            return node;
        }

        if (definition.IsDeprecated)
        {
            _findings.Add(Severity.Info, "matroska.deprecated-element", node, count =>
                $"{(count == 1 ? "An element is one" : $"{count} elements are ones")} that RFC 9559 lists as no longer used (appendix A); the first is the {name} at 0x{header.Offset:X}.");
        }

        if (parentDefinition is not null && !Schema.IsChildOf(definition, parentDefinition))
        {
            _findings.Add(Severity.Warning, "matroska.misplaced-element", node, count => MisplacedMessage(count, definition, header.Offset, parentDefinition));
        }

        // The values of an element the schema does not place where it stands, or under one, are shown and reported but not used: a reader that
        // follows the schema skips them (mkvinfo: "Known element, but invalid at this position"; FFmpeg's syntax tables list the children of each master).
        bool isPlaced = Schema.IsChildOf(definition, parentDefinition) && _displaced == 0;
        if (definition.Type == ElementType.Master)
        {
            if (isPlaced)
            {
                Begin(definition, node);
            }
            else
            {
                _displaced++;
            }

            if (depth >= MaxElementDepth)
            {
                _findings.Add(Severity.Warning, "matroska.too-deep", node, count =>
                    $"{(count == 1 ? "An element nests" : $"{count} elements nest")} more than {MaxElementDepth} levels deep, which no Matroska file does; the first is the {name} at 0x{header.Offset:X}, whose contents are not read.");
            }
            else
            {
                ParseChildren(node, header.DataOffset, end, definition, depth + 1, isComplete);
            }

            if (isPlaced)
            {
                End(definition, node);
            }
            else
            {
                _displaced--;
            }
        }
        else
        {
            ReadLeaf(header, definition, end, node, isPlaced);
        }

        return node;
    }

    private static string MisplacedMessage(int count, ElementDefinition definition, long offset, ElementDefinition? parent) =>
        $"{(count == 1 ? "An element is" : $"{count} elements are")} placed where the schema does not allow them (RFC 9559, section 5); the first is the {definition.Name} at 0x{offset:X}, {ParentText(parent)}, where it belongs in {PlaceOf(definition)}.";

    private static string ParentText(ElementDefinition? parent) => parent is null ? "at the root of the document" : $"in the {parent.Name}";

    private static string PlaceOf(ElementDefinition definition) =>
        definition.IsGlobal ? "any Master Element" : definition.ParentPath.Length == 0 ? "the root of the document" : $"the {definition.ParentPath[(definition.ParentPath.LastIndexOf('\\') + 1)..]}";

    private static string? TopicOf(ElementDefinition definition) => definition.Id switch
    {
        Schema.Ebml => Topics.Ebml,
        Schema.Segment or Schema.Cluster or Schema.SeekHead or Schema.Cues or Schema.Chapters or Schema.Attachments or Schema.Tags => Topics.Container,
        Schema.Tracks or 0x6D80 => Topics.Tracks,
        0x5035 or 0x47E1 or 0x47E2 or 0x47E7 or 0x47E8 or 0x5032 or 0x5031 or 0x5033 => Topics.ContentEncryption,
        0x2AD7B1 or Schema.Timestamp => Topics.Timestamps,
        Schema.Crc32 => Topics.Crc,
        Schema.SimpleBlock or Schema.Block => Topics.Lacing,
        _ => null,
    };

    // The children of a Master Element in [start, end). A CRC-32 element must be the first (RFC 8794 section 11.3.1), and covers the data
    // after it. isComplete: the element is not cut off by the end of the file, so its CRC can be checked.
    private void ParseChildren(Node node, long start, long end, ElementDefinition definition, int depth, bool isComplete)
    {
        long position = start;
        while (position < end)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            if (_nodes >= MaxNodes)
            {
                if (_segment is not null)
                {
                    _segment.HasSkippedElements = true;
                }

                Node rest = node.Add(Unparsed(position, end, "the element's data"));
                ReportBudget(position, () => rest);
                return;
            }

            // The EBML Header's own elements are read with the limits EBML defaults to; EBMLMaxIDLength and EBMLMaxSizeLength limit the Body (RFC 8794, section 11.2.4).
            bool isHeader = definition.Path.StartsWith("\\EBML", StringComparison.Ordinal);
            HeaderProblem problem = Ebml.TryReadHeader(_reader, position, end, isHeader ? Math.Max(4, _document.MaxIdLength) : _document.ReadIdLength,
                isHeader ? Math.Max(8, _document.MaxSizeLength) : _document.ReadSizeLength, out ElementHeader header);
            if (problem != HeaderProblem.None)
            {
                node.Add(Unparsed(position, end, problem, $"in the {definition.Name}"));
                return;
            }

            Node child = ParseElement(header, definition, end, depth, out long next);
            node.Add(child);
            if (header.Id == Schema.Crc32)
            {
                if (position == start)
                {
                    if (isComplete)
                    {
                        VerifyCrc(child, header, next, end, definition.Name);
                    }
                }
                else
                {
                    _findings.Add(Severity.Warning, "matroska.crc-position", child, count =>
                        $"{(count == 1 ? "A CRC-32 element is" : $"{count} CRC-32 elements are")} not the first child of {(count == 1 ? "its" : "their")} parent, as RFC 8794 requires (section 11.3.1), so {(count == 1 ? "it is" : "they are")} not checked; the first is the CRC-32 at 0x{header.Offset:X} in the {definition.Name}.");
                }
            }

            position = next;
        }
    }

    // The CRC-32 element's value against the CRC computed over the parent's data after it (RFC 8794 section 11.3.1).
    private void VerifyCrc(Node node, ElementHeader crc, long dataStart, long parentEnd, string parentName)
    {
        // A CRC-32 element the parent's end cuts off holds fewer than its 4 bytes, wherever its own size says.
        long available = Math.Max(0, parentEnd - crc.DataOffset);
        if (crc.Size != 4 || available < 4)
        {
            long held = Math.Min((long)Math.Min(crc.Size ?? 0, long.MaxValue), available);
            _findings.Add(Severity.Warning, "matroska.crc-size", node, count =>
                $"{(count == 1 ? "A CRC-32 element holds" : $"{count} CRC-32 elements hold")} other than the 4 bytes RFC 8794 gives it (section 11.3.1); the first, the CRC-32 at 0x{crc.Offset:X} in the {parentName}, holds {Bytes(held)}, so {(count == 1 ? "it is" : "they are")} not checked.");
            return;
        }

        _reader.Position = crc.DataOffset;
        uint stored = _reader.ReadUInt32LittleEndian();
        uint computed = Crc32.Of(_reader, dataStart, parentEnd);
        node.Add(new Node("Computed", crc.DataOffset, 4, NodeValue.Hex(computed, 8)) { Description = Explain.Computed, Topic = Topics.Crc });
        if (stored != computed)
        {
            _findings.Add(Severity.Warning, "matroska.crc-mismatch", node, count =>
                $"{(count == 1 ? "A CRC-32 element does" : $"{count} CRC-32 elements do")} not match the data {(count == 1 ? "it covers" : "they cover")}: the first, the CRC-32 at 0x{crc.Offset:X} in the {parentName}, stores 0x{stored:X8} but the {Bytes(parentEnd - dataStart)} after it give 0x{computed:X8}, so the {parentName} is damaged or was edited without updating it.");
        }
    }

    private Node Unparsed(long start, long end, HeaderProblem problem, string where)
    {
        Node rest = Unparsed(start, end, where);
        string why = problem switch
        {
            HeaderProblem.Truncated => "the data ends inside its element header",
            HeaderProblem.NotAnInteger => "it begins with a byte of zero bits, so it is no variable-size integer of eight octets or fewer (RFC 8794, section 4.1)",
            _ => "its Element ID is longer than the document's EBMLMaxIDLength allows or is the reserved value 0xFF (RFC 8794, section 5)",
        };
        _findings.AddNow(Severity.Warning, "matroska.element-header-invalid",
            $"No element can be read at 0x{start:X}, {where}: {why}. The {Bytes(end - start)} from there {Counts.Agree(end - start, "is", "are")} not read.", rest);
        return rest;
    }

    private static Node Unparsed(long start, long end, string where) =>
        new("Unparsed Data", start, end - start, NodeValue.Integer(end - start, "bytes", "byte")) { Description = Explain.UnparsedData };

    // A leaf: its value, read from its data and shown by its type, and what the analysis keeps of it.
    private void ReadLeaf(ElementHeader header, ElementDefinition definition, long end, Node node, bool isPlaced)
    {
        long length = end - header.DataOffset;
        ulong u = 0;
        long s = 0;
        double d = 0;
        string? text = null;
        byte[]? bytes = null;
        bool valid = true;
        if (length == 0 && definition.DefaultValue is { } fallback)
        {
            // An empty element has its default value (RFC 8794, section 6.1).
            switch (definition.Type)
            {
                case ElementType.Unsigned or ElementType.Signed:
                    long.TryParse(fallback, NumberStyles.Integer, CultureInfo.InvariantCulture, out s);
                    u = (ulong)s;
                    break;
                case ElementType.Float:
                    double.TryParse(fallback, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                    break;
                case ElementType.String or ElementType.Utf8:
                    text = fallback;
                    break;
            }

            bytes = [];
            node.Value = Values.Of(definition, u, s, d, text, bytes, length);
            if (isPlaced)
            {
                Collect(definition, header, node, new Leaf(u, s, d, text, bytes, length));
            }

            return;
        }

        switch (definition.Type)
        {
            case ElementType.Unsigned or ElementType.Signed:
                {
                    int count = (int)Math.Min(length, 8);
                    bytes = ReadAt(header.DataOffset, count);
                    if (length > 8)
                    {
                        valid = false;
                        BadSize(node, definition, header, length, "at most 8 bytes", "RFC 8794, sections 7.1 and 7.2");
                    }

                    u = Ebml.Unsigned(bytes);
                    s = Ebml.Signed(bytes);
                    break;
                }

            case ElementType.Float:
                if (length is 0 or 4 or 8)
                {
                    bytes = ReadAt(header.DataOffset, (int)length);
                    d = length == 4 ? BinaryPrimitives.ReadSingleBigEndian(bytes) : length == 8 ? BinaryPrimitives.ReadDoubleBigEndian(bytes) : 0;
                }
                else
                {
                    valid = false;
                    BadSize(node, definition, header, length, "0, 4 or 8 bytes", "RFC 8794, section 7.3");
                }

                break;

            case ElementType.Date:
                if (length is 0 or 8)
                {
                    bytes = ReadAt(header.DataOffset, (int)length);
                    s = Ebml.Signed(bytes);
                }
                else
                {
                    valid = false;
                    BadSize(node, definition, header, length, "0 or 8 bytes", "RFC 8794, section 7.6");
                }

                break;

            case ElementType.String or ElementType.Utf8:
                text = ReadText(header, definition, length, node);
                break;

            default:
                if (SmallBinary(definition) && length <= 32)
                {
                    bytes = ReadAt(header.DataOffset, (int)length);
                }

                break;
        }

        node.Value = valid ? Values.Of(definition, u, s, d, text, bytes, length) : null;
        if (valid && isPlaced)
        {
            Collect(definition, header, node, new Leaf(u, s, d, text, bytes, length));
        }
    }

    // The binary elements whose bytes are shown: checksums, IDs and UIDs.
    private static bool SmallBinary(ElementDefinition definition) =>
        definition.Name is "CRC-32" or "SeekID" or "SegmentUUID" or "PrevUUID" or "NextUUID" or "SegmentFamily" or "ChapterSegmentUUID" or "ContentEncKeyID";

    private byte[] ReadAt(long position, int count)
    {
        _reader.Position = position;
        return _reader.ReadBytes(count);
    }

    private void BadSize(Node node, ElementDefinition definition, ElementHeader header, long length, string expected, string where) =>
        _findings.Add(Severity.Warning, "matroska.value-size", node, count =>
            $"{(count == 1 ? "An element holds" : $"{count} elements hold")} data of a length its type does not allow: the {definition.Name} at 0x{header.Offset:X} holds {Bytes(length)}, where {expected} are allowed ({where}).");

    private string ReadText(ElementHeader header, ElementDefinition definition, long length, Node node)
    {
        int count = (int)Math.Min(length, MaxText);
        byte[] bytes = ReadAt(header.DataOffset, count);
        // The value ends at the first null octet; it and any data after it are ignored (RFC 8794, section 13).
        int used = Array.IndexOf(bytes, (byte)0);
        if (used < 0)
        {
            used = bytes.Length;
        }

        bool invalid = false;
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes, 0, used);
        }
        catch (DecoderFallbackException)
        {
            invalid = true;
            text = Encoding.UTF8.GetString(bytes, 0, used);
        }

        if (definition.Type == ElementType.String && !invalid)
        {
            foreach (byte b in bytes.AsSpan(0, used))
            {
                invalid |= b is < 0x20 or > 0x7E;
            }
        }

        if (invalid)
        {
            _findings.Add(Severity.Info, "matroska.invalid-text", node, count =>
                $"{(count == 1 ? "A text element holds" : $"{count} text elements hold")} characters its type does not allow (RFC 8794, sections 7.4 and 7.5); the first is the {definition.Name} at 0x{header.Offset:X}.");
        }

        return text.Length > MaxShownText || (used == bytes.Length && length > count) ? text[..Math.Min(text.Length, MaxShownText)] + "…" : text;
    }
}
