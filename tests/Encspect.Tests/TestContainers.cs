using System.Buffers.Binary;
using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Test-only containers for the host's routes, until the real RIFF, AIFF and Ogg parsers arrive.
//
// ChunkedContainer reads "RIFF" (little-endian sizes) and "FORM" (big-endian sizes) files: a 4-byte
// ID, a 4-byte size and a 4-byte form type, then chunks of a 4-byte ID and a 4-byte size, padded to
// an even length. A chunk whose ID is in Codecs goes to that codec through ParseStream, one in Tags
// to that tag parser through ParseTag.
internal sealed class ChunkedContainer(bool declaresEnd = true, IReadOnlyDictionary<string, string>? codecs = null, IReadOnlyDictionary<string, string>? tags = null)
    : IFormatParser
{
    private readonly IReadOnlyDictionary<string, string> _codecs = codecs ?? new Dictionary<string, string> { ["mpga"] = "mpeg-audio" };
    private readonly IReadOnlyDictionary<string, string> _tags = tags ?? new Dictionary<string, string> { ["ID3 "] = "ID3v2", ["id3 "] = "ID3v2" };

    public string Name => "Test chunks";

    public SniffResult Sniff(SniffContext context) =>
        context.Reader.MatchesAt(context.Reader.Start, "RIFF"u8) || context.Reader.MatchesAt(context.Reader.Start, "FORM"u8) ? new(100, context.Reader.Start) : SniffResult.None;

    public long? DeclaredEnd(SniffContext context)
    {
        if (!declaresEnd)
        {
            return null;
        }

        ByteReader reader = context.Reader;
        bool bigEndian = reader.MatchesAt(reader.Start, "FORM"u8);
        reader.Position = reader.Start + 4;
        return reader.Start + 8 + (bigEndian ? reader.ReadUInt32BigEndian() : reader.ReadUInt32LittleEndian());
    }

    public void Parse(ParseContext context)
    {
        ByteReader reader = context.Reader;
        long start = reader.Start;
        string magic = Ascii(reader.ReadBytesAt(start, 4));
        bool bigEndian = magic == "FORM";
        long declared = start + 8 + (bigEndian ? reader.ReadUInt32BigEndian() : reader.ReadUInt32LittleEndian());
        string form = Ascii(reader.ReadBytes(4));
        long end = Math.Min(declared, reader.End);
        Node container = context.Root.Add(new Node(magic, start, end - start, form));
        if (declared > reader.End)
        {
            context.AddFinding(Severity.Warning, "test.container-truncated", "The container ends early.", container);
        }

        long position = start + 12;
        while (position + 8 <= end)
        {
            string id = Ascii(reader.ReadBytesAt(position, 4));
            long size = bigEndian ? reader.ReadUInt32BigEndian() : reader.ReadUInt32LittleEndian();
            long dataStart = position + 8;
            long available = Math.Min(size, end - dataStart);
            Node chunk = container.Add(new Node("Chunk", position, 8 + available, id));
            if (available < size)
            {
                context.AddFinding(Severity.Warning, "test.chunk-truncated", $"Chunk {id} ends early.", chunk);
            }

            ByteReader data = reader.Slice(dataStart, available);
            if (_codecs.TryGetValue(id, out string? codec) && !context.ParseStream(codec, chunk, data))
            {
                context.AddFinding(Severity.Info, "test.codec-unknown", $"No codec parser for {codec}.", chunk);
            }
            else if (_tags.TryGetValue(id, out string? tag))
            {
                context.ParseTag(tag, data, chunk);
            }

            position = Math.Min(end, dataStart + available + (size % 2));
        }

        if (end < reader.End)
        {
            context.Root.Add(new Node("Trailing Data", end, reader.End - end));
        }
    }

    public static byte[] Build(string magic, string form, params (string Id, byte[] Data)[] chunks) => Build(magic, form, null, chunks);

    // declaredSize replaces the size the header would state.
    public static byte[] Build(string magic, string form, uint? declaredSize, params (string Id, byte[] Data)[] chunks)
    {
        bool bigEndian = magic == "FORM";
        var body = new List<byte>(Encoding.ASCII.GetBytes(form));
        foreach ((string id, byte[] data) in chunks)
        {
            body.AddRange(Encoding.ASCII.GetBytes(id));
            body.AddRange(Size((uint)data.Length, bigEndian));
            body.AddRange(data);
            if (data.Length % 2 != 0)
            {
                body.Add(0);
            }
        }

        return [.. Encoding.ASCII.GetBytes(magic), .. Size(declaredSize ?? (uint)body.Count, bigEndian), .. body];
    }

    private static byte[] Size(uint size, bool bigEndian)
    {
        var bytes = new byte[4];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, size);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, size);
        }

        return bytes;
    }

    private static string Ascii(byte[] bytes) => Encoding.ASCII.GetString(bytes);
}

