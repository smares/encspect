using Encspect.Core;
using Encspect.Formats.Ac3;
using Encspect.Host;

namespace Encspect.Tests;

// How the AC-3 sniff ranks a chain that starts after junk, against the ADTS chain that the headers' own bytes can make.
public class Ac3SniffTests
{
    private static int Sniff(byte[] data) => new Ac3Parser().Sniff(new SniffContext(new ByteReader(new MemoryStream(data)), "eac3"));

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ac3", name));

    private static byte[] Junk(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(0x80 | (i % 0x40)))];

    private static string FormatOf(byte[] data) => Analyzer.CreateDefault().Analyze("junk.eac3", new MemoryStream(data)).Root.Value?.ToString() ?? string.Empty;

    [Fact]
    public void AChainAtTheStartTakes92()
    {
        Assert.Equal(92, Sniff(Fixture("junk-adts-in-frames.eac3")[100..]));
    }

    // Three frames whose CRCs hold are no stray run: they rank above a bare ADTS chain's 50, still below MPEG audio.
    [Fact]
    public void AChainFurtherInWhoseCrcsHoldTakes55()
    {
        Assert.Equal(55, Sniff(Fixture("junk-adts-in-frames.eac3")));
        Assert.Equal(55, Sniff([.. Junk(30), .. Fixture("bsi-fields.ac3")]));
    }

    [Fact]
    public void AChainFurtherInWithADamagedCrcInItsFirstThreeFramesTakes50()
    {
        Assert.Equal(50, Sniff([.. Junk(30), .. Fixture("crc-damaged.ac3")]));
    }

    [Fact]
    public void AChainThatTheDataEndsInsideTakes50()
    {
        Assert.Equal(50, Sniff([.. Junk(30), .. Fixture("bsi-fields.ac3")[..(768 * 2 + 400)]]));
    }

    // The E-AC-3 frames hold bytes that chain as ADTS headers, but the file is E-AC-3.
    [Fact]
    public void ADtsLookingBytesInsideE_Ac3FramesDoNotTakeTheFile()
    {
        Assert.Equal("AC-3", FormatOf(Fixture("junk-adts-in-frames.eac3")));
    }
}
