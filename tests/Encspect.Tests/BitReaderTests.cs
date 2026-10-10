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
        Assert.Equal(0, reader.Position);
    }
}
