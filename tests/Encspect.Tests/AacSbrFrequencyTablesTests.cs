using Encspect.Formats.Aac;

namespace Encspect.Tests;

// The SBR frequency band tables and grid checks, from the test vectors of docs/design/aac-sbr-frequency-tables.md.
public class AacSbrFrequencyTablesTests
{
    private static SbrHeader Header(int start, int stop, int scale, int alter, int xover, int noise) => new()
    {
        StartFrequency = start,
        StopFrequency = stop,
        FrequencyScale = scale,
        AlterScale = alter,
        XoverBand = xover,
        NoiseBands = noise,
    };

    public static IEnumerable<object[]> Accepted =>
    [
        [44100, 5, 9, 2, 1, 0, 2, 14, 47, 16, 8, 3],
        [48000, 5, 9, 2, 1, 0, 2, 13, 45, 16, 8, 4],
        [48000, 5, 9, 2, 1, 7, 2, 20, 45, 9, 5, 2],
        [16000, 5, 9, 2, 1, 0, 2, 21, 57, 14, 7, 3],
        [44100, 15, 12, 1, 0, 0, 0, 32, 59, 10, 5, 1],
        [48000, 3, 5, 2, 0, 0, 2, 11, 32, 16, 8, 3],
        [48000, 3, 5, 2, 1, 0, 2, 11, 32, 14, 7, 3],
        [44100, 1, 6, 3, 0, 0, 2, 10, 37, 16, 8, 4],
        [32000, 13, 12, 3, 0, 0, 2, 27, 61, 10, 5, 2],
        [44100, 5, 14, 2, 1, 3, 1, 17, 28, 7, 4, 1],
        [44100, 5, 15, 0, 1, 1, 3, 16, 42, 13, 7, 4],
        [16000, 0, 0, 0, 0, 0, 2, 16, 48, 32, 16, 3],
        [16000, 0, 1, 0, 0, 0, 2, 16, 49, 32, 16, 3],
        [16000, 0, 1, 0, 1, 0, 2, 16, 49, 16, 8, 3],
        [16000, 0, 3, 0, 1, 0, 2, 16, 51, 18, 9, 3],
        [16000, 0, 2, 0, 1, 0, 2, 16, 50, 18, 9, 3],
        [16000, 0, 0, 1, 0, 0, 2, 16, 48, 20, 10, 3],
    ];

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Derive_AcceptedHeader_GivesTheFigures(int rate, int start, int stop, int scale, int alter, int xover, int noise, int kx, int k2, int high, int low, int noiseBands)
    {
        var (bands, error) = Sbr.Derive(Header(start, stop, scale, alter, xover, noise), rate);

        Assert.Null(error);
        Assert.Equal(new SbrBands(kx, k2, high, low, noiseBands), bands);
    }

    [Theory]
    [InlineData(8000, 0, 0, 2, 1, 0, 2, "SBR has no frequency tables for an output rate of 8000 Hz")]
    [InlineData(22050, 0, 12, 0, 0, 0, 2, "the SBR range spans 49 QMF bands, more than the 48 allowed at 22050 Hz")]
    [InlineData(44100, 9, 15, 1, 0, 0, 2, "the SBR range spans 36 QMF bands, more than the 35 allowed at 44100 Hz")]
    [InlineData(24000, 15, 0, 0, 0, 0, 2, "the SBR range would start at QMF band 32 and end at 32")]
    [InlineData(44100, 14, 3, 0, 0, 0, 2, "the SBR master frequency table is empty")]
    [InlineData(24000, 15, 1, 2, 0, 0, 2, "the SBR master frequency table's first region is empty")]
    [InlineData(22050, 0, 0, 1, 0, 0, 2, "the SBR master frequency table has a band of no width")]
    [InlineData(22050, 14, 0, 3, 0, 2, 2, "bs_xover_band 2 lies beyond the 2 bands of the master frequency table")]
    [InlineData(16000, 11, 0, 3, 0, 2, 2, "the SBR range starts at QMF band 33, beyond 32")]
    [InlineData(88200, 0, 8, 0, 0, 0, 2, "the header gives 6 noise floor bands, more than 5")]
    public void Derive_RejectedHeader_GivesTheMessage(int rate, int start, int stop, int scale, int alter, int xover, int noise, string message)
    {
        var (bands, error) = Sbr.Derive(Header(start, stop, scale, alter, xover, noise), rate);

        Assert.Null(bands);
        Assert.Equal(message, error);
    }

