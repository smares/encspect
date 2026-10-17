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

    [GeneratedRegex(@"(?<![\w.-]|to )1 (bytes|bits|items|entries|channels|blocks|frames|samples|packets|bands|subbands|levels|degrees|ticks)\b")]
    private static partial Regex PluralForOne();

    [GeneratedRegex(@"^\s*\[(Info|Warning|Error)\]")]
    private static partial Regex FindingLine();

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
    public void AUnitInTheTreeReadsInTheSingularForOne()
    {
        var problems = new List<string>();
        foreach (string name in DescriptionTests.FixtureNames())
        {
            Report report = Fixtures.AnalyzeMpeg(name);
            var text = new StringBuilder();
            using (var writer = new StringWriter(text))
            {
                ReportTextWriter.Write(report, writer, showOffsets: false, new ReportFilter(ReportSections.Frames), explain: false);
            }

            // The findings follow the tree; their messages are sentences and not units of nodes.
            foreach (string line in text.ToString().Split('\n').Where(l => !FindingLine().IsMatch(l)))
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
