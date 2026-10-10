using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Tags.Ape;

// APEv1 and APEv2 (hydrogenaud.io wiki, "APEv2 specification" and "APE Tags Header"). A tag is
// found through its 32-byte footer at the end of the file or directly before a following tag.
// A known version and a size that fits the file establish the tag; a disagreeing header or items
// that do not end at the footer are findings that lower the confidence.
public sealed class ApeTagParser : ITagParser
{
    private const int BlockSize = 32;

    // Value size, flags, a two-character key and its terminator.
    private const int MinItemSize = 11;
    private const int MaxDecodedValueBytes = 1 << 20;

    private static readonly byte[] Preamble = "APETAGEX"u8.ToArray();
    private static readonly string[] ItemTypes = ["Text", "Binary", "External", "Reserved"];
    private static readonly string[] ForbiddenKeys = ["ID3", "TAG", "OggS", "MP+"];

    public string Name => "APE";

    public TagProbe? ProbeBefore(ByteReader reader, long end)
    {
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

        var evidence = new List<Evidence> { new($"\"APETAGEX\" footer with version {footer.Version} and a size that fits the file", footerStart, BlockSize) };
        var findings = new List<Finding>();
        var problems = new List<(Node? Node, string Code, string Message)>();
        Confidence confidence = Confidence.High;
        long tagStart = itemsStart;
        Block? header = null;

        if (footer.Version == 2000 && footer.IsHeader)
        {
            problems.Add((null, "ape.footer-flags", "The footer has the is-header flag set."));
            confidence = Confidence.Medium;
        }

        if (footer.Version == 2000 && footer.HasHeader)
        {
            long headerStart = itemsStart - BlockSize;
            if (!reader.MatchesAt(headerStart, Preamble))
            {
                problems.Add((null, "ape.header-missing", $"The footer announces a header, but there is no \"APETAGEX\" header at 0x{headerStart:X}."));
                confidence = Confidence.Medium;
            }
            else
            {
                header = Block.Parse(reader.ReadBytesAt(headerStart, BlockSize));
                tagStart = headerStart;
                string? mismatch = header.Version != footer.Version ? $"version {header.Version} vs {footer.Version}"
                    : header.Size != footer.Size ? $"tag size {header.Size} vs {footer.Size}"
                    : header.ItemCount != footer.ItemCount ? $"item count {header.ItemCount} vs {footer.ItemCount}"
                    : !header.IsHeader ? "the is-header flag is not set"
                    : null;
                if (mismatch is null)
                {
                    evidence.Add(new Evidence("Header agrees with the footer", headerStart, BlockSize));
                }
                else
                {
                    problems.Add((null, "ape.header-mismatch", $"The header disagrees with the footer: {mismatch}."));
                    confidence = Confidence.Medium;
                }
            }
        }

        var tag = new Node(Name, tagStart, end - tagStart, footer.Version == 2000 ? "2.0" : "1.0");
        if (header is not null)
        {
            AddBlock(tag, "Header", tagStart, header, findings);
        }

        Node items = tag.Add("Items", itemsStart, footerStart - itemsStart, $"{footer.ItemCount} items");
        string? itemProblem = ParseItems(reader, items, itemsStart, footerStart, footer, findings, out long parsedEnd);
        if (itemProblem is null)
        {
            evidence.Add(new Evidence($"{footer.ItemCount} items end exactly at the footer", itemsStart, footerStart - itemsStart));
        }
        else
        {
            Node unparsed = tag.Add("Unparsed", parsedEnd, footerStart - parsedEnd, $"{footerStart - parsedEnd} bytes");
            problems.Add((unparsed, "ape.items-invalid", $"{itemProblem} The remaining {footerStart - parsedEnd} bytes are not parsed."));
            confidence = Confidence.Low;
        }

        AddBlock(tag, "Footer", footerStart, footer, findings);

        foreach ((Node? node, string code, string message) in problems)
        {
            findings.Add(new Finding(Severity.Warning, code, message, node ?? tag));
        }

        return TagProbe.Accept(tag, footerStart, confidence, evidence, findings);
    }

    // Returns why the items do not end exactly at the footer, or null; parsedEnd is where the
    // last complete item ends.
    private static string? ParseItems(ByteReader reader, Node items, long start, long end, Block footer, List<Finding> findings, out long parsedEnd)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long pos = start;
        parsedEnd = start;

