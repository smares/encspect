using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests;

// Decodes the DTS fixtures the core decoder reconstructs with a hashing sink: decoding changes no node, and what it adds to the report (the verification, the decode findings and the
// hash of the PCM) is compared with <name>.decode.json. The hash pins the analyzer's own output for determinism and says nothing about correctness, which crosscheck_dts_pcm.py checks
// against FFmpeg's decoder. The fixtures are FFmpeg's encoder files and the hand-built ones without prediction and vector quantization, whose tables the specification's PDF lacks.
public class DtsDecoderFixtureTests
{
    public static TheoryData<string> Names => new()
    {
        "dts/external-ffmpeg-mono-8k-64k.dts",
        "dts/external-ffmpeg-stereo-22k-192k.dts",
        "dts/external-ffmpeg-stereo-44k-256k.dts",
        "dts/external-ffmpeg-5.0-16k-384k.dts",
        "dts/external-ffmpeg-5.1-11k-384k.dts",
        "dts/external-ffmpeg-5.1-48k-768k.dts",
        "dts/subframes-reconstruct-stereo.dts",
        "dts/subframes-reconstruct-five-one.dts",
    };

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
    public void DecodingChangesNoNode(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        Report plain = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream);
        (Report decoded, _) = Decode(name);

        Assert.Equal(Tree(plain.Root), Tree(decoded.Root));
    }

    // Every frame the parser walked decodes: no finding of the decode route, and the samples the sink got are those the verification counts.
    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingFailsNoFrameTheParserWalked(string name)
    {
        (Report report, List<PcmHashSink> sinks) = Decode(name);

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed" or "decode.frame-invalid" or "decode.frame-too-large");
        StreamInfo stream = Assert.Single(report.Streams);
        StreamVerification verification = stream.Verification!;
        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.True(verification.IsLossy);
        Assert.Equal(stream.Samples, verification.DecodedSamples);
        Assert.Equal(verification.DecodedSamples, sinks.Sum(s => s.Samples));
    }

    // What the report states for a stream is what the measurements keep and skip: measured + lost + withheld (docs/design/samples-definition.md).
    [Theory]
    [MemberData(nameof(Names))]
    public void TheMeasurementsKeepWhatTheReportStates(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        StreamInfo info = Assert.Single(Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options).Streams);
        StreamMeasurements m = Assert.IsType<StreamMeasurements>(info.Measurements);
        Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
    }

    // A frame the decoder cannot use is a gap: the frames after it are withheld for the filter bank's history, and the three counts still add up.
    [Fact]
    public void AFrameThatIsCutLeavesAGapTheCountsKeep()
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, "dts/external-ffmpeg-stereo-44k-256k.dts");
        byte[] data = File.ReadAllBytes(path);
        // Overwrite the sync word of the fifth frame.
        int frame = (((data[5] & 3) << 12) | (data[6] << 4) | (data[7] >> 4)) + 1;
        byte[] damaged = [.. data];
        damaged[frame * 4] ^= 0xFF;
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        Report report = Analyzer.CreateDefault().Analyze("damaged.dts", new MemoryStream(damaged), options);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        foreach (StreamInfo info in report.Streams.Where(s => s.Measurements is not null))
        {
            StreamMeasurements m = info.Measurements!;
            Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingAddsWhatTheDecodeGoldenHolds(string name)
    {
        (Report report, List<PcmHashSink> sinks) = Decode(name);
        string actual = Golden(report, sinks);
        string path = Path.Combine(Fixtures.FixtureDirectory, Path.ChangeExtension(name, ".decode.json"));
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

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