// PagedContainer reads pages laid out like Ogg's (Ogg specification, "page header" and "packet
// segmentation"): "PAGE", a segment count and that many lacing values, then the segments. A lacing
// value below 255 ends a packet, so a packet continues across pages, with the next page's header
// between its parts. Every packet goes to Codec through ParsePackets; with configured set, the first
// packet is the codec's configuration instead, under the node of its first page, as MP4 hands AAC its
// AudioSpecificConfig.
internal sealed class PagedContainer(string codec = PacketCodec.CodecId, bool configured = false) : IFormatParser
{
    public string Name => "Test pages";

    public SniffResult Sniff(SniffContext context) => context.Reader.MatchesAt(context.Reader.Start, "PAGE"u8) ? new(100, context.Reader.Start) : SniffResult.None;

    public void Parse(ParseContext context)
    {
        ByteReader reader = context.Reader;
        Node container = context.Root.Add(new Node("Pages", reader.Start, reader.Length));
        var packets = new List<List<ByteRange>>();
        var current = new List<ByteRange>();
        long position = reader.Start;
        while (position < reader.End && reader.MatchesAt(position, "PAGE"u8))
        {
            reader.Position = position + 4;
            int count = reader.ReadByte();
            byte[] lacing = reader.ReadBytes(count);
            long payload = position + 5 + count;
            long at = payload;
            foreach (byte value in lacing)
            {
                current.Add(new ByteRange(at, value));
                at += value;
                if (value < 255)
                {
                    packets.Add(current);
                    current = [];
                }
            }

            Node page = container.Add(new Node("Page", position, at - position));
            page.Add("Header", position, payload - position);
            position = at;
        }

        CodecConfiguration? configuration = null;
        if (configured && packets.Count > 0)
        {
            Node first = container.Children.First(c => c.Name == "Page");
            configuration = new CodecConfiguration(MappedBytes.Read(reader, packets[0]), first) { IsVbr = true };
            packets.RemoveAt(0);
        }

        context.ParsePackets(codec, container, packets.Select(ranges => MappedBytes.Read(reader, ranges)), configuration);
    }

    // The file and, per packet, the file ranges its bytes end up in. A page holds at most
    // segmentsPerPage segments.
    public static (byte[] File, List<List<ByteRange>> Ranges) Build(int segmentsPerPage, params byte[][] packets)
    {
        var segments = new List<(byte[] Packet, int Index, int Start, int Length)>();
        for (int p = 0; p < packets.Length; p++)
        {
            int start = 0;
            while (true)
            {
                int length = Math.Min(255, packets[p].Length - start);
                segments.Add((packets[p], p, start, length));
                start += length;
                if (length < 255)
                {
                    break;
                }
            }
        }

        var file = new List<byte>();
        var ranges = packets.Select(_ => new List<ByteRange>()).ToList();
        for (int first = 0; first < segments.Count; first += segmentsPerPage)
        {
            var page = segments.Skip(first).Take(segmentsPerPage).ToList();
            file.AddRange("PAGE"u8.ToArray());
            file.Add((byte)page.Count);
            file.AddRange(page.Select(s => (byte)s.Length));
            foreach ((byte[] packet, int index, int start, int length) in page)
            {
                if (length > 0)
                {
                    ranges[index].Add(new ByteRange(file.Count, length));
                }

                file.AddRange(packet.AsSpan(start, length).ToArray());
            }
        }

        return ([.. file], ranges);
    }
}

