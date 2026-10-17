using System.Buffers.Binary;
using System.Text;
using Encspect.Core;
using Encspect.Formats.Asf;
using Encspect.Host;

namespace Encspect.Tests;

// One damaged or unusual file for each finding the ASF parser emits that no hand-built fixture's manifest lists:
// a fixture of tests/fixtures/asf with the bytes of one field changed (or a file built in memory), and the finding
// that must come out. CoverageTests checks that every code the parsers emit is asserted somewhere.
public class AsfFindingTests
{
    private const string Header = "75B22630-668E-11CF-A6D9-00AA0062CE6C";
    private const string Data = "75B22636-668E-11CF-A6D9-00AA0062CE6C";
    private const string FileProperties = "8CABDCA1-A947-11CF-8EE4-00C00C205365";
    private const string StreamProperties = "B7DC0791-A9B7-11CF-8EE6-00C00C205365";
    private const string HeaderExtension = "5FBF03B5-A92E-11CF-8EE3-00C00C205365";
    private const string StreamBitrate = "7BF875CE-468D-11D1-8D82-006097C9A2B2";
    private const string ContentBranding = "2211B3FA-BD23-11D2-B4B7-00A0C955FC6E";
    private const string CodecList = "86D15240-311D-11D0-A3A4-00A0C90348F6";
    private const string ContentDescription = "75B22633-668E-11CF-A6D9-00AA0062CE6C";
    private const string ExtendedContent = "D2D0A440-E307-11D2-97F0-00A0C95EA850";
    private const string ExtendedStream = "14E6A5CB-C672-4332-8399-A96952065B5A";
    private const string Index = "D6E229D3-35DA-11D1-9034-00A0C90349BE";
    private const string SimpleIndex = "33000890-E5B1-11CF-89F4-00A0C90349CB";
    private const string IndexParameters = "D6E229DF-35DA-11D1-9034-00A0C90349BE";
    private const string TimecodeIndex = "3CB73FD0-0C4A-4803-953D-EDF7B6228F0C";
    private const string Metadata = "C5F8CBEA-5BAF-4877-8467-AA8C44FA4CCA";

