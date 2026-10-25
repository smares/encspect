using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Formats.Mp4;
using Encspect.Host;

namespace Encspect.Tests;

// The speakers each format's streams report (StreamInfo.ChannelLayout), their common names, and how
// the summary and the CSV show them.
public class ChannelLayoutTests
{
    private const Speaker C = Speaker.FrontCenter;
    private const Speaker L = Speaker.FrontLeft;
    private const Speaker R = Speaker.FrontRight;
    private const Speaker Lfe = Speaker.LowFrequency;
    private const Speaker Bl = Speaker.BackLeft;
    private const Speaker Br = Speaker.BackRight;
    private const Speaker Sl = Speaker.SideLeft;
    private const Speaker Sr = Speaker.SideRight;
    private const Speaker Bc = Speaker.BackCenter;
    private const Speaker X = Speaker.Discrete;

    [Fact]
    public void NamesFollowTheSpeakersNotTheirOrder()
    {
        Assert.Equal("mono", ChannelLayouts.CommonName([C]));
        Assert.Equal("stereo", ChannelLayouts.CommonName([R, L]));
        Assert.Equal("5.1", ChannelLayouts.CommonName([C, L, R, Bl, Br, Lfe]));
        Assert.Equal("5.1", ChannelLayouts.CommonName([L, R, C, Lfe, Sl, Sr]));
        Assert.Equal("quadraphonic", ChannelLayouts.CommonName([L, R, Bl, Br]));
        Assert.Equal("7.1", ChannelLayouts.CommonName([C, L, R, Sl, Sr, Bl, Br, Lfe]));
        Assert.Equal("7.1 (wide)", ChannelLayouts.CommonName([C, Speaker.FrontLeftOfCenter, Speaker.FrontRightOfCenter, L, R, Bl, Br, Lfe]));
        Assert.Equal("22.2", ChannelLayouts.CommonName(AacTables.Layout(13)!));

        // MPEG-2's 2/1, dual channel and a speaker named twice have no common name.
        Assert.Null(ChannelLayouts.CommonName([L, R, Bc]));
        Assert.Null(ChannelLayouts.CommonName([X, X]));
        Assert.Null(ChannelLayouts.CommonName([L, L]));
    }

    [Fact]
    public void DescriptionsWriteEverySpeakerOut()
    {
        Assert.Equal("5.1 (front center, front left, front right, back left, back right, LFE)", ChannelLayouts.Describe([C, L, R, Bl, Br, Lfe]));
        Assert.Equal("front left, front right, back center", ChannelLayouts.Describe([L, R, Bc]));
        Assert.Equal("discrete, discrete", ChannelLayouts.Describe(ChannelLayouts.Discrete(2)));
        Assert.All(Enum.GetValues<Speaker>(), s => Assert.DoesNotMatch("^[A-Z]{1,2}$|LS|RS|FL|FR", ChannelLayouts.Name(s)));
        Assert.True(ChannelLayouts.HaveSameSpeakers([C, L, R, Bl, Br, Lfe], [L, R, C, Lfe, Bl, Br]));
        Assert.False(ChannelLayouts.HaveSameSpeakers([C, L, R, Bl, Br, Lfe], [L, R, C, Lfe, Sl, Sr]));
    }

    // ISO/IEC 14496-3 Table 1.19, in the order of the elements.
    [Theory]
    [InlineData(1, new[] { C })]
    [InlineData(2, new[] { L, R })]
    [InlineData(3, new[] { C, L, R })]
    [InlineData(4, new[] { C, L, R, Bc })]
    [InlineData(5, new[] { C, L, R, Bl, Br })]
    [InlineData(6, new[] { C, L, R, Bl, Br, Lfe })]
    [InlineData(7, new[] { C, Speaker.FrontLeftOfCenter, Speaker.FrontRightOfCenter, L, R, Bl, Br, Lfe })]
    [InlineData(11, new[] { C, L, R, Bl, Br, Bc, Lfe })]
    [InlineData(12, new[] { C, L, R, Sl, Sr, Bl, Br, Lfe })]
    [InlineData(14, new[] { C, L, R, Bl, Br, Lfe, Speaker.TopFrontLeft, Speaker.TopFrontRight })]
    public void AacChannelConfigurations(int configuration, Speaker[] expected)
    {
        Assert.Equal(expected, AacTables.Layout(configuration));
        Assert.Equal(AacTables.Channels(configuration), expected.Length);
    }

