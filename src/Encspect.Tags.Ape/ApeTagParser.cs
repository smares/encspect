using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;
using Encspect.Core;

namespace Encspect.Tags.Ape;

// APEv1 and APEv2 (APEv2 specification, mirrored at mutagen-specs.readthedocs.io). A tag at the
// end of a file is found through its 32-byte footer; APEv2 also allows a tag at the start of a
// file, found through its header. A known version and a size that fits the file establish the
// tag; a disagreeing header or footer, undefined flags or items that do not end where the size
// says are findings that lower the confidence.
public sealed class ApeTagParser : ITagParser
{
    private const int BlockSize = 32;

    // Value size, flags, a two-character key and its terminator.
    private const int MinItemSize = 11;
    private const int MaxDecodedValueBytes = 1 << 20;

    // Tag flags: bit 31 has header, bit 30 has no footer, bit 29 is header, bit 0 read-only;
    // bits 28 to 3 "are undefined and must be zero". Item flags define bits 2 to 0.
    private const uint UndefinedTagFlags = 0x1FFFFFF8;
    private const uint UndefinedItemFlags = 0xFFFFFFF8;

    private static readonly byte[] Preamble = "APETAGEX"u8.ToArray();
    private static readonly string[] ItemTypes = ["Text", "Binary", "External", "Reserved"];
    private static readonly string[] ForbiddenKeys = ["ID3", "TAG", "OggS", "MP+"];

    public string Name => "APE";

    // A tag at the start of a file "must have at least a header", which only APEv2 has. The size
    // in the header covers the items and the footer if there is one, never the header itself.
    public TagProbe? ProbeAt(TagProbeContext context, long start)
    {
        ByteReader reader = context.Reader;
        if (!reader.MatchesAt(start, Preamble))
        {
            return null;
        }

        if (reader.End - start < BlockSize)
        {
            return TagProbe.Reject(start, "the file ends inside the 32-byte header");
        }

        Block header = Block.Parse(reader.ReadBytesAt(start, BlockSize));
        if (header.Version == 1000)
        {
            return TagProbe.Reject(start, "APEv1 tags have no header, so a version 1000 block cannot start a tag");
        }

        if (header.Version != 2000)
        {
            return TagProbe.Reject(start, $"version {header.Version} is neither 1000 nor 2000");
        }

        if (!header.IsHeader)
        {
            return TagProbe.Reject(start, "the block is marked as a footer, so no tag starts here");
        }

        if (header.HasFooter && header.Size < BlockSize)
        {
            return TagProbe.Reject(start, $"the tag size {header.Size} is smaller than the 32-byte footer the header announces");
        }

        long itemsStart = start + BlockSize;
        long end = itemsStart + header.Size;
        if (end > reader.End)
        {
            return TagProbe.Reject(start, $"the header declares {header.Size} bytes after the header, but the file ends {reader.End - itemsStart} bytes into them, so the size points outside the file");
        }

        long itemsEnd = header.HasFooter ? end - BlockSize : end;

        var builder = new Builder(reader, header, "header", start);
        builder.Evidence.Add(new Evidence("\"APETAGEX\" header with version 2000", start, BlockSize));
        if (!header.HasHeader)
        {
            builder.Problem(null, "ape.header-flags", "The header does not set the has-header flag.", Confidence.Medium);
        }

        builder.Header = (start, header);
        builder.IsLeading = true;
        if (header.HasFooter)
        {
            long footerStart = itemsEnd;
            if (!reader.MatchesAt(footerStart, Preamble))
            {
                builder.MissingFooter = footerStart;
            }
            else
            {
                Block footer = Block.Parse(reader.ReadBytesAt(footerStart, BlockSize));
                builder.Footer = (footerStart, footer);
                builder.Compare(footer, "footer", footerStart, expectHeader: false);
            }
        }

        return builder.Build(start, end, itemsStart, itemsEnd);
    }

