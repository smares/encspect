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

    // The first items become nodes at once; the later ones are listed in blocks from records and the bytes kept for them, up to a
    // budget (the report outlives the file, which cannot be read again). Items past it are checked and counted, so a tag of millions
    // of items (11 bytes each) cannot grow the report.
    internal const int MaxItemNodes = 1024;
    internal const int MaxKeptBytes = 4 << 20;
    private const int MaxKeptValueBytes = 4096;

    // The most Encoder items that become claims: a tag repeating the item has nothing more to say.
    private const int MaxEncoderClaims = 16;
    private const int MaxCaseNames = 3;

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

        // Findings about items are reported once per kind with a count, pointing at the first case.
        private readonly Dictionary<string, Aggregate> _aggregated = [];
        private readonly List<string> _order = [];

        // The items after the first MaxItemNodes whose bytes are kept: the record of the item at an offset from the items' start and
        // what the store holds of it (the fixed part, the key and a text value), listed in blocks when read.
        private readonly List<Node> _listed = [];
        private readonly ChunkedList<ItemRecord> _records = new();
        private readonly ByteStore _store = new();
        private long _keptBytes;
        private uint _read;
        private int _encoderClaims;
        private Node _itemsRef = null!;
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

        // Cases of one code are one finding, pointing at the first case, which is only made then; a variant keeps cases that read
        // differently apart, and the names of the first few cases are kept for the message.
        private void Report(Severity severity, string code, Func<Node> node, Func<int, string, string> message, string? name = null, string? variant = null)
        {
            string key = variant is null ? code : $"{code}|{variant}";
            if (_aggregated.TryGetValue(key, out Aggregate? existing))
            {
                existing.Count++;
                if (name is not null && existing.Names.Count < MaxCaseNames && !existing.Names.Contains(name))
                {
                    existing.Names.Add(name);
                }

                return;
            }

            _aggregated[key] = new Aggregate(code, severity, node(), message) { Names = name is null ? [] : [name] };
            _order.Add(key);
        }

        // " Examples: "a", "b"." for a finding of several cases.
        private static string Examples(int count, List<string> names) =>
            count > 1 && names.Count > 0 ? $" Examples: {string.Join(", ", names.Select(n => $"\"{n}\""))}." : string.Empty;

        private sealed class Aggregate(string code, Severity severity, Node node, Func<int, string, string> message)
        {
            public string Code => code;

            public Severity Severity => severity;

            public Node Node => node;

            public Func<int, string, string> Message => message;

            public int Count { get; set; } = 1;

            public List<string> Names { get; init; } = [];
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

            // What findings about items that are not listed point at: equal in name, offset and length to the Items node added below.
            _itemsRef = new Node("Items", itemsStart, itemsEnd - itemsStart, NodeValue.Integer(anchor.ItemCount, "items")) { Description = Explain.Items };
            string? itemProblem = ParseItems(itemsStart, itemsEnd, out long parsedEnd);
            uint notListed = _read - (uint)(_listed.Count + _records.Count);
            if (notListed > 0)
            {
                Report(Severity.Info, "ape.items-limited", () => _itemsRef, (count, _) => count == 1
                    ? $"1 item is checked and counted but not listed: the first {MaxItemNodes} items are shown and the others only while their bytes fit in {MaxKeptBytes >> 20} MiB (text values of up to {MaxKeptValueBytes} bytes), and duplicate keys among items that are not listed are not looked for."
                    : $"{count} items are checked and counted but not listed: the first {MaxItemNodes} items are shown and the others only while their bytes fit in {MaxKeptBytes >> 20} MiB (text values of up to {MaxKeptValueBytes} bytes), and duplicate keys among items that are not listed are not looked for.");
                _aggregated["ape.items-limited"].Count = (int)Math.Min(int.MaxValue, notListed);
            }

            tag.Add(ItemsNode(itemsStart));
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

            foreach (string key in _order)
            {
                var entry = _aggregated[key];
                _findings.Add(new Finding(entry.Severity, entry.Code, entry.Message(entry.Count, Examples(entry.Count, entry.Names)), entry.Node) { Count = entry.Count });
            }

            foreach ((Node? node, string code, string message) in _problems)
            {
                _findings.Add(new Finding(Severity.Warning, code, message, node ?? tag));
            }

            return TagProbe.Accept(tag, anchorOffset, _confidence, Evidence, _findings, null, _claims);
        }

        // The Items node: the nodes of the first items, then the kept ones, built in blocks when read.
        private Node ItemsNode(long itemsStart)
        {
            if (_records.Count == 0)
            {
                var plain = new Node(_itemsRef.Name, _itemsRef.Offset, _itemsRef.Length, _itemsRef.Value) { Description = Explain.Items };
                foreach (Node child in _listed)
                {
                    plain.Add(child);
                }

                return plain;
            }

            Node[] leading = [.. _listed];
            ChunkedList<ItemRecord> records = _records;
            ByteStore store = _store;
            bool isV1 = IsV1;
            return new Node(_itemsRef.Name, _itemsRef.Offset, _itemsRef.Length, _itemsRef.Value, () => new BlockedChildren(leading, records.Count, i => BuildKept(isV1, itemsStart, store, records[i])))
            {
                Description = Explain.Items,
            };
        }

        // Returns why the items do not end exactly at end, or null; parsedEnd is where the last
        // complete item ends. Every item is checked; the first MaxItemNodes become nodes.
        private string? ParseItems(long start, long end, out long parsedEnd)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long pos = start;
            parsedEnd = start;
            long previousSize = 0;
            bool isUnsorted = false;
            _read = 0;

            for (uint index = 1; index <= anchor.ItemCount; index++)
            {
                if (index % 4096 == 0)
                {
                    reader.CancellationToken.ThrowIfCancellationRequested();
                }

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
                if (keyLength is < 2 or > 255 || keyBytes.AsSpan(0, keyLength).ContainsAnyExceptInRange((byte)0x20, (byte)0x7E))
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
                long itemPos = pos;
                // APEv1 defines no item flags: every value is text.
                int type = IsV1 ? 0 : (int)((flags >> 1) & 0x03);
                bool isText = IsV1 || type is 0 or 2;
                byte[] value = isText ? reader.ReadBytesAt(valueStart, (int)Math.Min(valueSize, MaxDecodedValueBytes)) : [];

                Func<Node> itemNode;
                Func<Node> flagsOrItem;
                bool isListedAtOnce = _listed.Count < MaxItemNodes;
                if (isListedAtOnce)
                {
                    Node item = BuildItem(IsV1, itemPos, valueSize, flags, key, keyLength, value);
                    _listed.Add(item);
                    itemNode = () => item;
                    flagsOrItem = () => item.Children.First(c => c.Name == "Flags");
                }
                else if (Keep(fixedPart, keyBytes, keyLength, value, valueSize, isText, itemPos - start) is { } kept)
                {
                    _records.Add(kept);
                    itemNode = () => BuildKept(IsV1, start, _store, kept);
                    flagsOrItem = () => itemNode().Children.First(c => c.Name == "Flags");
                }
                else
                {
                    itemNode = () => _itemsRef;
                    flagsOrItem = itemNode;
                }

                CheckValue(itemNode, key, value, valueSize, type);

                // Keys are compared without regard to case, so the item of a writer that spells it
                // "encoder" (FFmpeg's) is read as "Encoder" is. No source at hand lists an APEv2 key for the
                // encoder (the APEv2 key list could not be checked), so it is given to no role: the claim is the writer's, unless its text is a libavcodec encoder ID, which names the encoder.
                if (type == 0 && key.Equals("Encoder", StringComparison.OrdinalIgnoreCase) && _encoderClaims < MaxEncoderClaims
                    && string.Join("; ", TextValues(IsV1, value)) is { Length: > 0 } encoder && !string.IsNullOrWhiteSpace(encoder))
                {
                    _encoderClaims++;
                    _claims.Add(new EncoderClaim(encoder, itemNode(), "APE Encoder item", ClaimRoles.Of(ClaimRole.Writer, encoder)));
                }

                if (IsV1 && flags != 0)
                {
                    Report(Severity.Warning, "ape.v1-flags", flagsOrItem, (count, examples) => count == 1
                        ? $"Item \"{key}\" sets flags 0x{flags:X8}; APEv1 writers must leave them zero, so readers that apply the APEv2 flags misread the item."
                        : $"{count} items set flags; APEv1 writers must leave them zero, so readers that apply the APEv2 flags misread them. The first is \"{key}\" with 0x{flags:X8}.{examples}", key);
                }
                else if (!IsV1 && (flags & UndefinedItemFlags) != 0)
                {
                    Report(Severity.Warning, "ape.item-flags-unknown", flagsOrItem, (count, examples) => count == 1
                        ? $"Item \"{key}\" sets undefined flag bits 0x{flags & UndefinedItemFlags:X8}."
                        : $"{count} items set undefined flag bits. The first is \"{key}\" with 0x{flags & UndefinedItemFlags:X8}.{examples}", key);
                }

                // Duplicates are looked for among the listed items' keys, so the set stays bounded.
                if (isListedAtOnce ? !keys.Add(key) : keys.Contains(key))
                {
                    Report(Severity.Warning, "ape.duplicate-key", itemNode, (count, examples) => count == 1
                        ? $"The key \"{key}\" appears more than once; keys are case-insensitive and must be unique."
                        : $"{count} items repeat a key; keys are case-insensitive and must be unique. The first is \"{key}\".{examples}", key);
                }

                // "APE Tags Item Key are case sensitive", so "Tag" is allowed and only "TAG" is not.
                if (ForbiddenKeys.Contains(key, StringComparer.Ordinal))
                {
                    Report(Severity.Warning, "ape.key-forbidden", itemNode, (count, examples) => count == 1
                        ? $"The key \"{key}\" is not allowed by the APEv2 specification."
                        : $"{count} items have a key the APEv2 specification does not allow. The first is \"{key}\".{examples}", key);
                }

                if (type == 3)
                {
                    Report(Severity.Warning, "ape.item-type-reserved", itemNode, (count, examples) => count == 1
                        ? $"Item \"{key}\" uses the reserved item type 3."
                        : $"{count} items use the reserved item type 3. The first is \"{key}\".{examples}", key);
                }

                // "APE tag items should be sorted ascending by size ... This is not a must, but
                // strongly recommended."
                if (itemEnd - itemPos < previousSize && !isUnsorted)
                {
                    isUnsorted = true;
                    Report(Severity.Info, "ape.items-unsorted", itemNode, (_, _) => $"Item \"{key}\" is smaller than the item before it; the APEv2 specification recommends sorting items ascending by size.");
                }

                previousSize = itemEnd - itemPos;
                pos = itemEnd;
                parsedEnd = pos;
                _read++;
            }

            return pos == end ? null : $"The {anchorName} lists {anchor.ItemCount} item(s), which end at 0x{pos:X}, {end - pos} bytes before the end of the items.";
        }

        // The record of an item past the first MaxItemNodes, with the bytes it is built from in the store: the fixed part, the key and
        // a text value. Null where the budget is spent or the value is too long to keep.
        private ItemRecord? Keep(byte[] fixedPart, byte[] keyBytes, int keyLength, byte[] value, uint valueSize, bool isText, long offset)
        {
            int size = 8 + keyLength + 1 + (isText ? value.Length : 0);
            if (isText && (value.Length != valueSize || valueSize > MaxKeptValueBytes) || _keptBytes + size + 12 > MaxKeptBytes)
            {
                return null;
            }

            var stored = new byte[size];
            fixedPart.CopyTo(stored, 0);
            keyBytes.AsSpan(0, keyLength + 1).CopyTo(stored.AsSpan(8));
            if (isText)
            {
                value.CopyTo(stored, 8 + keyLength + 1);
            }

            _keptBytes += size + 12;
            return new ItemRecord((uint)offset, _store.Add(stored), size);
        }

        // The node of a kept item, made again from its record.
        private static Node BuildKept(bool isV1, long itemsStart, ByteStore store, ItemRecord record)
        {
            ReadOnlySpan<byte> stored = store.Span(record.Stored, record.StoredLength);
            uint valueSize = BinaryPrimitives.ReadUInt32LittleEndian(stored);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(stored[4..]);
            int keyLength = stored[8..].IndexOf((byte)0);
            string key = Encoding.ASCII.GetString(stored.Slice(8, keyLength));
            return BuildItem(isV1, itemsStart + record.Offset, valueSize, flags, key, keyLength, stored[(8 + keyLength + 1)..].ToArray());
        }

        // An item's node with its parts, from what the item holds; text is the value's bytes (up to MaxDecodedValueBytes) of a text or external item.
        private static Node BuildItem(bool isV1, long pos, uint valueSize, uint flags, string key, int keyLength, byte[] text)
        {
            long valueStart = pos + 8 + keyLength + 1;
            var item = new Node(key, pos, valueStart + valueSize - pos) { Description = Explain.Item(key), Topic = Explain.ItemTopic(key) };
            item.Add("Size", pos, 4, NodeValue.Integer(valueSize), Explain.ItemSize);
            int type = isV1 ? 0 : (int)((flags >> 1) & 0x03);
            Node flagsNode = item.Add("Flags", pos + 4, 4, NodeValue.Hex(flags, 8), Explain.ItemFlags);
            flagsNode.Add("IsReadOnly", pos + 4, 4, NodeValue.Boolean(!isV1 && (flags & 1) != 0), Explain.ItemReadOnly);
            flagsNode.Add("Item Type", pos + 4, 4, ItemTypes[type], Explain.ItemType);
            item.Add("Key", pos + 8, keyLength, key, Explain.Key);

            if (isV1)
            {
                item.Value = V1Text(text);
                item.Add("Value", valueStart, valueSize, V1Text(text), Explain.Value);
            }
            else if (type is 1 or 3)
            {
                NodeValue bytes = NodeValue.Integer(valueSize, "bytes");
                item.Add("Value", valueStart, valueSize, bytes, Explain.BinaryValue);
                item.Value = bytes;
            }
            else
            {
                var values = new List<string>();
                int i = 0;
                while (true)
                {
                    int separator = type == 0 ? Array.IndexOf(text, (byte)0, i) : -1;
                    int valueEnd = separator < 0 ? text.Length : separator;
                    string value = Encoding.UTF8.GetString(text, i, valueEnd - i);
                    values.Add(value);
                    item.Add("Value", valueStart + i, valueEnd - i, value, Explain.Value);
                    if (separator < 0)
                    {
                        break;
                    }

                    i = separator + 1;
                }

                item.Value = string.Join("; ", values);
            }

            return item;
        }

        // The values of a text item: APEv2 text may hold several, separated by $00; an APEv1 value is one string.
        private static List<string> TextValues(bool isV1, byte[] text)
        {
            if (isV1)
            {
                return [V1Text(text)];
            }

            var values = new List<string>();
            int i = 0;
            while (true)
            {
                int separator = Array.IndexOf(text, (byte)0, i);
                int valueEnd = separator < 0 ? text.Length : separator;
                values.Add(Encoding.UTF8.GetString(text, i, valueEnd - i));
                if (separator < 0)
                {
                    return values;
                }

                i = separator + 1;
            }
        }

        // APEv1 values are a single ASCII string. Writers that put other bytes there used UTF-8
        // or a Windows code page, so valid UTF-8 is read as UTF-8 and anything else as ISO-8859-1.
        private static string V1Text(byte[] data) =>
            Ascii.IsValid(data) ? Encoding.ASCII.GetString(data) : Utf8.IsValid(data) ? Encoding.UTF8.GetString(data) : Encoding.Latin1.GetString(data);

        // What a value breaks, for every item whether listed or not.
        private void CheckValue(Func<Node> itemNode, string key, byte[] data, uint size, int type)
        {
            if (IsV1)
            {
                if (!Ascii.IsValid(data))
                {
                    bool utf8 = Utf8.IsValid(data);
                    Report(Severity.Warning, "ape.v1-not-ascii", itemNode, (count, examples) => count == 1
                        ? $"The APEv1 value of item \"{key}\" is not ASCII; it is read as {(utf8 ? "UTF-8" : "ISO-8859-1")}."
                        : $"{count} APEv1 values are not ASCII; they are read as {(utf8 ? "UTF-8" : "ISO-8859-1")}. The first is item \"{key}\".{examples}", key, utf8 ? "utf8" : "latin1");
                }

                if (Array.IndexOf(data, (byte)0) >= 0)
                {
                    Report(Severity.Warning, "ape.v1-zero-byte", () => ValueOf(itemNode()), (count, examples) => count == 1
                        ? $"The APEv1 value of item \"{key}\" contains a $00 byte; APEv1 has no value lists, so it is kept as one value."
                        : $"{count} APEv1 values contain a $00 byte; APEv1 has no value lists, so each is kept as one value. The first is item \"{key}\".{examples}", key);
                }

                return;
            }

            // A value read only in part may end inside a character.
            if (type is 0 or 2 && Utf8.ToUtf16(data, new char[data.Length], out _, out _, replaceInvalidSequences: false, isFinalBlock: data.Length == size) == OperationStatus.InvalidData)
            {
                Report(Severity.Warning, "ape.text-not-utf8", itemNode, (count, examples) => count == 1
                    ? $"Item \"{key}\" holds text that is not valid UTF-8, which APEv2 requires; the invalid bytes are shown as U+FFFD."
                    : $"{count} items hold text that is not valid UTF-8, which APEv2 requires; the invalid bytes are shown as U+FFFD. The first is \"{key}\".{examples}", key);
            }
        }

        private static Node ValueOf(Node item) => item.Children.FirstOrDefault(c => c.Name == "Value") ?? item;

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

    private readonly record struct ItemRecord(uint Offset, uint Stored, int StoredLength);

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
