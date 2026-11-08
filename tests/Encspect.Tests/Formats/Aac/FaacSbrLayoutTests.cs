using Encspect.Core;
using Encspect.Formats.Aac;

namespace Encspect.Tests;

// The SBR layout of FAAC 2.x's HE-AAC encoder (libfaac/sbr_bitstream.c and sbr.c) as AacEncoder applies it
// to a stream's tally: hand-made tallies, each one change away from what FAAC 2.2 writes. The encoder-written
// files are in the fixtures (faac-2.2-he*), and check_faac_sbr.py sweeps the releases and fdkaac.
public class FaacSbrLayoutTests
{
    private static readonly SbrHeader Faac22 = new()
    {
        AmpRes = 1,
        StartFrequency = 15,
        StopFrequency = 12,
        XoverBand = 0,
        FrequencyScale = 1,
        AlterScale = 0,
        NoiseBands = 0,
        HasHeaderExtra1 = true,
    };

    private static readonly SbrHeader Faac20 = Faac22 with { AmpRes = 0, StopFrequency = 10, FrequencyScale = 0, AlterScale = 1 };

    private static CodingTally Tally(SbrHeader header, long headers = 2, long payloads = 35) => new()
    {
        SbrHeaders = headers,
        SbrPlainHeaders = headers,
        SbrPayloads = payloads,
        SbrPlainPayloads = payloads,
        FirstHeader = header,
    };

    [Theory]
    [InlineData(1, 12)]
    [InlineData(2, 13)]
    [InlineData(3, 10)]
    public void Faac22LayoutIsNamed(int frequencyScale, int stopFrequency)
    {
        Node? encoder = Detect(Tally(Faac22 with { FrequencyScale = frequencyScale, StopFrequency = stopFrequency }));
        Assert.NotNull(encoder);
        Assert.Equal("FAAC", encoder.Value?.ToString());
        Assert.Equal("Medium", Child(encoder, "Confidence"));
        string evidence = Child(encoder, "Evidence")!;
        Assert.Contains($"bs_noise_bands 0 (one noise floor band) and stop frequency {stopFrequency}, none off FAAC's 30-access-unit header period", evidence);
    }

    [Fact]
    public void Faac20And21LayoutIsNamed()
    {
        Node? encoder = Detect(Tally(Faac20));
        Assert.NotNull(encoder);
        Assert.DoesNotContain("period", Child(encoder, "Evidence"));
    }

    [Fact]
    public void ClaimSuppliesTheVersion()
    {
        var source = new Node("©too", 0x40, 8);
        Node? encoder = Detect(Tally(Faac22), [new EncoderClaim("FAAC 2.2.0 (6acbe23)", source, "MP4 ©too atom (encoding tool)", ClaimRole.Encoder)]);
        Assert.NotNull(encoder);
        Assert.Equal("FAAC 2.2.0 (6acbe23)", encoder.Value?.ToString());
        Assert.Equal("Medium", Child(encoder, "Confidence"));
        Assert.Equal(2, encoder.Children.Count(c => c.Name == "Evidence"));
    }

    [Fact]
    public void ClaimOfAnotherEncoderIsADifference()
    {
        var differences = new List<string>();
        Node? encoder = Detect(Tally(Faac22), [new EncoderClaim("fdkaac 1.0.0", new Node("©too", 0x40, 8), "MP4 ©too atom (encoding tool)", ClaimRole.Encoder)], differences);
        Assert.Equal("FAAC", encoder?.Value?.ToString());
        Assert.Single(differences);
    }

    private static readonly Dictionary<string, SbrHeader> Unfitting = new()
    {
        ["no extra 1"] = Faac22 with { HasHeaderExtra1 = false },
        ["extra 2"] = Faac22 with { HasHeaderExtra2 = true },
        ["noise bands 2"] = Faac22 with { NoiseBands = 2 },
        ["start frequency 14"] = Faac22 with { StartFrequency = 14 },
        ["start frequency 12"] = Faac22 with { StartFrequency = 12 },
        ["crossover band 1"] = Faac22 with { XoverBand = 1 },
        ["stop frequency 9"] = Faac22 with { StopFrequency = 9 },
        ["stop frequency 14"] = Faac22 with { StopFrequency = 14 },
        ["frequency scale 2 with alter scale 1"] = Faac22 with { FrequencyScale = 2, AlterScale = 1 },
        ["frequency scale 3 with 1.5 dB"] = Faac22 with { FrequencyScale = 3, AmpRes = 0 },
        ["linear table with stop frequency 12"] = Faac20 with { StopFrequency = 12 },
        ["fdkaac's header"] = new SbrHeader { AmpRes = 1, StartFrequency = 12, StopFrequency = 9, FrequencyScale = 2, AlterScale = 1, NoiseBands = 2 },
    };

    public static TheoryData<string> UnfittingHeaders => new(Unfitting.Keys);

    [Theory]
    [MemberData(nameof(UnfittingHeaders))]
    public void OtherHeadersAreNotNamed(string what)
    {
        SbrHeader header = Unfitting[what];
        Assert.False(FaacSbr.HeaderFits(header), what);
        CodingTally tally = Tally(header);
        tally.SbrPlainHeaders = 0;
        Assert.Null(Detect(tally));
    }

    [Fact]
    public void OneUnfittingHeaderIsEnough()
    {
        CodingTally tally = Tally(Faac22, headers: 3);
        tally.SbrPlainHeaders = 2;
        Assert.Null(Detect(tally));
    }

    [Fact]
    public void DataWithOptionalToolsIsNotNamed()
    {
        CodingTally tally = Tally(Faac22);
        tally.SbrPlainPayloads = 34;
        Assert.Null(Detect(tally));
    }

    [Fact]
    public void UnreadSbrDataIsNotNamed()
    {
        CodingTally tally = Tally(Faac22);
        tally.SbrUnread = 1;
        Assert.Null(Detect(tally));
    }

    [Fact]
    public void HeadersOffThePeriodAreNotNamed()
    {
        CodingTally tally = Tally(Faac22);
        tally.SbrHeadersOnPeriod = false;
        Assert.Null(Detect(tally));
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(1, false)]
    [InlineData(29, false)]
    [InlineData(31, false)]
    public void Faac22RepeatsItsHeaderEvery30AccessUnits(int gap, bool onPeriod) => Assert.Equal(onPeriod, FaacSbr.OnPeriod(Faac22, gap));

    [Fact]
    public void Faac20And21SpacingIsNotChecked() => Assert.True(FaacSbr.OnPeriod(Faac20, 1));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(19)]
    public void TooFewPayloadsAreNotNamed(int payloads) => Assert.Null(Detect(Tally(Faac22, headers: payloads == 0 ? 0 : 1, payloads: payloads)));

    [Fact]
    public void TwentyPayloadsAreEnough() => Assert.NotNull(Detect(Tally(Faac22, headers: 1, payloads: 20)));

    [Fact]
    public void NoTallyNamesNothing() => Assert.Null(AacEncoder.Detect(null, [], Cover, (_, _, _) => { }));

    private static Node Cover(string name, NodeValue? value, string description) => new(name, 0, 0, value) { Description = description };

    private static string? Child(Node node, string name) => node.Children.FirstOrDefault(c => c.Name == name)?.Value?.ToString();

    private static Node? Detect(CodingTally tally, IReadOnlyList<EncoderClaim>? claims = null, List<string>? differences = null) =>
        AacEncoder.Detect(null, claims ?? [], Cover, (_, text, _) => differences?.Add(text), tally: tally);
}
