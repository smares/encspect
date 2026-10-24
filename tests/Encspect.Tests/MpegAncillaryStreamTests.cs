using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The ancillary data rules of docs/formats/mpeg-audio.md, "Ancillary data", on streams built in
// memory: each stream of a region has its own signature, counts and Original File Length block, and
// R5 leaves out the segments of truncated frames.
public class MpegAncillaryStreamTests
{
    private const int Mpeg1Data = 417 - 4 - 17;
    private const int Mpeg2Data = 208 - 4 - 9;

    [Fact]
    public void SignatureOfEachStreamIsCountedApart()
    {
        // Ten segments of 0xFF 0xFF in all, but neither stream has the eight that detection needs.
        byte[] file = [.. Mpeg2Frames(5, [0xFF, 0xFF]), .. Mpeg1Frames(5, [0xFF, 0xFF])];

        Report report = Analyze(file);

        Assert.Contains(report.Findings, f => f.Code == "mpeg.stream-changed");
        Assert.DoesNotContain(report.Findings, f => f.Code == "mpeg.mp3pro");
        Assert.DoesNotContain(Region(report).Children, c => c.Name == "Enhancement");
    }

    [Fact]
    public void Mp3ProDetectionOfTheFirstStreamSurvivesAnotherStream()
    {
        byte[] file = [.. Mpeg2Frames(12, [0xC5, 0x0A]), .. Mpeg1Frames(6, [0x00, 0x00])];

        Report report = Analyze(file);

        Node enhancement = Assert.Single(Region(report).Children, c => c.Name == "Enhancement");
        Assert.Equal("mp3PRO", enhancement.Value?.ToString());
        Assert.Equal(0, enhancement.Offset - Region(report).Offset);
        Assert.Equal(12 * 208, enhancement.Length);
        Assert.Equal($"{12 * Mpeg2Data - 12 * 100} bytes", Fixtures.Resolve(enhancement, "Ancillary Data").Single().Value?.ToString());
        Assert.Single(report.Findings, f => f.Code == "mpeg.mp3pro");
    }

    [Fact]
    public void EachStreamReadsItsOwnOriginalFileLengthBlock()
    {
        byte[] mp3ProBlock = [0xE0, 0xBA, .. new byte[12], 0x05, 0xDC, 0x00, 0x00, 0x2E, 0xE0, 0x00];
        byte[] plainBlock = [0xB4, 0x04, 0xB1, 0x00, 0x00, 0x2E, 0xE0, 0x00, 0x00, 0x00];
        byte[] file = [.. Mpeg2Frames(3, mp3ProBlock, 1), .. Mpeg1Frames(3, plainBlock, 1)];

        Report report = Analyze(file);

        Node[] blocks = [.. Region(report).Children.Where(c => c.Name == "Original File Length")];
        Assert.Equal(2, blocks.Length);
        Assert.Equal(["1500 samples", "1201 samples"], blocks.Select(b => Fixtures.Resolve(b, "Delay").Single().Value?.ToString()));
        Assert.Equal(["12000 samples", "12000 samples"], blocks.Select(b => b.Value?.ToString()));
        Assert.Equal(["44100 Hz", "44100 Hz"], blocks.Select(b => Fixtures.Resolve(b, "Sample Rate").Single().Value?.ToString()));
    }

    [Fact]
    public void SegmentOfFrameCutShortByTheFollowingFrameIsLeftOutOfTheTotal()
    {
        byte[] frame = Mpeg1Frame(100, [0x11, 0x22], 1);
        byte[] file = [.. frame, .. frame, .. frame[..200], .. frame, .. frame, .. frame];

        Assert.Equal(5 * (Mpeg1Data - 100), Ancillary(Analyze(file)));
    }

    [Fact]
    public void SegmentOfTruncatedLastFrameIsLeftOutOfTheTotal()
    {
        byte[] frame = Mpeg1Frame(100, [0x11, 0x22], 1);
        byte[] file = [.. frame, .. frame, .. frame, .. frame[..250]];

        Assert.Equal(3 * (Mpeg1Data - 100), Ancillary(Analyze(file)));
    }

    [Fact]
    public void BlockThatContinuesIntoTheNextFrameHasRangesOfItsOwnBytes()
    {
        // The first frame's main data ends three bytes before its end; the block's last four bytes
        // (a length of zero) are in the data of the VBRI frame that follows it.
        byte[] first = Mpeg1Frame(Mpeg1Data - 3, [0xB4, 0x04, 0x00], 0);
        byte[] vbri = new byte[417];
        new byte[] { 0xFF, 0xFB, 0x90, 0xC0 }.CopyTo(vbri, 0);
        "VBRI"u8.CopyTo(vbri.AsSpan(36));
        vbri[36 + 5] = 1;
        byte[] file = [.. first, .. vbri];

        Report report = Analyze(file);

        Node block = Assert.Single(Region(report).Children, c => c.Name == "Original File Length");
        Assert.Equal([new ByteRange(417 - 3, 3), new ByteRange(417 + 21, 4)], TestFiles.RangesOf(block));
        Assert.Equal(417 - 3, block.Offset);
        Assert.Equal(21 + 4 + 3, block.Length);
    }

    private static Report Analyze(byte[] data) => Analyzer.CreateDefault().Analyze("ancillary.mp3", new MemoryStream(data));

    private static Node Region(Report report) => Assert.Single(report.Root.Children, c => c.Name == "MPEG Audio");

    private static long Ancillary(Report report) =>
        long.Parse(Fixtures.Resolve(Region(report), "Statistics/Ancillary Data").Single().Value!.ToString()!.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] Mpeg1Frames(int count, byte[] segment, int firstOnly = 0) =>
        [.. Enumerable.Range(0, count).SelectMany(i => Mpeg1Frame(100, segment, firstOnly > 0 && i > 0 ? 2 : 1))];

    private static byte[] Mpeg2Frames(int count, byte[] segment, int firstOnly = 0) =>
        [.. Enumerable.Range(0, count).SelectMany(i => Mpeg2Frame(100, segment, firstOnly > 0 && i > 0 ? 2 : 1))];

    // Mono MPEG-1 Layer III at 128 kbps and 44100 Hz, 417 bytes: main_data_begin 0, main data of the
    // given length at the start of the frame data and the segment bytes after it.
    private static byte[] Mpeg1Frame(int main, byte[] segment, int kind) => Frame([0xFF, 0xFB, 0x90, 0xC0], 417, 17, 18, main, segment, kind);

    // Mono MPEG-2 Layer III at 64 kbps and 22050 Hz, 208 bytes.
    private static byte[] Mpeg2Frame(int main, byte[] segment, int kind) => Frame([0xFF, 0xF3, 0x80, 0xC0], 208, 9, 9, main, segment, kind);

    // kind 2 gives the frame the filler segment 0x00 0x00 instead of the segment.
    private static byte[] Frame(byte[] header, int length, int sideInfo, int lengthBit, int main, byte[] segment, int kind)
    {
        var frame = new byte[length];
        header.CopyTo(frame, 0);
        int bits = main * 8;
        for (int i = 0; i < 12; i++)
        {
            if ((bits >> (11 - i) & 1) != 0)
            {
                int bit = lengthBit + i;
                frame[4 + bit / 8] |= (byte)(0x80 >> bit % 8);
            }
        }

        (kind == 2 ? new byte[] { 0x00, 0x00 } : segment).CopyTo(frame, 4 + sideInfo + main);
        return frame;
    }
}