    // Every sample rate with a table row and every header the syntax can state (4, 4, 2, 1, 3 and 2 bits): what Derive gives, hashed. The
    // band edges and counts rounded to integers come from DetMath's logarithm and power, not the platform's, and this pin holds the results
    // of the platform library they replaced (Linux, glibc, .NET 10), so a tie that rounds the other way shows here before any golden.
    [Fact]
    public void DerivedTablesOfEveryRateAndHeaderAreTheOnesTheLibraryMathGave()
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> record = stackalloc byte[24];
        foreach (int rate in new[] { 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000, 128000, 176400, 192000 })
        {
            for (int start = 0; start < 16; start++)
            {
                for (int stop = 0; stop < 16; stop++)
                {
                    for (int scale = 0; scale < 4; scale++)
                    {
                        for (int alter = 0; alter < 2; alter++)
                        {
                            for (int xover = 0; xover < 8; xover++)
                            {
                                for (int noise = 0; noise < 4; noise++)
                                {
                                    var (bands, error) = Sbr.Derive(Header(start, stop, scale, alter, xover, noise), rate);
                                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record, bands is null ? -1 : bands.Value.Kx);
                                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record[4..], bands?.K2 ?? 0);
                                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record[8..], bands?.High ?? 0);
                                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record[12..], bands?.Low ?? 0);
                                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record[16..], bands?.Noise ?? 0);
                                    hash.AppendData(record[..20]);
                                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes(error ?? string.Empty));
                                }
                            }
                        }
                    }
                }
            }
        }

        string result = Convert.ToHexStringLower(hash.GetHashAndReset());
        Assert.True(result == "1c373d54eff3007180cb1c044806bb051c3de0b4c22d242dd408a2978e6d3192", result);
    }

    private static string? ReadGrid(string bitText) => Read(bitText, 0).Error;

    private static (int PsAt, string? Error, bool Plain) Read(string bitText, int ampRes)
    {
        SbrHeader header = Header(5, 9, 2, 1, 0, 2) with { Bands = Sbr.Derive(Header(5, 9, 2, 1, 0, 2), 44100).Bands, AmpRes = ampRes };
        string padded = ("0" + bitText).PadRight(512, '0');
        byte[] data = new byte[padded.Length / 8];
        for (int i = 0; i < padded.Length; i++)
        {
            if (padded[i] == '1')
            {
                data[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        var bits = new AacBits(data, 0, data.Length * 8);
        return Sbr.ReadData(ref bits, header, false);
    }

    [Fact]
    public void Grid_FixfixWithEightEnvelopes_IsRejected() =>
        Assert.Equal("an SBR frame of class FIXFIX with 8 envelopes, more than 4", ReadGrid("00" + "11"));

    [Fact]
    public void Grid_VarvarWithSevenEnvelopes_IsRejected() =>
        Assert.Equal("an SBR frame of class VARVAR with 7 envelopes, more than 5", ReadGrid("11" + "0000" + "11" + "11"));

    [Fact]
    public void Grid_PointerPastTheEnvelopes_IsRejected() =>
        Assert.Equal("bs_pointer 7 points past the 4 envelopes of the SBR frame", ReadGrid("01" + "00" + "11" + "000000" + "111"));

    [Fact]
    public void Grid_FixfixWithFourEnvelopes_IsAccepted() =>
        Assert.Null(ReadGrid("00" + "10"));

    [Fact]
    public void Grid_VarvarWithFiveEnvelopes_IsAccepted() =>
        Assert.Null(ReadGrid("11" + "0000" + "10" + "10" + "00000000" + "000" + "00000"));

    [Theory]
    [InlineData("01" + "00" + "11" + "000000" + "101" + "0000")] // FIXVAR, 4 envelopes, bs_pointer 5
    [InlineData("11" + "0000" + "10" + "10" + "00000000" + "110" + "00000")] // VARVAR, 5 envelopes, bs_pointer 6
    public void Grid_PointerOfEnvelopesPlusOne_IsAccepted(string bits) =>
        Assert.Null(ReadGrid(bits));

    [Fact]
    public void Grid_FixfixWithOneEnvelope_IgnoresTheHeaderAmplitudeResolution()
    {
        // One envelope: the amplitude resolution is 0 whatever bs_amp_res says, so the envelope codebooks
        // and start bits do not change with the header.
        var random = new Random(7);
        bool differsWithTwo = false;
        for (int i = 0; i < 64; i++)
        {
            string payload = string.Concat(Enumerable.Range(0, 300).Select(_ => random.Next(2) == 1 ? '1' : '0'));
            Assert.Equal(Outcome("00" + "00" + "0" + payload, 0), Outcome("00" + "00" + "0" + payload, 1));
            differsWithTwo |= Outcome("00" + "01" + "0" + payload, 0) != Outcome("00" + "01" + "0" + payload, 1);
        }

        Assert.True(differsWithTwo);

        static string Outcome(string bits, int ampRes)
        {
            try
            {
                return Read(bits, ampRes).ToString();
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
            {
                return e.GetType().Name;
            }
        }
    }
}
