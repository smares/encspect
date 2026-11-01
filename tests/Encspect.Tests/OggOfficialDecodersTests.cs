using System.Security.Cryptography;
using System.Text.Json;
using Encspect.Core;
using Xunit.Abstractions;

namespace Encspect.Tests;

// Holds the analyzer's figures that the official decoders speak to to tests/fixtures/ogg/official-decoders.json (docs/design/official-decoder-crosschecks.md,
// "Records and the comparison"): for each fixture the reference figure of each stage (libopus and libvorbis fed packet by packet at the coded stage, "Decoded Samples";
// opusdec and oggdec at the trimmed stage, the stream's samples), and every difference with its status. A figure that equals the reference passes; one that differs
// passes only as a listed difference whose recorded analyzer figure it still is. An entry whose SHA-256 is not the fixture's is stale: it is listed and does not fail,
// since a regenerated fixture would otherwise need a new run before its pull request could pass.
public sealed class OggOfficialDecodersTests(ITestOutputHelper output)
{
    private static readonly string[] Statuses = ["explained", "noted", "pending", "follow", "keep", "build-dependent", "stale"];

    // Lists go to the test output and, in CI, to the job summary, where they are seen without opening a log.
    private void Note(string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
        {
            File.AppendAllText(summary, line + "\n");
        }
    }

