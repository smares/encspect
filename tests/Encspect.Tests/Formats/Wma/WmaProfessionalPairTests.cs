namespace Encspect.Tests;

// The stereo pairs of WMA Professional.
public class WmaProfessionalPairTests
{
    // WMA Professional: a pair whose transform is on in no band is left/right, as one that is off.
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(20, true)]
    public void A_wma_professional_pair_is_mid_side_when_its_transform_is_on_in_a_band(int onBands, bool expected)
    {
        Assert.Equal(expected, Encspect.Formats.Wma.ProFrameReader.IsMidSide(onBands));
    }

    [Theory]
    [InlineData(2, true, 2, true)]
    [InlineData(2, false, 2, false)]
    [InlineData(1, true, 2, false)]
    [InlineData(3, true, 2, false)]
    [InlineData(2, true, 6, false)]
    public void Only_a_valid_group_of_both_channels_of_a_stereo_stream_is_a_pair(int size, bool isValid, int channels, bool expected)
    {
        Assert.Equal(expected, Encspect.Formats.Wma.ProFrameReader.IsPair(size, isValid, channels));
    }
}
