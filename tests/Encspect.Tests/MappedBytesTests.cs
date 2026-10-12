using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Mapped bytes, node ranges and how the writers show them.
public class MappedBytesTests
{
    private static readonly byte[] Data = [10, 11, 12, 13, 14, 15, 16, 17];

    // Bytes 0-2 at 0x100, 3-5 at 0x200 and 6-7 at 0x203, which touch the second range.
    private static MappedBytes Split() =>
        new(Data, [new ByteRange(0x100, 3), new ByteRange(0x200, 3), new ByteRange(0x203, 2)]);

    [Fact]
    public void ContiguousBytesMapByTheirStart()
    {
        var bytes = new MappedBytes(Data, 0x40);

        Assert.Equal((0x40L, 0x48L), (bytes.FileStart, bytes.FileEnd));
        Assert.Equal(0x43, bytes.OffsetOf(3));
        Assert.Equal(0x48, bytes.OffsetOf(Data.Length));
        Assert.Equal(5, bytes.FileLength(1, 6));
        Assert.Equal([new ByteRange(0x42, 4)], bytes.FileRanges(2, 6));
        Assert.Equal((0x40L, 0x40L), (new MappedBytes(Array.Empty<byte>(), 0x40).FileStart, new MappedBytes(Array.Empty<byte>(), 0x40).FileEnd));
    }

    [Fact]
    public void BytesFromSeveralRangesMapPerSegment()
    {
        MappedBytes bytes = Split();

        Assert.Equal([0x100L, 0x101, 0x102, 0x200, 0x201, 0x202, 0x203, 0x204, 0x205], Enumerable.Range(0, 9).Select(bytes.OffsetOf));
        Assert.Equal([new ByteRange(0x100, 3), new ByteRange(0x200, 5)], bytes.FileRanges());
        Assert.Equal([new ByteRange(0x102, 1), new ByteRange(0x200, 2)], bytes.FileRanges(2, 5));
        Assert.Empty(bytes.FileRanges(4, 4));
        Assert.Equal(0x200 - 0x101, bytes.FileLength(1, 3));
        Assert.Equal((0x100L, 0x205L), (bytes.FileStart, bytes.FileEnd));
    }

    [Fact]
    public void SlicesKeepTheirFileOffsets()
    {
        MappedBytes slice = Split().Slice(2, 4);

        Assert.Equal(Data[2..6], slice.Span.ToArray());
        Assert.Equal([new ByteRange(0x102, 1), new ByteRange(0x200, 3)], slice.FileRanges());
        Assert.Equal(0x203, slice.FileEnd);
        Assert.Equal(0x205, Split().Slice(5).FileEnd);
        Assert.Equal(0x203, Split().Slice(6, 0).FileStart);
    }

    // ID3v2 unsynchronization drops a $00 after the last byte, which the bytes still cover.
    [Fact]
    public void AnEndAfterTheLastRangeCoversDroppedBytes()
    {
        var bytes = new MappedBytes(new byte[] { 0xFF }, [new ByteRange(0x10, 1)], fileEnd: 0x12);

        Assert.Equal(0x12, bytes.OffsetOf(1));
        Assert.Equal(2, bytes.FileLength(0, 1));
        Assert.Equal([new ByteRange(0x10, 1)], bytes.FileRanges());
    }

    [Fact]
    public void OpaqueBytesMapToTheirWholeSource()
    {
        MappedBytes bytes = MappedBytes.Opaque(Data, 0x30, 0x34);

        Assert.True(bytes.IsOpaque);
        Assert.Equal(0x30, bytes.OffsetOf(5));
        Assert.Equal(4, bytes.FileLength(2, 3));
        Assert.Equal([new ByteRange(0x30, 4)], bytes.FileRanges(2, 3));
        Assert.True(bytes.Slice(3).IsOpaque);
        Node node = Node.FromMapped("Field", bytes, 2, 3);
        Assert.Equal((0x30L, 4L, null), (node.Offset, node.Length, node.Ranges));
        Node bits = Node.FromBits("Bits", bytes, 3, 9);
        Assert.Equal((0x30L, 4L, (int?)null), (bits.Offset, bits.Length, bits.BitOffset));
    }

