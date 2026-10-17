using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;
using Encspect.Core;

namespace Encspect.Tags.VorbisComment;

// The Vorbis comment (Vorbis I specification, section 5, "comment field and header
// specification"): a vendor string and a list of "NAME=value" comments, each preceded by its
// length as a 32-bit little-endian integer, in UTF-8. Vorbis, Opus, Speex and FLAC all carry it,
// each in its own packet or block, so it is a tag parser that containers and codecs reach through
// AnalysisContext.ParseTag. It parses only the comment itself: what a codec puts around it (Vorbis'
// "\x03vorbis" prefix and framing bit, Opus' "OpusTags", or nothing in Speex and FLAC) is the
// codec's to check, and the tag ends after its last comment, so the codec sees what follows.
//
// It has no signature, so it is never looked for in a file, only parsed where a container or codec
// says one is. The data it is given is the comment's whole extent, so a length that runs past it is
// reported and the tag still shown, never rejected.
public sealed class VorbisCommentTagParser : ITagParser
{
    public const string TagName = "Vorbis Comment";

    // Values longer than this are shown cut short, as APE values are.
    private const int MaxDecodedValueBytes = 1 << 20;

    public string Name => TagName;

    // A comment held whole in the file, such as a FLAC VORBIS_COMMENT block.
    public TagProbe? ParseWhole(TagProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ByteReader reader = context.Reader;
        if (reader.Length > Array.MaxLength)
        {
            throw new InvalidDataException($"A Vorbis comment of {Counts.Of(reader.Length, "byte", "bytes")} is too large to read.");
        }

        byte[] data = reader.ReadBytesAt(reader.Start, (int)reader.Length);
        return ParseWhole(new MappedBytes(data, reader.Start), context.CancellationToken);
    }

    public TagProbe? ParseWhole(MappedBytes tag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tag);

        if (tag.Length == 0)
        {
            return TagProbe.Reject(tag.FileStart, "the data for the comment is empty");
        }

