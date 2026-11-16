using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Decodes every Speex fixture with a hashing sink: decoding changes no node, and what it adds to the report (the verification, the decode findings and the
// hash of the PCM) is compared with <name>.decode.json (docs/design/lossy-decoders.md, decision 14: the hash pins the analyzer's own output for determinism and says
// nothing about correctness, which crosscheck_speex_pcm.py checks against libspeex's fixed-point build).
public class SpeexDecoderFixtureTests
{
    private static readonly string[] Folders = ["ogg"];

    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string folder in Folders)
            {
                string directory = Path.Combine(Fixtures.FixtureDirectory, folder);
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(IsMedia).Order(StringComparer.Ordinal))
                {
                    string name = folder + "/" + Path.GetRelativePath(directory, file).Replace('\\', '/');
                    if (HasSpeex(name))
                    {
                        data.Add(name);
                    }
                }
            }

            return data;
        }
    }

    private static bool IsMedia(string path) => Path.GetExtension(path) is ".spx" or ".ogg";

    private static bool HasSpeex(string name) => Fixtures.AnalyzeMpeg(name).Streams.Any(s => s.Codec.StartsWith("Speex", StringComparison.Ordinal));

    private static (Report Report, List<PcmHashSink> Sinks) Decode(string name)
    {
        var sinks = new List<PcmHashSink>();
        var options = new AnalysisOptions
        {
            Decoding = new DecodeOptions
            {
                Output = stream =>
                {
                    var sink = new PcmHashSink();
                    sink.Format(stream.Format);
                    sinks.Add(sink);
                    return sink;
                },
            },
        };
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        return (Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options), sinks);
    }

    // A few of the fixtures are measured too: the three modes, stereo, discontinuous transmission and a stream with a gap.
    public static TheoryData<string> MeasuredNames => new()
    {
        "ogg/external-speexenc-nb.spx",
        "ogg/external-speexenc-wb.spx",
        "ogg/external-speexenc-uwb.spx",
        "ogg/external-speexenc-wb-stereo.spx",
        "ogg/external-speexenc-wb-vad-dtx.spx",
        "ogg/external-speexenc-nb-vbr-dtx.spx",
        "ogg/sequence-gap.spx",
    };

    [Theory]
    [MemberData(nameof(MeasuredNames))]
    public void MeasuringAddsWhatTheMeasureGoldenHolds(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options);
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

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingChangesNoNode(string name)
    {
        Report plain = Fixtures.AnalyzeMpeg(name);
        (Report decoded, _) = Decode(name);

        Assert.Equal(Tree(plain.Root), Tree(decoded.Root));
    }

    // Name, value, offset, length and children of the first levels and of a sample of the packets.
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

    // A decoder fails no frame the parser walked: every frame the walk reads decodes, and the frames it cannot read are gaps the parser reported.
    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingFailsNoFrameTheParserWalked(string name)
    {
        (Report report, List<PcmHashSink> sinks) = Decode(name);

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed" or "decode.frame-invalid" or "decode.frame-too-large");
        foreach (StreamInfo stream in report.Streams.Where(s => s.Codec.StartsWith("Speex", StringComparison.Ordinal) && s.Verification is not null))
        {
            StreamVerification verification = stream.Verification!;
            Assert.Contains(verification.Decoding, new[] { DecodeStatus.Decoded, DecodeStatus.NotPossible });
            Assert.True(verification.IsLossy || verification.Decoding == DecodeStatus.NotPossible);

            // A Speex stream stores no checksum.
            Assert.Empty(verification.Checksums);
            Assert.NotEqual(true, verification.IsVerified);
        }

        Assert.Equal(report.Streams.Sum(s => s.Verification?.DecodedSamples ?? 0), sinks.Sum(s => s.Samples));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingAddsWhatTheDecodeGoldenHolds(string name)
    {
        (Report report, List<PcmHashSink> sinks) = Decode(name);
        string actual = Golden(report, sinks);
        string path = Fixtures.MpegCompanion(name, ".decode.json");
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    // The verification of every stream that has one, with the hash of the PCM it decoded to, and the findings the decoder and the decode route add.
    private static string Golden(Report report, List<PcmHashSink> sinks)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("streams");
            int hashed = 0;
            foreach (StreamInfo info in report.Streams.Where(s => s.Verification is not null))
            {
                StreamVerification v = info.Verification!;
                writer.WriteStartObject();
                writer.WriteString("decoding", v.Decoding.ToString());
                writer.WriteNumber("decodedSamples", v.DecodedSamples);
                if (v.DecodedSamples > 0 && hashed < sinks.Count)
                {
                    writer.WriteString("pcmSha256", sinks[hashed++].Hash());
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("findings");
            foreach (Finding finding in report.Findings.Where(f => f.Code.StartsWith("decode.", StringComparison.Ordinal)))
            {
                writer.WriteStartObject();
                writer.WriteString("code", finding.Code);
                writer.WriteString("severity", finding.Severity.ToString());
                writer.WriteNumber("count", finding.Count);
                writer.WriteString("message", finding.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n") + "\n";
    }
}
