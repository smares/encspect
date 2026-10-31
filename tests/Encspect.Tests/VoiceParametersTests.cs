using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// The configuration of Windows Media Audio Voice: the checks of the codec data and the widths that follow from the sample rate
// (docs/design/wma-voice-format.md, "Codec data").
public class VoiceParametersTests
{
    // The frame type table of the 8 kHz Windows files: 3 bits for each of the 17 frame types, the group of its code.
    private static readonly int[] Groups = [1, 1, 0, 0, 0, 1, 2, 2, 4, 4, 5, 2, 3, 3, 5, 3, 4];

    private static byte[] CodecData(int[] groups, uint flags = 0x00000501)
    {
        byte[] data = new byte[VoiceParameters.ExtraDataSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(VoiceParameters.FlagsOffset), flags);
        for (int type = 0; type < groups.Length; type++)
        {
            for (int bit = 0; bit < 3; bit++)
            {
                int position = (3 * type) + bit;
                if (((groups[type] >> (2 - bit)) & 1) != 0)
                {
                    data[VoiceParameters.TableOffset + (position >> 3)] |= (byte)(0x80 >> (position & 7));
                }
            }
        }

        return data;
    }

    private static VoiceParameters Create(int rate, int align = 300, int[]? groups = null, uint flags = 0x00000501) =>
        VoiceParameters.Create(new AudioFormat(0x000A, 1, rate, rate / 8, align, 16), CodecData(groups ?? Groups, flags));

    [Theory]
    [InlineData(7_990, true)]
    [InlineData(7_989, false)]
    [InlineData(8_000, true)]
    [InlineData(22_097, true)]
    [InlineData(22_098, false)]
    [InlineData(44, false)]
    [InlineData(43, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(226_719, false)]
    public void OnlyRatesFrom7990To22097AreAccepted(int rate, bool accepted)
    {
        VoiceParameters parameters = Create(rate);
        Assert.Equal(accepted, parameters.IsUsable);
        if (!accepted)
        {
            Assert.Equal("wma.voice-sample-rate", Assert.Single(parameters.Issues).Code);
        }
    }

    // The widths of the table in the description: the pitch range, the pitch width, the block pitch width and the delta width.
    [Theory]
    [InlineData(8_000, 20, 148, 7, 8, 5)]
    [InlineData(11_025, 27, 204, 8, 9, 5)]
    [InlineData(16_000, 40, 296, 8, 9, 6)]
    [InlineData(22_050, 55, 408, 9, 10, 6)]
    public void TheWidthsFollowFromTheSampleRate(int rate, int minPitch, int maxPitch, int pitchBits, int blockPitchBits, int deltaBits)
    {
        VoiceParameters p = Create(rate);
        Assert.Equal(minPitch, p.MinPitch);
        Assert.Equal(maxPitch, p.MaxPitch);
        Assert.Equal(pitchBits, p.PitchBits);
        Assert.Equal(blockPitchBits, p.BlockPitchBits);
        Assert.Equal(deltaBits, p.BlockDeltaPitchBits);
    }

    [Theory]
    [InlineData(150, 11)]
    [InlineData(187, 11)]
    [InlineData(300, 12)]
    [InlineData(600, 13)]
    [InlineData(2, 4)]
    [InlineData(1, 3)]
    public void TheSpilloverFieldFollowsFromTheBlockAlignment(int align, int bits) => Assert.Equal(bits, Create(8_000, align).SpilloverBits);

    [Fact]
    public void TheFlagsSetTheLspWidths()
    {
        VoiceParameters ten = Create(8_000);
        Assert.Equal((10, 24, 48), (ten.LspCount, ten.IndependentLspBits, ten.ResidualLspBits));
        VoiceParameters sixteen = Create(8_000, flags: 0x00001501);
        Assert.Equal((16, 34, 60), (sixteen.LspCount, sixteen.IndependentLspBits, sixteen.ResidualLspBits));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(11, true)]
    [InlineData(12, false)]
    [InlineData(15, false)]
    public void ADenoiseStrengthAbove11IsRejected(int strength, bool accepted)
    {
        VoiceParameters p = Create(8_000, flags: (uint)(strength << 2));
        Assert.Equal(accepted, p.IsUsable);
        Assert.Equal(accepted ? 0 : 1, p.Issues.Count(i => i.Code == "wma.voice-denoise-strength"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1 << 23)]
    public void ABlockAlignmentOutOfRangeIsRejected(int align)
    {
        VoiceParameters p = Create(8_000, align);
        Assert.Equal("wma.voice-block-alignment", Assert.Single(p.Issues).Code);
    }

    [Fact]
    public void ACodecDataSizeOtherThan46IsRejected()
    {
        VoiceParameters p = VoiceParameters.Create(new AudioFormat(0x000A, 1, 8_000, 1_000, 300, 16), new byte[45]);
        Assert.Equal("wma.codec-data-size", Assert.Single(p.Issues).Code);
    }

    // Four types in a group are accepted (the fourth takes the first slot of the next group); five are not.
    [Fact]
    public void AFifthTypeInAGroupIsRejectedAndAFourthTakesTheNextSlot()
    {
        Assert.Equal("wma.voice-frame-type-table", Assert.Single(Create(8_000, groups: [0, 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4]).Issues).Code);

        VoiceParameters four = Create(8_000, groups: [0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5]);
        Assert.True(four.IsUsable);
        Assert.Equal(0, four.OverfullGroup);
        Assert.Equal(-1, four.SlotOfType[3]);
        Assert.Equal(3, four.SlotOfType[4]);
    }

    [Fact]
    public void TheSlotsOfTheDefaultTableMatchTheCodeLengthsOfTheWindowsFile()
    {
        int[] lengths = [.. Create(8_000).SlotOfType.Select(slot => (int)VoiceTables.FrameTypeCodeLengths[slot])];
        Assert.Equal([4, 4, 2, 2, 2, 4, 6, 6, 10, 10, 12, 6, 8, 8, 12, 8, 10], lengths);
    }
}
