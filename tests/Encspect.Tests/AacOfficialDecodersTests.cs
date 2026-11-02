using System.Security.Cryptography;
using System.Text.Json;
using Encspect.Core;
using Xunit.Abstractions;

namespace Encspect.Tests;

// Holds the analyzer's AAC sample counts to tests/fixtures/{aac,adts,mp4}/official-decoders.json (docs/design/official-decoder-crosschecks.md, "Records and the comparison"):
// for each fixture the reference figure of the coded stage, which is what Fraunhofer's FDK AAC decoder (2.0.2, concealment method 1) output when it was fed the units of the
// file one at a time, per run of constant output format; every difference with its status; and the units it refuses, conceals or outputs nothing for beside the analyzer's
// findings at the same bytes. A figure that equals the reference passes; one that differs passes only as a listed difference whose recorded analyzer figure it still is. The
// findings of the aac codes (warnings and errors) must be the listed ones, so a finding that appears, goes or moves fails here. The trimmed figures (iTunSMPB, edit list)
// have no reference until Core Audio (rollout step 5) and are not held. An entry whose SHA-256 is not the fixture's is stale: it is listed and does not fail, since a
// regenerated fixture would otherwise need a new run before its pull request could pass. tests/fixtures/apple has no goldens, so its files are not read here.
public sealed class AacOfficialDecodersTests(ITestOutputHelper output)
{
    private static readonly string[] Statuses = ["explained", "noted", "pending", "follow", "keep", "build-dependent", "stale"];
    private static readonly string[] Folders = ["aac", "adts", "mp4"];

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

    private static string ResultsPath(string folder) => Path.Combine(Fixtures.FixtureDirectory, folder, "official-decoders.json");

