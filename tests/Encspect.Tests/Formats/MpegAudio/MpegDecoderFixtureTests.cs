using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Decodes every MPEG audio fixture that has a stream with a hashing sink: decoding changes no node, and what it adds to the report (the verification with the
// frame CRC-16, the decode findings and the hash of the PCM) is compared with <name>.decode.json (docs/design/lossy-decoders.md, decision 14: the hash pins the
// analyzer's own output for determinism and says nothing about correctness, which crosscheck_mpeg_pcm.py checks against minimp3 and FFmpeg).
public class MpegDecoderFixtureTests
{
    private static readonly string[] Extensions = [".mp3", ".mp2", ".mp1"];

    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "mpeg-audio");
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.Ordinal)).Order(StringComparer.Ordinal))
            {
                data.Add("mpeg-audio/" + Path.GetRelativePath(directory, file).Replace('\\', '/'));
            }

            // The ASF files whose MPEG audio stream a decoder takes (format tag 0x0055).
            foreach (string file in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "asf"), "*.asf").Order(StringComparer.Ordinal))
            {
                string name = "asf/" + Path.GetFileName(file);
                if (Fixtures.AnalyzeMpeg(name).Streams.Any(s => s.Codec.StartsWith("MPEG-", StringComparison.Ordinal) && s.Verification is not null))
                {
                    data.Add(name);
                }
            }

            // The MPEG audio of IEC 61937 bursts in WAVE files, handed over as packets.
            foreach (string file in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "wav"), "*iec61937-mp*.wav").Order(StringComparer.Ordinal))
            {
                data.Add("wav/" + Path.GetFileName(file));
            }

            return data;
        }
    }

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

    // The measurements of the encoder-written stereo streams, which show what the mid and side spectra and the holes read for LAME's joint stereo, its forced
    // mid/side stereo and its plain stereo, and for Shine's joint stereo (docs/design/spectral-analysis.md, "As built: step 7, second change").
    public static TheoryData<string> MeasuredNames => new()
    {
        "mpeg-audio/external-lame-joint-music.mp3",
        "mpeg-audio/external-lame-stereo-music.mp3",
        "mpeg-audio/external-lame-forced-ms.mp3",
        "mpeg-audio/external-shine-joint.mp3",
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

    // The Helix encoder's A1 re-coding changes global_gain and the scalefactors against each other for the same step sizes (the fixtures' encoder is
    // docs/development.md, "Encoder-written fixtures"): the step of a band is one power of two of both together, so the decoded floats are the stock encode's.
    [Theory]
    [InlineData("mpeg-audio/external-helix-a1-vbr.mp3")]
    [InlineData("mpeg-audio/external-helix-a1-keep-gain-vbr.mp3")]
    [InlineData("mpeg-audio/external-helix-a1-no-tag-vbr.mp3")]
    public void AnA1ReCodingOfAStreamDecodesToTheSameFloatsAsTheStockEncode(string name)
    {
        string stock = Assert.Single(Decode("mpeg-audio/external-helix-vbr.mp3").Sinks).Hash();

        Assert.Equal(stock, Assert.Single(Decode(name).Sinks).Hash());
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
    public void DecodingFailsNoFrameOfAFileTheParserReadsWhole(string name)
    {
        (Report report, List<PcmHashSink> sinks) = Decode(name);

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        foreach (StreamInfo stream in report.Streams)
        {
            StreamVerification verification = stream.Verification!;
            Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
            Assert.True(verification.IsLossy);
            Assert.Equal(FrameCrc, Assert.Single(verification.Checksums).Name);
            Assert.Equal(ChecksumCoverage.Header, verification.Checksums[0].Covers);

            // The CRC covers no sample, so the stream is never verified; a CRC that fails says the stream is not.
            Assert.Equal(verification.Checksums[0].Failed > 0 ? false : (bool?)null, verification.IsVerified);
        }

        Assert.Equal(report.Streams.Sum(s => s.Verification!.DecodedSamples), sinks.Sum(s => s.Samples));
    }

    private const string FrameCrc = "frame CRC-16";

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingAddsWhatTheDecodeGoldenHolds(string name)
    {
        (Report report, List<PcmHashSink> sinks) = Decode(name);
        if (report.Streams.Count == 0)
        {
            return;
        }

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

    // The verification of every stream with the hash of the PCM it decoded to, and the findings the decoder and the decode route add.
    private static string Golden(Report report, List<PcmHashSink> sinks)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("streams");
            int hashed = 0;
            foreach (StreamInfo info in report.Streams)
            {
                StreamVerification v = info.Verification!;
                writer.WriteStartObject();
                writer.WriteString("decoding", v.Decoding.ToString());
                writer.WriteNumber("decodedSamples", v.DecodedSamples);
                if (v.IsVerified is { } verified)
                {
                    writer.WriteBoolean("isVerified", verified);
                }
                else
                {
                    writer.WriteNull("isVerified");
                }

                writer.WriteStartArray("checksums");
                foreach (ChecksumTally tally in v.Checksums)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", tally.Name);
                    writer.WriteNumber("stored", tally.Stored);
                    writer.WriteNumber("checked", tally.Checked);
                    writer.WriteNumber("failed", tally.Failed);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
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
