using System.Text.Json;
using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Tests;

// The guess at the compression level (docs/formats/flac.md, "Compression level"): every fixture written with a known level must have that level among
// the ones named, and a fixture written with settings of its own must not be named. The exact claim of each fixture is pinned by its golden report.
public class FlacCompressionLevelTests
{
    private static readonly string[] SettingsThatMoveALevel = ["-b", "-l", "-r", "-A", "-M", "-m", "-e", "--no-mid-side", "-q", "-p", "-E"];

    // The fixtures whose manifest records the level an encoder was asked for, with the level and whether the arguments change nothing else.
    public static TheoryData<string, int, bool> Written
    {
        get
        {
            var data = new TheoryData<string, int, bool>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "flac");
            foreach (string manifest in Directory.EnumerateFiles(directory, "external-*.expected.json").Order(StringComparer.Ordinal))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (!document.RootElement.TryGetProperty("encoder", out JsonElement encoder) || !encoder.TryGetProperty("arguments", out JsonElement arguments))
                {
                    continue;
                }

                string[] words = [.. arguments.EnumerateArray().Select(a => a.GetString()!)];
                string tool = encoder.GetProperty("tool").GetString()!;
                string joined = string.Join(' ', words);
                int level = -1;
                if (tool.StartsWith("flac ", StringComparison.Ordinal) && words.Count(w => w.Length == 2 && w[0] == '-' && char.IsAsciiDigit(w[1])) == 1)
                {
                    level = words.Single(w => w.Length == 2 && w[0] == '-' && char.IsAsciiDigit(w[1]))[1] - '0';
                }
                else if (tool.StartsWith("ffmpeg ", StringComparison.Ordinal) && Regex.Match(joined, @"-compression_level (\d+)") is { Success: true } found)
                {
                    level = int.Parse(found.Groups[1].Value);
                }

                if (level >= 0)
                {
                    string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                    string file = Directory.EnumerateFiles(directory, stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
                    bool isPlain = !words.Any(w => SettingsThatMoveALevel.Contains(w)) && !joined.Contains("-ch_mode", StringComparison.Ordinal) && !joined.Contains("-frame_size", StringComparison.Ordinal);
                    data.Add("flac/" + Path.GetFileName(file), level, isPlain);
                }
            }

            return data;
        }
    }

    private static Node? LevelNode(Report report) => Find(report.Root, "Compression Level");

    private static Node? Find(Node node, string name) => node.Name == name && node.Children.Count > 0 ? node : node.Children.Select(c => Find(c, name)).FirstOrDefault(n => n is not null);

    // "7 or 8", "5, 6 or 7" and "4 to 8".
    private static int[] Named(Node node)
    {
        int[] numbers = [.. Regex.Matches(node.Value!.ToString()!, @"\d+").Select(m => int.Parse(m.Value))];
        return node.Value!.ToString()!.Contains(" to ", StringComparison.Ordinal) ? [.. Enumerable.Range(numbers[0], numbers[1] - numbers[0] + 1)] : numbers;
    }

    [Theory]
    [MemberData(nameof(Written))]
    public void NamesTheLevelTheEncoderWasAsked(string name, int level, bool isPlain)
    {
        Node? node = LevelNode(Fixtures.AnalyzeMpeg(name));
        if (!isPlain)
        {
            return;
        }

        if (node is null)
        {
            // A short stream, or one of a rate the tables lack, is not named; the level fixtures are, but for FFmpeg's levels 0 to 2, which write no linear
            // predictors, so a native file's muxer string alone does not name its encoder.
            Assert.True(!name.Contains("level-", StringComparison.Ordinal) || (name.Contains("ffmpeg-level-", StringComparison.Ordinal) && level <= 2), name);
            return;
        }

        Assert.Contains(level, Named(node));
    }

    [Theory]
    [InlineData("flac/external-level-0-stereo.flac", "libFLAC 0, 1 or 2", "Medium")]
    [InlineData("flac/external-level-3-stereo.flac", "libFLAC 3 to 8", "Medium")]
    [InlineData("flac/external-level-4-stereo.flac", "libFLAC 4 to 8", "Medium")]
    [InlineData("flac/external-level-5-stereo.flac", "libFLAC 5 to 8", "Medium")]
    [InlineData("flac/external-level-6-stereo.flac", "libFLAC 6, 7 or 8", "Medium")]
    [InlineData("flac/external-level-7-stereo.flac", "libFLAC 7 or 8", "Medium")]
    [InlineData("flac/external-level-8-stereo.flac", "libFLAC 7 or 8", "Medium")]
    [InlineData("flac/external-level-7-stereo-libflac-1.2.1.flac", "libFLAC 6, 7 or 8", "Medium")]
    [InlineData("flac/external-level-8-stereo-libflac-1.2.1.flac", "libFLAC 8", "Medium")]
    [InlineData("flac/external-level-7-stereo-libflac-1.3.0.flac", "libFLAC 6, 7 or 8", "Medium")]
    [InlineData("flac/external-level-7-stereo-libflac-1.3.1.flac", "libFLAC 7 or 8", "Medium")]
    public void LevelsThatShareTheirSettingsAreNamedTogether(string name, string levels, string confidence)
    {
        Node node = LevelNode(Fixtures.AnalyzeMpeg(name))!;
        Assert.Equal(levels, node.Value!.ToString());
        Assert.Equal(confidence, node.Children.Single(c => c.Name == "Confidence").Value!.ToString());
    }

    [Theory]
    [InlineData("flac/external-16-stereo-b576.flac")]
    [InlineData("flac/external-16-stereo-b1152.flac")]
    [InlineData("flac/external-16-stereo-b4608.flac")]
    [InlineData("flac/external-16-stereo-b16384.flac")]
    [InlineData("flac/external-lossyflac-b512.flac")]
    [InlineData("flac/external-ffmpeg-16-in-24-mid-side-b512.flac")]
    public void SettingsOfTheirOwnAreNotNamed(string name) => Assert.Null(LevelNode(Fixtures.AnalyzeMpeg(name)));

    // Frames of a higher level that do not use its extra room look like a lower level's: the higher level stays named, and the confidence is low.
    [Theory]
    [InlineData("flac/external-level-6-music-stereo.flac", "libFLAC 4 to 8", "Low")]
    [InlineData("flac/external-ffmpeg-level-5-music-stereo.flac", "FFmpeg 5 to 12", "Low")]
    public void AFittingLevelIsNeverDropped(string name, string levels, string confidence)
    {
        Node node = LevelNode(Fixtures.AnalyzeMpeg(name))!;
        Assert.Equal(levels, node.Value!.ToString());
        Assert.Equal(confidence, node.Children.Single(c => c.Name == "Confidence").Value!.ToString());
    }

    [Fact]
    public void TheValueSaysWhoseLevelsAndTheClaimStaysBelowTheEncoder()
    {
        Assert.Equal("libFLAC 7 or 8", LevelNode(Fixtures.AnalyzeMpeg("flac/external-level-7-stereo.flac"))!.Value!.ToString());
        Node linear = LevelNode(Fixtures.AnalyzeMpeg("flac/external-ffmpeg-level-3-stereo.flac"))!;
        Assert.Equal("FFmpeg 3 to 12", linear.Value!.ToString());
        // FFmpeg's muxer string over linear predictors of its precision: capped at medium.
        Assert.Equal("Medium", linear.Children.Single(c => c.Name == "Confidence").Value!.ToString());
    }

    // FFmpeg's muxer writes its vendor string over a libFLAC stream it copies: the muxer's string alone does not name the encoder, so a stream without linear
    // predictors (levels 0 to 2) is not guessed, and libFLAC's own linear predictors do not have FFmpeg's precision.
    [Theory]
    [InlineData("flac/external-ffmpeg-remux-libflac-level-2-stereo.flac")]
    [InlineData("flac/external-ffmpeg-remux-libflac-level-5-stereo.flac")]
    [InlineData("flac/external-ffmpeg-level-1-stereo.flac")]
    public void AMuxerStringAloneDoesNotNameTheEncoder(string name) => Assert.Null(LevelNode(Fixtures.AnalyzeMpeg(name)));

    [Fact]
    public void EveryLevelClaimHasEvidenceAndADescription()
    {
        Node node = LevelNode(Fixtures.AnalyzeMpeg("flac/external-level-5-stereo.flac"))!;
        Assert.False(string.IsNullOrEmpty(node.Description));
        Assert.Contains(node.Children, c => c.Name == "Evidence" && c.Value!.ToString()!.Contains("block size", StringComparison.Ordinal));
        Assert.All(node.Children, c => Assert.False(string.IsNullOrEmpty(c.Description)));
    }
}
