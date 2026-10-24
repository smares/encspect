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
}
