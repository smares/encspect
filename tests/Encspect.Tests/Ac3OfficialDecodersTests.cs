using System.Security.Cryptography;
using System.Text.Json;
using Encspect.Core;
using Xunit.Abstractions;

namespace Encspect.Tests;

// Holds the analyzer's AC-3 and E-AC-3 sample counts to tests/fixtures/ac3/official-decoders.json (docs/design/official-decoder-crosschecks.md, "Records and the comparison"): for each
// fixture the total of Core Audio's AudioConverter (macOS), fed the units of the file one at a time, with the constant by which that decoder is short of the coded stage (288 frames
// on every file, recorded as `referenceOffset`), and the units it refuses beside the analyzer's findings at the same bytes. The analyzer's `samples` plus the offset must equal the
// reference; one that differs passes only as a listed difference whose recorded analyzer figure it still is. The findings of the ac3 codes (warnings and errors) must be the listed
// ones, so a finding that appears, goes or moves fails here. The edit list of an MP4 file has no reference (ExtAudioFile applies none to AC-3). An entry whose SHA-256 is not the
// fixture's is stale: it is listed and does not fail, since a regenerated fixture would otherwise need a macOS run before its pull request could pass.
public sealed class Ac3OfficialDecodersTests(ITestOutputHelper output)
{
    private static readonly string[] Statuses = ["explained", "noted", "pending", "follow", "keep", "build-dependent", "stale"];

    private void Note(string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("ENCSPECT_OFFICIAL_SUMMARY") == "1" && Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
        {
            File.AppendAllText(summary, line + "\n");
        }
    }

    private static string ResultsPath => Path.Combine(Fixtures.FixtureDirectory, "ac3", "official-decoders.json");

