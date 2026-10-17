using System.Text;
using System.Text.RegularExpressions;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// One concept has one name in every format: the codec of a coding, the arrangement of channels, the software behind a Lavc or Lavf text, a DRM
// system ID, a protection scheme, and a unit that reads in the singular for one. Checked over the streams and trees of every fixture.
public partial class CrossFormatNamingTests
{
    // The names a codec is written with that are not the project's: a container's own word for a coding another container or a codec parser names.
    private static readonly string[] RetiredCodecNames =
    [
        "ALAC", "MPEG-1 Audio", "encrypted audio", "encrypted video", "DTS lossless", "linear PCM", "IEEE float",
    ];

    // Codec names that do not start with a capital letter or a digit in their own spelling.
    private static readonly string[] LowercaseStarts = ["xHE-AAC (USAC)"];

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NotAlphanumeric();

    [GeneratedRegex(@"^\d/\d \((left|center|right)")]
    private static partial Regex ChannelModeWithSpeakers();

    [GeneratedRegex(@"(?<![\w.-]|to )1 (bytes|bits|items|entries|channels|blocks|frames|samples|packets|bands|subbands|levels|degrees|ticks|times|pages|streams|records|elements|segments|objects|values|sets|words|sections|tracks|chunks|payloads|fields|steps|coefficients|granules|groups|ranges|pieces|parts|headers|sizes)\b")]
    private static partial Regex PluralForOne();

