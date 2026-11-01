using System.Security.Cryptography;
using System.Text.Json;
using Encspect.Core;
using Xunit.Abstractions;

namespace Encspect.Tests;

// Holds the analyzer's TTA sample count to tests/fixtures/tta/official-decoders.json (docs/design/official-decoder-crosschecks.md, "Records and the comparison"):
// for each fixture the reference figure, which is what libtta-c 2.3's `tta -d` wrote (`tta -dp secret` for the file written with that password), counted from the
// PCM bytes of the WAVE file with its own channel count and bits per sample; where `tta -d` gives none (a nonzero exit status, a crash) ffmpeg 8.0.1's count, labeled
// as the fallback; and every difference with its status. `tta -d` gives one figure per file and no per-frame information, so only figures are checked, not units.
// A figure that equals the reference passes; one that differs passes only as a listed difference whose recorded analyzer figure it still is. An entry whose
// SHA-256 is not the fixture's is stale: it is listed and does not fail, since a regenerated fixture would otherwise need a new run before its pull request could pass.
public sealed class TtaOfficialDecodersTests(ITestOutputHelper output)
{
    private static readonly string[] Statuses = ["explained", "noted", "pending", "follow", "keep", "build-dependent", "stale"];

    // Lists go to the test output and, in the one CI job that sets ENCSPECT_OFFICIAL_SUMMARY (the rest shard on Linux), to the job summary, where they are seen
    // without opening a log; the other jobs would only repeat them.
    private void Note(string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("ENCSPECT_OFFICIAL_SUMMARY") == "1" && Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
        {
            File.AppendAllText(summary, line + "\n");
        }
    }

    private static readonly string ResultsPath = Path.Combine(Fixtures.FixtureDirectory, "tta", "official-decoders.json");

    public static TheoryData<string> Entries()
    {
        var data = new TheoryData<string>();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResultsPath));
        foreach (JsonElement file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            data.Add(file.GetProperty("path").GetString()!);
        }

        return data;
    }

    [Fact]
    public void ResultsNameTheirRunAndEveryStatusIsOneTheDesignDefines()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResultsPath));
        JsonElement run = document.RootElement.GetProperty("run");
        Assert.False(string.IsNullOrEmpty(run.GetProperty("id").GetString()));
        Assert.Contains("8.0.1", run.GetProperty("ffmpeg").GetString(), StringComparison.Ordinal);
        Assert.Contains("version 2.3", run.GetProperty("tta").GetString(), StringComparison.Ordinal);
        Assert.Equal(Statuses, document.RootElement.GetProperty("statuses").EnumerateArray().Select(s => s.GetString()));
        foreach (JsonElement file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            Assert.Equal(0, file.GetProperty("units").GetArrayLength());
            foreach (JsonElement difference in file.GetProperty("differences").EnumerateArray())
            {
                string status = difference.GetProperty("status").GetString()!;
                Assert.True(Statuses.Contains(status), $"{file.GetProperty("path").GetString()}: status {status}");
            }
        }
    }

    // TTA fixtures that have no entry are listed, not failed: a new fixture is run by the next run of the workflow.
    [Fact]
    public void FixturesWithoutAnEntryAreListed()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResultsPath));
        HashSet<string> known = [.. document.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)];
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(ResultsPath)!, "*.tta").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            if (!known.Contains(name))
            {
                Note($"official decoders: no entry for {name}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void AnalyzerFiguresEqualTheReferenceOrAreListedDifferences(string name)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResultsPath));
        JsonElement entry = document.RootElement.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("path").GetString() == name);
        string fixture = Path.Combine(Fixtures.FixtureDirectory, "tta", name);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture)));
        if (sha256 != entry.GetProperty("sha256").GetString())
        {
            Note($"official decoders: stale {name} is no longer the file the run decoded ({entry.GetProperty("sha256").GetString()![..8]}, now {sha256[..8]})");
            return;
        }

        Report report = Fixtures.AnalyzeMpeg($"tta/{name}");
        long? actual = report.Streams.FirstOrDefault(s => s.Codec == "TTA")?.Samples;
        JsonElement figure = entry.GetProperty("figures").EnumerateArray().Single(f => f.GetProperty("id").GetString() == "samples");
        if (figure.GetProperty("reference").ValueKind != JsonValueKind.Null)
        {
            long reference = figure.GetProperty("reference").GetInt64();
            string path = figure.GetProperty("referencePath").GetString()!;
            if (actual != reference)
            {
                Assert.True(
                    entry.GetProperty("differences").EnumerateArray().Any(d => d.GetProperty("figure").GetString() == "samples" && d.GetProperty("kind").GetString() == "analyzer-differs"),
                    $"{name}: the samples are {actual?.ToString() ?? "absent"}, the reference ({path}) {reference}, and no difference lists it");
            }
        }

        foreach (JsonElement difference in entry.GetProperty("differences").EnumerateArray())
        {
            string kind = difference.GetProperty("kind").GetString()!;
            string status = difference.GetProperty("status").GetString()!;
            string decoder = difference.GetProperty("decoder").GetString()!;
            if (kind == "witness-differs")
            {
                if (status == "explained")
                {
                    long reference = difference.GetProperty("reference").GetInt64();
                    Assert.True(actual == reference, $"{name}: the difference with {decoder} is explained by the analyzer equaling the reference {reference}, but the analyzer gives {actual?.ToString() ?? "absent"}");
                }

                continue;
            }

            long? listed = difference.GetProperty("analyzer").ValueKind == JsonValueKind.Null ? null : difference.GetProperty("analyzer").GetInt64();
            if (kind == "decoder-gives-no-count")
            {
                Assert.True(actual == listed, $"{name}: the samples are {actual?.ToString() ?? "absent"}; the list has the analyzer's {listed?.ToString() ?? "absent"} beside the decoder's missing count");
                continue;
            }

            long value = difference.GetProperty("value").GetInt64();
            bool resolved = actual == value;
            Assert.True(resolved || actual == listed, $"{name}: the samples are {actual?.ToString() ?? "absent"}; the difference with {decoder} ({value}) lists the analyzer's {listed?.ToString() ?? "absent"}");
            if (status == "follow" && !resolved)
            {
                Note($"official decoders: follow {name} samples are {actual?.ToString() ?? "absent"}, the reference {value}; the analyzer is to follow it in its own change");
            }
            else if (status == "pending")
            {
                Note($"official decoders: pending {name} samples are {actual?.ToString() ?? "absent"}, {decoder} gives {value}");
            }
        }
    }
}
