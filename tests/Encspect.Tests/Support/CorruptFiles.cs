using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The check behind the CorruptTagFileTests classes: truncated, byte-flipped and garbage-prefixed variants of a
// fixture must yield a report, never an exception or a hang, and every node must stay inside the file and its
// parent. The hang guard is per variant: a variant takes milliseconds, so the limit is far from anything CPU
// contention on a slow runner causes, where a limit on a fixture's 1,300 or so variants together was not
// (macOS on x64 once took over a minute for one fixture's).
internal static class CorruptFiles
{
    private const int FlipsPerFixture = 300;
    private static readonly TimeSpan VariantLimit = TimeSpan.FromSeconds(10);

    public static TheoryData<string> Names(Func<string, bool> include)
    {
        var data = new TheoryData<string>();
        foreach (string name in All().Where(include))
        {
            data.Add(name);
        }

        return data;
    }

    public static IEnumerable<string> All()
    {
        foreach (string name in Fixtures.TagFixtureNames())
        {
            yield return $"tags/{name}.mp3";
        }

        foreach (string name in Fixtures.MpegFixtures())
        {
            yield return name;
        }

        foreach (string name in Fixtures.Mp4FixtureNames())
        {
            yield return name;
        }

        foreach (string name in Fixtures.OggFixtures())
        {
            yield return name;
        }

        foreach (string family in new[] { "dts", "ac3", "matroska", "musepack", "flac", "monkeys-audio", "optimfrog", "tak", "tta", "alac", "truehd", "wavpack", "wav", "aiff", "asf" })
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, family), "*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)
                .Where(p => !p.EndsWith(".json", StringComparison.Ordinal)))
            {
                yield return $"{family}/{Path.GetFileName(path)}";
            }
        }

        // The real files of encoders the CI cannot run (sample-kit), the files with one stored checksum changed (damaged-units) and the files of Apple's encoder: the most foreign input of their formats.
        foreach (string directory in Directory.EnumerateDirectories(Fixtures.FixtureDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(Fixtures.FixtureDirectory, directory).Replace('\\', '/');
            if (Path.GetFileName(directory) is "sample-kit" or "damaged-units" || relative == "aac/apple")
            {
                foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).Where(p => !p.EndsWith(".json", StringComparison.Ordinal)))
                {
                    yield return $"{relative}/{Path.GetFileName(path)}";
                }
            }
        }

        // The WavPack files of versions 1 to 3 (the correction files alone are no format and are left out).
        foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "wavpack", "legacy"), "*.wv", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            yield return $"wavpack/legacy/{Path.GetFileName(path)}";
        }
    }

    // The media files of tests/fixtures that no damaged-input class mutates, by folder, with the reason; CorruptFilesTests holds every other media file to All().
    public static readonly (string Prefix, string Reason)[] NotMutated =
    [
        ("fuzz/", "regression inputs of FuzzRegressionTests, each a file the fuzzer broke a parser with"),
        ("ogg/fuzz/", "regression inputs of FuzzRegressionTests"),
        ("measurement/", "synthetic PCM signals of the measurement tests, in the WAV, AIFF and FLAC forms the families above are damaged in"),
    ];

    // The correction files of WavPack 1 to 3 are no format alone.
    public static bool IsNotMutated(string name) =>
        NotMutated.Any(n => name.StartsWith(n.Prefix, StringComparison.Ordinal)) || name.EndsWith(".wvc", StringComparison.Ordinal);

    private enum VariantKind
    {
        Truncation,
        Change,
        Prefix,
    }

    // Every fixture is also decoded where a decoder can read it, unless its folder is named here with the reason. A folder or a decoder added later is decoded by default.
    // The folders of containers (mp4, matroska, ogg, asf) hold streams of several codings and are decoded for the ones that have a decoder.
    internal static readonly (string Prefix, string Reason)[] NotDecoded =
    [
        ("tags/", "the audio of a tag fixture is a few frames; the tag parsers are what these fixtures test"),
        ("aac/", "AAC has no decoder"),
        ("optimfrog/", "OptimFROG has no decoder"),
    ];

    // FLAC, Musepack (a frame costs about 100 microseconds), ALAC, WAV, Wave64 and AIFF decode every variant (a frame costs a few microseconds). A Monkey's Audio frame costs a
    // thousand times what a FLAC frame does, so only some variants are decoded: every third of the byte changes (the damage the decoder meets), the prefixed ones, and one truncation in 64.
    // Each is decoded for up to 2^28 units of work: the fixtures of the lower levels are decoded to their last frame, so damage in a later frame is
    // decoded too, and at the highest levels the budget ends the decoding of a variant early.
    private const long MonkeysAudioBudget = 1L << 28;

    // WavPack, TAK, TTA (decoding every variant took the TTA fixtures 6.5 minutes), TrueHD and MLP (the truehd folder), and the hand-built Monkey's Audio files (three frames of 160,000 samples), decode a block or a unit at the cost of a few
    // hundred FLAC frames, so they are decoded like Monkey's Audio (the same selection of variants and the same budget). FLAC includes the Ogg FLAC files of its folder.
    private static bool IsCostlyDecoder(string name) =>
        name.StartsWith("monkeys-audio/", StringComparison.Ordinal) || name.StartsWith("wavpack/", StringComparison.Ordinal) || name.StartsWith("truehd/", StringComparison.Ordinal) || name.StartsWith("tak/", StringComparison.Ordinal) || name.StartsWith("tta/", StringComparison.Ordinal);

    // The lossy decoders (MPEG audio, Vorbis, Opus, Speex, AC-3 and E-AC-3, DTS) and the containers that carry them: a sparser selection still, every twelfth change, the
    // prefixed files and one truncation in 128, since there are far more of these files than of the others (the fuzzer, which runs on every pull request, covers their decoders at random).
    private static bool IsLossyDecoder(string name) =>
        name.StartsWith("mpeg-audio/", StringComparison.Ordinal) || name.StartsWith("ogg/", StringComparison.Ordinal) || name.StartsWith("ac3/", StringComparison.Ordinal) || name.StartsWith("dts/", StringComparison.Ordinal)
        || name.StartsWith("mp4/", StringComparison.Ordinal) || name.StartsWith("matroska/", StringComparison.Ordinal) || name.StartsWith("asf/", StringComparison.Ordinal);

    internal static bool IsDecoded(string name) => !NotDecoded.Any(n => name.StartsWith(n.Prefix, StringComparison.Ordinal));

    private static bool IsDecoded(string name, VariantKind kind, int number) =>
        IsDecoded(name) && (kind, IsCostlyDecoder(name), IsLossyDecoder(name)) switch
        {
            (VariantKind.Change, true, _) => number % 3 == 0,
            (VariantKind.Change, _, true) => number % 12 == 0,
            (VariantKind.Truncation, true, _) => number % 64 == 0,
            (VariantKind.Truncation, _, true) => number % 128 == 0,
            _ => true,
        };

    public static async Task MutatedFixturesProduceReports(string name)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Fixtures.TagDirectory, "..", name));

        int index = 0;
        foreach ((byte[] variant, VariantKind kind, int number) in Variants(original, name))
        {
            try
            {
                // The limit counts from the start of the analysis, not from Task.Run: parallel test
                // classes can keep the pool busy for seconds on a loaded runner before it starts.
                var started = new TaskCompletionSource();
                Task work = Task.Run(() =>
                {
                    started.SetResult();
                    AnalyzeAndCheck(variant, IsDecoded(name, kind, number), IsCostlyDecoder(name) || IsLossyDecoder(name) ? MonkeysAudioBudget : 1L << 30);
                });
                await started.Task;
                await work.WaitAsync(VariantLimit);
            }
            catch (TimeoutException)
            {
                Assert.Fail($"Variant {index} of {name} ({variant.Length} bytes) took longer than {VariantLimit}.");
            }

            index++;
        }
    }

    public static void RandomFilesProduceReports()
    {
        var random = new Random(20261010);
        for (int i = 0; i < 500; i++)
        {
            var data = new byte[random.Next(0, 2048)];
            random.NextBytes(data);
            AnalyzeAndCheck(data);
        }
    }

    // string.GetHashCode is randomized per process, which would test different variants on every
    // run; FNV-1a gives each fixture the same variants everywhere, so a failure reproduces.
    private static int StableSeed(string name)
    {
        uint hash = 2166136261;
        foreach (char c in name)
        {
            hash = (hash ^ c) * 16777619;
        }

        return (int)hash;
    }

    private static IEnumerable<(byte[] Data, VariantKind Kind, int Number)> Variants(byte[] original, string name)
    {
        var random = new Random(StableSeed(name));

        // Truncations, densest near the ends where tags live.
        var lengths = new SortedSet<int>();
        for (int i = 0; i <= Math.Min(original.Length, 600); i++)
        {
            lengths.Add(i);
            lengths.Add(original.Length - i);
        }

        for (int i = 0; i < 100; i++)
        {
            lengths.Add(random.Next(0, original.Length + 1));
        }

        int number = 0;
        foreach (int length in lengths)
        {
            yield return (original[..length], VariantKind.Truncation, number++);
        }

        // Single and multiple byte changes, biased toward the tag regions at both ends.
        for (int i = 0; i < FlipsPerFixture; i++)
        {
            byte[] copy = (byte[])original.Clone();
            int changes = 1 + random.Next(4);
            for (int c = 0; c < changes && copy.Length > 0; c++)
            {
                int index = random.Next(3) switch
                {
                    0 => random.Next(Math.Min(copy.Length, 512)),
                    1 => copy.Length - 1 - random.Next(Math.Min(copy.Length, 512)),
                    _ => random.Next(copy.Length),
                };

                copy[index] = random.Next(4) switch
                {
                    0 => (byte)(copy[index] ^ (1 << random.Next(8))),
                    1 => 0xFF,
                    2 => 0x00,
                    _ => (byte)random.Next(256),
                };
            }

            yield return (copy, VariantKind.Change, i);
        }

        // Garbage before the first byte shifts every leading anchor.
        for (int prefix = 1; prefix <= 3; prefix++)
        {
            byte[] shifted = new byte[original.Length + prefix];
            original.CopyTo(shifted, prefix);
            yield return (shifted, VariantKind.Prefix, prefix);
        }
    }

    private static void AnalyzeAndCheck(byte[] data, bool alsoDecodes = false, long maxWork = 1L << 30)
    {
        if (alsoDecodes)
        {
            // The decoder works on what the parser hands it, damaged frames included: it must give a report as well, and never fail.
            // The measurements run on what it decodes, with a budget of their own lowered the same way: a bug in them is measure.failed.
            var options = new AnalysisOptions { Decoding = new DecodeOptions { MaxWork = maxWork, Measure = new MeasureOptions { MaxWork = Math.Min(maxWork, 1L << 26) } } };
            Report decoded = Analyzer.CreateDefault().Analyze("variant", new MemoryStream(data), options);
            Assert.DoesNotContain(decoded.Findings, f => f.Code is "parse.failed" or "decode.failed" or "measure.failed");
            Assert.All(decoded.Findings, f => Assert.True(f.Path is not null, $"The node of {f.Code} is not in the tree."));
        }

        Report report = Analyzer.CreateDefault().Analyze("variant", new MemoryStream(data));

        Assert.Equal(data.Length, report.Root.Length);
        CheckNode(report.Root, report.Root);

        long previousEnd = 0;
        foreach (Node region in report.Root.Children)
        {
            Assert.True(region.Offset >= previousEnd, $"Region {region.Name} at {region.Offset} overlaps the previous region ending at {previousEnd}.");
            previousEnd = region.End;
        }

        Assert.All(report.Findings, f => Assert.NotNull(f.Node));

        // A parser exception the host caught is a bug, and every finding points at a node of the
        // tree, as the fuzzer requires.
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.True(f.Path is not null, $"The node of {f.Code} is not in the tree."));
    }

    private static void CheckNode(Node node, Node parent)
    {
        Assert.True(node.Offset >= parent.Offset && node.End <= parent.End, $"{node.Name} [{node.Offset}, {node.End}) is outside {parent.Name} [{parent.Offset}, {parent.End}).");
        if (parent.Ranges is { } ranges && node != parent)
        {
            foreach (ByteRange range in node.Ranges ?? [new ByteRange(node.Offset, node.Length)])
            {
                Assert.True(range.Length == 0 || ranges.Any(r => r.Offset <= range.Offset && range.End <= r.End), $"{node.Name} lies in a gap between the ranges of {parent.Name}.");
            }
        }

        foreach (Node child in node.Children)
        {
            CheckNode(child, node);
        }
    }
}
