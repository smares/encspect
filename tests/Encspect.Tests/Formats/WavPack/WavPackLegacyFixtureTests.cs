using System.Globalization;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Checks every WavPack fixture of versions 1 to 3 (tests/fixtures/wavpack/legacy: the encoder-written external-* files and the hand-built built-legacy-* ones) against
// its manifest, which wavpack_legacy_model.py works out from the bytes apart from the analyzer: the stored WAV header's chunks, the WavPack header's fields, the
// bitstream and the trailer, the figures (the mode, the stereo coding, the codec) and exactly the findings listed; and against a golden report.
public class WavPackLegacyFixtureTests
{
    private static string Directory_ => Path.Combine(Fixtures.FixtureDirectory, "wavpack", "legacy");

    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string manifest in Directory.EnumerateFiles(Directory_, "*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                string file = Directory.EnumerateFiles(Directory_, stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
                data.Add("wavpack/legacy/" + Path.GetFileName(file));
            }

            return data;
        }
    }

    [Fact]
    public void TheFolderHoldsTheFixturesTheDesignListsAndTheHandBuiltOnes() =>
        Assert.True(Directory.EnumerateFiles(Directory_, "external-*.wv").Count() >= 60 && Directory.EnumerateFiles(Directory_, "built-legacy-*.wv").Count() >= 30);

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        Assert.Equal(manifest.GetProperty("length").GetInt64(), report.Root.Length);
        if (manifest.TryGetProperty("kind", out JsonElement kind) && kind.GetString() == "correction")
        {
            Assert.Empty(report.Streams);
            Assert.Contains(report.Findings, f => f.Code == "format.unrecognized");
            return;
        }

        Assert.Equal("WavPack", report.Root.Value?.ToString());
        Node stream = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        CheckFindings(manifest.GetProperty("findings"), report.Findings, name);
        CheckHeader(manifest.GetProperty("header"), stream, name);
        if (manifest.TryGetProperty("wav", out JsonElement wav))
        {
            CheckWav(wav, stream);
        }
        else
        {
            Assert.DoesNotContain(stream.Children, c => c.Name == "WAV Header");
        }

        if (manifest.TryGetProperty("bitstream", out JsonElement bitstream))
        {
            Node node = stream.Children.Single(c => c.Name == "Bitstream");
            Assert.Equal($"{bitstream.GetProperty("offset").GetInt64()}+{bitstream.GetProperty("length").GetInt64()}", $"{node.Offset}+{node.Length}");
        }

        CheckTrailer(manifest, stream, name);
        bool hasStream = manifest.GetProperty("stream").GetBoolean();
        Assert.Equal(hasStream, report.Streams.Count == 1);
        if (hasStream)
        {
            CheckStream(manifest.GetProperty("figures"), report.Streams.Single(), stream, name);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string name)
    {
        string actual = Fixtures.ToJson(Fixtures.AnalyzeMpeg(name));
        string path = Fixtures.MpegCompanion(name, ".report.json");

        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden report {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static void CheckFindings(JsonElement expected, IReadOnlyList<Finding> findings, string name)
    {
        string[] wanted = [.. expected.EnumerateArray().Select(f => $"{f.GetProperty("severity").GetString()} {f.GetProperty("code").GetString()}").Order(StringComparer.Ordinal)];
        string[] actual = [.. findings.Select(f => $"{f.Severity.ToString().ToLowerInvariant()} {f.Code}").Order(StringComparer.Ordinal)];
        Assert.True(wanted.SequenceEqual(actual), $"{name}: expected findings [{string.Join(", ", wanted)}], got [{string.Join("; ", findings.Select(f => $"{f.Code}: {f.Message}"))}]");
        Assert.All(findings, f => Assert.True(f.Path is not null, $"The node of {f.Code} is not in the tree."));
    }

    private static long Integer(Node parent, string child) => parent.Children.Single(c => c.Name == child).Value!.AsInteger()!.Value;

    private static void CheckHeader(JsonElement expected, Node stream, string name)
    {
        Node header = stream.Children.Single(c => c.Name == "WavPack Header");
        Assert.Equal($"{expected.GetProperty("offset").GetInt64()}+{expected.GetProperty("length").GetInt64()}", $"{header.Offset}+{header.Length}");
        if (!expected.TryGetProperty("ck_size", out JsonElement size) || expected.TryGetProperty("cut", out JsonElement cut) && cut.GetBoolean())
        {
            return;
        }

        Assert.Equal(size.GetInt64(), Integer(header, "Size"));
        int version = expected.GetProperty("version").GetInt32();
        Assert.Equal(version, Integer(header, "Version"));
        if (version is < 1 or > 3)
        {
            return;
        }

        if (version >= 2)
        {
            Node bits = header.Children.Single(c => c.Name is "Lossy Bit Limit" or "Hybrid Target");
            Assert.Equal(expected.GetProperty("bits").GetInt64(), bits.Value!.AsInteger());
        }

        if (version == 3)
        {
            Assert.Equal(expected.GetProperty("flags").GetString(), Integer(header, "Flags").ToString("x4", CultureInfo.InvariantCulture));
            Node flags = header.Children.Single(c => c.Name == "Flags");
            Assert.Equal(16, flags.Children.Count);
            string[] names = [.. expected.GetProperty("flag_names").EnumerateArray().Select(n => n.GetString()!)];
            Assert.Equal(names.Length, flags.Children.Count(c => c.Value!.AsBoolean() == true));
            Assert.Equal(expected.GetProperty("shift").GetInt64(), Integer(header, "Shift"));
            Assert.Equal(expected.GetProperty("total_samples").GetInt64(), Integer(header, "Total Samples"));
            Assert.Equal(expected.GetProperty("crc").GetString(), Integer(header, "Audio CRC").ToString("x8", CultureInfo.InvariantCulture));
            Assert.Equal(expected.GetProperty("crc2").GetString(), Integer(header, "Correction CRC").ToString("x8", CultureInfo.InvariantCulture));
            Assert.Equal(expected.GetProperty("extension").GetString(), header.Children.Single(c => c.Name == "Source Extension").Value?.AsText() ?? string.Empty);
            Assert.Equal(expected.GetProperty("extra_bc").GetInt64(), Integer(header, "Extra Byte Count"));
            Assert.Equal(expected.GetProperty("extras").GetString(), Integer(header, "Extra Bytes").ToString("x6", CultureInfo.InvariantCulture));
        }
    }

    private static void CheckWav(JsonElement wav, Node stream)
    {
        Node node = stream.Children.Single(c => c.Name == "WAV Header");
        Assert.Equal(wav.GetProperty("riff_size").GetInt64(), Integer(node.Children.Single(c => c.Name == "RIFF Chunk"), "Size"));
        JsonElement[] chunks = [.. wav.GetProperty("chunks").EnumerateArray()];
        Node[] nodes = [.. node.Children.Where(c => c.Name != "RIFF Chunk")];
        Assert.Equal(chunks.Select(c => $"{c.GetProperty("id").GetString()}@{c.GetProperty("offset").GetInt64()}:{c.GetProperty("size").GetInt64()}"),
            nodes.Select(c => $"{c.Children.Single(k => k.Name == "ID").Value}@{c.Offset}:{Integer(c, "Size")}"));
        if (wav.TryGetProperty("format", out JsonElement format) && nodes.FirstOrDefault(c => c.Name == "fmt Chunk") is { } fmt && Integer(fmt, "Size") >= 16)
        {
            Assert.Equal(format.GetProperty("channels").GetInt64(), Integer(fmt, "Channels"));
            Assert.Equal(format.GetProperty("sample_rate").GetInt64(), Integer(fmt, "Sample Rate"));
            Assert.Equal(format.GetProperty("block_align").GetInt64(), Integer(fmt, "Block Align"));
            Assert.Equal(format.GetProperty("bits_per_sample").GetInt64(), Integer(fmt, "Bits Per Sample"));
        }

        Assert.Equal(wav.GetProperty("data_size").GetInt64(), Integer(nodes.Single(c => c.Name == "data Chunk"), "Size"));
    }

    private static void CheckTrailer(JsonElement manifest, Node stream, string name)
    {
        Node? node = stream.Children.SingleOrDefault(c => c.Name == "Trailer");
        if (!manifest.TryGetProperty("trailer", out JsonElement trailer))
        {
            Assert.Null(node);
            return;
        }

        Assert.NotNull(node);
        Assert.Equal($"{trailer.GetProperty("offset").GetInt64()}+{trailer.GetProperty("length").GetInt64()}", $"{node.Offset}+{node.Length}");
        Assert.Equal(16, Integer(node, "Marker"));
        JsonElement[] chunks = [.. trailer.GetProperty("chunks").EnumerateArray()];
        Node[] listed = [.. node.Children.Where(c => c.Name.EndsWith(" Chunk", StringComparison.Ordinal))];
        Assert.Equal(Math.Min(chunks.Length, 64), listed.Length);
        Assert.Equal(chunks.Take(64).Select(c => $"{c.GetProperty("id").GetString()}:{c.GetProperty("size").GetInt64()}"), listed.Select(c => $"{c.Children.Single(k => k.Name == "ID").Value}:{Integer(c, "Size")}"));
        Assert.Equal(chunks.Length > 64, node.Children.Any(c => c.Name == "Further Chunks"));
        _ = name;
    }

    private static void CheckStream(JsonElement figures, StreamInfo info, Node stream, string name)
    {
        string mode = figures.GetProperty("mode").GetString()!;
        Assert.Equal(mode switch { "hybrid" => "WavPack hybrid", "lossy" => "WavPack lossy", _ => "WavPack lossless" }, info.Codec);
        Assert.Equal(figures.GetProperty("sample_rate").GetInt32(), info.SampleRate);
        Assert.Equal(figures.GetProperty("channels").GetInt32(), info.Channels);
        Assert.Equal(figures.GetProperty("bits_per_sample").GetInt32(), info.BitsPerSample);
        Assert.Equal(figures.GetProperty("samples").GetInt64(), info.Samples);
        Assert.Equal("WavPack " + stream.Children.Single(c => c.Name == "WavPack Header").Children.Single(c => c.Name == "Version").Value, info.Encoder);
        Assert.Equal(Confidence.Medium, info.EncoderConfidence);
        Assert.NotNull(info.Verification);
        Assert.Equal(DecodeStatus.NoDecoder, info.Verification.Decoding);
        Node statistics = stream.Children.Single(c => c.Name == "Statistics");
        Assert.Equal(figures.GetProperty("compression_mode").GetString(), statistics.Children.Single(c => c.Name == "Compression Mode").Value?.AsText());
        JsonElement stereo = figures.GetProperty("stereo_coding");
        Node? stereoNode = statistics.Children.SingleOrDefault(c => c.Name == "Stereo Coding");
        Assert.Equal(stereo.ValueKind == JsonValueKind.Null ? null : stereo.GetString(), stereoNode?.Value?.AsText());
        Assert.Equal(mode == "hybrid", statistics.Children.Single(c => c.Name == "IsHybrid").Value?.AsBoolean());
        Assert.Equal(mode == "lossless", statistics.Children.Single(c => c.Name == "IsLossless").Value?.AsBoolean());
        Assert.Equal(mode == "hybrid", statistics.Children.Any(c => c.Name == "Hybrid Target Bitrate"));
        Assert.Equal(mode == "lossy", statistics.Children.Any(c => c.Name == "Lossy Bit Limit"));
        _ = name;
    }
}

// Inputs built from a legacy fixture in memory: the chunk over 1 MiB that libwavpack refuses (a fixture of it would be a megabyte), and runs of 0xFF that must
// cost time and memory in proportion to the data.
[Collection(nameof(ComplexityTests))]
public class WavPackLegacyCraftedTests
{
    private const int StoredEnd = 44;

    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "wavpack", "legacy", "external-wv340-lossless-16-stereo.wv"));

    private static Report Analyze(byte[] data) => Analyzer.CreateDefault().Analyze("crafted.wv", new MemoryStream(data));

    [Fact]
    public void AChunkBeforeTheDataOverOneMebibyteIsFound()
    {
        byte[] original = Fixture();
        byte[] junk = [.. "JUNK"u8, .. BitConverter.GetBytes((uint)((1 << 20) + 2)), .. new byte[(1 << 20) + 2]];
        byte[] data = [.. original[..36], .. junk, .. original[36..]];
        Report report = Analyze(data);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "wavpack.legacy-wav-format-unsupported");
        Assert.Contains("1 MiB", finding.Message, StringComparison.Ordinal);
        Assert.NotNull(finding.Path);
        Assert.Single(report.Streams);
    }

    [Fact]
    public void AHeaderFollowedByAVeryLongRunOfFfIsRead()
    {
        byte[] original = Fixture();
        byte[] data = [.. original[..(StoredEnd + 36)], .. Enumerable.Repeat((byte)0xFF, 16 << 20)];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Report report = Analyze(data);
        watch.Stop();

        Assert.Contains(report.Findings, f => f.Code == "wavpack.legacy-trailer-unrecognized");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Took {watch.Elapsed}.");
    }

    // Each candidate run sits in a chunk body, so the chunks after it chain on to the last bytes, which are no chunk: without a budget every candidate would walk to
    // the end, and the work would grow with the square of the file.
    [Fact]
    public void ManyMarkersThatEachChainToTheEndAreCapped()
    {
        byte[] original = Fixture();
        byte[] chunk = [.. "abcd"u8, .. BitConverter.GetBytes(16u), .. Enumerable.Repeat((byte)0xFF, 16)];
        byte[] data = [.. original[..(StoredEnd + 36)], .. Enumerable.Repeat(chunk, 60_000).SelectMany(c => c), 1, 2, 3];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Report report = Analyze(data);
        watch.Stop();

        Assert.Single(report.Streams);
        Assert.Contains(report.Findings, f => f.Code == "wavpack.legacy-trailer-unrecognized");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public void AStoredHeaderOfManyChunksIsReadToTheDataOrLeftToTheWavParser()
    {
        byte[] original = Fixture();
        byte[] junk = [.. "JUNK"u8, .. new byte[4]];
        byte[] few = [.. original[..36], .. Enumerable.Repeat(junk, 200).SelectMany(c => c), .. original[36..]];
        byte[] many = [.. original[..36], .. Enumerable.Repeat(junk, 300).SelectMany(c => c), .. original[36..]];

        Assert.Single(Analyze(few).Streams, s => s.Codec == "WavPack lossless");
        Assert.DoesNotContain(Analyze(many).Streams, s => s.Codec.StartsWith("WavPack", StringComparison.Ordinal));
    }
}

