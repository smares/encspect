using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Decodes every FLAC fixture with a hashing sink and holds the analyzer to its manifest (the samples and the MD5 of the canonical layout that
// flac_model.py's decoder gives, which crosscheck_flac_fixtures.py confirms with flac and FFmpeg, and the verification by the rule of
// docs/design/decoders.md), checks that decoding changes no node, and compares what decoding adds to the report with <name>.decode.json.
public class DecoderFixtureTests
{
    public static TheoryData<string> Names => FlacFixtureTests.Names;

    private sealed class HashingSink : IPcmSink
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        private int _bits;

        public long Samples { get; private set; }

        public void Format(PcmFormat format) => _bits = format.BitsPerSample;

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

    private static (Report Report, HashingSink Sink) Decode(string name, bool lends = true)
    {
        var sink = new HashingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = stream => { sink.Format(stream.Format); return sink; } } };
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        Analyzer analyzer = lends ? Analyzer.CreateDefault()
            : new Analyzer(DefaultParsers.Tags, [.. DefaultParsers.Formats.Select(f => f is Encspect.Formats.Flac.FlacParser ? new Encspect.Formats.Flac.FlacParser { LendsBuffers = false } : f)], DefaultParsers.Codecs, DefaultParsers.Decoders);
        return (analyzer.Analyze(Path.GetFileName(name), stream, options), sink);
    }

    // The walk that stores nothing makes the decoder read every frame itself (the measurement in docs/design/decoders.md): the same samples
    // and the same verdict come out.
    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingWithoutLendingGivesTheSameAudioAndVerdict(string name)
    {
        (Report lent, HashingSink lentSink) = Decode(name);
        (Report read, HashingSink readSink) = Decode(name, lends: false);
        Assert.DoesNotContain(read.Findings, f => f.Code is "decode.failed" or "parse.failed");
        Assert.Equal(lentSink.Hash(), readSink.Hash());
        Assert.Equal(lentSink.Samples, readSink.Samples);
        Assert.Equal(lent.Streams.Select(s => s.Verification?.IsVerified), read.Streams.Select(s => s.Verification?.IsVerified));
        Assert.Equal(lent.Findings.Select(f => f.Code).Order(), read.Findings.Select(f => f.Code).Order());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodedAudioAndVerificationMatchTheManifest(string name)
    {
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        (Report report, HashingSink sink) = Decode(name);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        JsonElement expected = manifest.GetProperty("verification").GetProperty("verify");
        string decoding = expected.GetProperty("decoding").GetString()!;
        if (report.Streams.Count == 0)
        {
            return;
        }

        StreamVerification verification = report.Streams.Single().Verification!;
        FlacFixtureTests.CheckVerification(expected, verification, decoding == "decoded" ? DecodeStatus.Decoded : DecodeStatus.NotPossible);
        if (manifest.TryGetProperty("decoded_pcm_md5", out JsonElement md5))
        {
            Assert.Equal(manifest.GetProperty("decoded_samples").GetInt64(), verification.DecodedSamples);
            Assert.Equal(verification.DecodedSamples, sink.Samples);
            Assert.Equal(md5.GetString(), sink.Hash());
        }
        else
        {
            Assert.Equal(0, sink.Samples);
        }
    }

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
    internal static string Golden(Report report)
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
            foreach (Finding finding in report.Findings.Where(f => f.Code.StartsWith("decode.", StringComparison.Ordinal) || f.Code == "flac.md5-mismatch"))
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
