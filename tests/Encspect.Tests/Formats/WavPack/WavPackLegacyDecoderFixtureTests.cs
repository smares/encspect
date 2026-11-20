using System.Security.Cryptography;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Decodes every WavPack fixture of versions 1 to 3 with a hashing sink. The encoder-written files' PCM must match the MD5 of the original decoder's output stored in
// the manifest (each release's own unpacker: WVUNPACK 1.0 to 3.4 under DOSBox, wvunpack 3.97 on Windows); every file's verification, samples, PCM MD5 and decode findings are held to
// <name>.decode.json, and decoding must change no node. A main file whose manifest names a correction file is decoded with it, and its PCM must match the original
// decoder's output with the correction file.
public class WavPackLegacyDecoderFixtureTests
{
    public static TheoryData<string> Names => WavPackLegacyFixtureTests.Names;

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

    private static (Report Report, HashingSink Sink) Decode(string name)
    {
        var sink = new HashingSink();
        var options = new AnalysisOptions
        {
            Decoding = new DecodeOptions { Output = stream => { sink.Format(stream.Format); return sink; } },
            Companions = Fixtures.CompanionOptions(name)?.Companions,
        };
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        return (Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options), sink);
    }

    private static bool IsAdded(Finding f) => f.Code.StartsWith("decode.", StringComparison.Ordinal) || f.Code.StartsWith("correction.", StringComparison.Ordinal) || f.Code.StartsWith("wavpack.legacy-crc", StringComparison.Ordinal)
        || f.Code is "wavpack.legacy-data-ended" or "wavpack.legacy-bitstream-end-differs" or "wavpack.legacy-correction-ended";

    [Theory]
    [MemberData(nameof(Names))]
    public void PcmMatchesTheOriginalDecoder(string name)
    {
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        (Report report, HashingSink sink) = Decode(name);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        if (!manifest.TryGetProperty("crosscheck", out JsonElement check) || !check.TryGetProperty("original_decoder", out JsonElement original))
        {
            return;
        }

        // A hybrid file decoded alone is the lossy audio, which the original decoder's run without the correction file gives. The raw-stereo-extra files hold the audio of
        // raw-stereo with 1 to 3 stray bytes after it, which the original decoder writes out beyond the whole samples (96001 bytes) and a decoder of samples does not.
        bool hasCompanion = manifest.TryGetProperty("companion", out JsonElement companion) && companion.ValueKind == JsonValueKind.String;
        string expected = hasCompanion && original.TryGetProperty("with_correction", out JsonElement lossless) ? lossless.GetProperty("pcm_md5").GetString()!
            : original.TryGetProperty("without_correction", out JsonElement lossy) ? lossy.GetProperty("pcm_md5").GetString()!
            : original.GetProperty("pcm_bytes").GetInt64() % 4 != 0 ? Fixtures.LoadMpegManifest("wavpack/legacy/external-wv340-raw-stereo.wv").GetProperty("crosscheck").GetProperty("original_decoder").GetProperty("pcm_md5").GetString()!
            : original.GetProperty("pcm_md5").GetString()!;
        Assert.Equal(expected, sink.Hash());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingChangesNoNode(string name)
    {
        Report plain = Fixtures.AnalyzeMpeg(name);
        (Report decoded, _) = Decode(name);
        Assert.Equal(Fingerprint(plain.Root), Fingerprint(decoded.Root));
    }

    private static string Fingerprint(Node root)
    {
        var text = new System.Text.StringBuilder();
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
        (Report report, HashingSink sink) = Decode(name);
        string actual = Golden(report, sink);
        string path = Fixtures.MpegCompanion(name, ".decode.json");
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static string Golden(Report report, HashingSink sink)
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

                if (v.IsLossy)
                {
                    writer.WriteBoolean("isLossy", true);
                }

                if (v.WithCorrection)
                {
                    writer.WriteBoolean("withCorrection", true);
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
                if (report.Streams.Single().Correction is { } pairing)
                {
                    writer.WriteStartObject("correction");
                    writer.WriteString("status", CorrectionText.Word(pairing.Status));
                    writer.WriteString("basis", CorrectionText.Word(pairing.Basis));
                    writer.WriteStartArray("problems");
                    foreach (CorrectionProblem problem in pairing.Problems)
                    {
                        writer.WriteStringValue($"{CorrectionText.Word(problem.Kind)} {problem.Count} {problem.Node.Name}");
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                if (report.Companions.SingleOrDefault() is { } companion)
                {
                    writer.WriteStartArray("companionTree");
                    foreach (Node node in companion.Report.Root.Children)
                    {
                        writer.WriteStringValue($"{node.Name} {node.Offset}+{node.Length}");
                    }

                    writer.WriteEndArray();
                }

                writer.WriteNumber("pcmSamples", sink.Samples);
                writer.WriteString("pcmMd5", sink.Samples == 0 ? "" : sink.Hash());
            }

            writer.WriteStartArray("findings");
            foreach (Finding finding in report.Findings.Where(IsAdded))
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

        return System.Text.Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n") + "\n";
    }
}