    [Fact]
    public void AacReservedConfigurationsHaveNoLayout()
    {
        Assert.Equal(24, AacTables.Layout(13)!.Count);
        Assert.All(new[] { 0, 8, 9, 10, 15 }, c => Assert.Null(AacTables.Layout(c)));
        Assert.Equal([X, X], AacTables.UsacLayout(8));
        Assert.Equal([L, R, Bc], AacTables.UsacLayout(9));
        Assert.Equal([L, R, Sl, Sr, Bl, Br], AacTables.UsacPositions([0, 1, 4, 5, 8, 9]));
        Assert.Equal([L, R, Bl, Br, X], AacTables.UsacPositions([0, 1, 4, 5, 31]));
    }

    [Fact]
    public void ParametricStereoGivesStereoFromAMonoCore()
    {
        var mono = new AacConfig { ObjectType = 2, CoreSampleRate = 22050, ChannelConfiguration = 1, Channels = 1, CoreFrameLength = 1024, Sbr = true };
        Assert.Equal([C], mono.OutputLayout);
        Assert.Equal([L, R], (mono with { Ps = true }).OutputLayout);
    }

    // The program config element's lists: front from the center outward, side from front to back,
    // back from the sides to the center, then the LFE elements.
    [Theory]
    [InlineData(new[] { false, true }, new bool[0], new[] { true, false }, 1, new[] { C, L, R, Bl, Br, Bc, Lfe })]
    [InlineData(new[] { true, true }, new bool[0], new bool[0], 0, new[] { Speaker.FrontLeftOfCenter, Speaker.FrontRightOfCenter, L, R })]
    [InlineData(new[] { false, true }, new bool[0], new[] { true, true }, 1, new[] { C, L, R, Sl, Sr, Bl, Br, Lfe })]
    [InlineData(new[] { false, true }, new[] { true }, new[] { true }, 2, new[] { C, L, R, Sl, Sr, Bl, Br, Lfe, Speaker.LowFrequency2 })]
    [InlineData(new[] { true, false }, new[] { false }, new bool[0], 0, new[] { L, R, X, X })]
    public void ProgramConfigElements(bool[] front, bool[] side, bool[] back, int lfe, Speaker[] expected)
    {
        var bits = new List<int>();
        void Put(long value, int width)
        {
            for (int i = width - 1; i >= 0; i--)
            {
                bits.Add((int)(value >> i) & 1);
            }
        }

        Put(0, 4);
        Put(1, 2);
        Put(3, 4);
        Put(front.Length, 4);
        Put(side.Length, 4);
        Put(back.Length, 4);
        Put(lfe, 2);
        Put(0, 3);
        Put(0, 4);
        Put(0, 3);
        foreach (bool isPair in front.Concat(side).Concat(back))
        {
            Put(isPair ? 1 : 0, 1);
            Put(0, 4);
        }

        Put(0, 4 * lfe);
        while (bits.Count % 8 != 0)
        {
            bits.Add(0);
        }

        Put(0, 8);
        byte[] bytes = [.. bits.Chunk(8).Select(b => (byte)b.Aggregate(0, (v, bit) => (v << 1) | bit))];
        int channels = ProgramConfigElement.Read(new BitFields(new MappedBytes(bytes, 0)), new Node("PCE", 0, bytes.Length), 0, (_, _, _, _) => { },
            out IReadOnlyList<Speaker> layout);

        Assert.Equal(expected, layout);
        Assert.Equal(expected.Length, channels);
    }

    // Core Audio's layout tags, bitmap and labels: the surround pair is the back pair, and the side
    // pair where the layout has rear surrounds too.
    [Fact]
    public void CoreAudioLayouts()
    {
        Assert.Equal([L, R, C, Lfe, Bl, Br], Mp4Parser.FromAbbreviations(["L", "R", "C", "LFE", "Ls", "Rs"]));
        Assert.Equal([L, R, C, Lfe, Sl, Sr, Bl, Br], Mp4Parser.FromAbbreviations(["L", "R", "C", "LFE", "Ls", "Rs", "Rls", "Rrs"]));
        Assert.Equal([X, X], Mp4Parser.FromAbbreviations(["-", "-"]));
        Assert.Equal([L, R, C, Lfe, Bl, Br], Mp4Parser.FromBitmap(0x3F));
        Assert.Equal([L, R, C, Lfe, Sl, Sr], Mp4Parser.FromBitmap(0x60F));
        Assert.Equal([Speaker.TopSideLeft, Speaker.TopSideRight], Mp4Parser.FromBitmap((1U << 21) | (1U << 23)));
        Assert.Equal([L, R, C, Lfe, Bl, Br], Mp4Parser.FromLabels([1, 2, 3, 4, 5, 6]));
        Assert.Equal([L, R, Sl, Sr, Bl, Br], Mp4Parser.FromLabels([1, 2, 5, 6, 33, 34]));
        Assert.Equal([C, X, X], Mp4Parser.FromLabels([42, 0x10000, 0xFFFFFFFF]));
    }