    [GeneratedRegex(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$")]
    private static partial Regex Guid();

    private static IEnumerable<(string Name, StreamInfo Stream)> AllStreams()
    {
        foreach (string name in DescriptionTests.FixtureNames())
        {
            Report report = Fixtures.AnalyzeMpeg(name);
            foreach (StreamInfo stream in report.Streams)
            {
                yield return (name, stream);
            }
        }
    }

    [Fact]
    public void OneCodingHasOneCodecNameInEveryFixture()
    {
        var spellings = new Dictionary<string, SortedSet<string>>();
        var problems = new List<string>();
        foreach ((string name, StreamInfo stream) in AllStreams())
        {
            string codec = stream.Codec;
            string key = NotAlphanumeric().Replace(codec.ToLowerInvariant(), string.Empty);
            if (!spellings.TryGetValue(key, out SortedSet<string>? set))
            {
                spellings[key] = set = [];
            }

            if (set.Add(codec) && RetiredCodecNames.Contains(codec, StringComparer.Ordinal))
            {
                problems.Add($"{name}: \"{codec}\" is not the project's name for the coding");
            }

            if (codec.Length > 0 && !char.IsUpper(codec[0]) && !char.IsDigit(codec[0]) && !LowercaseStarts.Contains(codec, StringComparer.Ordinal) && codec != "unknown")
            {
                problems.Add($"{name}: \"{codec}\" does not start with a capital letter");
            }
        }

        problems.AddRange(spellings.Where(s => s.Value.Count > 1).Select(s => $"one coding, several spellings: {string.Join(" | ", s.Value)}"));
        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
    }

    // The form every MPEG audio name takes: the codec parser's "MPEG-1 Layer III"; a container that states only the layer drops the version
    // ("MPEG Layer III"), one that states only the version drops the layer ("MPEG-1 audio").
    [GeneratedRegex(@"^(MPEG(-(1|2|2\.5))? Layer (I|II|III|I and II)|MPEG-(1|2|4) audio)$")]
    private static partial Regex MpegAudioName();

    [Fact]
    public void MpegAudioHasOneFormOfNameInEveryFixture()
    {
        var bad = AllStreams().Select(x => x.Stream.Codec).Where(c => c.StartsWith("MPEG", StringComparison.Ordinal) && !MpegAudioName().IsMatch(c)).Distinct().ToList();
        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }

    [Fact]
    public void ContainersNameTheCodingsTheirCodecParsersDoNotReadInTheParsersForms()
    {
        // MP4: what the sample entry and the object type state.
        Assert.Equal("MPEG-1 audio", Formats.Mp4.Mp4Parser.StreamCodecName("mp4a", 0x6B));
        Assert.Equal("MPEG-2 audio", Formats.Mp4.Mp4Parser.StreamCodecName("mp4a", 0x69));
        Assert.Equal("MPEG-4 audio", Formats.Mp4.Mp4Parser.StreamCodecName("mp4a", 0x40));
        Assert.Equal("AAC LC", Formats.Mp4.Mp4Parser.StreamCodecName("mp4a", 0x67));
        Assert.Equal("Other", Formats.Mp4.Mp4Parser.StreamCodecName("mp4a", 0x01));
        Assert.Equal("MPEG Layer III", Formats.Mp4.Mp4Parser.StreamCodecName(".mp3", null));
        Assert.Equal("DTS-HD Master Audio", Formats.Mp4.Mp4Parser.StreamCodecName("dtsl", null));
        Assert.Equal("DTS Express", Formats.Mp4.Mp4Parser.StreamCodecName("dtse", null));
        Assert.Equal("Apple Lossless", Formats.Mp4.Mp4Parser.StreamCodecName("alac", null));
        Assert.Equal("PCM", Formats.Mp4.Mp4Parser.StreamCodecName("sowt", null));
        Assert.Equal("Encrypted audio", Formats.Mp4.Mp4Parser.StreamCodecName("enca", null));

        // Matroska and ASF: the layer alone, and the other codings.
        Assert.Equal("MPEG Layer II", Formats.Matroska.Codecs.NameOf("A_MPEG/L2"));
        Assert.Equal("MPEG Layer III", Formats.Matroska.Codecs.NameOf("A_MPEG/L3"));
        Assert.Equal("DTS-HD Master Audio", Formats.Matroska.Codecs.NameOf("A_DTS/LOSSLESS"));
        Assert.Equal("Apple Lossless", Formats.Matroska.Codecs.NameOf("A_ALAC"));
        Assert.Equal("PCM", Formats.Matroska.Codecs.NameOf("A_PCM/INT/BIG"));
        Assert.Equal("MPEG Layer III", Formats.Asf.AsfParser.CodecNameOf(0x0055));
        Assert.Equal("MPEG Layer I and II", Formats.Asf.AsfParser.CodecNameOf(0x0050));
        Assert.Equal("PCM (floating-point)", Formats.Asf.AsfParser.CodecNameOf(0x0003));

        // Every name matches the form the codec parsers give the same coding.
        foreach (string name in new[] { "MPEG Layer I", "MPEG Layer II", "MPEG Layer III", "MPEG Layer I and II", "MPEG-1 audio", "MPEG-2 audio", "MPEG-4 audio" })
        {
            Assert.Matches(MpegAudioName(), name);
        }
    }

    [Fact]
    public void TheArrangementsOfAc3AndDtsAreWrittenTheSameWay()
    {
        var modes = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((string name, StreamInfo stream) in AllStreams())
        {
            if (stream.ChannelMode is { } mode)
            {
                Assert.False(ChannelModeWithSpeakers().IsMatch(mode), $"{name}: \"{mode}\" names its speakers; the channel layout does");
                modes.Add(mode);
            }
        }

        Assert.Contains("2/0", modes);
        Assert.Contains("3/2", modes);
    }

    [Fact]
    public void LibavcodecAndLibavformatTextsNameFfmpegInEveryFormat()
    {
        var problems = new List<string>();
        foreach ((string name, StreamInfo stream) in AllStreams())
        {
            foreach (string? person in new[] { stream.Encoder, stream.Writer })
            {
                if (person is not null && (person.StartsWith("Lavc", StringComparison.Ordinal) || person.StartsWith("Lavf", StringComparison.Ordinal)))
                {
                    problems.Add($"{name}: \"{person}\" is shown as the software's raw text");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
    }

    [Fact]
    public void ProtectionSchemesAndSystemsAreWrittenTheSameWay()
    {
        var problems = new List<string>();
        foreach ((string name, StreamInfo stream) in AllStreams())
        {
            if (stream.Protection is not { } protection)
            {
                continue;
            }

            if (!char.IsUpper(protection.Scheme[0]) && !protection.Scheme.StartsWith("iTunes", StringComparison.Ordinal))
            {
                problems.Add($"{name}: the scheme \"{protection.Scheme}\" does not start with a capital letter");
            }

            foreach (string system in protection.Systems.Where(s => Guid().IsMatch(s)))
            {
                if (system != system.ToLowerInvariant())
                {
                    problems.Add($"{name}: the system ID {system} is not in lower case, as ProtectionSystems.Text writes it");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
    }

    [Fact]
    public void AUnitInTheWholeReportReadsInTheSingularForOne()
    {
        var problems = new List<string>();
        foreach (string name in DescriptionTests.FixtureNames())
        {
            Report report = Fixtures.AnalyzeMpeg(name);
            var text = new StringBuilder();
            using (var writer = new StringWriter(text))
            {
                ReportTextWriter.Write(report, writer, showOffsets: false, ReportFilter.All, explain: false);
            }

            // The whole report: every node at every level, frames and side information included, and the findings' messages.
            foreach (string line in text.ToString().Split('\n'))
            {
                foreach (Match match in PluralForOne().Matches(line))
                {
                    problems.Add($"{name}: \"{match.Value}\" in {line.Trim()}");
                }
            }

        }

        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct().Take(40)));
    }
}
