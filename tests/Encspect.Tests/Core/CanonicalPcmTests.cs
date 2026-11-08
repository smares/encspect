using Encspect.Core;

namespace Encspect.Tests;

public class CanonicalPcmTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 1)]
    [InlineData(12, 2)]
    [InlineData(16, 2)]
    [InlineData(20, 3)]
    [InlineData(24, 3)]
    [InlineData(32, 4)]
    public void ASampleTakesWholeBytes(int bits, int bytes) => Assert.Equal(bytes, CanonicalPcm.BytesPerSample(bits));

    [Fact]
    public void WritesChannelsInterleavedSignedLittleEndian()
    {
        int[] planar = [1, -2, 3, 0x1234, -0x1234, 0];
        var block = new PcmBlock(10, 3, 2, 3, planar);
        byte[] bytes = new byte[12];

        Assert.Equal(12, CanonicalPcm.Write(block, 16, bytes));

        Assert.Equal(new byte[] { 1, 0, 0x34, 0x12, 0xFE, 0xFF, 0xCC, 0xED, 3, 0, 0, 0 }, bytes);
        Assert.Equal(12, CanonicalPcm.ByteCount(block, 16));
    }

    [Fact]
    public void WritesThreeBytesAndFourBytesAndEightBitSamples()
    {
        var block = new PcmBlock(0, 2, 1, 2, new[] { -1, 0x123456 });

        byte[] three = new byte[6];
        CanonicalPcm.Write(block, 24, three);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x56, 0x34, 0x12 }, three);

        byte[] four = new byte[8];
        CanonicalPcm.Write(block, 32, four);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x56, 0x34, 0x12, 0 }, four);

        byte[] one = new byte[2];
        CanonicalPcm.Write(new PcmBlock(0, 2, 1, 2, new[] { -128, 127 }), 8, one);
        Assert.Equal(new byte[] { 0x80, 0x7F }, one);
    }

    [Fact]
    public void WritesFloatsAsTheirIeeeBytes()
    {
        var floats = new PcmBlock(0, 1, 1, 1, new[] { 1.0f });
        byte[] four = new byte[4];
        CanonicalPcm.Write(floats, 32, four);
        Assert.Equal(new byte[] { 0, 0, 0x80, 0x3F }, four);

        var doubles = new PcmBlock(0, 1, 1, 1, new[] { 1.0 });
        byte[] eight = new byte[8];
        CanonicalPcm.Write(doubles, 64, eight);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0xF0, 0x3F }, eight);
        Assert.Equal(SampleFormat.Float64, doubles.Format);
    }

    [Fact]
    public void ABlockNeedsSamplesToItsLastChannelAndARoomyDestination()
    {
        Assert.Throws<ArgumentException>(() => new PcmBlock(0, 3, 2, 3, new int[5]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmBlock(0, 3, 2, 2, new int[8]));
        int[] samples = new int[4];
        Assert.Throws<ArgumentException>(() => CanonicalPcm.Write(new PcmBlock(0, 2, 2, 2, samples), 16, new byte[7]));
        Assert.Equal(2, new PcmBlock(0, 2, 2, 2, samples).IntegerChannel(1).Length);
    }
}