    // The two layout tags whose speakers Apple's Core Audio Format specification describes differently from their abbreviations: Octagonal ends in the
    // side pair (not the wide pair), and WAVE_6_1 follows the WAVE channel mask, whose pair after the back center is the side pair.
    [Fact]
    public void CoreAudioLayoutTagsFollowApplesDescriptions()
    {
        Assert.Equal([L, R, Bl, Br, C, Bc, Sl, Sr], Encspect.Core.CoreAudioLayouts.Of((111U << 16) | 8, 0, null));
        Assert.Equal([L, R, C, Lfe, Bc, Sl, Sr], Encspect.Core.CoreAudioLayouts.Of((188U << 16) | 7, 0, null));
        Assert.Equal([L, R, C, Lfe, Bl, Br, Bc], Encspect.Core.CoreAudioLayouts.Of((125U << 16) | 7, 0, null));
    }

    // ISO/IEC 23091-3 speaker positions, as the chnl box lists them.
    [Fact]
    public void IsoPositions()
    {
        Assert.Equal([C, L, R, Bl, Br, Lfe], Mp4Parser.FromPositions([2, 0, 1, 4, 5, 3]));
        Assert.Equal([C, L, R, Sl, Sr, Bl, Br, Lfe], Mp4Parser.FromPositions([2, 0, 1, 4, 5, 8, 9, 3]));
        Assert.Equal([X, X, X], Mp4Parser.FromPositions([126, 127, 40]));
    }

    [Theory]
    [InlineData("mpeg/mpeg1-layer3-dual.mp3", 2, null, "discrete, discrete")]
    [InlineData("layer2/mc-3-2-lfe.mp2", 6, "5.1", "5.1 (front left, front right, front center, back left, back right, LFE)")]
    [InlineData("layer2/mc-2-1.mp2", 3, null, "front left, front right, back center")]
    [InlineData("layer2/mc-second-stereo.mp2", 5, null, "front left, front right, front center, discrete, discrete")]
    [InlineData("layer2/mc-configuration-change.mp2", 2, "stereo", "stereo (front left, front right)")]
    [InlineData("mp4/pce-5-1.m4a", 6, "5.1", "5.1 (front center, front left, front right, back left, back right, LFE)")]
    [InlineData("mp4/he-aac-v2-backward.m4a", 2, "stereo", "stereo (front left, front right)")]
    [InlineData("mp4/chan-completes-reserved.m4a", 2, "stereo", "stereo (front left, front right)")]
    [InlineData("ogg/external-speexenc-wb-stereo.spx", 2, "stereo", "stereo (front left, front right)")]
    public void StreamsSummaryAndCsvShowTheLayout(string name, int channels, string? layoutName, string text)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = report.Streams[0];
        Assert.Equal(channels, stream.Channels);
        Assert.Equal(channels, stream.ChannelLayout!.Count);
        Assert.Equal(layoutName, stream.ChannelLayoutName);

        ReportSummary summary = ReportSummary.From(report);
        Assert.Equal(layoutName, summary.ChannelLayoutName);
        Assert.Equal(text, summary.ChannelLayoutText);

        var writer = new StringWriter();
        ReportCsvWriter.Write([report], writer);
        string[] lines = writer.ToString().Split("\r\n");
        Assert.Contains("Channel Mode,Channel Layout,Encoder", lines[0], StringComparison.Ordinal);
        Assert.Contains(text.Contains(',', StringComparison.Ordinal) ? $",\"{text}\"," : $",{text},", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void MultilingualChannelsAreCountedApart()
    {
        var stream = new StreamInfo { Nodes = [], Codec = "MPEG-1 Layer II", Channels = 5, ChannelLayout = [L, R, C, Bl, Br], MultilingualChannels = 7 };
        var report = new Report("x", new Node("File", 0, 1), [], [stream]);
        string json = Fixtures.ToJson(report);
        Assert.Contains("\"channelLayout\": [", json, StringComparison.Ordinal);
        Assert.Contains("\"channelLayoutName\": \"5.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"multilingualChannels\": 7", json, StringComparison.Ordinal);
    }
}
