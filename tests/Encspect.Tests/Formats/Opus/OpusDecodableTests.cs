using Encspect.Formats.Opus;

namespace Encspect.Tests.Formats.Opus;

// Which family 3 identification headers the decoder takes (OpusDecodable.Check): RFC 8486 section 3.2 lets the decoded channel count N + M differ from the output channel count
// C, so only a matrix of the wrong size, a count libopus's projection decoder also refuses (no streams, more coupled than streams, more than 255 decoded channels) or a matrix
// over 65,004 bytes is refused.
public class OpusDecodableTests
{
    private static OpusHead Family3(int channels, int streams, int coupled, int matrixBytes)
    {
        byte[] head = [.. "OpusHead"u8, 1, (byte)channels, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 3, (byte)streams, (byte)coupled, .. new byte[matrixBytes]];
        return OpusHead.Read(head)!;
    }

    [Theory]
    [InlineData(4, 2, 2, true)]
    [InlineData(9, 2, 2, true)]
    [InlineData(2, 2, 2, true)]
    [InlineData(1, 1, 0, true)]
    [InlineData(171, 171, 0, true)]
    [InlineData(255, 2, 0, true)]
    [InlineData(2, 255, 0, true)]
    public void AMatrixOfAnyRowsAndColumnsIsDecodedWhateverTheChannelCount(int channels, int streams, int coupled, bool expected)
    {
        Assert.Equal(expected, OpusDecodable.Check(Family3(channels, streams, coupled, 2 * channels * (streams + coupled))));
    }

    [Theory]
    [InlineData(4, 2, 2, 31)]
    [InlineData(4, 2, 2, 0)]
    [InlineData(9, 2, 2, 71)]
    public void AMatrixShorterThanItsDimensionsIsNotDecoded(int channels, int streams, int coupled, int matrixBytes)
    {
        Assert.False(OpusDecodable.Check(Family3(channels, streams, coupled, matrixBytes)));
    }

    [Fact]
    public void MoreChannelsClaimedThanTheMatrixHoldsAreNotDecoded()
    {
        OpusHead head = Family3(4, 2, 2, 2 * 2 * (2 + 2));
        Assert.False(OpusDecodable.Check(head with { Channels = 5 }));
        Assert.False(OpusDecodable.Check(head with { Channels = 3 }));
    }

    [Theory]
    [InlineData(4, 0, 0)]
    [InlineData(4, 1, 2)]
    [InlineData(4, 130, 130)]
    public void ACountLibopusRefusesIsNotDecoded(int channels, int streams, int coupled)
    {
        Assert.False(OpusDecodable.Check(Family3(channels, streams, coupled, 2 * channels * (streams + coupled))));
    }

    [Fact]
    public void AMatrixOver65004BytesIsNotDecoded()
    {
        Assert.False(OpusDecodable.Check(Family3(200, 170, 0, 2 * 200 * 170)));
        Assert.True(OpusDecodable.Check(Family3(190, 171, 0, 2 * 190 * 171)));
    }

    [Fact]
    public void ZeroChannelsAreNotDecoded()
    {
        Assert.False(OpusDecodable.Check(Family3(0, 1, 0, 0)));
    }
}