    private static readonly Dictionary<string, Func<byte[]>> Crafts = new()
    {
        ["asf.bitrate-record-unknown"] = () => Patch("two-streams", d => Set16(d, At(d, StreamBitrate) + 26, 9)),
        ["asf.boolean-value"] = () => Patch("metadata", d => Set32(d, Text(d, "IsVBR\0") + 16, 5)),
        ["asf.branding-image"] = () => Patch("metadata", d => Set32(d, At(d, ContentBranding) + 24, 0)),
        ["asf.codec-list-count"] = () => Patch("header-objects", d => Set32(d, At(d, CodecList) + 40, 9)),
        ["asf.codec-list-reserved"] = () => Patch("header-objects", d => Array.Clear(d, At(d, CodecList) + 24, 16)),
        ["asf.command-type-index"] = () => Patch("header-objects", d => Set16(d, Text(d, "http://example.invalid/") - 4, 9)),
        ["asf.compressed-payload"] = () => CompressedWithoutOffset(),
        ["asf.data-after-tag"] = () => TagInsideTheFile(),
        ["asf.data-reserved"] = () => Patch("minimal", d => Set16(d, At(d, Data) + 48, 0)),
        ["asf.data-unused"] = () => TrailingInData(),
        ["asf.descriptor-count"] = () => Patch("metadata", d => Set16(d, At(d, ExtendedContent) + 24, 200)),
        ["asf.descriptor-length"] = () => Patch("metadata", d => Set16(d, Text(d, "WM/TrackNumber\0") + 32, 6)),
        ["asf.duplicate-data"] = () => Patch("minimal", d => d.Concat(d[At(d, Data)..]).ToArray()),
        ["asf.duplicate-file-properties"] = () => Duplicate("minimal", FileProperties),
        ["asf.duplicate-header"] = () => Patch("minimal", d => [.. d, .. Guid.Parse(Header).ToByteArray(), 24, 0, 0, 0, 0, 0, 0, 0]),
        ["asf.duplicate-header-extension"] = () => Duplicate("minimal", HeaderExtension),
        ["asf.extended-stream-unknown"] = () => Patch("extended-stream-properties", d => Set16(d, At(d, ExtendedStream) + 72, 9)),
        ["asf.extension-reserved"] = () => Patch("minimal", d => Set16(d, At(d, HeaderExtension) + 40, 3)),
        ["asf.extension-size"] = () => Patch("minimal", d => Set32(d, At(d, HeaderExtension) + 42, 77777)),
        ["asf.file-flags-reserved"] = () => Patch("minimal", d => Set32(d, At(d, FileProperties) + 88, 0x80)),
        ["asf.fragment-order"] = () => Routed(Fragment(1, 0, 100), Fragment(1, 50, 100)),
        ["asf.fragment-overrun"] = () => Routed(Fragment(1, 0, 30)),
        ["asf.index-interval"] = () => Patch("indexes", d => Set32(d, At(d, Index) + 24, 0)),
        ["asf.index-max-packets"] = () => Patch("indexes", d => Set32(d, At(d, SimpleIndex) + 48, 7)),
        ["asf.index-offset"] = () => Patch("indexes", d =>
        {
            int o = At(d, Index);
            Set32(d, o + 50, 100000);
            Set32(d, o + 54, 7);
        }),
        ["asf.index-order"] = () => Patch("indexes", d => Set32(d, At(d, SimpleIndex) + 56, 5)),
        ["asf.index-packet-number"] = () => Patch("indexes", d => Set32(d, At(d, SimpleIndex) + 56 + 12, 999)),
        ["asf.index-parameters-missing"] = () => Patch("indexes", d => d[At(d, IndexParameters)] ^= 0xFF),
        ["asf.index-reserved"] = () => Patch("index-kinds", d => Set32(d, At(d, TimecodeIndex) + 24, 7)),
        ["asf.index-size"] = () => Patch("indexes", d => Set32(d, At(d, SimpleIndex) + 52, 99)),
        ["asf.index-specifiers"] = () => Patch("indexes", d => Set16(d, At(d, Index) + 36, 2)),
        ["asf.marker-entry-length"] = () => Patch("header-objects", d => Set16(d, Text(d, "Start\0") - 14, 9)),
        ["asf.metadata-reserved"] = () => Patch("metadata", d => Set16(d, At(d, Metadata) + 26, 5)),
        ["asf.object-incomplete"] = () => Routed(Fragment(1, 0, 100), Fragment(2, 0, 40)),
        ["asf.object-size-invalid"] = () => Routed(Fragment(1, 0, 0)),
        ["asf.payload-empty"] = () => EmptyPayload(),
        ["asf.payload-length-type"] = () => MultipleWithoutLengthType(),
        ["asf.picture-size"] = () => Patch("metadata", d => Set32(d, Text(d, "image/png") - 4, 1)),
        ["asf.spread-silence"] = () => Patch("spread-audio", d => Set16(d, At(d, StreamProperties) + 78 + Get32(d, At(d, StreamProperties) + 64) + 5, 500)),
        ["asf.stream-data-length"] = () => Patch("minimal", d =>
        {
            int o = At(d, StreamProperties);
            Set32(d, o + 64, 9999);
            Set32(d, o + 68, 9999);
        }),
        ["asf.stream-number"] = () => Patch("minimal", d => Set16(d, At(d, StreamProperties) + 72, 0)),
        ["asf.stream-number-reused"] = () => Patch("two-streams", d => Set16(d, At(d, StreamProperties, 1) + 72, 1)),
        ["asf.string-odd-length"] = () => Patch("metadata", d => Set16(d, At(d, ContentDescription) + 24, 17)),
        ["asf.too-many-entries"] = () => ManyCodecs(),
        ["asf.too-many-objects"] = () => Patch("minimal", d => [.. d, .. Enumerable.Range(0, 10_001).SelectMany(_ => UnknownObject())]),
        ["asf.waveformat-extra"] = () => Patch("minimal", d => Set16(d, At(d, StreamProperties) + 78 + 16, 500)),
        ["asf.waveformat-truncated"] = () => Patch("minimal", d => Set32(d, At(d, StreamProperties) + 64, 10)),
    };

