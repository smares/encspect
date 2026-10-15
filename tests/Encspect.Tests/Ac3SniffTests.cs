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

    private static int Sniff(byte[] data) => new Ac3Parser().Sniff(Context(data)).Class;

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

    private static int SniffAdts(byte[] data) => new RawAacParser().Sniff(Context(data)).Class;

    private static SniffResult Evidence(byte[] data) => new Ac3Parser().Sniff(Context(data));

    [Fact]
    public void AChainAtTheStartIsClass92()
    {
        SniffResult result = Evidence(Fixture("ac3/junk-adts-in-frames.eac3")[100..]);
        Assert.Equal(92, result.Class);
        Assert.Equal(0, result.Offset);
        Assert.Equal(8, result.Frames);
    }

    // A chain further in is class 50 whatever its CRCs; they count as verified frames, and where the chain starts is the evidence the host
    // ranks it by.
    [Fact]
    public void AChainFurtherInIsClass50WithItsStartAndItsVerifiedFrames()
    {
        SniffResult result = Evidence(Fixture("ac3/junk-adts-in-frames.eac3"));
        Assert.Equal(50, result.Class);
        Assert.Equal(100, result.Offset);
        Assert.Equal(8, result.Frames);
        Assert.Equal(result.Frames, result.VerifiedFrames);

        SniffResult junked = Evidence([.. Junk(30), .. Fixture("ac3/bsi-fields.ac3")]);
        Assert.Equal((50, 30L), (junked.Class, junked.Offset));
        Assert.True(junked.VerifiedFrames >= 3);
    }

    // Bit errors in the frames of the real file (offsets 652 and 1164 are in its second and third frames) lower the verified count by one each
    // and leave the chain.
    [Fact]
    public void AFrameWithADamagedCrcIsNotCountedAsVerified()
    {
        byte[] data = Fixture("ac3/junk-adts-in-frames.eac3");
        int verified = Evidence(data).VerifiedFrames;
        data[100 + 20] ^= 0x01;
        data[100 + 512 + 20] ^= 0x01;
        SniffResult result = Evidence(data);
        Assert.Equal(50, result.Class);
        Assert.Equal(verified - 2, result.VerifiedFrames);
        Assert.Equal(8, result.Frames);
    }

    [Fact]
    public void ADamagedChainCountsOnlyTheFramesThatPass()
    {
        byte[] frames = Fixture("ac3/junk-adts-in-frames.eac3");
        byte[] damaged = [.. frames[..(100 + (512 * 4))]];
        for (int frame = 0; frame < 2; frame++)
        {
            damaged[100 + (512 * frame) + 20] ^= 0x01;
        }

        SniffResult result = Evidence(damaged);
        Assert.Equal((50, 4, 2), (result.Class, result.Frames, result.VerifiedFrames));
        SniffResult damagedFile = Evidence([.. Junk(30), .. Fixture("ac3/crc-damaged.ac3")[..(768 * 3)]]);
        Assert.Equal((3, 2), (damagedFile.Frames, damagedFile.VerifiedFrames));
    }

    [Fact]
    public void AnAc3FrameNeedsCrc1AsWellAsCrc2()
    {
        byte[] data = [.. Junk(30), .. Fixture("ac3/bsi-fields.ac3")];
        int verified = Evidence(data).VerifiedFrames;
        for (int frame = 0; frame < 8; frame++)
        {
            data[30 + (768 * frame) + 40] ^= 0x10;
        }

        Assert.Equal(verified - 8, Evidence(data).VerifiedFrames);
    }

    [Fact]
    public void AChainThatTheDataEndsInsideCountsOnlyItsWholeFrames()
    {
        SniffResult result = Evidence([.. Junk(30), .. Fixture("ac3/bsi-fields.ac3")[..((768 * 2) + 400)]]);
        Assert.Equal((50, 2, 2), (result.Class, result.Frames, result.VerifiedFrames));
    }

    // Cut off the start of the file, the first frame is partial and the ADTS look-alike in it (at its offset 300) starts before the first
    // whole frame. The two chains overlap, and the one whose frames pass their CRCs is the file's, wherever each starts.
    [Theory]
    [InlineData(101)]
    [InlineData(110)]
    [InlineData(128)]
    public void ACutFileStaysAc3BecauseItsFramesPassTheirCrcsNotBecauseItStartsFirst(int cut)
    {
        byte[] data = Fixture("ac3/junk-adts-in-frames.eac3")[cut..];
        SniffResult ac3 = Evidence(data);
        SniffResult adts = new RawAacParser().Sniff(Context(data));

        Assert.Equal((50, 50), (ac3.Class, adts.Class));
        Assert.True(adts.Offset < ac3.Offset);
        Assert.True(ac3.Overlaps(adts));
        Assert.Equal((7, 0), (ac3.VerifiedFrames, adts.VerifiedFrames));
        Assert.True(SniffResult.Compare(ac3, adts) < 0);
        Assert.Equal("AC-3", FormatOf(data));
    }

    // The E-AC-3 frames hold bytes that chain as ADTS headers, but the file is E-AC-3.
    [Fact]
    public void AdtsLookingBytesInsideEac3FramesDoNotTakeTheFile()
    {
        Assert.Equal("AC-3", FormatOf(Fixture("ac3/junk-adts-in-frames.eac3")));
    }

    // The fixture keeps meaning what it says: its look-alike headers chain eight frames to the end of the data, which the ADTS sniff still takes
    // for a chain further in (class 50, as the AC-3 chain is). The host ranks the AC-3 chain first because it starts earlier and its frames pass
    // their CRCs. The real file's frames chain three headers at most, which the ADTS sniff now rejects (see the manifest of the real samples).
    [Fact]
    public void TheAdtsLookingBytesRankBelowTheEac3ChainByWhereTheyStart()
    {
        byte[] data = Fixture("ac3/junk-adts-in-frames.eac3");
        SniffResult adts = new RawAacParser().Sniff(Context(data));
        SniffResult ac3 = Evidence(data);
        Assert.Equal(50, adts.Class);
        Assert.Equal(ac3.Class, adts.Class);
        Assert.True(adts.Offset > ac3.Offset);
        Assert.True(SniffResult.Compare(ac3, adts) < 0);
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
        SniffResult mpeg = new MpegAudioParser().Sniff(Context(data));
        Assert.Equal((30, 0), (mpeg.Class, mpeg.VerifiedFrames));
        Assert.Equal(50, Sniff(data));
        Assert.Equal("AC-3", FormatOf(data));
        Assert.Equal(30, new MpegAudioParser().Sniff(Context([.. Junk(352), .. Fixture("ac3/junk-mpeg-in-frames.ac3")[100..]])).Class);
    }

    // A protected stream with junk in front stays MPEG audio, as does one whose first frames are damaged and a chain at the start.
    [Fact]
    public void AProtectedMpegChainWhoseCrcsHoldKeeps90()
    {
        byte[] mp3 = Fixture("mpeg/external-lame-crc.mp3");
        var parser = new MpegAudioParser();
        Assert.Equal(90, parser.Sniff(Context(mp3)).Class);
        Assert.Equal(90, parser.Sniff(Context([.. Junk(30).Select(b => (byte)(b & 0x7F)), .. mp3])).Class);
    }
}
