using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The padding of Opus packets (an Ogg Opus file built from pages).
public class OpusPaddingTests
{
    // RFC 6716, section 3.2.5: the padding length bytes code the padding, where 255 adds 254 and continues; the node holds the padding
    // they code, not the bytes they take. Packets of code 3 with one frame of 20 bytes and 8 and 300 bytes of padding.
    [Theory]
    [InlineData(8, "8 bytes", 1)]
    [InlineData(300, "300 bytes", 2)]
    [InlineData(1, "1 byte", 1)]
    public void TheOpusPaddingLengthIsThePaddingItCodes(int padding, string expected, int lengthBytes)
    {
        byte[] length = padding < 255 ? [(byte)padding] : [255, (byte)(padding - 254)];
        byte[] packet = [0xFB, 0x41, .. length, .. new byte[20], .. new byte[padding]];
        byte[] head = [.. "OpusHead"u8, 1, 1, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0];
        byte[] data = new OggBuilder().Page(6, 0, 2, head).Page(6, 0, 0, [.. "OpusTags"u8, .. OggBuilder.VorbisComment("Encspect")]).Page(6, 960, 4, packet).Bytes;

        Report report = Analyzer.CreateDefault().Analyze("padding.opus", new MemoryStream(data));

        Node field = Fixtures.Resolve(report.Root, "Ogg/Logical Stream/Audio Packets/Packet/Padding Length").Single();
        Assert.Equal(expected, field.Value?.ToString());
        Assert.Equal(lengthBytes, field.Length);
        Assert.Equal($"{padding} bytes".Replace("1 bytes", "1 byte"), Fixtures.Resolve(report.Root, "Ogg/Logical Stream/Audio Packets/Packet/Padding").Single().Value?.ToString());
        Assert.Equal("1 frame, CBR, padded", Fixtures.Resolve(report.Root, "Ogg/Logical Stream/Audio Packets/Packet/Frame Count Byte").Single().Value?.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Severity >= Severity.Warning);
    }
}