    public TagProbe? ProbeBefore(TagProbeContext context, long end)
    {
        ByteReader reader = context.Reader;
        long footerStart = end - BlockSize;
        if (!reader.MatchesAt(footerStart, Preamble))
        {
            return null;
        }

        Block footer = Block.Parse(reader.ReadBytesAt(footerStart, BlockSize));
        if (footer.Version is not (1000 or 2000))
        {
            return TagProbe.Reject(footerStart, $"version {footer.Version} in the footer is neither 1000 nor 2000");
        }

        if (footer.Size < BlockSize)
        {
            return TagProbe.Reject(footerStart, $"the tag size {footer.Size} is smaller than the 32-byte footer");
        }

        // The size covers the items and the footer, not the header.
        long itemsStart = end - footer.Size;
        if (itemsStart < reader.Start)
        {
            return TagProbe.Reject(footerStart, $"the tag size {footer.Size} exceeds the {end - reader.Start} bytes up to the end of the footer");
        }

        var builder = new Builder(reader, footer, "footer", footerStart);
        builder.Evidence.Add(new Evidence($"\"APETAGEX\" footer with version {footer.Version} and a size that fits the file", footerStart, BlockSize));
        builder.Footer = (footerStart, footer);
        long tagStart = itemsStart;

        if (footer.Version == 2000 && footer.IsHeader)
        {
            builder.Problem(null, "ape.footer-flags", "The footer has the is-header flag set.", Confidence.Medium);
        }

        if (footer.Version == 2000 && !footer.HasFooter)
        {
            builder.Problem(null, "ape.footer-flags", "The footer has the contains-no-footer flag set.", Confidence.Medium);
        }

        if (footer.Version == 2000 && footer.HasHeader)
        {
            long headerStart = itemsStart - BlockSize;
            if (headerStart < 0)
            {
                builder.Problem(null, "ape.header-missing", $"The footer announces a header, which would start {-headerStart} bytes before the start of the file.", Confidence.Medium);
            }
            else if (!reader.MatchesAt(headerStart, Preamble))
            {
                builder.Problem(null, "ape.header-missing", $"The footer announces a header, but there is no \"APETAGEX\" header at 0x{headerStart:X}.", Confidence.Medium);
            }
            else
            {
                Block header = Block.Parse(reader.ReadBytesAt(headerStart, BlockSize));
                builder.Header = (headerStart, header);
                builder.Compare(header, "header", headerStart, expectHeader: true);
                tagStart = headerStart;
            }
        }

        return builder.Build(tagStart, end, itemsStart, footerStart);
    }

    // Collects the parts of one tag, whichever block anchored it, and turns them into a probe.
    private sealed class Builder(ByteReader reader, Block anchor, string anchorName, long anchorOffset)
    {
        private readonly List<(Node? Node, string Code, string Message)> _problems = [];
        private readonly List<Finding> _findings = [];
        private readonly List<EncoderClaim> _claims = [];
        private Confidence _confidence = Confidence.High;

        public List<Evidence> Evidence { get; } = [];

        public (long Offset, Block Block)? Header { get; set; }

        public (long Offset, Block Block)? Footer { get; set; }

        public long? MissingFooter { get; set; }

        public bool IsLeading { get; set; }

        private bool IsV1 => anchor.Version == 1000;

        public void Problem(Node? node, string code, string message, Confidence ceiling)
        {
            _problems.Add((node, code, message));
            if (ceiling < _confidence)
            {
                _confidence = ceiling;
            }
        }

        public void Compare(Block other, string otherName, long otherOffset, bool expectHeader)
        {
            string? mismatch = other.Version != anchor.Version ? $"version {other.Version} vs {anchor.Version}"
                : other.Size != anchor.Size ? $"tag size {other.Size} vs {anchor.Size}"
                : other.ItemCount != anchor.ItemCount ? $"item count {other.ItemCount} vs {anchor.ItemCount}"
                : other.IsHeader != expectHeader ? $"the is-header flag is {(other.IsHeader ? "set" : "not set")}"
                // "Header and Footer are different in 1 bit in the Tags Flags to distinguish between them."
                : other.HasHeader != anchor.HasHeader ? $"the has-header flag is {(other.HasHeader ? "set" : "not set")}"
                : other.HasFooter != anchor.HasFooter ? $"the contains-no-footer flag is {(other.HasFooter ? "not set" : "set")}"
                : null;
            if (mismatch is null)
            {
                Evidence.Add(new Evidence($"{Capitalize(otherName)} agrees with the {anchorName}", otherOffset, BlockSize));
            }
            else
            {
                Problem(null, $"ape.{otherName}-mismatch", $"The {otherName} disagrees with the {anchorName}: {mismatch}.", Confidence.Medium);
            }
        }

