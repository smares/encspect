using System.Text.Json;
using Encspect.Formats.MpegAudio;
using Encspect.Host;
using Xunit;

namespace Encspect.Tests;

// The habit rules of the MPEG audio parser (MpegAudioParser.Traits.cs) are evaluated on every
// fixture whose manifest states the encoder that wrote it, whatever evidence finally names the
// encoder. A fixture that matches a rule of another encoder is a lookalike, and the rule's
// lookalike evidence line and the table in docs/formats/mpeg-audio.md must list it.
public class TraitLookalikeTests
{
    // The tools whose files a rule is written for, by the start of the manifest's tool (command[0] for generated files, "tool" for the
    // sample kit's); a fixture of one of these is not a lookalike. A remux by ffmpeg of another encoder's file is owned by that encoder.
    private static readonly Dictionary<string, string[]> OwnTools = new(StringComparer.Ordinal)
    {
        ["Shine"] = ["shineenc"],
        ["Helix MP3 Encoder"] = ["hmp3", "tomp3"],
        ["Xing"] = ["tompg"],
        ["BladeEnc"] = ["bladeenc"],
        ["Fraunhofer"] = ["mp3sEncoder", "acmenc", "MP3 Encoder ACM Wrapper", "iTunes MP3 encoder", "Nero Burning ROM"],
        ["TwoLAME or Xing"] = ["twolame", "tompg"],
    };

    private static readonly Dictionary<string, string> Owned = new(StringComparer.Ordinal)
    {
        ["external-ffmpeg-shine.mp3"] = "Shine",
        ["external-ffmpeg-remux-blade.mp3"] = "BladeEnc",
    };

    // What the table of the docs lists, as "rule: fixture" in order.
    private static readonly string[] Listed =
    [
        "BladeEnc: external-8hz-flags.mp3",
        "BladeEnc: external-plugger-original.mp3",
        "Xing: external-shine-mpeg2.mp3",
        "Xing: external-shine-silence.mp3",
    ];

    [Fact]
    public void FixturesMatchingTheRuleOfAnotherEncoderAreTheListedLookalikes()
    {
        var found = new List<string>();
        foreach ((string rel, string tool) in ManifestFixtures())
        {
            var matched = new List<string>();
            MpegAudioParser.TraitRuleProbe = names => matched.AddRange(names);
            try
            {
                using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, "mpeg-audio", rel));
                Analyzer.CreateDefault().Analyze(Path.GetFileName(rel), stream, null);
            }
            finally
            {
                MpegAudioParser.TraitRuleProbe = null;
            }

            foreach (string rule in matched.Distinct())
            {
                if (!OwnTools[rule].Any(t => tool.StartsWith(t, StringComparison.Ordinal)) && !(Owned.TryGetValue(rel, out string? owner) && owner == rule))
                {
                    found.Add($"{rule}: {rel}");
                }
            }
        }

        Assert.Equal(Listed, found.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryRuleWithLookalikesHasItsLineAndNoOtherRuleHasOne()
    {
        string[] withLookalikes = [.. Listed.Select(l => l[..l.IndexOf(':', StringComparison.Ordinal)]).Distinct()];
        Assert.Equal(withLookalikes.Order(StringComparer.Ordinal), MpegAudioParser.TraitLookalikes.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheLinesNameTheListedEncodersAndSettings()
    {
        Assert.Equal(
            "Encspect's test files of Shine 3.1.1 (MPEG-2 at 22.05 kHz, and silence as input) match these habits too",
            MpegAudioParser.TraitLookalikes["Xing"]);
        Assert.Equal(
            "Encspect's test files of 8hz-MP3 0.2b-04J with -c -o and Plugger+ 0.4 with -y -o match these habits too",
            MpegAudioParser.TraitLookalikes["BladeEnc"]);
    }

    [Fact]
    public void DocsListTheLookalikesInTheirRows()
    {
        string docs = File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, "..", "..", "docs", "formats", "mpeg-audio.md"));
        string[] rows = [.. docs.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("| ", StringComparison.Ordinal))];
        foreach (string entry in Listed)
        {
            int colon = entry.IndexOf(": ", StringComparison.Ordinal);
            string rule = entry[..colon];
            string fixture = Path.GetFileNameWithoutExtension(entry[(colon + 2)..]);
            string? row = rows.FirstOrDefault(r => r.StartsWith($"| {rule} |", StringComparison.Ordinal));
            Assert.NotNull(row);
            Assert.Contains($"`{fixture}`", row, StringComparison.Ordinal);
        }
    }

    // The fixtures under tests/fixtures/mpeg-audio (and the sample kit) whose manifest names the encoder, with its tool.
    private static IEnumerable<(string Path, string Tool)> ManifestFixtures()
    {
        string root = Path.Combine(Fixtures.FixtureDirectory, "mpeg-audio");
        foreach (string p in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetExtension(p) is not (".mp1" or ".mp2" or ".mp3"))
            {
                continue;
            }

            string stem = p[..^Path.GetExtension(p).Length];
            string manifest = File.Exists(stem + ".provenance.json") ? stem + ".provenance.json" : stem + ".expected.json";
            if (!File.Exists(manifest))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("encoder", out JsonElement encoder))
            {
                continue;
            }

            string tool = encoder.TryGetProperty("tool", out JsonElement t) ? t.GetString()!
                : encoder.GetProperty("command")[0].GetString()!;
            yield return (System.IO.Path.GetRelativePath(root, p).Replace('\\', '/'), tool);
        }
    }
}
