using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Decodes every TTA fixture with a hashing sink and holds the analyzer to its manifest: for the encoder-written files the samples and the MD5 of the signal
// the encoder was given (decoded_samples, decoded_pcm_md5: crosscheck_tta_pcm.py confirms them with `tta -d` and FFmpeg), and the decode findings. It also checks
// that decoding changes no node, and compares what decoding adds to the report with <name>.decode.json and, for the encoder-written files, what measuring
// adds with <name>.measure.json. The hand-built files hold bytes in place of coded samples: their goldens pin what the decoder makes of them.
public class TtaDecoderFixtureTests
{
    public static TheoryData<string> Names => TtaFixtureTests.Names;

    public static TheoryData<string> DecodedNames
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "tta");
            foreach (string manifest in Directory.EnumerateFiles(directory, "external-*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                data.Add("tta/" + Path.GetFileName(Directory.EnumerateFiles(directory, stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal))));
            }

            return data;
        }
    }

    private static (Report Report, PcmHashingSink Sink) Decode(string name, MeasureOptions? measure = null)
    {
        var sink = new PcmHashingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = measure } };
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        return (Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options), sink);
    }

    [Theory]
    [MemberData(nameof(DecodedNames))]
    public void DecodedAudioMatchesTheManifest(string name)
    {
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        (Report report, PcmHashingSink sink) = Decode(name);

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        var wanted = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement item in manifest.GetProperty("decode_findings").EnumerateArray())
        {
            wanted[item.GetProperty("code").GetString()!] = item.TryGetProperty("count", out JsonElement count) ? count.GetInt32() : 1;
        }

        var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Finding finding in report.Findings.Where(f => f.Code.StartsWith("decode.", StringComparison.Ordinal)))
        {
            actual[finding.Code] = finding.Count;
        }

        Assert.Equal(wanted, actual);
        if (wanted.Count == 0)
        {
            Assert.Equal(manifest.GetProperty("decoded_samples").GetInt64(), sink.Samples);
            Assert.Equal(manifest.GetProperty("decoded_pcm_md5").GetString(), sink.Hash());
            StreamVerification verification = report.Streams.Single().Verification!;
            Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
            Assert.Equal(sink.Samples, verification.DecodedSamples);
            Assert.Equal(report.Streams.Single().Channels, sink.Channels);
            Assert.Equal(report.Streams.Single().SampleRate, sink.SampleRate);
            Assert.Equal(8 * ((report.Streams.Single().BitsPerSample + 7) / 8), sink.Bits);
        }
        else
        {
            Assert.Equal(0, sink.Declared);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingChangesNoNode(string name)
    {
        Report plain = Fixtures.AnalyzeMpeg(name);
        (Report decoded, _) = Decode(name);

        Assert.Equal(Tree(plain.Root), Tree(decoded.Root));
        Assert.DoesNotContain(decoded.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }

    // Name, value, offset, length and children of the first levels and of a sample of the frames: the tree without anything a front end could add.
    private static string Tree(Node root)
    {
        var text = new StringBuilder();
        Walk(root, 0);
        return text.ToString();

        void Walk(Node node, int depth)
        {
            text.Append(' ', depth).Append(node.Name).Append('|').Append(node.Value).Append('|').Append(node.Offset).Append('+').Append(node.Length).Append('\n');
            IReadOnlyList<Node> children = node.Children;
            for (int i = 0; i < children.Count && i < 200; i++)
            {
                Walk(children[i], depth + 1);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingAddsWhatTheDecodeGoldenHolds(string name)
    {
        (Report report, _) = Decode(name);
        string actual = DecoderFixtureTests.Golden(report);
        string path = Fixtures.MpegCompanion(name, ".decode.json");
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    [Theory]
    [MemberData(nameof(DecodedNames))]
    public void MeasuringAddsWhatTheMeasureGoldenHolds(string name)
    {
        (Report report, _) = Decode(name, new MeasureOptions());
        string actual = Encspect.Tests.Measurement.MeasurementFixtureTests.Golden(report, Path.GetFileNameWithoutExtension(name));
        string path = Fixtures.MpegCompanion(name, ".measure.json");
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }
}