        public TagProbe Build(long tagStart, long tagEnd, long itemsStart, long itemsEnd)
        {
            var tag = new Node("APE", tagStart, tagEnd - tagStart, IsV1 ? "1.0" : "2.0") { Kind = NodeKind.Tag, Description = Explain.Tag, Topic = Topics.ApeTag };
            if (Header is { } header)
            {
                AddBlock(tag, "Header", header.Offset, header.Block);
            }

            Node items = tag.Add("Items", itemsStart, itemsEnd - itemsStart, NodeValue.Integer(anchor.ItemCount, "items"), Explain.Items);
            string? itemProblem = ParseItems(items, itemsStart, itemsEnd, out long parsedEnd);
            if (itemProblem is null)
            {
                Evidence.Add(new Evidence($"{anchor.ItemCount} items end exactly where the size says", itemsStart, itemsEnd - itemsStart));
            }
            else
            {
                Node unparsed = tag.Add("Unparsed", parsedEnd, itemsEnd - parsedEnd, NodeValue.Integer(itemsEnd - parsedEnd, "bytes"), Explain.Unparsed);
                Problem(unparsed, "ape.items-invalid", $"{itemProblem} The remaining {itemsEnd - parsedEnd} bytes are not parsed.", Confidence.Low);
            }

            if (Footer is { } footer)
            {
                AddBlock(tag, "Footer", footer.Offset, footer.Block);
            }
            else if (MissingFooter is { } footerStart)
            {
                Node missing = tag.Add("Unparsed", footerStart, BlockSize, NodeValue.Integer(BlockSize, "bytes"), Explain.Unparsed);
                Problem(missing, "ape.footer-missing", $"The header announces a footer, but there is no \"APETAGEX\" footer at 0x{footerStart:X}.", Confidence.Medium);
            }

            // "An APE tag at the end of a file (strongly recommended) must have at least a footer,
            // an APE tag in the beginning of a file (strongly unrecommended) must have at least a
            // header."
            if (IsLeading)
            {
                _findings.Add(new Finding(Severity.Info, "ape.at-start", "The APE tag is at the beginning of the file, which the APEv2 specification calls \"strongly unrecommended\".", tag));
            }

            foreach ((Node? node, string code, string message) in _problems)
            {
                _findings.Add(new Finding(Severity.Warning, code, message, node ?? tag));
            }

            return TagProbe.Accept(tag, anchorOffset, _confidence, Evidence, _findings, null, _claims);
        }

        // Returns why the items do not end exactly at end, or null; parsedEnd is where the last
        // complete item ends.
        private string? ParseItems(Node items, long start, long end, out long parsedEnd)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long pos = start;
            parsedEnd = start;
            long previousSize = 0;
            bool isUnsorted = false;

            for (uint index = 1; index <= anchor.ItemCount; index++)
            {
                if (end - pos < MinItemSize)
                {
                    return $"Item {index} of {anchor.ItemCount} starts {end - pos} bytes before the end of the items, too few for an item.";
                }

                byte[] fixedPart = reader.ReadBytesAt(pos, 8);
                uint valueSize = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(4));

                int keyWindow = (int)Math.Min(256, end - pos - 8);
                byte[] keyBytes = reader.ReadBytesAt(pos + 8, keyWindow);
                int keyLength = Array.IndexOf(keyBytes, (byte)0);
                if (keyLength < 0)
                {
                    return $"Item {index} has no key terminator.";
                }

                // Keys are 2 to 255 characters from 0x20 to 0x7E.
                if (keyLength is < 2 or > 255 || keyBytes.Take(keyLength).Any(b => b is < 0x20 or > 0x7E))
                {
                    return $"Item {index} has an invalid key.";
                }

                string key = Encoding.ASCII.GetString(keyBytes, 0, keyLength);
                long valueStart = pos + 8 + keyLength + 1;
                if (valueSize > end - valueStart)
                {
                    return $"The {valueSize}-byte value of item \"{key}\" runs past the end of the items.";
                }

                long itemEnd = valueStart + valueSize;
                Node item = items.Add(key, pos, itemEnd - pos, null, Explain.Item(key), Explain.ItemTopic(key));
                item.Add("Size", pos, 4, NodeValue.Integer(valueSize), Explain.ItemSize);

                // APEv1 defines no item flags: every value is text.
                int type = IsV1 ? 0 : (int)((flags >> 1) & 0x03);
                Node flagsNode = item.Add("Flags", pos + 4, 4, NodeValue.Hex(flags, 8), Explain.ItemFlags);
                flagsNode.Add("IsReadOnly", pos + 4, 4, NodeValue.Boolean(!IsV1 && (flags & 1) != 0), Explain.ItemReadOnly);
                flagsNode.Add("Item Type", pos + 4, 4, ItemTypes[type], Explain.ItemType);
                item.Add("Key", pos + 8, keyLength, key, Explain.Key);

