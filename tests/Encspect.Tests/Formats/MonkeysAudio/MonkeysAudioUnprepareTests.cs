using Encspect.Formats.MonkeysAudio.Decoding;

namespace Encspect.Tests;

// How the values of the predictors become samples, where the SDK's checks of a sample's range apply (Prepare.cpp, CPrepare::Unprepare).
public class MonkeysAudioUnprepareTests
{
    private static void Write(int channels, int bits, params int[] values)
    {
        var unprepare = new Unprepare(channels, bits);
        var samples = new int[channels];
        var bytes = new byte[channels * (bits / 8)];
        unprepare.Write(values, 0, 1, samples, bytes);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(6, 0)]
    [InlineData(6, 2)]
    [InlineData(6, 3)]
    [InlineData(6, 4)]
    [InlineData(8, 2)]
    [InlineData(8, 3)]
    public void ASampleOfSixteenBitsThatDoesNotFitIsAnErrorWhereTheSdkChecksIt(int channels, int channel)
    {
        // Channels 2 and 3 of a file of six channels or more are plain samples, and the SDK checks them like the samples of a pair.
        var values = new int[channels];
        Write(channels, 16, values);
        values[channel] = 40000;
        Assert.Throws<InvalidDataException>(() => Write(channels, 16, values));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    [InlineData(5, 4)]
    public void ASampleThatTheSdkDoesNotCheckIsWrittenAsItIs(int channels, int channel)
    {
        // The mid and side channels of a pair are checked after they are undone; a single channel, and the channels of a file of 3 or 5 channels, are not.
        var values = new int[channels];
        values[channel] = 40000;
        Write(channels, 16, values);
    }

    [Fact]
    public void TheCenterOfAFileOf24BitsIsNotChecked()
    {
        var values = new int[6];
        values[2] = 40000;
        Write(6, 24, values);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void TheSeventhChannelOfSevenChannelsIsAPlainSample(int bits)
    {
        // SDK 13.44 codes channel 6 of a seven-channel file (Prepare.cpp: nStartChannel is 6); before it, that channel was never stored.
        var values = new[] { 0, 0, 0, 0, 0, 0, 1234 };
        var samples = new int[7];
        var bytes = new byte[7 * (bits / 8)];
        new Unprepare(7, bits).Write(values, 0, 1, samples, bytes);
        Assert.Equal(1234, samples[6]);
        Assert.Equal(1234 & 0xFF, bytes[6 * (bits / 8)]);
        Assert.Equal(1234 >> 8, bytes[(6 * (bits / 8)) + 1]);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void TheChannelsOfAFileOf8BitsAndMoreThanTwoChannelsAreAllPlainSamples(int channels)
    {
        // Prepare.cpp pairs the channels of 8-bit audio only for two channels; here a value of the first channels is not combined with its neighbor.
        var values = new int[channels];
        values[0] = 40;
        values[1] = 10;
        var samples = new int[channels];
        var bytes = new byte[channels];
        new Unprepare(channels, 8).Write(values, 0, 1, samples, bytes);
        Assert.Equal(40, samples[0]);
        Assert.Equal(10, samples[1]);
        Assert.Equal(168, bytes[0]);
        Assert.Equal(138, bytes[1]);
    }
}