// Shows each packet as a "Packet" node at its file ranges, its first byte as "Type", and a 12-bit
// "Field" that starts 4 bits before the end of the packet's first file range, so in a packet that
// spans pages the field straddles a page header. The first such field gets a finding, and the first
// packet is reported as a stream. Records what it saw, for the tests; one instance per test.
internal sealed class PacketCodec : ICodecParser
{
    public const string CodecId = "test-packets";

    public string Id => CodecId;

    public List<MappedBytes> Packets { get; } = [];

    public List<(MappedBytes Packet, BitReader Bits, long StartBit)> Fields { get; } = [];

    public CodecConfiguration? Configuration { get; private set; }

    public void Parse(CodecContext context)
    {
        Configuration = context.Configuration;
        if (context.Configuration is { } configuration)
        {
            configuration.Parent.Add(Node.FromMapped("Configuration", configuration.Data, 0, configuration.Data.Length));
        }

        Node? first = null;
        bool reported = false;
        foreach (MappedBytes packet in context.Packets!)
        {
            Packets.Add(packet);
            Node node = context.Parent.Add(Node.FromMapped("Packet", packet, 0, packet.Length, NodeValue.Integer(packet.Length, "bytes"), NodeKind.Frame));
            first ??= node;
            if (packet.Length == 0)
            {
                continue;
            }

            node.Add(Node.FromMapped("Type", packet, 0, 1, NodeValue.Integer(packet.Span[0])));
            long startBit = (packet.FileRanges()[0].Length * 8) - 4;
            if (startBit + 12 > packet.Length * 8L)
            {
                continue;
            }

            var bits = new BitReader(packet);
            for (long skipped = 0; skipped < startBit; skipped += 32)
            {
                bits.ReadLong((int)Math.Min(32, startBit - skipped));
            }

            Fields.Add((packet, bits, startBit));
            Node field = node.Add(Node.FromBits("Field", packet, startBit, startBit + 12, NodeValue.Integer(bits.Read(12))));
            if (field.Ranges is not null && !reported)
            {
                context.AddFinding(Severity.Info, "test.field-straddles-pages", "The field straddles a page header.", field);
                reported = true;
            }
        }

        if (first is not null)
        {
            context.AddStream(new StreamInfo { Nodes = [first], Codec = "Test packets" });
        }
    }
}

// A codec parser that runs an action on its payload, for exceptions, cancellation and nesting.
internal sealed class ActionCodec(string id, Action<CodecContext> action) : ICodecParser
{
    public string Id => id;

    public void Parse(CodecContext context) => action(context);
}

// A tag parser that runs an action when a container hands it a tag.
internal sealed class ActionTagParser(string name, Func<TagProbeContext, TagProbe?> action) : ITagParser
{
    public string Name => name;

    public TagProbe? ParseWhole(TagProbeContext context) => action(context);
}

