using System.Text;
using System.Text.Json;
using Encspect.Cli;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

public class CommandLineTests
{
    private static readonly string Tagged = Path.Combine(Fixtures.FixtureDirectory, "mpeg", "external-lame-tagged.mp3");

    // BladeEnc 0.80 writes frames whose main data overflows: three warnings, no errors.
    private static readonly string Broken = Path.Combine(Fixtures.FixtureDirectory, "mpeg", "external-blade080-stereo.mp3");

    private static (int Code, string Output, string Errors) Run(params string[] args)
    {
        using var stdout = new MemoryStream();
        var stderr = new StringWriter();
        int code = CommandLine.Run(args, stdout, stderr);
        return (code, Encoding.UTF8.GetString(stdout.ToArray()), stderr.ToString());
    }

    [Fact]
    public void TextTreeIsTheDefault()
    {
        (int code, string output, _) = Run(Tagged);

        Assert.Equal(CommandLine.Success, code);
        Assert.Contains("  File: MPEG Audio", output);
        Assert.Contains("Frame: MPEG-1 Layer III", output);
    }

    [Fact]
    public void HideLeavesOutSections()
    {
        (_, string output, _) = Run("--hide", "frames,tags", Tagged);

        Assert.DoesNotContain("Frame:", output);
        Assert.DoesNotContain("ID3v2: 2.3.0", output);
        Assert.DoesNotContain("ID3v1: 1.0", output);
        Assert.Contains("Statistics:", output);
    }

    [Fact]
    public void HideAppliesToJson()
    {
        (_, string output, _) = Run("--json", "--hide=statistics,side-info", Tagged);

        string json = JsonDocument.Parse(output).RootElement.GetRawText();
        Assert.DoesNotContain("\"Statistics\"", json);
        Assert.DoesNotContain("\"Side Info\"", json);
        Assert.Contains("\"Frame\"", json);
    }

    [Fact]
    public void CsvHasAHeaderAndARowPerFile()
    {
        (int code, string output, _) = Run("--csv", Tagged, Broken);

        string[] lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(CommandLine.Success, code);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Path,Format,Streams,Codec,Duration (s)", lines[0]);
        Assert.EndsWith(",MPEG Audio,1,MPEG-1 Layer III,0.549,128.0,44100,Joint stereo,LAME 3.100,High,\"ID3v2, ID3v1\",0,0,0", lines[1]);
        Assert.EndsWith(",0,3,0", lines[2]);
    }

    [Theory]
    [InlineData("warning", CommandLine.FindingsFound)]
    [InlineData("info", CommandLine.FindingsFound)]
    [InlineData("error", CommandLine.Success)]
    public void FailOnSetsTheExitCode(string severity, int expected)
    {
        Assert.Equal(expected, Run("--csv", "--fail-on", severity, Broken).Code);
    }

    [Fact]
    public void FindingsAloneDoNotFail()
    {
        Assert.Equal(CommandLine.Success, Run("--csv", Broken).Code);
    }

    [Fact]
    public void UnreadableFilesExitWithThree()
    {
        (int code, _, string errors) = Run("--csv", Path.Combine(Fixtures.FixtureDirectory, "no-such-file.mp3"), Tagged);

        Assert.Equal(CommandLine.UnreadableFile, code);
        Assert.Contains("no-such-file.mp3", errors);
    }

