using Encspect.Core;
using Encspect.Formats.Flac;
using Xunit;

namespace Encspect.Tests;

public class FlacFieldMapTests
{
    private static readonly NodeValue Value = NodeValue.Integer(1);

    [Fact]
    public void A_field_of_bytes_in_the_file_sits_at_its_position()
    {
        FieldMap map = FieldMap.At(100);
        Node bytes = map.Bytes("A", 3, 2, Value, "d");
        Assert.Equal((103L, 2L), (bytes.Offset, bytes.Length));
        Node bits = map.Bits("B", 12, 20, Value, "d");
        Assert.Equal(101L, bits.Offset);
        Assert.Equal(4, bits.BitOffset);
    }

    [Fact]
    public void A_field_of_bytes_a_container_changed_covers_the_whole_source_range_with_no_bit_position()
    {
        FieldMap map = FieldMap.Of(MappedBytes.Opaque(new byte[500], 40, 90));
        Node bytes = map.Bytes("A", 300, 20, Value, "d");
        Assert.Equal((40L, 50L), (bytes.Offset, bytes.Length));
        Node bits = map.Bits("B", 3000, 3010, Value, "d");
        Assert.Equal((40L, 50L), (bits.Offset, bits.Length));
        Assert.Null(bits.BitOffset);
        Assert.Equal(map.Bytes("C", 0, 1, Value, "d").Length, FieldMap.Whole(40, 50).Bits("D", 0, 1, Value, "d").Length);
    }

    [Fact]
    public void Fields_of_bytes_that_touch_in_one_range_are_placed_from_its_start()
    {
        FieldMap map = FieldMap.Of(new MappedBytes(new byte[10], 7));
        Node bytes = map.Bytes("A", 2, 3, Value, "d");
        Assert.Equal((9L, 3L), (bytes.Offset, bytes.Length));
    }
}
