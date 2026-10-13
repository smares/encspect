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

    // How long a run that could block on a FIFO or loop through links gets before the test fails.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

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
    public void DefaultOutputHasNoDescriptions()
    {
        (_, string text, _) = Run(Tagged);
        (_, string json, _) = Run("--json", Tagged);

        Assert.DoesNotContain("The whole file", text);
        Assert.DoesNotContain("[topic:", text);
        Assert.DoesNotContain("\"description\"", json);
        Assert.DoesNotContain("\"topic\"", json);
    }

    // Each node's description, wrapped at 100 columns and indented under the node, with its topic.
    [Fact]
    public void ExplainDescribesEveryNodeInTheTextTree()
    {
        (int code, string output, _) = Run("--explain", Tagged);

        Assert.Equal(CommandLine.Success, code);
        string[] lines = output.ReplaceLineEndings("\n").Split('\n');
        int mode = Array.FindIndex(lines, l => l.TrimStart().StartsWith("Mode Extension: ", StringComparison.Ordinal));
        Assert.True(mode > 0);
        int indent = lines[mode].Length - lines[mode].TrimStart().Length;
        Assert.StartsWith(new string(' ', indent + 4) + "Which joint stereo tools", lines[mode + 1]);
        Assert.Contains("[topic:", string.Join(' ', lines.Skip(mode + 1).Take(3)));
        Assert.All(lines, l => Assert.True(l.Length <= 100 || !l.StartsWith(' ') || l.TrimStart().Contains(':'), l));
        Assert.Contains("    The whole file,", output);
        Assert.Contains("[topic:id3v2]", output);
    }

    [Fact]
    public void ExplainAddsDescriptionsAndTopicsToJson()
    {
        (int code, string output, _) = Run("--json", "--explain", Tagged);

        Assert.Equal(CommandLine.Success, code);
        JsonElement root = JsonDocument.Parse(output).RootElement[0].GetProperty("root");
        Assert.StartsWith("The whole file", root.GetProperty("description").GetString());
        JsonElement tag = root.GetProperty("children").EnumerateArray().First(c => c.GetProperty("name").GetString() == "ID3v2");
        Assert.Equal("id3v2", tag.GetProperty("topic").GetString());
        JsonElement stream = JsonDocument.Parse(output).RootElement[0].GetProperty("streams")[0];
        Assert.All(stream.GetProperty("series").EnumerateArray(), s => Assert.False(string.IsNullOrEmpty(s.GetProperty("description").GetString())));
    }

    [Fact]
    public void ExplainIsNotForCsv()
    {
        (int code, _, string errors) = Run("--csv", "--explain", Tagged);

        Assert.Equal(CommandLine.UsageError, code);
        Assert.Contains("--explain", errors);
    }

    [Fact]
    public void LearnListsTopicsAndPrintsOne()
    {
        (int code, string list, _) = Run("--learn");
        Assert.Equal(CommandLine.Success, code);
        Assert.Contains("mid-side-stereo", list);
        Assert.Contains("Mid/side stereo", list);

        (code, string article, _) = Run("--learn", "mid-side-stereo");
        Assert.Equal(CommandLine.Success, code);
        Assert.StartsWith("Mid/side stereo" + Environment.NewLine + "===", article);
        Assert.Contains("Related: ", article);
        Assert.All(article.ReplaceLineEndings("\n").Split('\n'), l => Assert.True(l.Length <= 100, l));
        Assert.DoesNotContain("**", article);

        Assert.Equal(article, Run("--learn=mid-side-stereo").Output);
    }

    [Fact]
    public void LearnRejectsUnknownTopicsAndOtherArguments()
    {
        (int code, _, string errors) = Run("--learn", "no-such-topic");
        Assert.Equal(CommandLine.UsageError, code);
        Assert.Contains("Unknown topic", errors);

        Assert.Equal(CommandLine.UsageError, Run("--learn", "scfsi", Tagged).Code);
        Assert.Equal(CommandLine.UsageError, Run(Tagged, "--learn", "scfsi").Code);
    }

    [Fact]
    public void HelpNamesExplainAndLearn()
    {
        string help = Run("--help").Output;

        Assert.Contains("--explain", help);
        Assert.Contains("--learn [<topic>]", help);
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
        Assert.EndsWith(",MPEG Audio,1,MPEG-1 Layer III,0.549,24192,128.0,44100,Joint stereo,\"stereo (front left, front right)\",LAME 3.100,High,\"ID3v2, ID3v1\",0,0,0", lines[1]);
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
        (int code, string output, string errors) = Run("--csv", Path.Combine(Fixtures.FixtureDirectory, "no-such-file.mp3"), Tagged);

        Assert.Equal(CommandLine.UnreadableFile, code);
        Assert.Contains("no-such-file.mp3", errors);

        // The file still has its row, with one error.
        string[] lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains("no-such-file.mp3,,0,", lines[1]);
        Assert.EndsWith(",1,0,0", lines[1]);
    }

    [Fact]
    public void UnreadableFilesHaveAReportInJson()
    {
        string missing = Path.Combine(Fixtures.FixtureDirectory, "no-such-file.mp3");

        (int code, string output, _) = Run("--json", missing, Tagged);

        JsonElement reports = JsonDocument.Parse(output).RootElement;
        Assert.Equal(CommandLine.UnreadableFile, code);
        Assert.Equal(2, reports.GetArrayLength());
        Assert.Equal(missing, reports[0].GetProperty("source").GetString());
        JsonElement finding = Assert.Single(reports[0].GetProperty("findings").EnumerateArray());
        Assert.Equal("file.unreadable", finding.GetProperty("code").GetString());
        Assert.Equal("Error", finding.GetProperty("severity").GetString());
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
            string named = Run("--csv", "--only-format", "MPEG Audio", folder).Output;

            Assert.Contains("b.bin", all);
            Assert.Contains("a.mp3", mpeg);
            Assert.DoesNotContain("b.bin", mpeg);
            Assert.Equal(mpeg, named);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // MP4 and raw AAC are found by their bytes, whatever the files are called.
    [Fact]
    public void OnlyFormatTakesMp4AndAac()
    {
        string folder = Directory.CreateTempSubdirectory("encspect-").FullName;
        try
        {
            File.Copy(Tagged, Path.Combine(folder, "a.mp3"));
            File.Copy(Path.Combine(Fixtures.FixtureDirectory, "mp4", "lc-implicit.m4a"), Path.Combine(folder, "b.bin"));
            File.Copy(Path.Combine(Fixtures.FixtureDirectory, "adts", "cbr.aac"), Path.Combine(folder, "c.mp4"));

            string mp4 = Run("--csv", "--only-format", "mp4", folder).Output;
            string aac = Run("--csv", "--only-format", "aac", folder).Output;
            string both = Run("--csv", "--only-format", "MP4,AAC", folder).Output;

            Assert.Equal([Path.Combine(folder, "b.bin")], Rows(mp4).Select(r => r.Split(',')[0]));
            Assert.StartsWith(Path.Combine(folder, "b.bin") + ",MP4,1,AAC LC,", Rows(mp4)[0]);
            Assert.Equal([Path.Combine(folder, "c.mp4")], Rows(aac).Select(r => r.Split(',')[0]));
            Assert.StartsWith(Path.Combine(folder, "c.mp4") + ",AAC,1,AAC LC,", Rows(aac)[0]);
            Assert.Equal(2, Rows(both).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        static string[] Rows(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
    }

    [Fact]
    public void OnlyFormatOggKeepsOggFiles()
    {
        string folder = Directory.CreateTempSubdirectory("encspect-").FullName;
        try
        {
            File.Copy(Tagged, Path.Combine(folder, "a.mp3"));
            File.Copy(Path.Combine(Fixtures.FixtureDirectory, "ogg", "external-speexenc-nb.spx"), Path.Combine(folder, "b.spx"));

            string ogg = Run("--csv", "--only-format", "ogg", folder).Output;

            Assert.Contains("b.spx,Ogg,1,Speex narrowband", ogg);
            Assert.DoesNotContain("a.mp3", ogg);
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
    [InlineData("--only-format", "flac")]
    [InlineData("--only-format", "mpeg,flac")]
    [InlineData("--only-format", ",")]
    [InlineData("--hide", "")]
    [InlineData("--hide", " , ")]
    [InlineData("--offsets", "--json")]
    [InlineData("--ndjson", "--offsets")]
    [InlineData("--offsets", "--csv")]
    [InlineData("--csv", "--hide", "frames")]
    [InlineData("-")]
    [InlineData("--", "-")]
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
        Assert.Contains("4 the output could not be written", output);
    }

    [Fact]
    public void DoubleDashEndsTheOptions()
    {
        (int code, string output, string errors) = Run("--csv", "--", "--json", Tagged);

        // "--json" is a file name here, one that does not exist.
        Assert.Equal(CommandLine.UnreadableFile, code);
        Assert.StartsWith("Path,", output);
        Assert.StartsWith("--json:", errors);
    }

    [Fact]
    public void OffsetsLocateFindingsInTheTextTree()
    {
        string withOffsets = Run("--offsets", Broken).Output;
        string without = Run(Broken).Output;

        Assert.Contains("frame-truncated: The last frame at 0x7C15 has 405 of its 418 bytes. [0x7C15, 405 bytes]", withOffsets);
        Assert.Contains("frame-truncated: The last frame at 0x7C15 has 405 of its 418 bytes." + Environment.NewLine, without);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--csv")]
    [InlineData("--json")]
    [InlineData("--ndjson")]
    [InlineData("--offsets")]
    public void AFailedWriteExitsWithFour(string option)
    {
        var stderr = new StringWriter();

        int code = CommandLine.Run([option, Tagged], new FullStream(), stderr);

        Assert.Equal(CommandLine.OutputFailed, code);
        Assert.Equal("Could not write the output: No space left on device" + Environment.NewLine, stderr.ToString());
    }

    [Fact]
    public void FoldersIncludeHiddenEntriesInPathOrder()
    {
        using var folder = new TempFolder();
        folder.Copy(Tagged, "z.mp3");
        folder.Copy(Tagged, ".hidden.mp3");
        folder.Copy(Tagged, ".hiddendir/x.mp3");
        folder.Copy(Tagged, "sub/x.mp3");
        folder.Copy(Tagged, "sub.mp3");

        (int code, string output, _) = Run("--csv", folder.Path);

        string[] paths = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l[..l.IndexOf(",MPEG", StringComparison.Ordinal)]).ToArray();
        Assert.Equal(CommandLine.Success, code);
        Assert.Equal(new[] { ".hidden.mp3", ".hiddendir/x.mp3", "sub.mp3", "sub/x.mp3", "z.mp3" }.Select(p => folder[p]), paths);
    }

    [Fact]
    public async Task FolderLinksLeadingIntoOrAboveTheTreeAreNotFollowed()
    {
        // Creating symbolic links on Windows needs developer mode or administrator rights.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new TempFolder();
        folder.Copy(Tagged, "root/a.mp3");
        folder.Copy(Tagged, "root/sub/b.mp3");
        folder.Copy(Tagged, "outside/c.mp3");
        Directory.CreateSymbolicLink(folder["root/sub/loop"], "..");
        Directory.CreateSymbolicLink(folder["root/sub/up"], "../..");
        Directory.CreateSymbolicLink(folder["root/sub/self"], ".");
        Directory.CreateSymbolicLink(folder["root/alias"], "sub");
        Directory.CreateSymbolicLink(folder["root/top"], "/");
        Directory.CreateSymbolicLink(folder["root/x1"], folder["outside"]);
        Directory.CreateSymbolicLink(folder["root/x2"], "../outside");
        Directory.CreateSymbolicLink(folder["outside/back"], "../root/sub");

        (int code, string output, _) = await Task.Run(() => Run("--csv", folder["root"])).WaitAsync(Timeout);

        string[] paths = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l[..l.IndexOf(',', StringComparison.Ordinal)]).ToArray();
        Assert.Equal(CommandLine.Success, code);
        Assert.Equal(new[] { "root/a.mp3", "root/sub/b.mp3", "root/x1/c.mp3" }.Select(p => folder[p]), paths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FifosAreReportedWithoutBeingOpened(bool withWriter)
    {
        // Windows has no FIFOs in the file system.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new TempFolder();
        folder.Copy(Tagged, "a.mp3");
        string fifo = folder.MakeFifo("sub/pipe");

        // A writer's open blocks until a reader comes: the analyzer's look at the FIFO or the cleanup.
        // What it writes is never read, so it stays below PIPE_BUF (at least 512 bytes everywhere),
        // which always fits the pipe's buffer; a macOS FIFO's buffer can be smaller than a whole MP3.
        Task writer = withWriter ? Task.Run(() => File.WriteAllBytes(fifo, new byte[100])) : Task.CompletedTask;
        try
        {
            var (csv, filtered, text) = await Task.Run(() => (Run("--csv", folder.Path), Run("--csv", "--only-format", "mpeg", folder.Path), Run(fifo))).WaitAsync(Timeout);

            foreach ((int code, string output, string errors) in new[] { csv, filtered })
            {
                string[] lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(CommandLine.UnreadableFile, code);
                Assert.Equal(3, lines.Length);
                Assert.StartsWith(folder["a.mp3"] + ",MPEG Audio,", lines[1]);
                Assert.Equal(fifo + ",,0,,,,,,,,,,,1,0,0", lines[2]);
                Assert.Equal($"{fifo}: This is a named pipe, socket or terminal, not a regular file, so it was not read.{Environment.NewLine}", errors);
            }

            Assert.Equal(CommandLine.UnreadableFile, text.Code);
            Assert.Contains("[Error] file.not-regular: This is a named pipe, socket or terminal", text.Output);
        }
        finally
        {
            if (withWriter)
            {
                // Opened for reading and writing, a FIFO never blocks, and it lets a writer that is
                // still waiting finish; its bytes fit the pipe's buffer. A writer that the
                // analyzer's look at the FIFO let through has lost its reader since: a broken pipe.
                using (new FileStream(fifo, FileMode.Open, FileAccess.ReadWrite))
                {
                    try
                    {
                        await writer.WaitAsync(Timeout);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
    }

    [Fact]
    public void DevicesAreReportedWithoutBeingRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        (int code, string output, string errors) = Run("--csv", "/dev/zero", Tagged);

        string[] lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(CommandLine.UnreadableFile, code);
        Assert.Equal("/dev/zero,,0,,,,,,,,,,,1,0,0", lines[1]);
        Assert.Contains(",MPEG Audio,", lines[2]);
        Assert.StartsWith("/dev/zero: This is a device or kernel file that reports a size of 0 but yields data, not a regular file", errors);
    }

    [Fact]
    public void UnreadableFoldersAreReported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new TempFolder();
        folder.Copy(Tagged, "locked/a.mp3");
        folder.Copy(Tagged, "ok/a.mp3");
        File.SetUnixFileMode(folder["locked"], UnixFileMode.None);

        // Root, or a user with CAP_DAC_READ_SEARCH, reads the folder anyway.
        if (CanList(folder["locked"]))
        {
            return;
        }

        (int code, string output, string errors) = Run("--csv", folder.Path);

        string[] lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(CommandLine.UnreadableFile, code);
        Assert.Equal(3, lines.Length);
        Assert.Equal(folder["locked"] + ",,0,,,,,,,,,,,1,0,0", lines[1]);
        Assert.StartsWith(folder["ok/a.mp3"] + ",MPEG Audio,", lines[2]);
        Assert.StartsWith(folder["locked"] + ": ", errors);
    }

    [Fact]
    public void FilesWithoutReadPermissionAreReported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new TempFolder();
        string locked = folder.Copy(Tagged, "locked.mp3");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        if (CanRead(locked))
        {
            return;
        }

        (int code, string output, string errors) = Run("--ndjson", folder.Path);

        using JsonDocument report = JsonDocument.Parse(output);
        Assert.Equal(CommandLine.UnreadableFile, code);
        Assert.Equal("file.unreadable", report.RootElement.GetProperty("findings")[0].GetProperty("code").GetString());
        Assert.StartsWith(locked + ": ", errors);
    }

    // .NET decodes such names lossily, so the name it lists opens nothing; see docs/cli.md.
    [Fact]
    public void NamesThatAreNotUtf8AreReportedAsUnreadable()
    {
        // macOS and Windows file systems store only valid Unicode names.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var folder = new TempFolder();
        folder.Copy(Tagged, "a.mp3");
        Assert.True(TempFolder.Exec("sh", "-c", "cp \"$1\" \"$2/$(printf 'bad\\377.mp3')\" && mkdir \"$2/$(printf 'dir\\376')\"", "sh", Tagged, folder.Path));

        (int code, string output, string errors) = Run("--csv", folder.Path);

        string[] lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(CommandLine.UnreadableFile, code);
        Assert.Equal(4, lines.Length);
        Assert.Contains(",MPEG Audio,", lines[1]);
        Assert.EndsWith(",,0,,,,,,,,,,,1,0,0", lines[2]);
        Assert.EndsWith(",,0,,,,,,,,,,,1,0,0", lines[3]);
        Assert.Equal(2, errors.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private static bool CanList(string folder)
    {
        try
        {
            Directory.EnumerateFileSystemEntries(folder).ToList();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanRead(string file)
    {
        try
        {
            File.OpenRead(file).Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Standard output on a full disk.
    private sealed class FullStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("No space left on device");

        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("No space left on device");

        public override void WriteByte(byte value) => throw new IOException("No space left on device");
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
        Assert.Equal(0.549, summary.Duration!.Value, 3);
        Assert.Equal(128.0, summary.AverageBitrate!.Value, 1);
        Assert.Equal(44100, summary.SampleRate);
        Assert.Equal("Joint stereo", summary.ChannelMode);
        Assert.Equal("LAME 3.100", summary.Encoder);
        Assert.Equal(Confidence.High, summary.EncoderConfidence);
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
        var frame = new Node("Frame", 0, 4) { Kind = NodeKind.Frame };

        Assert.True(ReportFilter.All.Includes(frame));
        Assert.False(new ReportFilter(ReportSections.Frames).Includes(frame));
        Assert.False(new ReportFilter(ReportSections.SideInfo).Includes(new Node("Side Info", 4, 32) { Kind = NodeKind.SideInfo }));
        Assert.True(new ReportFilter(ReportSections.Frames).Includes(new Node("Side Info", 4, 32) { Kind = NodeKind.SideInfo }));
    }

    // Sections are found by kind, never by name: an APE item named "Frame" or "Statistics" is tag
    // content, and a node named "Frame" that is not a frame stays.
    [Fact]
    public void FilterMatchesKindsNotNames()
    {
        var filter = new ReportFilter(ReportSections.Frames | ReportSections.Statistics | ReportSections.SideInfo);

        Assert.True(filter.Includes(new Node("Frame", 0, 4)));
        Assert.True(filter.Includes(new Node("Statistics", 0, 4)));
        Assert.True(filter.Includes(new Node("Side Info", 0, 4)));
        Assert.False(filter.Includes(new Node("Anything", 0, 4) { Kind = NodeKind.Statistics }));
        Assert.True(new ReportFilter(ReportSections.Tags).Includes(new Node("ID3v2", 0, 4) { Kind = NodeKind.Stream }));
        Assert.False(new ReportFilter(ReportSections.Tags).Includes(new Node("Monkey", 0, 4) { Kind = NodeKind.Tag }));
    }
}
