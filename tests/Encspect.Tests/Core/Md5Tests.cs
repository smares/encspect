using System.Security.Cryptography;
using System.Text;
using Encspect.Core;

namespace Encspect.Tests;

public class Md5Tests
{
    private static string Hash(ReadOnlySpan<byte> data)
    {
        var md5 = new Md5();
        md5.AppendData(data);
        Span<byte> hash = stackalloc byte[16];
        md5.GetHashAndReset(hash);
        return Convert.ToHexStringLower(hash);
    }

    // RFC 1321, appendix A.5.
    [Theory]
    [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("a", "0cc175b9c0f1b6a831c399e269772661")]
    [InlineData("abc", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("message digest", "f96b697d7cb7938d525a2f31aaf161d0")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "c3fcd3d76192e4007dfb496cca67e13b")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "d174ab98d277d9f5a5611c2c9f419d9f")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "57edf4a22be3c955ac49da2e2107b67a")]
    public void GivesTheVectorsOfRfc1321(string text, string expected) => Assert.Equal(expected, Hash(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void AgreesWithTheSystemImplementationAtEveryLengthAcrossBlockBoundaries()
    {
        var random = new Random(1321);
        byte[] data = new byte[300];
        random.NextBytes(data);
        for (int length = 0; length <= data.Length; length++)
        {
            Assert.Equal(Convert.ToHexStringLower(MD5.HashData(data.AsSpan(0, length))), Hash(data.AsSpan(0, length)));
        }
    }

    [Fact]
    public void PiecesOfAnySizeGiveTheHashOfTheWhole()
    {
        var random = new Random(7);
        byte[] data = new byte[5000];
        random.NextBytes(data);
        string expected = Convert.ToHexStringLower(MD5.HashData(data));
        foreach (int piece in (int[])[1, 3, 55, 56, 63, 64, 65, 127, 1000])
        {
            var md5 = new Md5();
            for (int at = 0; at < data.Length; at += piece)
            {
                md5.AppendData(data.AsSpan(at, Math.Min(piece, data.Length - at)));
            }

            byte[] hash = new byte[16];
            md5.GetHashAndReset(hash);
            Assert.Equal(expected, Convert.ToHexStringLower(hash));
        }
    }

    [Fact]
    public void StartsANewMessageAfterGetHashAndReset()
    {
        var md5 = new Md5();
        Span<byte> hash = stackalloc byte[16];
        md5.AppendData("first"u8);
        md5.GetHashAndReset(hash);
        md5.AppendData("abc"u8);
        md5.GetHashAndReset(hash);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", Convert.ToHexStringLower(hash));
    }

    [Fact]
    public void RefusesADestinationShorterThanTheHash()
    {
        var md5 = new Md5();
        Assert.Throws<ArgumentOutOfRangeException>(() => md5.GetHashAndReset(new byte[15]));
    }
}
