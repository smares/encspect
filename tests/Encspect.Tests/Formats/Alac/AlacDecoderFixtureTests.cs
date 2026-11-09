using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;
using Encspect.Tests.Measurement;

namespace Encspect.Tests;

// Decodes every ALAC fixture (MP4 and Matroska; the CAF files have no parser) with a hashing sink: the samples of the undamaged ones are the input of the
// encoder (the hash of each channel in the manifest, which crosscheck_alac_fixtures.py confirms with Apple's decoder and FFmpeg), the lent and the unlent
// walk give the same samples, decoding changes no node, and what decoding and measuring add to the report is compared with <name>.decode.json and
// <name>.measure.json (rewritten with ENCSPECT_UPDATE_GOLDEN=1). The bit-exact comparison with Apple's decoder is crosscheck_alac_pcm.py's.
public class AlacDecoderFixtureTests
{
    public static TheoryData<string> Names => AlacFixtureTests.Names;

    // Collects every sample of every channel, for the comparison with the hashes of the encoder's input.
    private sealed class CollectingSink : IPcmSink
    {
        private readonly List<int>[] _channels;

        public CollectingSink(int channels) => _channels = [.. Enumerable.Range(0, channels).Select(_ => new List<int>())];

        public void Write(PcmBlock block)
        {
            for (int c = 0; c < block.Channels; c++)
            {
                _channels[c].AddRange(block.IntegerChannel(c).ToArray());
            }
        }

        public void End(long samples)
        {
        }

        public string[] Hashes(int width, int shift) => [.. _channels.Select(channel =>
        {
            var bytes = new byte[channel.Count * width];
            for (int i = 0; i < channel.Count; i++)
            {
                int value = channel[i] << shift;
                for (int b = 0; b < width; b++)
                {
                    bytes[(i * width) + b] = (byte)(value >> (8 * b));
                }
            }

            return Convert.ToHexStringLower(MD5.HashData(bytes));
        })];
    }

    private static (Report Report, PcmHashSink Sink) Decode(string name, MeasureOptions? measure = null)
    {
        var sink = new PcmHashSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = measure, Output = stream => { sink.Format(stream.Format); return sink; } } };
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        return (Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options), sink);
    }

    private static bool IsDamaged(JsonElement manifest) => manifest.GetProperty("findings").EnumerateArray().Any(f => f.GetProperty("code").GetString()!.StartsWith("alac.", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Names))]
    public void TheDecodedChannelsAreTheEncodersInput(string name)
    {
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        if (IsDamaged(manifest) || !manifest.TryGetProperty("input", out JsonElement input))
        {
            return;
        }

        int channels = input.GetProperty("channels").GetInt32();
        var collected = new CollectingSink(channels);
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => collected } };
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options);

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed" or "decode.frame-invalid" or "decode.not-possible");
        string[] expected = [.. input.GetProperty("channel_md5").EnumerateArray().Select(e => e.GetString()!)];
        string[] actual = collected.Hashes(input.GetProperty("width").GetInt32(), input.GetProperty("left_shift").GetInt32());

        // FFmpeg's encoder writes the channels of more than two in the stream's order, not the input's (crosscheck_alac_fixtures.py).
        if (channels > 2)
        {
            expected = [.. expected.Order(StringComparer.Ordinal)];
            actual = [.. actual.Order(StringComparer.Ordinal)];
        }

        Assert.Equal(expected, actual);
        Assert.Equal(input.GetProperty("samples").GetInt64(), report.Streams.Single().Verification!.DecodedSamples);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingChangesNoNode(string name)
    {
        Report plain = Fixtures.AnalyzeMpeg(name);
        (Report decoded, _) = Decode(name);
        Assert.Equal(Tree(plain.Root), Tree(decoded.Root));
        Assert.DoesNotContain(decoded.Findings, f => f.Code is "decode.failed" or "parse.failed");
        Assert.All(decoded.Findings, f => Assert.True(f.Path is not null, $"The node of {f.Code} is not in the tree."));
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
            for (int i = 0; i < children.Count && i < 100; i++)
            {
                Walk(children[i], depth + 1);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingAddsWhatTheDecodeGoldenHolds(string name)
    {
        (Report report, PcmHashSink sink) = Decode(name);
        string actual = DecoderFixtureTests.Golden(report, report.Streams.Count == 1 && sink.Samples > 0 ? sink.Hash() : null);
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
    [MemberData(nameof(Names))]
    public void MeasuringAddsWhatTheMeasureGoldenHolds(string name)
    {
        (Report report, _) = Decode(name, new MeasureOptions());
        string path = Fixtures.MpegCompanion(name, ".measure.json");
        if (report.Streams.Count != 1 || report.Streams[0].Measurements is null)
        {
            Assert.False(File.Exists(path), $"{path} should not exist: the file has no measured stream.");
            return;
        }

        string actual = MeasurementFixtureTests.Golden(report, Path.GetFileNameWithoutExtension(name));
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    // Measuring changes nothing else in the report: the nodes and the findings that are not the measurement's or the decoder's.
    [Theory]
    [MemberData(nameof(Names))]
    public void MeasuringChangesNoNode(string name)
    {
        Report plain = Fixtures.AnalyzeMpeg(name);
        (Report measured, _) = Decode(name, new MeasureOptions());
        Assert.Equal(Tree(plain.Root), Tree(measured.Root));
    }
}
