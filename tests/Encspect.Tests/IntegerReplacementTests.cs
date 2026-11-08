using System.Numerics;
using Encspect.Formats.Ac3;
using Encspect.Formats.Vorbis;

namespace Encspect.Tests;

// The integer code that replaced a platform math call in a parser (docs/design/lossy-decoders.md, "How it is held"), against the call it replaced.
public class IntegerReplacementTests
{
    [Fact]
    public void Lookup1ValuesIsTheLargestRootForEveryEntryCountAndDimension()
    {
        for (int dimensions = 1; dimensions <= 70; dimensions++)
        {
            for (long entries = 1; entries <= 5000; entries++)
            {
                long r = SetupDecoder.Lookup1Values(entries, dimensions);
                Assert.True(BigInteger.Pow(r, dimensions) <= entries && BigInteger.Pow(r + 1, dimensions) > entries, $"{entries} entries, {dimensions} dimensions: {r}");
            }
        }

        foreach (long entries in new long[] { 1 << 16, (1 << 16) - 1, (1 << 16) + 1, 1 << 24, (1 << 24) - 1, 4096 * 4096, 4095L * 4095, 255L * 255 * 255, 256L * 256 * 256 })
        {
            for (int dimensions = 1; dimensions <= 40; dimensions++)
            {
                long r = SetupDecoder.Lookup1Values(entries, dimensions);
                Assert.True(BigInteger.Pow(r, dimensions) <= entries && BigInteger.Pow(r + 1, dimensions) > entries, $"{entries} entries, {dimensions} dimensions: {r}");
            }
        }

        Assert.Equal(0, SetupDecoder.Lookup1Values(0, 3));
        Assert.Equal(0, SetupDecoder.Lookup1Values(5, 0));
    }

    [Fact]
    public void CeilingLog2MatchesTheLibraryForEveryFrameSize()
    {
        for (int n = 1; n <= 1 << 16; n++)
        {
            Assert.Equal((int)Math.Ceiling(Math.Log2(n)), FrameSyntax.CeilingLog2(n));
        }
    }
}