                item.Value = IsV1 ? AddV1Value(item, valueStart, valueSize) : AddValue(item, key, valueStart, valueSize, type);

                // Keys are compared without regard to case, so the item of a writer that spells it
                // "encoder" (FFmpeg's) is read as "Encoder" is. No source at hand lists an APEv2 key for the
                // encoder (the APEv2 key list could not be checked), so it is given to no role and the claim is the writer's.
                if (type == 0 && key.Equals("Encoder", StringComparison.OrdinalIgnoreCase) && item.Value?.AsText() is { Length: > 0 } encoder && !string.IsNullOrWhiteSpace(encoder))
                {
                    _claims.Add(new EncoderClaim(encoder, item, "APE Encoder item", ClaimRoles.Of(ClaimRole.Writer, encoder)));
                }

                if (IsV1 && flags != 0)
                {
                    _findings.Add(new Finding(Severity.Warning, "ape.v1-flags", $"Item \"{key}\" sets flags 0x{flags:X8}; APEv1 writers must leave them zero, so readers that apply the APEv2 flags misread the item.", flagsNode));
                }
                else if (!IsV1 && (flags & UndefinedItemFlags) != 0)
                {
                    _findings.Add(new Finding(Severity.Warning, "ape.item-flags-unknown", $"Item \"{key}\" sets undefined flag bits 0x{flags & UndefinedItemFlags:X8}.", flagsNode));
                }

                if (!keys.Add(key))
                {
                    _findings.Add(new Finding(Severity.Warning, "ape.duplicate-key", $"The key \"{key}\" appears more than once; keys are case-insensitive and must be unique.", item));
                }

                // "APE Tags Item Key are case sensitive", so "Tag" is allowed and only "TAG" is not.
                if (ForbiddenKeys.Contains(key, StringComparer.Ordinal))
                {
                    _findings.Add(new Finding(Severity.Warning, "ape.key-forbidden", $"The key \"{key}\" is not allowed by the APEv2 specification.", item));
                }

                if (type == 3)
                {
                    _findings.Add(new Finding(Severity.Warning, "ape.item-type-reserved", $"Item \"{key}\" uses the reserved item type 3.", item));
                }

                // "APE tag items should be sorted ascending by size ... This is not a must, but
                // strongly recommended."
                if (itemEnd - pos < previousSize && !isUnsorted)
                {
                    isUnsorted = true;
                    _findings.Add(new Finding(Severity.Info, "ape.items-unsorted", $"Item \"{key}\" is smaller than the item before it; the APEv2 specification recommends sorting items ascending by size.", item));
                }

                previousSize = itemEnd - pos;
                pos = itemEnd;
                parsedEnd = pos;
            }

