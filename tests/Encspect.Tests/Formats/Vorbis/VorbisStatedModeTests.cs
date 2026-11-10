using Encspect.Formats.Vorbis;

namespace Encspect.Tests;

// The bitrate mode the identification header's three hints state (Vorbis I, section 4.2.2).
public class VorbisStatedModeTests
{
    private static bool? Stated(int maximum, int nominal, int minimum) => new Identification(0, 2, 44100, maximum, nominal, minimum, 8, 11, true).StatedVariable;

    [Theory]
    [InlineData(128000, 128000, 128000, false)]
    [InlineData(160000, 128000, 96000, true)]
    [InlineData(160000, 128000, 0, true)]
    [InlineData(-1, 128000, 96000, true)]
    [InlineData(0, 128000, 0, null)]
    [InlineData(-1, -1, -1, null)]
    [InlineData(128000, 128000, 0, true)]
    public void AMaximumAndOrAMinimumStatesVariableAndAnEqualTripleConstant(int maximum, int nominal, int minimum, bool? expected)
    {
        Assert.Equal(expected, Stated(maximum, nominal, minimum));
    }
}
