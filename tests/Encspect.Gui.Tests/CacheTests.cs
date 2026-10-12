using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Encspect.Gui.Cache;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

public sealed class CacheTests : IDisposable
{
    private const string Version = "1.0.0+test";

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_cached_summary_is_found_while_the_file_is_unchanged_also_in_a_later_session()
    {
        string file = _folder.Copy(Fixtures.Mpeg("external-lame-vbr-v2.mp3"), "song.mp3");
        FileSummary summary = Summarize(file);
        FileFingerprint fingerprint = FileFingerprint.Read(file)!;
        using (SummaryCache cache = Cache())
        {
            Assert.Null(cache.TryGet(file, fingerprint));
            cache.Put(file, fingerprint, summary);
            AssertSame(summary, cache.TryGet(file, FileFingerprint.Read(file)!));
        }

        using SummaryCache later = Cache();
        AssertSame(summary, later.TryGet(file, FileFingerprint.Read(file)!));
        Assert.Null(later.TryGet(_folder["other.mp3"], fingerprint));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("time")]
    [InlineData("head")]
    [InlineData("tail")]
    [InlineData("file ID")]
    public void A_change_to_any_part_of_the_files_key_misses(string part)
    {
        string file = _folder.Write("long.mp3", Long(3));
        using SummaryCache cache = Cache();
        cache.Put(file, FileFingerprint.Read(file)!, Summarize(file));
        DateTime time = File.GetLastWriteTimeUtc(file);

        switch (part)
        {
            case "size":
                using (FileStream stream = File.Open(file, FileMode.Append))
                {
                    stream.WriteByte(0);
                }

                File.SetLastWriteTimeUtc(file, time);
                break;
            case "time":
                File.SetLastWriteTimeUtc(file, time.AddSeconds(2));
                break;
            case "head":
            case "tail":
                // A tagger rewriting a tag in place: same size, and the time put back.
                using (FileStream stream = File.Open(file, FileMode.Open))
                {
                    stream.Position = part == "head" ? 100 : stream.Length - 100;
                    stream.WriteByte(0x5A);
                }

                File.SetLastWriteTimeUtc(file, time);
                break;
            case "file ID":
                // Another file with the same bytes and time put in its place.
                string copy = _folder.Write("copy.tmp", File.ReadAllBytes(file));
                File.Move(copy, file, overwrite: true);
                File.SetLastWriteTimeUtc(file, time);
                Assert.SkipWhen(FileFingerprint.Read(file)!.Id is null, "This system gives files no ID.");
                break;
        }

        FileFingerprint changed = FileFingerprint.Read(file)!;
        Assert.Null(cache.TryGet(file, changed));
    }

    [Fact]
    public void Bytes_between_the_first_and_last_64_KiB_are_not_read()
    {
        string file = _folder.Write("long.mp3", Long(3));
        FileFingerprint before = FileFingerprint.Read(file)!;
        DateTime time = File.GetLastWriteTimeUtc(file);
        using (FileStream stream = File.Open(file, FileMode.Open))
        {
            stream.Position = stream.Length / 2;
            stream.WriteByte(0x5A);
        }

        File.SetLastWriteTimeUtc(file, time);

        Assert.True(before.Matches(FileFingerprint.Read(file)!));
    }

    [Theory]
    [InlineData("analyzer")]
    [InlineData("schema")]
    public void Another_analyzer_or_schema_version_misses(string part)
    {
        string file = _folder.Copy(Fixtures.Mpeg("external-lame-vbr-v2.mp3"), "song.mp3");
        FileFingerprint fingerprint = FileFingerprint.Read(file)!;
        using (SummaryCache cache = Cache())
        {
            cache.Put(file, fingerprint, Summarize(file));
        }

        using SummaryCache other = part == "analyzer" ? Cache(version: "1.0.0+another-commit") : Cache(schemaVersion: ReportJsonWriter.SchemaVersion + 1);
        Assert.Null(other.TryGet(file, fingerprint));
        using SummaryCache same = Cache();
        Assert.NotNull(same.TryGet(file, fingerprint));
    }