            return pos == end ? null : $"The {anchorName} lists {anchor.ItemCount} item(s), which end at 0x{pos:X}, {end - pos} bytes before the end of the items.";
        }

        // APEv2 text and external values are UTF-8; text may hold several values separated by $00.
        private NodeValue AddValue(Node item, string key, long start, uint size, int type)
        {
            if (type is 1 or 3)
            {
                NodeValue bytes = NodeValue.Integer(size, "bytes");
                item.Add("Value", start, size, bytes, Explain.BinaryValue);
                return bytes;
            }

            byte[] data = reader.ReadBytesAt(start, (int)Math.Min(size, MaxDecodedValueBytes));
            // A value read only in part may end inside a character.
            if (Utf8.ToUtf16(data, new char[data.Length], out _, out _, replaceInvalidSequences: false, isFinalBlock: data.Length == size) == OperationStatus.InvalidData)
            {
                _findings.Add(new Finding(Severity.Warning, "ape.text-not-utf8", $"Item \"{key}\" holds text that is not valid UTF-8, which APEv2 requires; the invalid bytes are shown as U+FFFD.", item));
            }

            var values = new List<string>();
            int i = 0;
            while (true)
            {
                int separator = type == 0 ? Array.IndexOf(data, (byte)0, i) : -1;
                int valueEnd = separator < 0 ? data.Length : separator;
                string value = Encoding.UTF8.GetString(data, i, valueEnd - i);
                values.Add(value);
                item.Add("Value", start + i, valueEnd - i, value, Explain.Value);
                if (separator < 0)
                {
                    break;
                }

                i = separator + 1;
            }

            return string.Join("; ", values);
        }

        // APEv1 values are a single ASCII string. Writers that put other bytes there used UTF-8
        // or a Windows code page, so valid UTF-8 is read as UTF-8 and anything else as ISO-8859-1.
        private string AddV1Value(Node item, long start, uint size)
        {
            byte[] data = reader.ReadBytesAt(start, (int)Math.Min(size, MaxDecodedValueBytes));
            string value;
            if (Ascii.IsValid(data))
            {
                value = Encoding.ASCII.GetString(data);
            }
            else
            {
                bool utf8 = Utf8.IsValid(data);
                value = utf8 ? Encoding.UTF8.GetString(data) : Encoding.Latin1.GetString(data);
                _findings.Add(new Finding(
                    Severity.Warning,
                    "ape.v1-not-ascii",
                    $"The APEv1 value of item \"{item.Name}\" is not ASCII; it is read as {(utf8 ? "UTF-8" : "ISO-8859-1")}.",
                    item));
            }

            Node node = item.Add("Value", start, size, value, Explain.Value);
            if (Array.IndexOf(data, (byte)0) >= 0)
            {
                _findings.Add(new Finding(
                    Severity.Warning,
                    "ape.v1-zero-byte",
                    $"The APEv1 value of item \"{item.Name}\" contains a $00 byte; APEv1 has no value lists, so it is kept as one value.",
                    node));
            }

            return value;
        }

        private void AddBlock(Node tag, string name, long offset, Block block)
        {
            Node node = tag.Add(name, offset, BlockSize, null, name == "Header" ? Explain.Header : Explain.Footer);
            node.Add("Preamble", offset, 8, "APETAGEX", Explain.Preamble);
            node.Add("Version", offset + 8, 4, NodeValue.Integer(block.Version), Explain.Version);
            node.Add("Tag Size", offset + 12, 4, NodeValue.Integer(block.Size), Explain.TagSize);
            node.Add("Item Count", offset + 16, 4, NodeValue.Integer(block.ItemCount), Explain.ItemCount);

            Node flags = node.Add("Flags", offset + 20, 4, NodeValue.Hex(block.Flags, 8), Explain.Flags);
            if (block.Version == 2000)
            {
                flags.Add("HasHeader", offset + 20, 4, NodeValue.Boolean(block.HasHeader), Explain.HasHeader);
                flags.Add("HasFooter", offset + 20, 4, NodeValue.Boolean(block.HasFooter), Explain.HasFooter);
                flags.Add("IsHeader", offset + 20, 4, NodeValue.Boolean(block.IsHeader), Explain.IsHeader);
                flags.Add("IsReadOnly", offset + 20, 4, NodeValue.Boolean((block.Flags & 1) != 0), Explain.IsReadOnly);
                if ((block.Flags & UndefinedTagFlags) != 0)
                {
                    Problem(flags, "ape.flags-unknown", $"The {name.ToLowerInvariant()} sets undefined flag bits 0x{block.Flags & UndefinedTagFlags:X8}.", Confidence.Medium);
                }
            }
            else if (block.Flags != 0)
            {
                // "APE Tags 1.0 do not use any of the APE Tag flags. All are set to zero on
                // creation and ignored on reading."
                _findings.Add(new Finding(Severity.Warning, "ape.v1-flags", $"The APEv1 {name.ToLowerInvariant()} sets flags 0x{block.Flags:X8}; APEv1 writers must leave them zero, and readers ignore them.", flags));
            }

            Node reserved = node.Add("Reserved", offset + 24, 8, null, Explain.Reserved);
            if (!block.IsReservedZero)
            {
                _findings.Add(new Finding(Severity.Warning, "ape.reserved-not-zero", $"The reserved bytes of the APE {name.ToLowerInvariant()} are not zero.", reserved));
            }
        }

        private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];
    }

    private sealed class Block
    {
        public uint Version { get; private init; }

        public uint Size { get; private init; }

        public uint ItemCount { get; private init; }

        public uint Flags { get; private init; }

        public bool IsReservedZero { get; private init; }

        public bool HasHeader => (Flags & (1u << 31)) != 0;

        public bool HasFooter => (Flags & (1u << 30)) == 0;

        public bool IsHeader => (Flags & (1u << 29)) != 0;

        public static Block Parse(byte[] data) => new()
        {
            Version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)),
            Size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)),
            ItemCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)),
            IsReservedZero = data.AsSpan(24, 8).IndexOfAnyExcept((byte)0) < 0,
        };
    }
}