    private static readonly string ResultsPath = Path.Combine(Fixtures.FixtureDirectory, "ogg", "official-decoders.json");

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
        Assert.Equal(Statuses, document.RootElement.GetProperty("statuses").EnumerateArray().Select(s => s.GetString()));
        foreach (JsonElement file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            foreach (JsonElement difference in file.GetProperty("differences").EnumerateArray().Concat(file.GetProperty("units").EnumerateArray()))
            {
                string status = difference.GetProperty("status").GetString()!;
                Assert.True(Statuses.Contains(status) || status == "agree", $"{file.GetProperty("path").GetString()}: status {status}");
            }
        }
    }

    // Opus and Vorbis fixtures that have no entry are listed, not failed: a new fixture is run by the next run of the workflow.
    [Fact]
    public void FixturesWithoutAnEntryAreListed()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ResultsPath));
        HashSet<string> known = [.. document.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)];
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(ResultsPath)!).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            if ((name.EndsWith(".ogg", StringComparison.Ordinal) || name.EndsWith(".opus", StringComparison.Ordinal)) && !known.Contains(name))
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
        string fixture = Path.Combine(Fixtures.FixtureDirectory, "ogg", name);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture)));
        if (sha256 != entry.GetProperty("sha256").GetString())
        {
            Note($"official decoders: stale {name} is no longer the file the run decoded ({entry.GetProperty("sha256").GetString()![..8]}, now {sha256[..8]})");
            return;
        }

        Report report = Fixtures.AnalyzeMpeg($"ogg/{name}");
        List<Node> streams = [.. report.Root.Children.Single(n => n.Name == "Ogg").Children.Where(n => n.Name == "Logical Stream")];
        var figures = new Dictionary<string, long?>();
        foreach (JsonElement figure in entry.GetProperty("figures").EnumerateArray())
        {
            string id = figure.GetProperty("id").GetString()!;
            long? actual = AnalyzerFigure(report, streams, figure);
            figures[id] = actual;
            if (figure.GetProperty("reference").ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            Assert.True(actual is not null, $"{name}: the analyzer's {figure.GetProperty("analyzerFigure").GetString()} of {id} is gone, and the reference is {figure.GetProperty("reference").GetInt64()}");

            long reference = figure.GetProperty("reference").GetInt64();
            if (actual == reference)
            {
                continue;
            }

            Assert.True(
                entry.GetProperty("differences").EnumerateArray().Any(d => d.GetProperty("figure").GetString() == id && d.GetProperty("kind").GetString() == "analyzer-differs"),
                $"{name}: {figure.GetProperty("analyzerFigure").GetString()} of {id} is {actual}, the reference ({figure.GetProperty("referencePath").GetString()}) {reference}, and no difference lists it");
        }

        foreach (JsonElement difference in entry.GetProperty("differences").EnumerateArray())
        {
            string id = difference.GetProperty("figure").GetString()!;
            string kind = difference.GetProperty("kind").GetString()!;
            string status = difference.GetProperty("status").GetString()!;
            long? actual = figures.GetValueOrDefault(id);
            if (kind == "decoder-refuses-stream")
            {
                long? listedFigure = difference.GetProperty("analyzer").ValueKind == JsonValueKind.Null ? null : difference.GetProperty("analyzer").GetInt64();
                Assert.True(actual == listedFigure, $"{name}: {id} is {actual}; the decoder refuses the stream and the list has the analyzer's {listedFigure}");
                if (status == "pending")
                {
                    Note($"official decoders: pending {name} {id}: the decoder refuses the stream, the analyzer counts {actual}");
                }

                continue;
            }

            if (actual is null)
            {
                Assert.Fail($"{name}: the analyzer's figure of {id} is gone");
            }

            if (kind == "witness-differs")
            {
                if (status == "explained")
                {
                    long reference = difference.GetProperty("reference").GetInt64();
                    Assert.True(actual == reference, $"{name}: the difference of {id} is explained by the analyzer equaling the reference {reference}, but the analyzer gives {actual}");
                }

                continue;
            }

            if (difference.GetProperty("analyzer").ValueKind == JsonValueKind.Null || difference.GetProperty("value").ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            long decoder = difference.GetProperty("value").GetInt64();
            long listed = difference.GetProperty("analyzer").GetInt64();
            bool resolved = actual == decoder;
            Assert.True(resolved || actual == listed, $"{name}: {id} is {actual}; the difference with {difference.GetProperty("decoder").GetString()} ({decoder}) lists the analyzer's {listed}");
            if (status == "follow" && !resolved)
            {
                Note($"official decoders: follow {name} {id} is {actual}, the reference {decoder}; the analyzer is to follow it in its own change");
            }
            else if (status == "pending")
            {
                Note($"official decoders: pending {name} {id} is {actual}, {difference.GetProperty("decoder").GetString()} gives {decoder}");
            }
        }

        // Every unit the decoder refuses and every finding about a unit is listed, so a finding that appears, goes or moves fails here.
        var listedFindings = new Dictionary<string, int>();
        var actualFindings = new Dictionary<string, int>();
        foreach (Finding finding in report.Findings.Where(f => f.Node is not null && IsUnitFinding(f.Code)))
        {
            actualFindings[finding.Code] = actualFindings.GetValueOrDefault(finding.Code) + 1;
        }

        foreach (JsonElement unit in entry.GetProperty("units").EnumerateArray())
        {
            List<(long Start, long Length)> ranges = unit.GetProperty("ranges").ValueKind == JsonValueKind.Null
                ? []
                : [.. unit.GetProperty("ranges").EnumerateArray().Select(r => (r[0].GetInt64(), r[1].GetInt64()))];
            long offset = unit.GetProperty("offset").ValueKind == JsonValueKind.Null ? -1 : unit.GetProperty("offset").GetInt64();
            bool Holds(Finding f) => f.Node is not null && IsUnitFinding(f.Code)
                && (ranges.Count == 0 ? f.Node.Offset == offset : ranges.Any(r => r.Length == 0 ? f.Node.Offset == r.Start : f.Node.Offset >= r.Start && f.Node.Offset < r.Start + r.Length));
            string[] expected = [.. unit.GetProperty("findings").EnumerateArray().Select(f => f.GetString()!)];
            if (ranges.Count == 0 && expected.Length == 0)
            {
                continue;
            }

            foreach (string code in expected)
            {
                listedFindings[code] = listedFindings.GetValueOrDefault(code) + 1;
            }

            Assert.True(
                report.Findings.Where(Holds).Select(f => f.Code).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)),
                $"{name}: the findings at {offset} are [{string.Join(", ", report.Findings.Where(Holds).Select(f => f.Code))}], the list has [{string.Join(", ", expected)}] beside the decoder's {unit.GetProperty("decoder").GetString()}");
        }

        Assert.True(
            listedFindings.OrderBy(k => k.Key, StringComparer.Ordinal).SequenceEqual(actualFindings.OrderBy(k => k.Key, StringComparer.Ordinal)),
            $"{name}: the unit findings of the report ({string.Join(", ", actualFindings.Select(k => $"{k.Key} {k.Value}"))}) are not the listed ones ({string.Join(", ", listedFindings.Select(k => $"{k.Key} {k.Value}"))})");
    }

    private static bool IsUnitFinding(string code) =>
        code.StartsWith("opus.packet-r", StringComparison.Ordinal) || code is "opus.self-delimiting" or "opus.stream-durations-differ"
        or "vorbis.window-flag-mismatch" or "vorbis.mode-invalid" or "vorbis.not-audio" or "vorbis.empty-packet";

    private static long? AnalyzerFigure(Report report, List<Node> streams, JsonElement figure)
    {
        long total = 0;
        JsonElement indexes = figure.GetProperty("streams");
        if (indexes.GetArrayLength() == 0)
        {
            return null;
        }

        foreach (JsonElement index in indexes.EnumerateArray())
        {
            if (index.ValueKind != JsonValueKind.Number || index.GetInt32() >= streams.Count)
            {
                return null;
            }

            Node node = streams[index.GetInt32()];
            long? value = figure.GetProperty("analyzerFigure").GetString() == "samples"
                ? report.Streams.FirstOrDefault(s => s.Nodes.Contains(node))?.Samples
                : Find(node, "Decoded Samples")?.Value?.AsInteger();
            if (value is null)
            {
                return null;
            }

            total += value.Value;
        }

        return total;
    }

    private static Node? Find(Node node, string name)
    {
        if (node.Name == name)
        {
            return node;
        }

        foreach (Node child in node.Children)
        {
            if (Find(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
