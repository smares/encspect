using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Decodes every Monkey's Audio fixture with a hashing sink and holds the analyzer to its manifest: the samples and the MD5 of the canonical layout that
// the signal given to the encoder makes (which crosscheck_monkeys_audio_fixtures.py confirms with mac and FFmpeg), the verification by the rule of
// docs/design/decoders.md and the findings the decoding adds. It also checks that decoding changes no node, and compares what decoding adds to the report
// with <name>.decode.json.
public class MonkeysAudioDecoderFixtureTests
{
    public static TheoryData<string> Names => MonkeysAudioFixtureTests.Names;

    private sealed class HashingSink : IPcmSink
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        private int _bits;

        public long Samples { get; private set; }

        public PcmFormat? Declared { get; private set; }

        public void Format(PcmFormat format)
        {
            _bits = format.BitsPerSample;
            Declared = format;
        }

        public void Write(PcmBlock block)
        {
            var buffer = new byte[CanonicalPcm.ByteCount(block, _bits)];
            CanonicalPcm.Write(block, _bits, buffer);
            _hash.AppendData(buffer);
            Samples += block.Length;
        }

        public void End(long samples)
        {
        }

        public string Hash() => Convert.ToHexStringLower(_hash.GetHashAndReset());
    }

    private static (Report Report, HashingSink Sink) Decode(string name)
    {
        var sink = new HashingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = stream => { sink.Format(stream.Format); return sink; } } };
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        return (Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options), sink);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodedAudioAndVerificationMatchTheManifest(string name)
    {
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        (Report report, HashingSink sink) = Decode(name);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        if (report.Streams.Count == 0)
        {
            return;
        }

        JsonElement expected = manifest.GetProperty("verification").GetProperty("verify");
        string decoding = expected.GetProperty("decoding").GetString()!;
        StreamVerification verification = report.Streams.Single().Verification!;
        MonkeysAudioFixtureTests_Verification(expected, verification, decoding == "decoded" ? DecodeStatus.Decoded : DecodeStatus.NotPossible);
        Assert.Equal(manifest.GetProperty("decoded_samples").GetInt64(), verification.DecodedSamples);
        Assert.Equal(manifest.GetProperty("pcm_samples").GetInt64(), sink.Samples);
        if (manifest.TryGetProperty("decoded_pcm_md5", out JsonElement md5))
        {
            Assert.Equal(md5.GetString(), sink.Hash());
        }

        // The layout the decoder tells its sink is the stream's.
        if (sink.Declared is { } declared)
        {
            Assert.Equal(report.Streams.Single().ChannelLayout, declared.ChannelLayout);
        }

        var wanted = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement item in manifest.GetProperty("decode_findings").EnumerateArray())
        {
            wanted[item.GetProperty("code").GetString()!] = item.GetProperty("count").GetInt32();
        }

        var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Finding finding in report.Findings.Where(f => f.Code.StartsWith("decode.", StringComparison.Ordinal) || f.Code is "monkeys-audio.frame-crc-mismatch" or "monkeys-audio.frame-checksum-mismatch"))
        {
            actual[finding.Code] = finding.Count;
        }

        Assert.Equal(wanted, actual);
    }

    private static void MonkeysAudioFixtureTests_Verification(JsonElement expected, StreamVerification verification, DecodeStatus status) =>
        WavPackDecoderFixtureTests.CheckVerification(expected, verification, status);

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingChangesNoNode(string name)
    {
        Report plain = Fixtures.AnalyzeMpeg(name);
        (Report decoded, _) = Decode(name);
        Assert.Equal(Tree(plain.Root), Tree(decoded.Root));
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
        string actual = Golden(report);
        string path = Fixtures.MpegCompanion(name, ".decode.json");
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    // The verification of the stream and the findings the decoder and the decode route add.
    private static string Golden(Report report)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            if (report.Streams.SingleOrDefault()?.Verification is { } v)
            {
                writer.WriteStartObject("verification");
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
                writer.WriteEndObject();
            }

            writer.WriteStartArray("findings");
            foreach (Finding finding in report.Findings.Where(f => f.Code.StartsWith("decode.", StringComparison.Ordinal) || f.Code is "monkeys-audio.frame-crc-mismatch" or "monkeys-audio.frame-checksum-mismatch"))
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

    private static readonly Speaker[] LeftRight = [Speaker.FrontLeft, Speaker.FrontRight];

    // What the decoder tells its sink about the channels, from the channel mask of the stored WAV header: the decoder of the current format (a file with a
    // descriptor, and one of 3.95 with the old header), the decoder of 3.80 to 3.92, and the defaults where the header has no mask.
    public static TheoryData<string, Speaker[]?> DeclaredLayouts => new()
    {
        { "monkeys-audio/external-mask-5.1-sides.ape", [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.SideLeft, Speaker.SideRight] },
        { "monkeys-audio/external-bw64-5.1-sides.ape", [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.SideLeft, Speaker.SideRight] },
        { "monkeys-audio/external-w64-5.1-back.ape", [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight] },
        { "monkeys-audio/legacy-3950-normal-16-stereo-mask-center-lfe.ape", [Speaker.FrontCenter, Speaker.LowFrequency] },
        { "monkeys-audio/legacy-3910-normal-16-stereo-mask-sides.ape", [Speaker.SideLeft, Speaker.SideRight] },
        { "monkeys-audio/external-level-normal-16-stereo.ape", LeftRight },
        { "monkeys-audio/external-normal-16-mono.ape", [Speaker.FrontCenter] },
        { "monkeys-audio/legacy-3910-fast-8-mono.ape", [Speaker.FrontCenter] },
        { "monkeys-audio/external-plain-format-6ch.ape", null },
    };

    [Theory]
    [MemberData(nameof(DeclaredLayouts))]
    public void TheDecoderDeclaresTheLayoutTheMaskGives(string name, Speaker[]? expected)
    {
        (Report report, HashingSink sink) = Decode(name);

        Assert.NotNull(sink.Declared);
        Assert.Equal(expected, sink.Declared.ChannelLayout);
        Assert.Equal(expected, report.Streams.Single().ChannelLayout);
    }
}
