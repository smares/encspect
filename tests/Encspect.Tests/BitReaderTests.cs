using Encspect.Core;

namespace Encspect.Tests;

public class BitReaderTests
{
    [Fact]
    public void ReadsFieldsMostSignificantBitFirstAcrossBytes()
    {
        var reader = new BitReader([0b1011_0011, 0b1100_0001]);

        Assert.Equal(0b101, reader.Read(3));
        Assert.Equal(0b1_0011_110, reader.Read(8));
        Assert.Equal(0b0_0001, reader.Read(5));
        Assert.Equal(16, reader.Position);
        Assert.False(reader.IsOverrun);
    }

    [Fact]
    public void ReadingPastTheEndYieldsZeroBitsAndReportsOverrun()
    {
        var reader = new BitReader([0xFF]);

        Assert.Equal(0b1111_1111_0000, reader.Read(12));
        Assert.True(reader.IsOverrun);
    }

    [Fact]
    public void ReadsNothingForZeroBits()
    {
        var reader = new BitReader([0xFF]);

        Assert.Equal(0, reader.Read(0));
        Assert.Equal(0L, reader.ReadLong(0));
        Assert.Equal(0, reader.Position);
    }

    [Fact]
    public void ReadsAnEmptyBufferAsOverrun()
    {
        var reader = new BitReader(ReadOnlyMemory<byte>.Empty);

        Assert.Equal(0L, reader.ReadLong(64));
        Assert.True(reader.IsOverrun);
        Assert.Equal(64, reader.Position);
    }

    [Fact]
    public void ReadsSixtyFourBitsAtAnyBitPosition()
    {
        var reader = new BitReader([0b1000_0000, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0b1000_0000]);

        Assert.Equal(1, reader.Read(1));
        Assert.Equal(0L, reader.ReadLong(7));
        Assert.Equal(0x123456789ABCDEF0L, reader.ReadLong(64));
        Assert.Equal(1L, reader.ReadLong(1));
        Assert.False(reader.IsOverrun);
    }

    [Fact]
    public void SixtyFourOneBitsComeBackAsTheirBitPattern()
    {
        var reader = new BitReader([0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        reader.Read(1);

        Assert.Equal(-1L, reader.ReadLong(64));
    }

    [Fact]
    public void ReadsMoreThanThirtyOneBitsWithReadLong()
    {
        var reader = new BitReader([0xFF, 0xFF, 0xFF, 0xFF, 0x80]);

        Assert.Equal(0x1_FFFF_FFFFL, reader.ReadLong(33));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(32));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadLong(65));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadLong(-1));
    }

    [Fact]
    public void LongReadPastTheEndFillsWithZeroBits()
    {
        var reader = new BitReader([0xAB, 0xCD]);

        reader.Read(4);

        Assert.Equal(0xBCD0_0000_0000L, reader.ReadLong(48));
        Assert.True(reader.IsOverrun);
        Assert.Equal(52, reader.Position);
    }

    // Vorbis I specification, section 2.1.6: the bytes of its example, which packs 12 in 4 bits, -1
    // in 3 (read back unsigned as 7), 17 in 7 and 6969 in 13.
    [Fact]
    public void ReadsLeastSignificantBitFirstAsVorbisPacksIt()
    {
        var reader = new BitReader([0b1111_1100, 0b0100_1000, 0b1100_1110, 0b0000_0110], BitOrder.LeastSignificantFirst);

        Assert.Equal(12, reader.Read(4));
        Assert.Equal(7, reader.Read(3));
        Assert.Equal(17, reader.Read(7));
        Assert.Equal(6969, reader.Read(13));
        Assert.Equal(27, reader.Position);
    }

    [Fact]
    public void ReadsSixtyFourBitsLeastSignificantBitFirst()
    {
        var reader = new BitReader([0xF0, 0xDE, 0xBC, 0x9A, 0x78, 0x56, 0x34, 0x12, 0x01], BitOrder.LeastSignificantFirst);

        Assert.Equal(0x123456789ABCDEF0L, reader.ReadLong(64));
        Assert.Equal(1L, reader.ReadLong(1));
        Assert.Equal(0L, reader.ReadLong(16));
        Assert.True(reader.IsOverrun);
    }

    [Fact]
    public void ReadsFromAPartOfABuffer()
    {
        byte[] data = [0x00, 0xA5, 0xFF];

        var reader = new BitReader(data.AsMemory(1, 1));

        Assert.Equal(8, reader.Length);
        Assert.Equal(0xA5, reader.Read(8));
        Assert.Equal(0, reader.Read(8));
        Assert.True(reader.IsOverrun);
    }
}