    public static TheoryData<string> Entries()
    {
        var data = new TheoryData<string>();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResultsPath));
        foreach (JsonElement file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            if (!file.TryGetProperty("notFed", out _))
            {
                data.Add(file.GetProperty("path").GetString()!);
            }
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
        JsonElement mac = run.GetProperty("coreAudio");
        Assert.False(string.IsNullOrEmpty(mac.GetProperty("macOS").GetString()));
        Assert.False(string.IsNullOrEmpty(mac.GetProperty("build").GetString()));
        Assert.False(string.IsNullOrEmpty(mac.GetProperty("audioCodecs").GetString()));
        Assert.Equal(Statuses, document.RootElement.GetProperty("statuses").EnumerateArray().Select(s => s.GetString()));
        foreach (JsonElement file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            string path = file.GetProperty("path").GetString()!;
            if (file.TryGetProperty("notFed", out JsonElement reason))
            {
                Assert.False(string.IsNullOrEmpty(reason.GetString()), $"{path}: not fed, and no reason");
            }

            foreach (JsonElement difference in file.GetProperty("differences").EnumerateArray().Concat(file.GetProperty("units").EnumerateArray()))
            {
                string status = difference.GetProperty("status").GetString()!;
                Assert.True(Statuses.Contains(status) || status == "agree", $"{path}: status {status}");
                if (status is "keep" or "follow")
                {
                    Assert.False(string.IsNullOrWhiteSpace(difference.TryGetProperty("note", out JsonElement note) ? note.GetString() : null), $"{path}: a {status} entry has no note with its reason");
                }
            }
        }
    }

    [Fact]
    public void FixturesWithoutAnEntryAreListed()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResultsPath));
        HashSet<string> known = [.. document.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)];
        string[] extensions = [".ac3", ".eac3", ".mp4"];
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(ResultsPath)!).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            if (extensions.Contains(Path.GetExtension(name).ToLowerInvariant()) && !known.Contains(name))
            {
                Note($"official decoders: no entry for ac3/{name}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void AnalyzerFiguresPlusTheConverterOffsetEqualTheReferenceOrAreListedDifferences(string file)
    {
        string name = "ac3/" + file;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResultsPath));
        JsonElement entry = document.RootElement.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("path").GetString() == file);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name))));
        if (sha256 != entry.GetProperty("sha256").GetString())
        {
            Note($"official decoders: stale {name} is no longer the file the run decoded ({entry.GetProperty("sha256").GetString()![..8]}, now {sha256[..8]})");
            return;
        }

        Report report = Fixtures.AnalyzeMpeg(name);
        var figures = new Dictionary<string, long?>();
        foreach (JsonElement figure in entry.GetProperty("figures").EnumerateArray())
        {
            string id = figure.GetProperty("id").GetString()!;
            if (figure.GetProperty("stage").GetString() != "coded")
            {
                continue;
            }

            long actual = figure.GetProperty("streams").EnumerateArray().Sum(i => report.Streams[i.GetInt32()].Samples);
            figures[id] = actual;
            HashSet<long> rates = [.. figure.GetProperty("analyzerRates").EnumerateArray().Select(r => r.GetInt64())];
            if (rates.Count > 0)
            {
                Assert.True(
                    figure.GetProperty("streams").EnumerateArray().Select(i => (long)report.Streams[i.GetInt32()].SampleRate).ToHashSet().SetEquals(rates),
                    $"{name}: the rates of the streams of {id} are not the recorded {string.Join("/", rates)}");
            }

            if (figure.TryGetProperty("refused", out _))
            {
                Assert.True(
                    entry.GetProperty("differences").EnumerateArray().Any(d => d.GetProperty("figure").GetString() == id && d.GetProperty("kind").GetString() == "decoder-refuses-stream"),
                    $"{name}: {id} carries refused and no decoder-refuses-stream difference lists it");
            }

            if (figure.GetProperty("reference").ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            long reference = figure.GetProperty("reference").GetInt64();
            long offset = figure.GetProperty("referenceOffset").GetInt64();
            Assert.Equal(-288, offset);
            if (actual + offset == reference)
            {
                continue;
            }

            Assert.True(
                entry.GetProperty("differences").EnumerateArray().Any(d => d.GetProperty("figure").GetString() == id && d.GetProperty("kind").GetString() == "analyzer-differs"),
                $"{name}: the samples of {id} are {actual} (less 288: {actual + offset}), the reference ({figure.GetProperty("referencePath").GetString()}) {reference}, and no difference lists it");
        }

        foreach (JsonElement difference in entry.GetProperty("differences").EnumerateArray())
        {
            string id = difference.GetProperty("figure").GetString()!;
            string kind = difference.GetProperty("kind").GetString()!;
            string status = difference.GetProperty("status").GetString()!;
            long? actual = figures.GetValueOrDefault(id);
            Assert.True(actual is not null, $"{name}: the analyzer's samples of {id} are gone");
            long? listed = difference.GetProperty("analyzer").ValueKind == JsonValueKind.Null ? null : difference.GetProperty("analyzer").GetInt64();
            Assert.True(actual == listed, $"{name}: the samples of {id} are {actual}; the list ({kind}) has the analyzer's {listed}");
            if (kind == "witness-differs" && status == "explained")
            {
                long reference = difference.GetProperty("reference").GetInt64();
                Assert.True(actual - 288 == reference, $"{name}: the difference with {difference.GetProperty("decoder").GetString()} is explained by the analyzer equaling the reference {reference} (less 288), but the analyzer gives {actual}");
            }

            if (status == "pending")
            {
                Note($"official decoders: pending {name} {id}: {kind} ({difference.GetProperty("decoder").GetString()}), the analyzer counts {actual}");
            }
        }

        var listedFindings = new Dictionary<string, int>();
        var actualFindings = new Dictionary<string, int>();
        foreach (Finding finding in report.Findings.Where(IsUnitFinding))
        {
            actualFindings[finding.Code] = actualFindings.GetValueOrDefault(finding.Code) + 1;
        }

        foreach (JsonElement unit in entry.GetProperty("units").EnumerateArray())
        {
            List<(long Start, long Length)> ranges = unit.GetProperty("ranges").ValueKind == JsonValueKind.Null
                ? []
                : [.. unit.GetProperty("ranges").EnumerateArray().Select(r => (r[0].GetInt64(), r[1].GetInt64()))];
            long offset = unit.GetProperty("offset").ValueKind == JsonValueKind.Null ? -1 : unit.GetProperty("offset").GetInt64();
            bool Holds(Finding f) => IsUnitFinding(f)
                && (ranges.Count == 0 ? f.Node!.Offset == offset : ranges.Any(r => f.Node!.Offset >= r.Start && f.Node.Offset < r.Start + r.Length));
            string[] expected = [.. unit.GetProperty("findings").EnumerateArray().Select(f => f.GetString()!)];
            foreach (string code in expected)
            {
                listedFindings[code] = listedFindings.GetValueOrDefault(code) + 1;
            }

            Assert.True(
                report.Findings.Where(Holds).Select(f => f.Code).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)),
                $"{name}: the findings at {offset} are [{string.Join(", ", report.Findings.Where(Holds).Select(f => f.Code))}], the list has [{string.Join(", ", expected)}] beside the decoder's {unit.GetProperty("decoder").GetString()}");
            if (unit.GetProperty("status").GetString() == "pending")
            {
                Note($"official decoders: pending {name} unit at {offset}: {unit.GetProperty("verdict").GetString()} ({unit.GetProperty("decoder").GetString()})");
            }
        }

        Assert.True(
            listedFindings.OrderBy(k => k.Key, StringComparer.Ordinal).SequenceEqual(actualFindings.OrderBy(k => k.Key, StringComparer.Ordinal)),
            $"{name}: the unit findings of the report ({string.Join(", ", actualFindings.Select(k => $"{k.Key} {k.Value}"))}) are not the listed ones ({string.Join(", ", listedFindings.Select(k => $"{k.Key} {k.Value}"))})");
    }

    private static bool IsUnitFinding(Finding finding) =>
        finding.Node is not null && finding.Severity != Severity.Info && finding.Code.StartsWith("ac3.", StringComparison.Ordinal);
}