    public static TheoryData<string> Entries()
    {
        var data = new TheoryData<string>();
        foreach (string folder in Folders)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResultsPath(folder)));
            foreach (JsonElement file in document.RootElement.GetProperty("files").EnumerateArray())
            {
                if (!file.TryGetProperty("notFed", out _))
                {
                    data.Add($"{folder}/{file.GetProperty("path").GetString()}");
                }
            }
        }

        return data;
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("adts")]
    [InlineData("mp4")]
    public void ResultsNameTheirRunAndEveryStatusIsOneTheDesignDefines(string folder)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResultsPath(folder)));
        JsonElement run = document.RootElement.GetProperty("run");
        Assert.False(string.IsNullOrEmpty(run.GetProperty("id").GetString()));
        Assert.Contains("8.0.1", run.GetProperty("ffmpeg").GetString(), StringComparison.Ordinal);
        Assert.Equal("v2.0.2", run.GetProperty("fdkAac").GetProperty("version").GetString());
        Assert.Equal(1, run.GetProperty("concealMethod").GetInt32());
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
            }
        }
    }

    // AAC fixtures that have no entry are listed, not failed: a new fixture is run by the next run of the workflow.
    [Theory]
    [InlineData("aac")]
    [InlineData("adts")]
    [InlineData("mp4")]
    public void FixturesWithoutAnEntryAreListed(string folder)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResultsPath(folder)));
        HashSet<string> known = [.. document.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)];
        string[] extensions = [".aac", ".m4a", ".mp4", ".m4b", ".3gp", ".mov", ".adts"];
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(ResultsPath(folder))!).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            if (extensions.Contains(Path.GetExtension(name).ToLowerInvariant()) && !known.Contains(name))
            {
                Note($"official decoders: no entry for {folder}/{name}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void AnalyzerFiguresEqualTheReferenceOrAreListedDifferences(string name)
    {
        string folder = name[..name.IndexOf('/', StringComparison.Ordinal)];
        string file = name[(folder.Length + 1)..];
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResultsPath(folder)));
        JsonElement entry = document.RootElement.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("path").GetString() == file);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, folder, file))));
        if (sha256 != entry.GetProperty("sha256").GetString())
        {
            Note($"official decoders: stale {name} is no longer the file the run decoded ({entry.GetProperty("sha256").GetString()![..8]}, now {sha256[..8]})");
            return;
        }

        Report report = Fixtures.AnalyzeMpeg(name);
        var figures = new Dictionary<string, long?>();
        foreach (JsonElement figure in entry.GetProperty("figures").EnumerateArray().Where(f => f.GetProperty("stage").GetString() == "coded"))
        {
            string id = figure.GetProperty("id").GetString()!;
            long? actual = AnalyzerFigure(report, figure);
            figures[id] = actual;
            if (figure.GetProperty("reference").ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            long reference = figure.GetProperty("reference").GetInt64();
            if (actual == reference)
            {
                continue;
            }

            Assert.True(
                entry.GetProperty("differences").EnumerateArray().Any(d => d.GetProperty("figure").GetString() == id && d.GetProperty("kind").GetString() == "analyzer-differs"),
                $"{name}: the samples of {id} are {actual?.ToString() ?? "absent"}, the reference ({figure.GetProperty("referencePath").GetString()}) {reference}, and no difference lists it");
        }

        foreach (JsonElement difference in entry.GetProperty("differences").EnumerateArray())
        {
            string id = difference.GetProperty("figure").GetString()!;
            string kind = difference.GetProperty("kind").GetString()!;
            string status = difference.GetProperty("status").GetString()!;
            long? actual = figures.GetValueOrDefault(id);
            Assert.True(actual is not null, $"{name}: the analyzer's samples of {id} are gone");
            long? listed = difference.GetProperty("analyzer").ValueKind == JsonValueKind.Null ? null : difference.GetProperty("analyzer").GetInt64();
            if (kind == "decoder-refuses-stream")
            {
                Assert.True(actual == listed, $"{name}: the samples of {id} are {actual}; the decoder refuses the stream and the list has the analyzer's {listed}");
                if (status == "pending")
                {
                    Note($"official decoders: pending {name} {id}: the decoder refuses the stream, the analyzer counts {actual}");
                }

                continue;
            }

            if (kind is "decoder-holds-blocks" or "rate-differs")
            {
                Assert.True(actual == listed, $"{name}: the samples of {id} are {actual}; the list ({kind}) has the analyzer's {listed}");
                if (status == "pending")
                {
                    Note($"official decoders: pending {name} {id}: {kind}, the analyzer counts {actual}");
                }

                continue;
            }

            if (kind == "witness-differs")
            {
                if (status == "explained")
                {
                    long reference = difference.GetProperty("reference").GetInt64();
                    Assert.True(actual == reference, $"{name}: the difference with {difference.GetProperty("decoder").GetString()} is explained by the analyzer equaling the reference {reference}, but the analyzer gives {actual}");
                }

                continue;
            }

            long value = difference.GetProperty("value").GetInt64();
            bool resolved = actual == value;
            if (resolved)
            {
                Note($"official decoders: {name} {id} now equals {difference.GetProperty("decoder").GetString()} ({value}); update the JSON from a new run");
            }

            Assert.True(resolved || actual == listed, $"{name}: the samples of {id} are {actual}; the difference with {difference.GetProperty("decoder").GetString()} ({value}) lists the analyzer's {listed}");
            if (status == "follow" && !resolved)
            {
                Note($"official decoders: follow {name} {id} is {actual}, the reference {value}; the analyzer is to follow it in its own change");
            }
            else if (status == "pending")
            {
                Note($"official decoders: pending {name} {id} is {actual}, {difference.GetProperty("decoder").GetString()} gives {value}");
            }
        }

        // Every unit the decoder refuses, conceals or outputs nothing for, every unit with an error and every finding about a unit is listed, so a finding that appears,
        // goes or moves fails here.
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
        finding.Node is not null && finding.Severity != Severity.Info && finding.Code.StartsWith("aac.", StringComparison.Ordinal);

    // The stream samples of the figure's streams; where the figure counts at one rate (the decoder's runs and the analyzer's cannot be paired), each run of consecutive
    // streams of one format is converted to that rate, to the nearest with halves up, as the comparison does.
    private static long? AnalyzerFigure(Report report, JsonElement figure)
    {
        long? rate = figure.TryGetProperty("unpaired", out JsonElement unpaired) ? unpaired.GetProperty("rate").GetInt64() : null;
        long total = 0;
        long run = 0;
        (long Rate, long Channels)? current = null;
        foreach (JsonElement index in figure.GetProperty("streams").EnumerateArray())
        {
            if (index.ValueKind != JsonValueKind.Number || index.GetInt32() >= report.Streams.Count)
            {
                return null;
            }

            StreamInfo stream = report.Streams[index.GetInt32()];
            if (rate is null)
            {
                total += stream.Samples;
                continue;
            }

            if (current != (stream.SampleRate, stream.Channels))
            {
                total += AtRate(run, current?.Rate, rate.Value);
                run = 0;
                current = (stream.SampleRate, stream.Channels);
            }

            run += stream.Samples;
        }

        return rate is null ? total : total + AtRate(run, current?.Rate, rate.Value);
    }

    private static long AtRate(long samples, long? from, long to) => from is null or 0 || from == to ? samples : (2 * samples * to + from.Value) / (2 * from.Value);
}