    [Fact]
    public void OnlyFormatSkipsOtherFormats()
    {
        string folder = Directory.CreateTempSubdirectory("encspect-").FullName;
        try
        {
            File.Copy(Tagged, Path.Combine(folder, "a.mp3"));
            File.WriteAllBytes(Path.Combine(folder, "b.bin"), [1, 2, 3, 4, 5, 6, 7, 8]);

            string all = Run("--csv", folder).Output;
            string mpeg = Run("--csv", "--only-format", "mpeg", folder).Output;
            string other = Run("--csv", "--only-format", "flac", folder).Output;

            Assert.Contains("b.bin", all);
            Assert.Contains("a.mp3", mpeg);
            Assert.DoesNotContain("b.bin", mpeg);
            Assert.Single(other.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--json", "--csv")]
    [InlineData("--hide", "lyrics")]
    [InlineData("--fail-on", "1")]
    [InlineData("--fail-on")]
    [InlineData("--json=yes")]
    public void WrongUsageExitsWithTwo(params string[] options)
    {
        (int code, string output, string errors) = Run([.. options, Tagged]);

        Assert.Equal(CommandLine.UsageError, code);
        Assert.Empty(output);
        Assert.NotEmpty(errors);
    }

    // ID3v1 text is ISO 8859-1; every output format writes it as UTF-8, without a byte order mark.
    [Theory]
    [InlineData("--csv")]
    [InlineData("--json")]
    [InlineData("--ndjson")]
    [InlineData("--offsets")]
    public void OutputIsUtf8(string format)
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("encspect-").FullName, "Björk.mp3");
        try
        {
            byte[] tag = new byte[128];
            "TAG"u8.CopyTo(tag);
            Encoding.Latin1.GetBytes("Jóga").CopyTo(tag, 3);
            Encoding.Latin1.GetBytes("Björk").CopyTo(tag, 33);
            File.WriteAllBytes(path, [.. new byte[64], .. tag]);

            using var stdout = new MemoryStream();
            CommandLine.Run([format, path], stdout, new StringWriter());

            byte[] bytes = stdout.ToArray();
            string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.Contains("Björk", text);
            if (format != "--csv")
            {
                Assert.Contains("Jóga", text);
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void NoPathIsWrongUsage()
    {
        Assert.Equal(CommandLine.UsageError, Run("--csv").Code);
    }

    [Fact]
    public void HelpPrintsTheUsage()
    {
        (int code, string output, _) = Run("--help");

        Assert.Equal(CommandLine.Success, code);
        Assert.StartsWith("Usage: encspect", output);
    }
}

public class ReportSummaryTests
{
    [Fact]
    public void SummaryReadsTheFirstStream()
    {
        ReportSummary summary = ReportSummary.From(Fixtures.AnalyzeMpeg("mpeg/external-lame-tagged.mp3"));

        Assert.Equal("MPEG Audio", summary.Format);
        Assert.Equal(1, summary.Streams);
        Assert.Equal("MPEG-1 Layer III", summary.Codec);
        Assert.Equal("0.549", summary.Duration);
        Assert.Equal("128.0", summary.AverageBitrate);
        Assert.Equal("44100", summary.SampleRate);
        Assert.Equal("Joint stereo", summary.ChannelMode);
        Assert.Equal("LAME 3.100", summary.Encoder);
        Assert.Equal("High", summary.EncoderConfidence);
        Assert.Equal(["ID3v2", "ID3v1"], summary.Tags);
    }

    [Fact]
    public void UnrecognizedFilesHaveOnlyFindings()
    {
        var report = Analyzer.CreateDefault().Analyze("data.bin", new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]));

        ReportSummary summary = ReportSummary.From(report);

        Assert.Null(summary.Format);
        Assert.Equal(0, summary.Streams);
        Assert.Null(summary.Duration);
        Assert.Equal(1, summary.Infos);
    }

    [Fact]
    public void CsvQuotesSeparatorsAndDefusesFormulas()
    {
        var root = new Node("File", 0, 10);
        var writer = new StringWriter();

        ReportCsvWriter.Write([new Report("=cmd|' /C calc'!A0", root, []), new Report("a,\"b\".mp3", root, [])], writer);

        string[] lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("'=cmd|' /C calc'!A0,", lines[1]);
        Assert.StartsWith("\"a,\"\"b\"\".mp3\",", lines[2]);
    }

    [Fact]
    public void DetectFormatLooksPastLeadingTags()
    {
        var analyzer = Analyzer.CreateDefault();

        Assert.Equal("MPEG Audio", analyzer.DetectFormat(Path.Combine(Fixtures.FixtureDirectory, "mpeg", "external-lame-tagged.mp3")));
    }

    [Fact]
    public void FilterKeepsEverythingByDefault()
    {
        var frame = new Node("Frame", 0, 4);

        Assert.True(ReportFilter.All.Includes(frame, null));
        Assert.False(new ReportFilter(ReportSections.Frames).Includes(frame, null));
        Assert.False(new ReportFilter(ReportSections.SideInfo).Includes(new Node("Side Info", 4, 32), frame));
        Assert.True(new ReportFilter(ReportSections.SideInfo).Includes(new Node("Side Info", 4, 32), new Node("Other", 0, 4)));
    }
}