    public static TheoryData<string> Codes => [.. Crafts.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Codes))]
    public void DamagedFileGivesTheFinding(string code)
    {
        byte[] data = Crafts[code]();
        Report report = Analyzer.CreateDefault().Analyze(code + ".asf", new MemoryStream(data));
        Assert.Contains(report.Findings, f => f.Code == code);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        Assert.Equal(data.Length, report.Root.Length);
        TestFiles.CheckInsideParents(report.Root);

        // Top-level nodes own their bytes, as the fuzzer checks.
        long previousEnd = 0;
        foreach (ByteRange range in report.Root.Children.SelectMany(TestFiles.RangesOf).OrderBy(r => r.Offset))
        {
            Assert.True(range.Offset >= previousEnd, $"A top-level node at {range.Offset} overlaps the one before, which ends at {previousEnd}.");
            previousEnd = range.End;
        }
    }

    // Packets of 200 bytes that state a packet length of 150: the file fixes the size of every packet at 200 (section
    // 8.2.13), so the packets are 200 bytes apart, each with one payload, and the difference is the padding (FFmpeg
    // treats it so, asfdec_f.c) besides a finding.
    [Fact]
    public void ShorterPacketLengthFieldDoesNotMoveThePacketWalk()
    {
        Report report = Analyzer.CreateDefault().Analyze("stepping.asf", new MemoryStream(PacketsStating(150)));
        Assert.Contains(report.Findings, f => f.Code == "asf.packet-length");
        Node list = FindNode(report.Root, "Data Packets")!;
        Assert.Equal(6, list.Children.Count);
        Assert.All(list.Children, packet => Assert.Single(packet.Children, c => c.Name == "Payload"));
        Assert.DoesNotContain(report.Findings, f => f.Code is "asf.packet-truncated" or "asf.payload-overrun" or "asf.data-unused");
    }

    // A packet length field above the packet size is read within the packet: the payload is there, and the excess is an overrun.
    [Fact]
    public void LongerPacketLengthFieldIsReadWithinThePacket()
    {
        Report report = Analyzer.CreateDefault().Analyze("long.asf", new MemoryStream(PacketsStating(250)));
        Assert.Contains(report.Findings, f => f.Code == "asf.packet-length");
        Assert.Contains(report.Findings, f => f.Code == "asf.payload-overrun");
        Node list = FindNode(report.Root, "Data Packets")!;
        Assert.Equal(6, list.Children.Count);
        Assert.All(list.Children, packet => Assert.Single(packet.Children, c => c.Name == "Payload"));
        TestFiles.CheckInsideParents(report.Root);
    }

    private static byte[] PacketsStating(int length)
    {
        var packets = new List<byte[]>();
        for (int i = 0; i < 6; i++)
        {
            byte[] p = new byte[200];
            p[0] = 0x40;
            p[1] = 0x5D;
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), (ushort)length);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), (uint)(i * 10));
            p[10] = 0x81;
            p[11] = (byte)(i + 1);
            p[16] = 8;
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(17), 125);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(21), (uint)(i * 10));
            Array.Fill(p, (byte)0x55, 25, 125);
            packets.Add(p);
        }

        return AsfBuilder.Assemble(WmaStream(), 200, packets);
    }

    // FFmpeg turns descrambling off where the chunk size is 0 (asfdec_f.c) and decodes the media objects as they are, so
    // the stream is analyzed instead of skipped.
    [Fact]
    public void SpreadAudioWithoutChunksIsNotSkipped()
    {
        byte[] scrambled = Patch("spread-audio", _ => { });
        Assert.Contains(Analyzer.CreateDefault().Analyze("a.asf", new MemoryStream(scrambled)).Findings, f => f.Code == "asf.audio-spread");
        byte[] data = Patch("spread-audio", d => Set16(d, At(d, StreamProperties) + 78 + Get32(d, At(d, StreamProperties) + 64) + 3, 0));
        Assert.DoesNotContain(Analyzer.CreateDefault().Analyze("b.asf", new MemoryStream(data)).Findings, f => f.Code == "asf.audio-spread");
    }

    // One record with more encrypted object records than become nodes: the record is shown as far as it is read, the
    // rest of the object is Extra Data, and the finding names the count.
    [Fact]
    public void LongEncryptionRecordIsShownAsFarAsItIsRead()
    {
        byte[] data = AsfBuilder.InHeader("minimal", AsfBuilder.EncryptionRecords(1, 5000), extension: true);
        Report report = Analyzer.CreateDefault().Analyze("ace.asf", new MemoryStream(data));
        Finding finding = Assert.Single(report.Findings, f => f.Code == "asf.too-many-entries");
        Assert.Contains("5000 encrypted object records in record 1 of 1", finding.Message, StringComparison.Ordinal);
        Node encryption = FindNode(report.Root, "Advanced Content Encryption Object")!;
        Assert.Single(encryption.Children, c => c.Name == "Content Encryption Record");
        Node extra = Assert.Single(encryption.Children, c => c.Name == "Extra Data");
        Assert.Equal(encryption.Children.Single(c => c.Name == "Content Encryption Record").End, extra.Offset);
        Assert.Equal(encryption.End, extra.End);
        TestFiles.CheckInsideParents(report.Root);
    }

    // Every packet issue the parser can flag has a finding written for it.
    [Fact]
    public void EveryPacketIssueHasAFinding()
    {
        foreach (PacketIssues issue in Enum.GetValues<PacketIssues>().Where(i => i != PacketIssues.None))
        {
            Assert.NotNull(AsfParser.Describe(issue));
        }
    }

    // The findings of the Content Description and Extended Content Description Objects point at the length and count fields they are about,
    // not at the Object ID and Object Size that come first in each object.
    [Theory]
    [InlineData("asf.string-odd-length", "Title Length")]
    [InlineData("asf.object-truncated", "Author Length")]
    [InlineData("asf.descriptor-count", "Content Descriptors Count")]
    public void MetadataLengthFindingsPointAtTheirFields(string code, string field)
    {
        Report report = Fixtures.AnalyzeMpeg("asf/content-description-findings.asf");
        Finding finding = Assert.Single(report.Findings, f => f.Code == code);
        Assert.NotNull(finding.Path);
        Node node = report.Root;
        foreach (int index in finding.Path)
        {
            node = node.Children[index];
        }

        Assert.Equal(field, node.Name);
    }

    // The fixed packet size makes the bytes after a payload padding whatever the packet length field says: the statistics count what the
    // Padding nodes show (3 packets of 256 bytes, payloads and headers 510 and 90 bytes, the rest padding).
    [Fact]
    public void PaddingStatisticsCountTheDifferenceToAShorterLengthField()
    {
        Report report = Fixtures.AnalyzeMpeg("asf/length-field-short.asf");
        Node statistics = FindNode(report.Root, "Statistics")!;
        Assert.Equal(168L, FindNode(statistics, "Padding Bytes")!.Value!.AsInteger());
        Assert.Equal(90L, FindNode(statistics, "Packet Overhead")!.Value!.AsInteger());
        Node list = FindNode(report.Root, "Data Packets")!;
        Assert.All(list.Children, packet => Assert.Equal(56L, packet.Children.Single(c => c.Name == "Padding").Value!.AsInteger()));
    }

    // The findings of media objects are kept per stream: each stream's damage is counted under its own number.
    [Fact]
    public void MediaObjectFindingsAreCountedPerStream()
    {
        Report report = Fixtures.AnalyzeMpeg("asf/fragments-in-two-streams.asf");
        var order = report.Findings.Where(f => f.Code == "asf.fragment-order").ToList();
        Assert.Equal(2, order.Count);
        Assert.Single(order, f => f.Count == 1 && f.Message.Contains("of stream 1 ", StringComparison.Ordinal));
        Assert.Single(order, f => f.Count == 2 && f.Message.Contains("of stream 2 ", StringComparison.Ordinal));
    }

    // An Encrypted Object Record that cannot be read as a stream number covers every stream, whatever the Encrypted Content Flag says.
    [Fact]
    public void ObjectRecordWithNoStreamNumberCoversEveryStream()
    {
        Report report = Fixtures.AnalyzeMpeg("asf/protection-object-ids.asf");
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.True(stream.IsProtected);
        Assert.Equal("Windows Media DRM for network devices", stream.Protection!.Scheme);
        Assert.Contains(report.Findings, f => f.Code == "asf.content-encrypted" && f.Message.Contains("every stream is taken to be encrypted", StringComparison.Ordinal));
    }

    // A broadcast file (Data Object of size 0) runs to the end of the file, so a TAG at its end is not an ID3v1 tag
    // that cuts the last packet.
    [Fact]
    public void BroadcastFileOwnsItsLastBytes()
    {
        byte[] data = Patch("broadcast", d =>
        {
            // The last packet ends with 'TAG' and 125 more bytes, which would be an ID3v1 tag at the end of the file.
            Array.Copy("TAG"u8.ToArray(), 0, d, d.Length - 128, 3);
        });
        Report report = Analyzer.CreateDefault().Analyze("broadcast.asf", new MemoryStream(data));
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("tag.", StringComparison.Ordinal) && f.Code != "tag.near-miss");
        Assert.DoesNotContain(report.Root.Children, c => c.Name.StartsWith("ID3", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Findings, f => f.Code == "asf.packet-truncated");
    }

    private static Node? FindNode(Node node, string name)
    {
        if (node.Name == name)
        {
            return node;
        }

        foreach (Node child in node.Children)
        {
            if (FindNode(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static byte[] Patch(string fixture, Action<byte[]> change)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf", fixture + ".asf"));
        change(data);
        return data;
    }

    private static byte[] Patch(string fixture, Func<byte[], byte[]> change)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf", fixture + ".asf"));
        return change(data);
    }

    // The offset of the nth object with this GUID.
    private static int At(byte[] data, string guid, int nth = 0)
    {
        byte[] id = Guid.Parse(guid).ToByteArray();
        int from = 0;
        while (true)
        {
            int found = data.AsSpan(from).IndexOf(id);
            Assert.True(found >= 0, $"no object {guid}");
            if (nth-- == 0)
            {
                return from + found;
            }

            from += found + 1;
        }
    }

    // The offset of UTF-16 text.
    private static int Text(byte[] data, string text)
    {
        int found = data.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text));
        Assert.True(found >= 0, $"no text {text}");
        return found;
    }

    private static void Set16(byte[] data, int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), (ushort)value);

    private static void Set32(byte[] data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);

    private static int Get32(byte[] data, int at) => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));

    // A copy of a header object next to the first, with the Header Object's size and count raised.
    private static byte[] Duplicate(string fixture, string guid)
    {
        return Patch(fixture, d =>
        {
            int at = At(d, guid);
            int size = (int)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(at + 16));
            byte[] copy = d.AsSpan(at, size).ToArray();
            byte[] result = [.. d[..(at + size)], .. copy, .. d[(at + size)..]];
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(16)) + (ulong)size);
            Set32(result, 24, (uint)Get32(result, 24) + 1);
            return result;
        });
    }

    // An ID3v2.4 tag in front whose SEEK frame points into the packets, where another tag lies, in a file whose Header
    // Object states a size of 26 bytes: it declares no end of its own, so the host passes the tag and the objects stop
    // at it.
    private static byte[] TagInsideTheFile()
    {
        byte[] asf = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf", "minimal.asf"));
        int header = (int)BinaryPrimitives.ReadUInt64LittleEndian(asf.AsSpan(16));
        BinaryPrimitives.WriteUInt64LittleEndian(asf.AsSpan(16), 26);
        int at = header + 24 + 10;
        byte[] seek = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 14, (byte)'S', (byte)'E', (byte)'E', (byte)'K', 0, 0, 0, 4, 0, 0, 0, 0, (byte)(at >> 8), (byte)at];
        byte[] inner = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 14, (byte)'T', (byte)'I', (byte)'T', (byte)'2', 0, 0, 0, 4, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c'];
        return [.. seek, .. asf[..at], .. inner, .. asf[at..]];
    }

    private static byte[] UnknownObject() => [.. Guid.Parse("12345678-1234-1234-1234-123456789ABC").ToByteArray(), 24, 0, 0, 0, 0, 0, 0, 0];

    private static byte[] TrailingInData()
    {
        return Patch("minimal", d =>
        {
            int at = At(d, Data);
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(at + 16), BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(at + 16)) + 1);
            return [.. d, 0];
        });
    }

    // A codec list with more entries than become nodes, in place of the codec list of header-objects.
    private static byte[] ManyCodecs()
    {
        return Patch("header-objects", d =>
        {
            int at = At(d, CodecList);
            int size = (int)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(at + 16));
            const int count = 1003;
            var list = new MemoryStream();
            list.Write(Guid.Parse(CodecList).ToByteArray());
            list.Write(new byte[8]);
            list.Write(Guid.Parse("86D15241-311D-11D0-A3A4-00A0C90348F6").ToByteArray());
            list.Write(BitConverter.GetBytes((uint)count));
            for (int i = 0; i < count; i++)
            {
                list.Write([2, 0, 0, 0, 0, 0, 0, 0]);
            }

            byte[] bytes = list.ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), (ulong)bytes.Length);
            byte[] result = [.. d[..at], .. bytes, .. d[(at + size)..]];
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(16)) + (ulong)(bytes.Length - size));
            return result;
        });
    }

    // ---- files built in memory: a stream of Windows Media Audio 2 whose media objects the analyzer follows ----

    private static byte[] WmaStream() => AsfBuilder.StreamProperties(1, 0x0161, 2, 44100, 16000, 743, 16, new byte[10]);

    // A packet of 40 bytes of the object number's data that states the object's size and where it starts.
    private static byte[] Fragment(int number, uint offset, uint size) => AsfBuilder.Packet(AsfBuilder.SmallPacket, 1, number, offset, size, (uint)(number * 10));

    private static byte[] Routed(params byte[][] packets) => AsfBuilder.Assemble(WmaStream(), AsfBuilder.SmallPacket, packets);

    private static byte[] Pcm() => AsfBuilder.StreamProperties(1, 1, 1, 8000, 16000, 2, 16, []);

    // Replicated data length 1 (a compressed payload) in a packet whose offset field has width 0.
    private static byte[] CompressedWithoutOffset()
    {
        byte[] p = new byte[AsfBuilder.SmallPacket];
        p[0] = 0x00;
        p[1] = 0x51;
        p[8] = 0x81;
        p[9] = 1;
        p[10] = 1;
        p[11] = 10;
        return AsfBuilder.Assemble(Pcm(), p.Length, [p]);
    }

    // A single payload whose packet states all the rest as padding.
    private static byte[] EmptyPayload()
    {
        byte[] p = AsfBuilder.Packet(AsfBuilder.SmallPacket, 1, 1, 0, 40, 0);
        // Padding length type 1 (a byte): the header shifts by one, and the padding takes all but the headers.
        byte[] shifted = new byte[p.Length];
        shifted[0] = 0x08;
        shifted[1] = 0x5D;
        shifted[2] = (byte)(p.Length - 24);
        Array.Copy(p, 2, shifted, 3, 21);
        return AsfBuilder.Assemble(Pcm(), p.Length, [shifted]);
    }

    // Multiple payloads whose payload flags give no width to the payload length.
    private static byte[] MultipleWithoutLengthType()
    {
        byte[] p = new byte[AsfBuilder.SmallPacket];
        p[0] = 0x01;
        p[1] = 0x5D;
        p[8] = 0x01;
        return AsfBuilder.Assemble(Pcm(), p.Length, [p]);
    }
}
