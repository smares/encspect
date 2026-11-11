namespace Encspect.Tests;

public class PoolElementSizesTests
{
    [Fact]
    public void A_colliding_ID_is_sized_by_the_largest_element_size()
    {
        var sizes = new PoolElementSizes();
        sizes.Add(7, 4);
        sizes.Add(7, 16);
        sizes.Add(7, 8);

        long expected = ((3L * IntPtr.Size) + (16L * 128) + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
        Assert.Equal(expected, sizes.Bytes(7, 128));
    }

    [Fact]
    public void An_unregistered_ID_is_not_sized()
    {
        var sizes = new PoolElementSizes();
        sizes.Add(7, 4);

        Assert.Null(sizes.Bytes(8, 128));
    }
}