internal static class TestFiles
{
    // An ID3v2.3 tag of text frames (ID3v2.3 section 3: header, then frames with a 4-byte
    // big-endian size and two flag bytes; text in ISO-8859-1).
    public static byte[] Id3v23(params (string Id, string Text)[] frames)
    {
        var body = new List<byte>();
        foreach ((string id, string text) in frames)
        {
            byte[] data = [0, .. Encoding.Latin1.GetBytes(text)];
            body.AddRange(Encoding.ASCII.GetBytes(id));
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)data.Length);
            body.AddRange(size);
            body.AddRange([0, 0]);
            body.AddRange(data);
        }

        int length = body.Count;
        byte[] syncsafe = [(byte)((length >> 21) & 0x7F), (byte)((length >> 14) & 0x7F), (byte)((length >> 7) & 0x7F), (byte)(length & 0x7F)];
        return [.. "ID3"u8, 3, 0, 0, .. syncsafe, .. body];
    }

    // An APEv2 tag with header and footer holding one text item.
    public static byte[] ApeText(string key, string value)
    {
        byte[] data = Encoding.UTF8.GetBytes(value);
        byte[] body = [.. BitConverter.GetBytes((uint)data.Length), .. BitConverter.GetBytes(0u), .. Encoding.ASCII.GetBytes(key), 0, .. data];
        byte[] Block(uint flags)
        {
            byte[] block = new byte[32];
            "APETAGEX"u8.CopyTo(block);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), 2000);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), (uint)(body.Length + 32));
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), flags);
            return block;
        }

        return [.. Block(0xA0000000), .. body, .. Block(0x80000000)];
    }

    // The FORM/AIFF file of the Gate 1 contract review (gate1/work-host/aiff-id3-chunk.aiff): COMM,
    // an SSND chunk of silence and, last, an "ID3 " chunk holding an ID3v2.4 tag with a footer, so
    // the tag also ends the file. formSize replaces the size FORM states.
    public static byte[] AiffWithId3Chunk(uint? formSize = null)
    {
        byte[] comm = [0, 1, 0, 0, 0, 100, 0, 16, .. new byte[10]];
        byte[] ssnd = new byte[0xD0];
        byte[] frames =
        [
            .. "TIT2"u8, 0, 0, 0, 12, 0, 0, 3, .. "Chunk title"u8,
            .. "TPE1"u8, 0, 0, 0, 8, 0, 0, 3, .. "Artists"u8,
        ];
        byte[] header = [4, 0, 0x10, 0, 0, 0, 0x28];
        byte[] tag = [.. "ID3"u8, .. header, .. frames, .. "3DI"u8, .. header];
        return ChunkedContainer.Build("FORM", "AIFF", formSize, ("COMM", comm), ("SSND", ssnd), ("ID3 ", tag));
    }

    public static Report Analyze(byte[] file, IFormatParser container, IEnumerable<ICodecParser>? codecs = null, IEnumerable<ITagParser>? tags = null,
        AnalysisOptions? options = null) =>
        new Analyzer(tags ?? DefaultParsers.Tags, [container, .. DefaultParsers.Formats], codecs ?? DefaultParsers.Codecs).Analyze("test.bin", new MemoryStream(file), options);

    // The node a finding's or a stream's path leads to.
    public static Node Follow(Node root, IReadOnlyList<int> path)
    {
        Node node = root;
        foreach (int index in path)
        {
            node = node.Children[index];
        }

        return node;
    }

    public static IReadOnlyList<ByteRange> RangesOf(Node node) => node.Ranges ?? [new ByteRange(node.Offset, node.Length)];

    // The fuzzer's invariants: children lie inside their parent's bytes, and inside its ranges where
    // it has them.
    public static void CheckInsideParents(Node parent)
    {
        foreach (Node child in parent.Children)
        {
            Assert.True(parent.Offset <= child.Offset && child.End <= parent.End, $"{child.Name} lies outside {parent.Name}");
            Assert.False(child.Name == "Error" && child.Value?.ToString()?.StartsWith("The children could not be built", StringComparison.Ordinal) == true, $"{parent.Name}: {child.Value}");
            foreach (ByteRange range in RangesOf(child).Where(r => r.Length > 0))
            {
                Assert.Contains(RangesOf(parent), p => p.Offset <= range.Offset && range.End <= p.End);
            }

            CheckInsideParents(child);
        }
    }
}