    [Fact]
    public void RangesThatDoNotFitTheDataAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new MappedBytes(Data, [new ByteRange(0, 5), new ByteRange(4, 3)]));
        Assert.Throws<ArgumentException>(() => new MappedBytes(Data, [new ByteRange(0, 5)]));
        Assert.Throws<ArgumentException>(() => new MappedBytes(Data, [new ByteRange(0, 8)], fileEnd: 7));
        Assert.Throws<ArgumentException>(() => new MappedBytes(Data, [new ByteRange(-1, 8)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Split().OffsetOf(9));
        Assert.Throws<ArgumentOutOfRangeException>(() => Split().Slice(4, 5));
    }

    [Fact]
    public void ReadPutsRangesTogether()
    {
        byte[] file = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        var reader = new ByteReader(new MemoryStream(file));

        MappedBytes packet = MappedBytes.Read(reader, [new ByteRange(10, 3), new ByteRange(40, 2)]);

        Assert.Equal([10, 11, 12, 40, 41], packet.Span.ToArray());
        Assert.Equal(40, packet.OffsetOf(3));
        Assert.Throws<EndOfStreamException>(() => MappedBytes.Read(reader, [new ByteRange(60, 5)]));
        Assert.Throws<EndOfStreamException>(() => MappedBytes.Read(reader, [new ByteRange(0, 40), new ByteRange(40, 24), new ByteRange(0, 40)]));
        Assert.Throws<EndOfStreamException>(() => MappedBytes.Read(reader.Slice(8, 8), [new ByteRange(4, 2)]));
    }

    [Fact]
    public void NodeRangesAreMergedAndMustSpanTheNode()
    {
        var node = new Node("Packet", 0x10, 0x30) { Ranges = [new ByteRange(0x10, 4), new ByteRange(0x14, 4), new ByteRange(0x30, 0), new ByteRange(0x38, 8)] };

        Assert.Equal([new ByteRange(0x10, 8), new ByteRange(0x38, 8)], node.Ranges);
        Assert.Null(new Node("Packet", 0x10, 8) { Ranges = [new ByteRange(0x10, 3), new ByteRange(0x13, 5)] }.Ranges);
        Assert.Throws<ArgumentException>(() => new Node("Packet", 0x10, 0x30) { Ranges = [new ByteRange(0x11, 4), new ByteRange(0x38, 8)] });
        Assert.Throws<ArgumentException>(() => new Node("Packet", 0x10, 0x30) { Ranges = [new ByteRange(0x10, 4), new ByteRange(0x30, 8)] });
        Assert.Throws<ArgumentException>(() => new Node("Packet", 0x10, 0x30) { Ranges = [new ByteRange(0x10, 8), new ByteRange(0x12, 8), new ByteRange(0x38, 8)] });
    }

    [Fact]
    public void NodesFromMappedBytesPointAtTheFile()
    {
        MappedBytes bytes = Split();

        Node whole = Node.FromMapped("Packet", bytes, 0, bytes.Length, kind: NodeKind.Frame);
        Assert.Equal((0x100L, 0x105L, NodeKind.Frame), (whole.Offset, whole.Length, whole.Kind));
        Assert.Equal([new ByteRange(0x100, 3), new ByteRange(0x200, 5)], whole.Ranges);

        Node inside = Node.FromMapped("Field", bytes, 3, 6);
        Assert.Equal((0x200L, 3L, null), (inside.Offset, inside.Length, inside.Ranges));
        Assert.Equal((0x203L, 0L), (Node.FromMapped("Empty", bytes, 6, 6).Offset, Node.FromMapped("Empty", bytes, 6, 6).Length));
    }

    [Fact]
    public void BitFieldsFromMappedBytesKeepTheirBitPositions()
    {
        MappedBytes bytes = Split();

        // Within one range the node is the one FromBits gives at that file offset.
        Node within = Node.FromBits("Within", bytes, 26, 37);
        Node direct = Node.FromBits("Within", 0x200, 2, 13);
        Assert.Equal((direct.Offset, direct.Length, direct.BitOffset, direct.BitLength, direct.Ranges), (within.Offset, within.Length, within.BitOffset, within.BitLength, within.Ranges));

        Node straddling = Node.FromBits("Straddling", bytes, 20, 30);
        Assert.Equal((0x102L, 0x200 - 0x102 + 1L, 4, 10), (straddling.Offset, straddling.Length, straddling.BitOffset, straddling.BitLength));
        Assert.Equal([new ByteRange(0x102, 1), new ByteRange(0x200, 1)], straddling.Ranges);

        Node bytesOnly = Node.FromBits("Bytes", bytes, 16, 32);
        Assert.Equal(((int?)null, (int?)null), (bytesOnly.BitOffset, bytesOnly.BitLength));
        Assert.Equal([new ByteRange(0x102, 1), new ByteRange(0x200, 1)], bytesOnly.Ranges);

        Assert.Throws<ArgumentOutOfRangeException>(() => Node.FromBits("Past", bytes, 60, 65));
    }

    [Fact]
    public void BitReadersOverMappedBytesMapPositions()
    {
        var reader = new BitReader(Split());

        Assert.Equal(0x101, reader.FileOffsetOf(15));
        Assert.Equal(0x200, reader.FileOffsetOf(24));
        Assert.Equal(0x205, reader.FileOffsetOf(reader.Length));
        Assert.Equal(Data[0], reader.Read(8));
        Assert.Throws<InvalidOperationException>(() => new BitReader(Data).FileOffsetOf(0));
    }

    [Fact]
    public void WritersShowRanges()
    {
        var root = new Node("File", 0, 0x40);
        Node packet = root.Add(new Node("Packet", 0x4, 0x30, "p") { Ranges = [new ByteRange(0x4, 2), new ByteRange(0x30, 4)] });
        packet.Add(Node.FromBits("Field", new MappedBytes(new byte[] { 1, 2, 3, 4, 5, 6 }, packet.Ranges!), 12, 20));
        var report = new Report("test.bin", root, [new Finding(Severity.Info, "test.code", "Here", packet) { Path = [0] }]);

        var text = new StringWriter();
        ReportTextWriter.Write(report, text, showOffsets: true);
        Assert.Contains("  Packet: p [0x4, 48 bytes in 0x4 2 bytes, 0x30 4 bytes]", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("    Field [0x5+4, 8 bits in 0x5 1 bytes, 0x30 1 bytes]", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("[0x4, 48 bytes in 0x4 2 bytes, 0x30 4 bytes]", text.ToString().Split('\n').Single(l => l.Contains("test.code", StringComparison.Ordinal)), StringComparison.Ordinal);

        string json = Fixtures.ToJson(report);
        Assert.Contains("\"length\": 48,\n          \"ranges\": [\n            {\n              \"offset\": 4,\n              \"length\": 2\n            },", json, StringComparison.Ordinal);
        Assert.Contains("\"bitLength\": 8,\n              \"ranges\": [", json, StringComparison.Ordinal);
    }
}