        return new Builder(tag, cancellationToken).Build();
    }

    private sealed class Builder(MappedBytes data, CancellationToken cancellationToken)
    {
        // A tag with more comments than this builds the nodes of its comments when they are read, in blocks, from a record of
        // 12 bytes each (the data stays alive with the report); a smaller one builds them at once.
        private const int EagerComments = 1024;

        // The most ENCODER fields that become claims: a tag repeating the field has nothing more to say.
        private const int MaxEncoderClaims = 16;

        // Findings of one kind are reported once per tag with a count, pointing at the first case; the case's node is built only then.
        private readonly Dictionary<string, (Severity Severity, Func<Node?> Node, int Count, Func<int, string> Message)> _aggregated = [];
        private readonly List<string> _order = [];
        private readonly List<Evidence> _evidence = [];
        private readonly List<EncoderClaim> _claims = [];
        private readonly List<Node> _leading = [];

        // The comments after the leading nodes (vendor and counts), and a trailing cut length.
        private readonly ChunkedList<CommentRecord> _records = new();
        private bool _inComments;
        private int _encoderFields;
        private Confidence _confidence = Confidence.High;

        public TagProbe Build()
        {
            ReadOnlySpan<byte> bytes = data.Span;
            int position = 0;
            uint parsed = 0;

            // 1) [vendor_length], 2) [vendor_string].
            if (ReadLength(bytes, ref position, "Vendor Length", "the vendor string's length") is { } vendorLength)
            {
                Node? vendor = ReadText(bytes, ref position, vendorLength, "Vendor", "the vendor string", out string? text);
                if (vendor is not null && text is { Length: > 0 })
                {
                    // The vendor string names the encoder (libvorbis writes its own), except that ffmpeg's Ogg muxer writes its version as
                    // the vendor, "ffmpeg" when bit-exact (libavformat/oggenc.c:281), which names the writer.
                    _claims.Add(new EncoderClaim(text, vendor, "Vorbis comment vendor string", text == "ffmpeg" ? ClaimRole.Writer : ClaimRoles.Of(ClaimRole.Encoder, text)));
                }

                // 3) [user_comment_list_length], 4) to 6): the comments.
                if (vendor is not null && ReadLength(bytes, ref position, "Comment Count", "the number of comments") is { } count)
                {
                    parsed = ReadComments(bytes, ref position, count);
                }
            }

            Node outline = Node.FromMapped("Vorbis Comment", data, 0, position, NodeValue.Integer(parsed, "comments", "comment"), NodeKind.Tag, Explain.Tag, Topics.VorbisComments);
            Node tag;
            if (_records.Count <= EagerComments)
            {
                tag = outline;
                foreach (Node child in _leading)
                {
                    tag.Add(child);
                }

                for (int i = 0; i < _records.Count; i++)
                {
                    tag.Add(RecordNode(_records[i]));
                }
            }
            else
            {
                // Held by the report for as long as the tag's nodes can be read again.
                MappedBytes kept = data;
                ChunkedList<CommentRecord> records = _records;
                Node[] leading = [.. _leading];
                tag = new Node(outline.Name, outline.Offset, outline.Length, outline.Value, () => new BlockedChildren(leading, records.Count, i => BuildRecordNode(kept, records[i])))
                {
                    Kind = outline.Kind,
                    Description = outline.Description,
                    Topic = outline.Topic,
                    Ranges = outline.Ranges,
                };
            }

            if (_confidence == Confidence.High)
            {
                ByteRange first = data.FileRanges(0, position)[0];
                _evidence.Add(new Evidence($"The vendor string and {parsed} {(parsed == 1 ? "comment" : "comments")} fit the data", first.Offset, first.Length));
            }

            // A finding without a node of its own points at the tag.
            var findings = new List<Finding>();
            foreach (string code in _order)
            {
                var entry = _aggregated[code];
                findings.Add(new Finding(entry.Severity, code, entry.Message(entry.Count), entry.Node() ?? tag) { Count = entry.Count });
            }

            return TagProbe.Accept(tag, data.FileStart, _confidence, _evidence, findings, encoderClaims: _claims);
        }

        private Node RecordNode(CommentRecord record) => BuildRecordNode(data, record);

        private uint ReadComments(ReadOnlySpan<byte> bytes, ref int position, uint count)
        {
            _inComments = true;
            for (uint index = 0; index < count; index++)
            {
                if (index % 4096 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int start = position;
                if (ReadLength(bytes, ref position, null, $"comment {index + 1} of {count}") is not { } length)
                {
                    return index;
                }

                if (length > bytes.Length - position)
                {
                    var cut = new CommentRecord(start, position, bytes.Length);
                    uint number = index + 1;
                    int remaining = bytes.Length - position;
                    Problem(() => RecordNode(cut), "vorbis-comment.length-overrun",
                        _ => $"Comment {number} of {count} declares {Count(length, "byte", "bytes")}, but only {remaining} remain; the comments end there.", Confidence.Low);
                    _records.Add(cut);
                    position = bytes.Length;
                    return index + 1;
                }

                Comment(bytes, start, position, position + (int)length);
                position += (int)length;
            }

            return count;
        }

        // A comment "NAME=value": its node is named after the field, as APE items are, and holds the value (BuildRecordNode). Only
        // what the findings and claims need is worked out here, without allocating.
        private void Comment(ReadOnlySpan<byte> bytes, int start, int textStart, int end)
        {
            ReadOnlySpan<byte> text = bytes[textStart..end];
            int separator = text.IndexOf((byte)'=');
            var record = new CommentRecord(start, textStart, end);
            _records.Add(record);
            bool isFieldName = separator > 0 && !text[..separator].ContainsAnyExceptInRange((byte)0x20, (byte)0x7D);

            if (!Utf8.IsValid(text))
            {
                Problem(() => RecordNode(record), "vorbis-comment.not-utf8",
                    count => $"{(count == 1 ? "A comment is" : $"{count} comments are")} not valid UTF-8, which the Vorbis comment requires; the invalid bytes are shown as U+FFFD.", Confidence.Medium);
            }

            // Section 5.2.2: "A case-insensitive field name that may consist of ASCII 0x20 through
            // 0x7D, 0x3D ('=') excluded."
            if (separator < 0)
            {
                Problem(() => RecordNode(record), "vorbis-comment.no-separator",
                    count => $"{(count == 1 ? $"The comment \"{Shorten(Decode(TextOf(record)))}\" has" : $"{count} comments have")} no '=' between a field name and a value; readers skip {(count == 1 ? "it" : "them")}.", Confidence.Medium);
            }
            else if (!isFieldName)
            {
                Problem(() => RecordNode(record), "vorbis-comment.invalid-field-name",
                    count => count == 1
                        ? $"The field name \"{Shorten(Decode(TextOf(record)[..separator]))}\" is {(separator == 0 ? "empty" : "not made of ASCII 0x20 to 0x7D")}, which the Vorbis comment requires."
                        : $"{count} field names are empty or not made of ASCII 0x20 to 0x7D, which the Vorbis comment requires; the first is \"{Shorten(Decode(TextOf(record)[..separator]))}\".", Confidence.Medium);
            }
            else if (Ascii.EqualsIgnoreCase(text[..separator], "ENCODER"u8) && separator + 1 < text.Length && _encoderFields++ < MaxEncoderClaims)
            {
                // The field names of section 5.2.2 of the Vorbis I specification ("TITLE", "VERSION", "ALBUM" and so on) include no
                // ENCODER, so that specification gives it to no role and the claim is the writer's (RFC 7845 section 5.2 has Opus
                // applications use it "to identify themselves"). ffmpeg writes the stream's "encoder" metadata into it
                // (libavformat/vorbiscomment.c ff_vorbiscomment_write, in lower case "encoder"), which is its libavcodec encoder ID when
                // it encodes (ffmpeg_enc.c set_encoder_id) and names the encoder; oggenc.c:281 writes the muxer's version as the vendor string.
                string value = Decode(text[(separator + 1)..], out _);
                _claims.Add(new EncoderClaim(value, RecordNode(record), "Vorbis comment ENCODER field", ClaimRoles.Of(ClaimRole.Writer, value)));
            }

            ReadOnlySpan<byte> TextOf(CommentRecord r) => data.Span[r.TextStart..r.End];
        }

        // The node of a comment: Length and then Text for a comment without '=', else Field Name and Value.
        private static Node BuildRecordNode(MappedBytes data, CommentRecord record)
        {
            if (record.TextStart < 0)
            {
                return Node.FromMapped("Unparsed", data, record.Start, record.End, NodeValue.Integer(record.End - record.Start, "bytes"), description: Explain.Unparsed);
            }

            (int start, int textStart, int end) = (record.Start, record.TextStart, record.End);
            ReadOnlySpan<byte> text = data.Span[textStart..end];
            int separator = text.IndexOf((byte)'=');
            string name = Decode(separator < 0 ? text : text[..separator], out _);
            string value = separator < 0 ? string.Empty : Decode(text[(separator + 1)..], out _);
            bool isFieldName = separator > 0 && !text[..separator].ContainsAnyExceptInRange((byte)0x20, (byte)0x7D);

            // A field name that breaks the rules, or a very long one, does not name the node.
            string description = separator < 0 ? Explain.NoSeparator : isFieldName ? Explain.Field(name) : Explain.InvalidName;
            string? topic = isFieldName ? Explain.FieldTopic(name) : null;
            Node comment = Node.FromMapped(isFieldName && separator <= 64 ? name : "Comment", data, start, end, separator < 0 ? name : value, description: description, topic: topic);
            comment.Add(Node.FromMapped("Length", data, start, textStart, NodeValue.Integer(end - textStart, "bytes"), description: Explain.Length));
            if (separator < 0)
            {
                comment.Add(Node.FromMapped("Text", data, textStart, end, name, description: Explain.Text));
            }
            else
            {
                comment.Add(Node.FromMapped("Field Name", data, textStart, textStart + separator, name, description: Explain.FieldName));
                comment.Add(Node.FromMapped("Value", data, textStart + separator + 1, end, value, description: Explain.Value));
            }

            return comment;
        }

        private uint? ReadLength(ReadOnlySpan<byte> bytes, ref int position, string? name, string what)
        {
            if (bytes.Length - position < 4)
            {
                if (bytes.Length > position)
                {
                    if (_inComments)
                    {
                        _records.Add(new CommentRecord(position, -1, bytes.Length));
                    }
                    else
                    {
                        _leading.Add(Node.FromMapped("Unparsed", data, position, bytes.Length, NodeValue.Integer(bytes.Length - position, "bytes"), description: Explain.Unparsed));
                    }
                }

                int left = bytes.Length - position;
                Problem(null, "vorbis-comment.truncated", _ => $"The data ends {Count(left, "byte", "bytes")} into the 4-byte length of {what}.", Confidence.Low);
                position = bytes.Length;
                return null;
            }

            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]);
            if (name is not null)
            {
                bool isCount = name == "Comment Count";
                _leading.Add(Node.FromMapped(name, data, position, position + 4, isCount ? NodeValue.Integer(value) : NodeValue.Integer(value, "bytes"),
                    description: isCount ? Explain.CommentCount : Explain.VendorLength));
            }

            position += 4;
            return value;
        }

        private Node? ReadText(ReadOnlySpan<byte> bytes, ref int position, uint length, string name, string what, out string? text)
        {
            int end = (int)Math.Min(bytes.Length, position + (long)length);
            text = Decode(bytes[position..end], out _);
            Node node = Node.FromMapped(name, data, position, end, text, description: Explain.Vendor);
            _leading.Add(node);
            if (!Utf8.IsValid(bytes[position..end]))
            {
                Problem(() => node, "vorbis-comment.not-utf8",
                    count => $"{(count == 1 ? $"The text of {what} is" : $"The text of {what} and {Count(count - 1, "comment", "comments")} are")} not valid UTF-8, which the Vorbis comment requires; the invalid bytes are shown as U+FFFD.", Confidence.Medium);
            }

            bool fits = end - position == length;
            position = end;
            if (!fits)
            {
                Problem(() => node, "vorbis-comment.length-overrun", _ => $"The length of {what} is {Count(length, "byte", "bytes")}, but only {node.Length} remain; the comment ends there.", Confidence.Low);
                return null;
            }

            return node;
        }

        // The case's node is built for the first case only, when the findings are reported; null points at the tag.
        private void Problem(Func<Node?>? node, string code, Func<int, string> message, Confidence ceiling)
        {
            _confidence = ceiling < _confidence ? ceiling : _confidence;
            if (_aggregated.TryGetValue(code, out var existing))
            {
                _aggregated[code] = existing with { Count = existing.Count + 1 };
                return;
            }

            _aggregated[code] = (Severity.Warning, node ?? (() => null), 1, message);
            _order.Add(code);
        }

        private static string Decode(ReadOnlySpan<byte> bytes) => Decode(bytes, out _);

        private static string Decode(ReadOnlySpan<byte> bytes, out bool valid)
        {
            valid = Utf8.IsValid(bytes);
            return Encoding.UTF8.GetString(bytes.Length > MaxDecodedValueBytes ? bytes[..MaxDecodedValueBytes] : bytes);
        }

        private static string Count(long count, string singular, string plural) => count == 1 ? $"1 {singular}" : $"{count} {plural}";

        private static string Shorten(string text) => text.Length > 40 ? text[..40] + "…" : text;
    }

    // Where a comment lies in the data: its start (the length field), the start of its text and its end. A trailing cut length field
    // is a record with a TextStart of -1.
    private readonly record struct CommentRecord(int Start, int TextStart, int End);
}
