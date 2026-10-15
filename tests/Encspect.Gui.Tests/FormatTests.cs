using Encspect.Core;
using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0.261, "0:00.3")]
    [InlineData(59.94, "0:59.9")]
    [InlineData(59.96, "1:00")]
    [InlineData(119.97, "2:00")]
    [InlineData(205.4, "3:25")]
    [InlineData(3723, "1:02:03")]
    public void Durations(double seconds, string expected) => Assert.Equal(expected, Format.Duration(seconds));

    [Fact]
    public void Bitrates_rates_encoders_and_locations()
    {
        Assert.Equal("139 kbps VBR", Format.Bitrate(138.97, true));
        Assert.Equal("128 kbps CBR", Format.Bitrate(128, false));
        Assert.Equal("44.1 kHz", Format.SampleRate(44100));
        Assert.Equal("11.025 kHz", Format.SampleRate(11025));
        Assert.Equal("LAME 3.100 (high)", Format.Encoder("LAME 3.100", Confidence.High));
        Assert.Equal("0x1A, 4 bytes", Format.Location(new Node("x", 0x1A, 4)));
        Assert.Equal("0x1+3, 2 bits", Format.Location(Node.FromBits("x", 1, 3, 5)));
        Assert.Equal("0x1+3, 2 bits, LSB first", Format.Location(Node.FromBits("x", 1, 3, 5, order: BitOrder.LeastSignificantFirst)));
        Assert.Equal("0x1A, 40 bytes in 0x1A 2 bytes, 0x40 2 bytes", Format.Location(new Node("x", 0x1A, 40) { Ranges = [new ByteRange(0x1A, 2), new ByteRange(0x40, 2)] }));
        Assert.Equal("0x0, 1 byte", Format.Location(new Node("x", 0, 1)));
    }

    [Fact]
    public void A_stream_line_names_object_audio_and_its_object_count()
    {
        var stream = new StreamInfo { Nodes = [new Node("x", 0, 1)], Codec = "E-AC-3", SampleRate = 48000, Channels = 8, HasObjectAudio = true, JocObjectCount = 15 };
        Assert.Contains("object audio (15 objects)", new StreamItem(stream).Text, StringComparison.Ordinal);
        Assert.Contains("object audio", new StreamItem(stream with { JocObjectCount = null }).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("object audio", new StreamItem(stream with { HasObjectAudio = false, JocObjectCount = null }).Text, StringComparison.Ordinal);
    }
}