    [Fact]
    public void The_analyzer_version_names_the_commit_and_the_analyzers_code()
    {
        string version = AnalyzerVersion.Current;

        Assert.StartsWith(ReportJsonWriter.ToolVersion + " ", version, StringComparison.Ordinal);
        Assert.Contains(typeof(Analyzer).Assembly.ManifestModule.ModuleVersionId.ToString("N"), version.Split(' '));
        Assert.Contains(DefaultParsers.Formats[0].GetType().Assembly.ManifestModule.ModuleVersionId.ToString("N"), version.Split(' '));
        Assert.Contains(typeof(FileSummary).Assembly.ManifestModule.ModuleVersionId.ToString("N"), version.Split(' '));

        // Built from a git checkout, the version names the commit.
        if (Directory.Exists(Path.Combine(Fixtures.Directory, "..", "..", ".git")) || File.Exists(Path.Combine(Fixtures.Directory, "..", "..", ".git")))
        {
            Assert.Matches("^[0-9.]+\\+[0-9a-f]{40}$", ReportJsonWriter.ToolVersion);
        }
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("truncated")]
    [InlineData("flipped")]
    [InlineData("empty")]
    public void A_corrupt_store_reads_as_empty_and_is_rebuilt(string damage)
    {
        string file = _folder.Copy(Fixtures.Mpeg("external-lame-vbr-v2.mp3"), "song.mp3");
        FileFingerprint fingerprint = FileFingerprint.Read(file)!;
        using (SummaryCache cache = Cache())
        {
            cache.Put(file, fingerprint, Summarize(file));
        }

        byte[] store = File.ReadAllBytes(StorePath);
        byte[] damaged = damage switch
        {
            "garbage" => [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 31))],
            "truncated" => store[..(store.Length / 2)],
            "flipped" => [.. store[..200], (byte)(store[200] ^ 1), .. store[201..]],
            _ => [],
        };
        File.WriteAllBytes(StorePath, damaged);

        using (SummaryCache cache = Cache())
        {
            Assert.Equal(0, cache.Count);
            Assert.Null(cache.TryGet(file, fingerprint));
            cache.Put(file, fingerprint, Summarize(file));
        }

        using SummaryCache rebuilt = Cache();
        Assert.NotNull(rebuilt.TryGet(file, fingerprint));
    }

    [Fact]
    public void Two_instances_writing_at_once_leave_a_valid_store()
    {
        string file = _folder.Copy(Fixtures.Mpeg("external-lame-vbr-v2.mp3"), "song.mp3");
        FileFingerprint fingerprint = FileFingerprint.Read(file)!;
        FileSummary summary = Summarize(file);
        using SummaryCache first = Cache();
        using SummaryCache second = Cache();

        Parallel.For(0, 2, writer =>
        {
            SummaryCache cache = writer == 0 ? first : second;
            for (int batch = 0; batch < 20; batch++)
            {
                for (int i = 0; i < 50; i++)
                {
                    cache.Put($"/writer{writer}/batch{batch}/{i}.mp3", fingerprint, summary);
                }

                cache.Flush();
            }
        });

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StorePath)!, "*.tmp"));
        using SummaryCache reader = Cache();

        // Each write merges what is on disk, so what a write loses is the other's latest batch.
        Assert.InRange(reader.Count, 50, 2000);
        Assert.NotNull(reader.TryGet("/writer0/batch19/49.mp3", fingerprint) ?? reader.TryGet("/writer1/batch19/49.mp3", fingerprint));
    }

    [Fact]
    public void The_least_recently_used_entries_are_evicted_past_the_bound()
    {
        string file = _folder.Copy(Fixtures.Mpeg("external-lame-vbr-v2.mp3"), "song.mp3");
        FileFingerprint fingerprint = FileFingerprint.Read(file)!;
        FileSummary summary = Summarize(file);
        long now = 0;
        using (var cache = new SummaryCache(StorePath, Version, maxEntries: 10, flushDelay: TimeSpan.FromHours(1), clock: () => ++now))
        {
            for (int i = 0; i < 25; i++)
            {
                cache.Put($"/music/{i}.mp3", fingerprint, summary);
            }

            // A hit counts as a use.
            Assert.NotNull(cache.TryGet("/music/0.mp3", fingerprint));
            Assert.NotNull(cache.TryGet("/music/1.mp3", fingerprint));
        }

        using SummaryCache later = Cache();
        Assert.Equal(10, later.Count);
        Assert.NotNull(later.TryGet("/music/0.mp3", fingerprint));
        Assert.NotNull(later.TryGet("/music/1.mp3", fingerprint));
        Assert.NotNull(later.TryGet("/music/24.mp3", fingerprint));
        Assert.Null(later.TryGet("/music/2.mp3", fingerprint));
        Assert.Null(later.TryGet("/music/16.mp3", fingerprint));
        Assert.True(new FileInfo(StorePath).Length < 10 * 1024);
    }

    [Fact]
    public void A_hit_does_not_rewrite_the_store_until_the_final_flush()
    {
        string file = _folder.Copy(Fixtures.Mpeg("external-lame-vbr-v2.mp3"), "song.mp3");
        FileFingerprint fingerprint = FileFingerprint.Read(file)!;
        long now = 0;
        byte[] written;
        using (var cache = new SummaryCache(StorePath, Version, flushDelay: TimeSpan.FromMilliseconds(1), clock: () => ++now))
        {
            cache.Put(file, fingerprint, Summarize(file));
            cache.Put(file + ".other", fingerprint, Summarize(file));

            // A scheduled flush may have written the first entry alone; this writes both.
            cache.Flush();
            written = File.ReadAllBytes(StorePath);

            for (int i = 0; i < 100; i++)
            {
                Assert.NotNull(cache.TryGet(file, fingerprint));
            }

            // Many flush delays later, nothing was written for the hits.
            Thread.Sleep(100);
            Assert.Equal(written, File.ReadAllBytes(StorePath));
        }

        Assert.NotEqual(written, File.ReadAllBytes(StorePath));

        // The hits made the entry the more recently used of the two, so a bound evicts the other.
        using (var bounded = new SummaryCache(StorePath, Version, maxEntries: 2, flushDelay: TimeSpan.FromHours(1), clock: () => ++now))
        {
            bounded.Put(_folder["third.mp3"], fingerprint, Summarize(file));
        }

        using SummaryCache later = Cache();
        Assert.NotNull(later.TryGet(file, fingerprint));
        Assert.Null(later.TryGet(file + ".other", fingerprint));
    }

    [Fact]
    public void Progress_never_counts_more_files_analyzed_than_listed()
    {
        string song = _folder.Copy(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "song.mp3");
        for (int i = 0; i < 3000; i++)
        {
            _folder.Write($"many/{i:D4}.bin", []);
        }

        using SummaryCache cache = Cache();
        cache.Put(song, FileFingerprint.Read(song)!, Summarize(song));

        // The song is found in the cache while the folder after it is still being listed, so its
        // result comes before its row is listed.
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var model = new MainViewModel(Analyzer.CreateDefault(), queue.Enqueue, parallelism: 2, roots: [], cache: cache);
        model.Open(FileSource.ForItems([song, _folder["many"]]));
        var clock = Stopwatch.StartNew();
        while (model.IsRunning && clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (queue.TryDequeue(out Action? action))
            {
                action();
                Assert.True(model.Analyzed <= model.Total, $"{model.Analyzed} analyzed of {model.Total} listed.");
            }
        }

        Assert.Equal(3001, model.Analyzed);
        Assert.True(model.Rows[0].IsCached);
    }

    [Fact]
    public void Clear_forgets_every_entry_and_deletes_the_store()
    {
        string file = _folder.Copy(Fixtures.Mpeg("external-lame-vbr-v2.mp3"), "song.mp3");
        FileFingerprint fingerprint = FileFingerprint.Read(file)!;
        using SummaryCache cache = Cache();
        cache.Put(file, fingerprint, Summarize(file));
        cache.Flush();
        Assert.True(File.Exists(StorePath));

        cache.Clear();

        Assert.False(File.Exists(StorePath));
        Assert.Null(cache.TryGet(file, fingerprint));
        using SummaryCache later = Cache();
        Assert.Equal(0, later.Count);
    }

    [Fact]
    public void A_folder_view_takes_unchanged_files_from_the_cache_and_analyzes_only_the_changed_one()
    {
        string music = Path.Combine(_folder.Path, "music");
        string[] names = ["a.mp3", "b.mp3", "c.mp3"];
        string[] fixtures = ["external-lame-vbr-v2.mp3", "external-lame-cbr128-joint.mp3", "external-blade-stereo.mp3"];
        for (int i = 0; i < names.Length; i++)
        {
            _folder.Copy(Fixtures.Mpeg(fixtures[i]), "music/" + names[i]);
        }

        CountingParser cold = CountingParser.Mpeg();
        using (SummaryCache cache = Cache())
        {
            MainViewModel model = Run(cold, cache, music);
            Assert.Equal(3, cold.Lengths.Count);
            Assert.All(model.Rows, r => Assert.False(r.IsCached));
        }

        // b.mp3 becomes another file between the sessions.
        File.Copy(Fixtures.Mpeg("external-lame-tagged.mp3"), Path.Combine(music, "b.mp3"), overwrite: true);
        CountingParser warm = CountingParser.Mpeg();
        using (SummaryCache cache = Cache())
        {
            MainViewModel model = Run(warm, cache, music);
            Assert.Single(warm.Lengths);
            Assert.Equal([true, false, true], model.Rows.Select(r => r.IsCached));
            Assert.Equal("LAME 3.100 (high)", model.Rows[1].EncoderText);
            Assert.Contains("ID3v2", model.Rows[1].Tags, StringComparison.Ordinal);
            Assert.Equal("MPEG Audio", model.Rows[0].Format);
            Assert.EndsWith("VBR", model.Rows[0].BitrateText, StringComparison.Ordinal);
            Assert.Equal("3 files", model.Status);
        }
    }

    [AvaloniaFact]
    public void The_detail_window_analyzes_the_file_and_never_shows_a_cached_summary()
    {
        string file = _folder.Copy(Fixtures.Mpeg("external-lame-vbr-v2.mp3"), "song.mp3");
        FileSummary real = Summarize(file);
        FileSummary planted = real with { Summary = real.Summary with { Format = "Planted", Codec = "From the cache" } };
        using SummaryCache cache = Cache();
        cache.Put(file, FileFingerprint.Read(file)!, planted);
        CountingParser parser = CountingParser.Mpeg();

        var main = new MainWindow(parser.Analyzer(), new Settings(), new MainViewModel(parser.Analyzer(), roots: [], cache: cache));
        main.Show();
        main.Model.Open(FileSource.ForFolder(_folder.Path));
        UiPump.Until(() => !main.Model.IsRunning);
        FileRow row = Assert.Single(main.Model.Rows);
        Assert.True(row.IsCached);
        Assert.Equal("Planted", row.Format);
        Assert.Empty(parser.Lengths);

        DetailWindow detail = main.DetailWindows.Open(row);
        UiPump.Until(() => detail.Model.Report is not null);

        Assert.Single(parser.Lengths);
        Assert.Equal("MPEG Audio", detail.Model.Report!.Root.Value?.ToString());
        Assert.StartsWith("MPEG-1 Layer III", detail.Model.StreamsText, StringComparison.Ordinal);
        main.Close();
    }

    [Fact]
    public void A_file_ID_is_the_same_through_a_hard_link_and_differs_between_files()
    {
        string file = _folder.Write("a.bin", [1, 2, 3]);
        string other = _folder.Write("b.bin", [1, 2, 3]);
        string link = _folder["link.bin"];
        HardLink(file, link);

        FileId? id = IdOf(file);
        Assert.SkipWhen(id is null && !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()), "This system gives files no ID.");

        Assert.NotNull(id);
        Assert.Equal(id, IdOf(link));
        Assert.NotEqual(id, IdOf(other));
        Assert.Equal(id!.Value.Volume, IdOf(other)!.Value.Volume);
    }

    [Fact]
    public void A_file_ID_on_unix_is_the_device_and_inode_stat_reports()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Device and inode are what Linux and macOS use.");
        string file = _folder.Write("a.bin", [1, 2, 3]);

        string output = Command("stat", OperatingSystem.IsLinux() ? ["-c", "%d %i", file] : ["-f", "%d %i", file]);

        string[] parts = output.Trim().Split(' ');
        Assert.Equal(new FileId(ulong.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), ulong.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)), IdOf(file));
    }

    private string StorePath => _folder["cache/summaries.bin"];

    private SummaryCache Cache(string version = Version, int schemaVersion = ReportJsonWriter.SchemaVersion) =>
        new(StorePath, version, schemaVersion, flushDelay: TimeSpan.FromHours(1));

    private static FileSummary Summarize(string path) => FileSummary.From(Analyzer.CreateDefault().AnalyzeFile(path));

    private static MainViewModel Run(CountingParser parser, SummaryCache cache, string folder)
    {
        var ui = new TestDispatcher();
        var model = new MainViewModel(parser.Analyzer(), ui.Post, parallelism: 2, roots: [], cache: cache);
        model.Open(FileSource.ForFolder(folder));
        ui.Until(() => !model.IsRunning);
        return model;
    }

    // A file of more than 128 KiB, so its middle is neither in the first nor the last 64 KiB.
    private static byte[] Long(int copies)
    {
        byte[] fixture = File.ReadAllBytes(Fixtures.Mpeg("external-blade-flags.mp3"));
        return [.. Enumerable.Repeat(fixture, copies).SelectMany(b => b)];
    }

    private static void AssertSame(FileSummary expected, FileSummary? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.IsVbr, actual.IsVbr);
        Assert.Equal(expected.Problem, actual.Problem);
        Assert.Equal(expected.Summary.Tags, actual.Summary.Tags);
        Assert.Equal(expected.Summary.ChannelLayout, actual.Summary.ChannelLayout);
        Assert.Equal(expected.Summary with { Tags = [], ChannelLayout = null }, actual.Summary with { Tags = [], ChannelLayout = null });
    }

    private static FileId? IdOf(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return FileId.Of(stream.SafeFileHandle);
    }

    private static void HardLink(string target, string link)
    {
        if (OperatingSystem.IsWindows())
        {
            Command("cmd", ["/c", "mklink", "/H", link, target]);
        }
        else
        {
            Command("ln", [target, link]);
        }
    }

    private static string Command(string name, string[] arguments)
    {
        var start = new ProcessStartInfo(name) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }
}
