using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Formats.Ac3;
using Encspect.Formats.Mpeg;
using Encspect.Host;

namespace Encspect.Tests;

// How the sniffs rank a chain that starts after junk, where another format's frames can repeat header-like bytes at a fixed distance: the
// CRC-checked AC-3 chain against the ADTS and MPEG audio chains those bytes make.
public class Ac3SniffTests
{
    private static SniffContext Context(byte[] data) => new(new ByteReader(new MemoryStream(data)), "eac3");

    private static int Sniff(byte[] data) => new Ac3Parser().Sniff(Context(data));

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));

    private static byte[] Junk(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(0x80 | (i % 0x40)))];

    private static string FormatOf(byte[] data) => Analyzer.CreateDefault().Analyze("junk.eac3", new MemoryStream(data)).Root.Value?.ToString() ?? string.Empty;

    // An ADTS frame of the given length (MPEG-4 LC, 48 kHz, stereo, no CRC) with zero payload.
    private static byte[] AdtsFrame(int length) =>
    [
        0xFF, 0xF1, 0x4C, (byte)(0x80 | (length >> 11)), (byte)(length >> 3), (byte)(((length & 7) << 5) | 0x1F), 0xFC,
        .. new byte[length - 7],
    ];

    private static byte[] AdtsFrames(int count) => [.. Enumerable.Range(0, count).SelectMany(_ => AdtsFrame(20))];

    private static int SniffAdts(byte[] data) => new RawAacParser().Sniff(Context(data));

    [Fact]
    public void AChainAtTheStartTakes92()
    {
        Assert.Equal(92, Sniff(Fixture("ac3/junk-adts-in-frames.eac3")[100..]));
    }

    // Three passing frames are no stray run: they rank above a bare ADTS chain, still below MPEG audio.
    [Fact]
    public void AChainFurtherInWhoseCrcsHoldTakes55()
    {
        Assert.Equal(55, Sniff(Fixture("ac3/junk-adts-in-frames.eac3")));
        Assert.Equal(55, Sniff([.. Junk(30), .. Fixture("ac3/bsi-fields.ac3")]));
    }

    // Bit errors in the first frames of the real file (offsets 652 and 1164 are in its second and third frames) leave three passing
    // frames of the first 16; only too few passing frames rank 50.
    [Fact]
    public void AFrameWithADamagedCrcDoesNotLowerAChainThatHasThreePassingFramesOfTheFirst16()
    {
        byte[] data = Fixture("ac3/junk-adts-in-frames.eac3");
        data[100 + 20] ^= 0x01;
        data[100 + 512 + 20] ^= 0x01;
        Assert.Equal(55, Sniff(data));
    }

    [Fact]
    public void AChainWithTooFewPassingFramesTakes50()
    {
        byte[] frames = Fixture("ac3/junk-adts-in-frames.eac3");
        byte[] twoPassing = [.. frames[..(100 + (512 * 4))]];
        for (int frame = 0; frame < 2; frame++)
        {
            twoPassing[100 + (512 * frame) + 20] ^= 0x01;
        }

        Assert.Equal(50, Sniff(twoPassing));
        Assert.Equal(50, Sniff([.. Junk(30), .. Fixture("ac3/crc-damaged.ac3")[..(768 * 3)]]));
    }

    [Fact]
    public void AnAc3FrameNeedsCrc1AsWellAsCrc2()
    {
        byte[] data = [.. Junk(30), .. Fixture("ac3/bsi-fields.ac3")];
        for (int frame = 0; frame < 8; frame++)
        {
            data[30 + (768 * frame) + 40] ^= 0x10;
        }

        Assert.Equal(50, Sniff(data));
    }

    [Fact]
    public void AChainThatTheDataEndsInsideTakes50()
    {
        Assert.Equal(50, Sniff([.. Junk(30), .. Fixture("ac3/bsi-fields.ac3")[..((768 * 2) + 400)]]));
    }

    // The E-AC-3 frames hold bytes that chain as ADTS headers, but the file is E-AC-3.
    [Fact]
    public void AdtsLookingBytesInsideEac3FramesDoNotTakeTheFile()
    {
        Assert.Equal("AC-3", FormatOf(Fixture("ac3/junk-adts-in-frames.eac3")));
    }

    // The fixture keeps meaning what it says: its look-alike headers chain eight frames to the end of the data, which the ADTS sniff still takes
    // for a chain further in (50) and which only the CRC-checked E-AC-3 chain (55) outranks. The real file's frames chain three headers at
    // most, which the ADTS sniff now rejects (see the manifest of the real samples).
    [Fact]
    public void TheAdtsLookingBytesRankBelowTheEac3Chain()
    {
        Assert.Equal(50, SniffAdts(Fixture("ac3/junk-adts-in-frames.eac3")));
    }

    [Fact]
    public void AdtsChainsFurtherInNeedEightFramesOrTheEndOfTheData()
    {
        Assert.Equal(91, SniffAdts(AdtsFrames(3)));
        Assert.Equal(91, SniffAdts([.. AdtsFrames(3), .. Junk(40)]));
        Assert.Equal(50, SniffAdts([.. Junk(30), .. AdtsFrames(3)]));
        Assert.Equal(50, SniffAdts([.. Junk(30), .. AdtsFrames(8), .. Junk(40)]));
        Assert.Equal(0, SniffAdts([.. Junk(30), .. AdtsFrames(7), .. Junk(40)]));
        Assert.Equal(0, SniffAdts([.. Junk(30), .. AdtsFrames(3), .. Junk(40)]));
    }

    // The AC-3 frames of the fixture hold a protected MPEG-2.5 Layer I header whose CRC does not hold, so MPEG audio ranks the chain below AC-3's.
    [Fact]
    public void AMpegLookingChainWithBrokenCrcsInsideAc3FramesRanks30()
    {
        byte[] data = Fixture("ac3/junk-mpeg-in-frames.ac3");
        Assert.Equal(30, new MpegAudioParser().Sniff(Context(data)));
        Assert.Equal(55, Sniff(data));
        Assert.Equal("AC-3", FormatOf(data));
        Assert.Equal(30, new MpegAudioParser().Sniff(Context([.. Junk(352), .. Fixture("ac3/junk-mpeg-in-frames.ac3")[100..]])));
    }

    // A protected stream with junk in front stays MPEG audio, as does one whose first frames are damaged and a chain at the start.
    [Fact]
    public void AProtectedMpegChainWhoseCrcsHoldKeeps90()
    {
        byte[] mp3 = Fixture("mpeg/external-lame-crc.mp3");
        var parser = new MpegAudioParser();
        Assert.Equal(90, parser.Sniff(Context(mp3)));
        Assert.Equal(90, parser.Sniff(Context([.. Junk(30).Select(b => (byte)(b & 0x7F)), .. mp3])));
    }
}
