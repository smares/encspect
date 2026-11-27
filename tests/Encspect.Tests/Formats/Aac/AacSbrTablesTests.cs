using System.Security.Cryptography;
using Encspect.Formats.Aac.Decoding;

namespace Encspect.Tests.Formats.Aac;

// The format tables of the HE-AAC decoder (SbrTableData.cs, PsTableData.cs) come from libxaac through scripts/fixtures/make_sbr_tables.py and make_ps_tables.py, whose --check the
// CI job of the AAC cross-checks runs against the pinned libxaac checkout. These tests hold the committed files to the same values without a checkout.
public class AacSbrTablesTests
{
    private static string Hash(float[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    [Fact]
    public void TheQmfPrototypeHas640CoefficientsWithSymmetricMagnitudes()
    {
        float[] c = SbrTableData.Qmf;

        Assert.Equal(640, c.Length);
        Assert.Equal(0f, c[0]);
        for (int n = 1; n < 640; n++)
        {
            Assert.Equal(MathF.Abs(c[n]), MathF.Abs(c[640 - n]));
        }

        Assert.Equal(-0.0005525285378098488f, c[1]);
    }

    [Fact]
    public void TheNoiseTableHoldsThe512ComplexPhasesInTheUnitSquare()
    {
        float[] v = SbrTableData.Noise;

        Assert.Equal(1024, v.Length);
        Assert.All(v, x => Assert.InRange(x, -1f, 1f));
        Assert.Equal(-0.99948153278296f, v[0]);
        Assert.Equal(-0.59483417516607f, v[1]);
    }

    // Changed only by regenerating the files (make_sbr_tables.py, make_ps_tables.py), whose --check compares them with libxaac's.
    [Fact]
    public void TheTablesAreThoseTheGeneratorsWrote()
    {
        Assert.Equal("49ded33391a73b5b80511b30e376af9fbd3992acc06ae432e3181054a460eef3", Hash(SbrTableData.Qmf));
        Assert.Equal("9ff10a78f3ef0e1d0ba27a423a1e16d30edd016279222ee0ac8308ce1d65aa4f", Hash(SbrTableData.Noise));
        Assert.Equal("8ce32ecdffcdd58a54e5a36aa608985d058536bffb7ed83e694668d0c5cc0aeb", PsHash());
    }

    private static string PsHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var field in typeof(PsTableData).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.ASCII.GetBytes(field.Name));
            if (field.GetValue(null) is float[] floats)
            {
                byte[] bytes = new byte[floats.Length * 4];
                Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
                hash.AppendData(bytes);
            }
            else if (field.GetValue(null) is int[] ints)
            {
                byte[] bytes = new byte[ints.Length * 4];
                Buffer.BlockCopy(ints, 0, bytes, 0, bytes.Length);
                hash.AppendData(bytes);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
