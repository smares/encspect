using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Streams behind more than a megabyte of leading bytes, built in memory rather than committed as
// fixtures. The search for the first frame looks 1 MiB past any leading zero bytes.
public class MpegLeadingDataTests
{
    private const int MiB = 1 << 20;

    [Fact]
    public void StreamAfterMoreThanOneMebibyteOfZerosIsFound()
    {
        Report report = Analyze([.. new byte[MiB + 102_400], .. Frames(50)]);

        Node region = Assert.Single(report.Root.Children, c => c.Name == "MPEG Audio");
        Assert.Equal("50 frames", region.Value?.ToString());
        Finding junk = Assert.Single(report.Findings, f => f.Code == "mpeg.leading-junk");
        Assert.Contains("(all zero bytes)", junk.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroLabelCoversTheWholeGap()
    {
        byte[] data = [.. new byte[MiB], 1, .. new byte[1000], .. Frames(20)];

        Finding junk = Assert.Single(Analyze(data).Findings, f => f.Code == "mpeg.leading-junk");
        Assert.DoesNotContain("zero", junk.Message, StringComparison.Ordinal);
    }

    private static Report Analyze(byte[] data) => Analyzer.CreateDefault().Analyze("leading.mp3", new MemoryStream(data));

    // MPEG-1 Layer III frames at 32 kbps and 32000 Hz, 144 bytes each, with zero side information.
    private static byte[] Frames(int count)
    {
        var frame = new byte[144];
        new byte[] { 0xFF, 0xFB, 0x18, 0xC0 }.CopyTo(frame, 0);
        return [.. Enumerable.Repeat(frame, count).SelectMany(f => f)];
    }
}
