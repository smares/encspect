using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Tests.Core;

public class WaveFormatExTests
{
    private static byte[] Structure(int tag, int channels, int rate, int bits, byte[]? extra = null, int? cbSize = null)
    {
        byte[] data = new byte[18 + (extra?.Length ?? 0)];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)tag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)rate);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)bits);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), (ushort)(cbSize ?? extra?.Length ?? 0));
        extra?.CopyTo(data, 18);
        return data;
    }

    [Fact]
    public void ABufferShorterThanTheFixedFieldsIsNoStructure()
    {
        Assert.Null(WaveFormatEx.Read(new byte[15]));
        Assert.NotNull(WaveFormatEx.Read(new byte[16]));
    }

    [Fact]
    public void ACbSizeThatStatesMoreThanFollowsIsReadAsFarAsItGoes()
    {
        WaveFormatEx format = WaveFormatEx.Read(Structure(0x0161, 2, 44100, 16, new byte[6], cbSize: 10))!;

        Assert.True(format.IsDataCut);
        Assert.Equal(6, format.DataLength);
        Assert.Equal("wma", format.CodecId);
    }

    [Fact]
    public void TheExtensibleFormatIsRoutedByTheTagInItsSubFormat()
    {
        byte[] extension = new byte[22];
        BinaryPrimitives.WriteUInt16LittleEndian(extension, 24);
        Guid pcm = new("00000001-0000-0010-8000-00aa00389b71");
        pcm.TryWriteBytes(extension.AsSpan(6));
        WaveFormatEx format = WaveFormatEx.Read(Structure(0xFFFE, 2, 48000, 24, extension))!;

        Assert.Equal(0x0001, format.CodingTag);
        Assert.Null(format.CodecId);
        Assert.Equal(24, format.ValidBits);
        Assert.Equal(PcmEncoding.SignedInteger, WaveFormatRoutes.PcmEncodingOf(format.CodingTag, 24, 24));

        byte[] ac3 = new byte[22];
        new Guid("e06d802c-db46-11cf-b4d1-00805f6cbbea").TryWriteBytes(ac3.AsSpan(6));
        Assert.Equal("ac-3", WaveFormatEx.Read(Structure(0xFFFE, 2, 48000, 16, ac3))!.CodecId);
    }

    [Fact]
    public void AnExtensibleFormatWithoutItsExtensionNamesNoCoding()
    {
        WaveFormatEx format = WaveFormatEx.Read(Structure(0xFFFE, 2, 48000, 16, new byte[2]))!;

        Assert.Equal(0xFFFE, format.CodingTag);
        Assert.Null(format.CodecId);
    }

    [Theory]
    [InlineData(0x0050, "mpeg-audio")]
    [InlineData(0x0055, "mpeg-audio")]
    [InlineData(0x2000, "ac-3")]
    [InlineData(0x2001, "dts")]
    [InlineData(0x0008, "dts")]
    public void TheCodecTagsGoToTheParsersWavRoutesTo(int tag, string codec)
    {
        Assert.Equal(codec, WaveFormatRoutes.CodecId(tag));
        Assert.Null(WaveFormatRoutes.WindowsMediaCodecId(tag));
    }

    [Theory]
    [InlineData(0x0160, "wma")]
    [InlineData(0x0161, "wma")]
    [InlineData(0x0162, "wma-pro")]
    [InlineData(0x0163, "wma-lossless")]
    [InlineData(0x000A, "wma-voice")]
    public void TheWindowsMediaTagsGoToTheWmaParsers(int tag, string codec)
    {
        Assert.Equal(codec, WaveFormatRoutes.WindowsMediaCodecId(tag));
        Assert.Null(WaveFormatRoutes.CodecId(tag));
    }

    [Theory]
    [InlineData(0x0001, "pcm-samples")]
    [InlineData(0x0003, "pcm-samples")]
    [InlineData(0x0006, "pcm-samples")]
    [InlineData(0x0007, "pcm-samples")]
    [InlineData(0x0050, "mpeg-audio")]
    [InlineData(0x0055, "mpeg-audio")]
    [InlineData(0x0160, "wma")]
    [InlineData(0x0162, "wma-pro")]
    [InlineData(0x2000, "ac-3")]
    [InlineData(0x2001, "dts")]
    [InlineData(0x0002, null)]
    public void OneTableRoutesEveryTagForTheThreeReaders(int tag, string? codec)
    {
        Assert.Equal(codec, WaveFormatRoutes.RouteOf(tag));
    }
}