        for (uint index = 1; index <= footer.ItemCount; index++)
        {
            if (end - pos < MinItemSize)
            {
                return $"Item {index} of {footer.ItemCount} starts {end - pos} bytes before the footer, too few for an item.";
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
                return $"The {valueSize}-byte value of item \"{key}\" runs past the footer.";
            }

            long itemEnd = valueStart + valueSize;
            Node item = items.Add(key, pos, itemEnd - pos);
            item.Add("Size", pos, 4, valueSize.ToString(CultureInfo.InvariantCulture));

            int type = footer.Version == 2000 ? (int)((flags >> 1) & 0x03) : 0;
            Node flagsNode = item.Add("Flags", pos + 4, 4, $"0x{flags:X8}");
            flagsNode.Add("IsReadOnly", pos + 4, 4, (flags & 1) != 0 ? "true" : "false");
            flagsNode.Add("Item Type", pos + 4, 4, ItemTypes[type]);
            item.Add("Key", pos + 8, keyLength, key);

            item.Value = AddValue(reader, item, valueStart, valueSize, type);

            if (!keys.Add(key))
            {
                findings.Add(new Finding(Severity.Warning, "ape.duplicate-key", $"The key \"{key}\" appears more than once; keys are case-insensitive and must be unique.", item));
            }

            if (ForbiddenKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new Finding(Severity.Warning, "ape.key-forbidden", $"The key \"{key}\" is not allowed by the APEv2 specification.", item));
            }

            if (type == 3)
            {
                findings.Add(new Finding(Severity.Warning, "ape.item-type-reserved", $"Item \"{key}\" uses the reserved item type 3.", item));
            }

            pos = itemEnd;
            parsedEnd = pos;
        }

        return pos == end ? null : $"The footer lists {footer.ItemCount} item(s), which end at 0x{pos:X}, {end - pos} bytes before the footer.";
    }

    // Text and external values are UTF-8; text may hold several values separated by $00.
    private static string AddValue(ByteReader reader, Node item, long start, uint size, int type)
    {
        if (type is 1 or 3)
        {
            string bytes = $"{size} bytes";
            item.Add("Value", start, size, bytes);
            return bytes;
        }

        int length = (int)Math.Min(size, MaxDecodedValueBytes);
        byte[] data = reader.ReadBytesAt(start, length);
        var values = new List<string>();
        int i = 0;
        while (true)
        {
            int separator = type == 0 ? Array.IndexOf(data, (byte)0, i) : -1;
            int valueEnd = separator < 0 ? data.Length : separator;
            string value = Encoding.UTF8.GetString(data, i, valueEnd - i);
            values.Add(value);
            item.Add("Value", start + i, valueEnd - i, value);
            if (separator < 0)
            {
                break;
            }

            i = separator + 1;
        }

        return string.Join("; ", values);
    }

    private static void AddBlock(Node tag, string name, long offset, Block block, List<Finding> findings)
    {
        Node node = tag.Add(name, offset, BlockSize);
        node.Add("Preamble", offset, 8, "APETAGEX");
        node.Add("Version", offset + 8, 4, block.Version.ToString(CultureInfo.InvariantCulture));
        node.Add("Tag Size", offset + 12, 4, block.Size.ToString(CultureInfo.InvariantCulture));
        node.Add("Item Count", offset + 16, 4, block.ItemCount.ToString(CultureInfo.InvariantCulture));

        Node flags = node.Add("Flags", offset + 20, 4, $"0x{block.Flags:X8}");
        if (block.Version == 2000)
        {
            flags.Add("HasHeader", offset + 20, 4, block.HasHeader ? "true" : "false");
            flags.Add("HasFooter", offset + 20, 4, block.HasFooter ? "true" : "false");
            flags.Add("IsHeader", offset + 20, 4, block.IsHeader ? "true" : "false");
            flags.Add("IsReadOnly", offset + 20, 4, (block.Flags & 1) != 0 ? "true" : "false");
        }

        Node reserved = node.Add("Reserved", offset + 24, 8);
        if (!block.IsReservedZero)
        {
            findings.Add(new Finding(Severity.Warning, "ape.reserved-not-zero", $"The reserved bytes of the APE {name.ToLowerInvariant()} are not zero.", reserved));
        }
    }

    private sealed class Block
    {
        public uint Version { get; private init; }

        public uint Size { get; private init; }

        public uint ItemCount { get; private init; }

        public uint Flags { get; private init; }

        public bool IsReservedZero { get; private init; }

        // Bit 31: the tag has a header. Bit 30: the tag has no footer. Bit 29: this is the header.
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
