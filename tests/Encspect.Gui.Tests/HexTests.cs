using Encspect.Core;
using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Tests;

public class HexTests
{
    [Fact]
    public void A_node_of_whole_bytes_marks_every_bit_of_them()
    {
        HexHighlight highlight = HexHighlight.For(new Node("Size", 0x10, 4))!;

        Assert.False(highlight.Contains(0x0F));
        Assert.True(highlight.Contains(0x10));
        Assert.True(highlight.Contains(0x13));
        Assert.False(highlight.Contains(0x14));
        Assert.Equal((0, 8), highlight.BitsOf(0x12));
        Assert.Null(highlight.BitsOf(0x14));
        Assert.Null(highlight.BitLength);
    }

    [Fact]
    public void A_bit_field_marks_only_its_bits()
    {
        // Two bits from the fourth bit of byte 0x1A, as an MPEG header's version field.
        HexHighlight version = HexHighlight.For(Node.FromBits("Version", 0x1A, 3, 5))!;
        Assert.Equal((3, 5), version.BitsOf(0x1A));
        Assert.Null(version.BitsOf(0x1B));

        // Twelve bits from bit 6: the last two of the first byte, all of the second, two of the third.
        HexHighlight wide = HexHighlight.For(Node.FromBits("Field", 0x100, 6, 18))!;
        Assert.Equal(3, wide.Ranges[0].Length);
        Assert.Equal((6, 8), wide.BitsOf(0x100));
        Assert.Equal((0, 8), wide.BitsOf(0x101));
        Assert.Equal((0, 2), wide.BitsOf(0x102));
    }

    [Fact]
    public void A_bit_field_counted_from_the_least_significant_bit_marks_its_bits_mirrored()
    {
        // Vorbis packs fields from the least significant bit: four bits from bit 2 take the bits
        // 0x3C of the byte, which counted from the most significant bit are 2 to 6.
        HexHighlight nibble = HexHighlight.For(Node.FromBits("Blocksize", 0x20, 2, 6, order: BitOrder.LeastSignificantFirst))!;
        Assert.Equal((2, 6), nibble.BitsOf(0x20));

        // Ten bits from bit 5: the top three bits of the first byte, then the low seven of the next.
        HexHighlight wide = HexHighlight.For(Node.FromBits("Field", 0x20, 5, 15, order: BitOrder.LeastSignificantFirst))!;
        Assert.Equal((0, 3), wide.BitsOf(0x20));
        Assert.Equal((1, 8), wide.BitsOf(0x21));
        Assert.Null(wide.BitsOf(0x22));
    }

    [Fact]
    public void A_node_with_ranges_marks_only_the_bytes_it_holds()
    {
        var packet = new Node("Packet", 0x1A, 40) { Ranges = [new ByteRange(0x1A, 2), new ByteRange(0x40, 2)] };
        HexHighlight highlight = HexHighlight.For(packet)!;

        Assert.True(highlight.Contains(0x1A));
        Assert.True(highlight.Contains(0x1B));
        Assert.False(highlight.Contains(0x1C));
        Assert.False(highlight.Contains(0x3F));
        Assert.True(highlight.Contains(0x40));
        Assert.True(highlight.Contains(0x41));
        Assert.False(highlight.Contains(0x42));
        Assert.Equal(0x42, highlight.End);
    }

    [Fact]
    public void A_bit_field_split_by_a_gap_continues_its_bits_after_it()
    {
        // Bytes 0 and 1 of the data come from 0x10 and 0x11, bytes 2 and 3 from 0x20 and 0x21,
        // as a field that a page header splits.
        var bytes = new MappedBytes(new byte[4], [new ByteRange(0x10, 2), new ByteRange(0x20, 2)]);
        Node field = Node.FromBits("Field", bytes, 12, 20);
        HexHighlight highlight = HexHighlight.For(field)!;

        Assert.Equal([new ByteRange(0x11, 1), new ByteRange(0x20, 1)], highlight.Ranges);
        Assert.Equal((4, 8), highlight.BitsOf(0x11));
        Assert.Equal((0, 4), highlight.BitsOf(0x20));
        Assert.Null(highlight.BitsOf(0x21));
    }

    [Fact]
    public void The_bit_strip_marks_the_bits_of_the_selected_field()
    {
        using var document = new HexDocument(new MemoryStream([0x00, 0xFF, 0xFB, 0x90]));

        BitStrip strip = BitStrip.For(HexHighlight.For(Node.FromBits("Version", 2, 3, 5)), document)!;

        Assert.Equal(2, strip.Offset);
        Assert.Equal([0xFB], strip.Bytes);
        Assert.Equal([false, false, false, true, true, false, false, false], strip.Marked);
        Assert.False(strip.IsCut);
    }

    [Fact]
    public void The_document_reads_pages_on_demand_and_stops_at_the_end()
    {
        byte[] data = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        using var document = new HexDocument(new MemoryStream(data));
        var buffer = new byte[32];

        Assert.Equal(32, document.Read(65_530, buffer));
        Assert.Equal(data.AsSpan(65_530, 32).ToArray(), buffer);
        Assert.Equal(10, document.Read(199_990, buffer));
        Assert.Equal(0, document.Read(200_000, buffer));
    }

    // On Windows a file cannot be replaced while any handle to it is open, so a document that held
    // one would keep a tagger from saving the file the detail window shows.
    [Fact]
    public void A_file_shown_can_be_replaced_and_its_document_then_reads_nothing_more()
    {
        using var folder = new TempFolder();
        byte[] data = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        string path = folder.Write("shown.bin", data);
        using HexDocument document = HexDocument.Open(path, data.Length, File.GetLastWriteTimeUtc(path));
        int changes = 0;
        document.Changed += (_, _) => changes++;
        var buffer = new byte[16];

        Assert.Equal(16, document.Read(10, buffer));
        Assert.Equal(data.AsSpan(10, 16).ToArray(), buffer);

        string replacement = folder.Write("replacement.bin", new byte[1000]);
        File.Move(replacement, path, overwrite: true);

        Assert.Equal(16, document.Read(10, buffer));
        Assert.Equal(data.AsSpan(10, 16).ToArray(), buffer);
        Assert.Equal(0, document.Read(150_000, buffer));
        Assert.Equal(0, document.Read(140_000, buffer));
        Assert.True(document.HasChanged);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void A_file_rewritten_in_place_at_the_same_length_counts_as_changed()
    {
        using var folder = new TempFolder();
        byte[] data = new byte[100_000];
        string path = folder.Write("shown.bin", data);
        DateTime analyzed = File.GetLastWriteTimeUtc(path);
        using HexDocument document = HexDocument.Open(path, data.Length, analyzed);

        File.WriteAllBytes(path, Enumerable.Repeat((byte)0xAA, data.Length).ToArray());
        File.SetLastWriteTimeUtc(path, analyzed.AddSeconds(5));

        Assert.Equal(0, document.Read(0, new byte[16]));
        Assert.True(document.HasChanged);
    }
}
