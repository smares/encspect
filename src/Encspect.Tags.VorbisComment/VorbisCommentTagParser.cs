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
            throw new InvalidDataException($"A Vorbis comment of {reader.Length} bytes is too large to read.");
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
        private readonly List<Finding> _findings = [];
        private readonly List<Evidence> _evidence = [];
        private readonly List<EncoderClaim> _claims = [];
        private readonly List<Node> _children = [];
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

            Node tag = Node.FromMapped("Vorbis Comment", data, 0, position, NodeValue.Integer(parsed, "comments", "comment"), NodeKind.Tag, Explain.Tag, Topics.VorbisComments);
            foreach (Node child in _children)
            {
                tag.Add(child);
            }

            if (_confidence == Confidence.High)
            {
                ByteRange first = data.FileRanges(0, position)[0];
                _evidence.Add(new Evidence($"The vendor string and {parsed} comments fit the data", first.Offset, first.Length));
            }

            // A finding without a node of its own points at the tag.
            List<Finding> findings = _findings.Select(f => f.Node is null ? f with { Node = tag } : f).ToList();
            return TagProbe.Accept(tag, data.FileStart, _confidence, _evidence, findings, encoderClaims: _claims);
        }

        private uint ReadComments(ReadOnlySpan<byte> bytes, ref int position, uint count)
        {
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
                    Node cut = Comment(bytes, start, position, bytes.Length);
                    Problem(cut, "vorbis-comment.length-overrun", $"Comment {index + 1} of {count} declares {length} bytes, but only {bytes.Length - position} remain; the comments end there.", Confidence.Low);
                    position = bytes.Length;
                    return index + 1;
                }

                Comment(bytes, start, position, position + (int)length);
                position += (int)length;
            }

            return count;
        }

        // A comment "NAME=value": its node is named after the field, as APE items are, and holds the
        // value.
        private Node Comment(ReadOnlySpan<byte> bytes, int start, int textStart, int end)
        {
            ReadOnlySpan<byte> text = bytes[textStart..end];
            int separator = text.IndexOf((byte)'=');
            string name = Decode(separator < 0 ? text : text[..separator], out _);
            string value = separator < 0 ? string.Empty : Decode(text[(separator + 1)..], out _);
            bool valid = Utf8.IsValid(text);
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

            _children.Add(comment);

            if (!valid)
            {
                Problem(comment, "vorbis-comment.not-utf8", "A comment is not valid UTF-8, which the Vorbis comment requires; the invalid bytes are shown as U+FFFD.", Confidence.Medium);
            }

            // Section 5.2.2: "A case-insensitive field name that may consist of ASCII 0x20 through
            // 0x7D, 0x3D ('=') excluded."
            if (separator < 0)
            {
                Problem(comment, "vorbis-comment.no-separator", $"The comment \"{Shorten(name)}\" has no '=' between a field name and a value; readers skip it.", Confidence.Medium);
            }
            else if (!isFieldName)
            {
                Problem(comment, "vorbis-comment.invalid-field-name", $"The field name \"{Shorten(name)}\" is {(separator == 0 ? "empty" : "not made of ASCII 0x20 to 0x7D")}, which the Vorbis comment requires.", Confidence.Medium);
            }
            else if (name.Equals("ENCODER", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                // The field names of section 5.2.2 of the Vorbis I specification ("TITLE", "VERSION", "ALBUM" and so on) include no
                // ENCODER, so that specification gives it to no role and the claim is the writer's (RFC 7845 section 5.2 has Opus
                // applications use it "to identify themselves"). ffmpeg writes the stream's "encoder" metadata into it
                // (libavformat/vorbiscomment.c ff_vorbiscomment_write, in lower case "encoder"), which is its libavcodec encoder ID when
                // it encodes (ffmpeg_enc.c set_encoder_id) and names the encoder; oggenc.c:281 writes the muxer's version as the vendor string.
                _claims.Add(new EncoderClaim(value, comment, "Vorbis comment ENCODER field", ClaimRoles.Of(ClaimRole.Writer, value)));
            }

            return comment;
        }

        private uint? ReadLength(ReadOnlySpan<byte> bytes, ref int position, string? name, string what)
        {
            if (bytes.Length - position < 4)
            {
                if (bytes.Length > position)
                {
                    _children.Add(Node.FromMapped("Unparsed", data, position, bytes.Length, NodeValue.Integer(bytes.Length - position, "bytes"), description: Explain.Unparsed));
                }

                Problem(null, "vorbis-comment.truncated", $"The data ends {bytes.Length - position} bytes into the 4-byte length of {what}.", Confidence.Low);
                position = bytes.Length;
                return null;
            }

            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]);
            if (name is not null)
            {
                bool isCount = name == "Comment Count";
                _children.Add(Node.FromMapped(name, data, position, position + 4, isCount ? NodeValue.Integer(value) : NodeValue.Integer(value, "bytes"),
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
            _children.Add(node);
            if (!Utf8.IsValid(bytes[position..end]))
            {
                Problem(node, "vorbis-comment.not-utf8", $"The text of {what} is not valid UTF-8, which the Vorbis comment requires; the invalid bytes are shown as U+FFFD.", Confidence.Medium);
            }

            bool fits = end - position == length;
            position = end;
            if (!fits)
            {
                Problem(node, "vorbis-comment.length-overrun", $"The length of {what} is {length} bytes, but only {node.Length} remain; the comment ends there.", Confidence.Low);
                return null;
            }

            return node;
        }

        private void Problem(Node? node, string code, string message, Confidence ceiling)
        {
            _findings.Add(new Finding(Severity.Warning, code, message, node));
            _confidence = ceiling < _confidence ? ceiling : _confidence;
        }

        private static string Decode(ReadOnlySpan<byte> bytes, out bool valid)
        {
            valid = Utf8.IsValid(bytes);
            return Encoding.UTF8.GetString(bytes.Length > MaxDecodedValueBytes ? bytes[..MaxDecodedValueBytes] : bytes);
        }

        private static string Shorten(string text) => text.Length > 40 ? text[..40] + "…" : text;
    }
}
