using System.Globalization;
using System.Text.Json;
using Encspect.Core;
using Encspect.Formats.Tak;

namespace Encspect.Tests;

// Checks every TAK fixture against its manifest (import_windows_tak_fixtures.py, worked out by tak_model.py, a reading of takStream.pas apart from the analyzer, and
// confirmed by crosscheck_tak_fixtures.py): the metadata objects with their CRC-24s, the stream info, the encoder info, the wave data, the last frame info, the stream info of the
// report, the verification and exactly the findings listed. Every metadata CRC-24 of every file Takc wrote must hold under the reading docs/formats/tak.md lists.
public class TakFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "tak");
            foreach (string manifest in Directory.EnumerateFiles(directory, "*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                data.Add("tak/" + stem + ".tak");
            }

            return data;
        }
    }

    [Fact]
    public void TheFixturesAreThere() => Assert.True(Names.Count >= 45, $"Found only {Names.Count} fixtures.");

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        JsonElement manifest = Fixtures.LoadMpegManifest(name);

        Assert.Equal(manifest.GetProperty("length").GetInt64(), report.Root.Length);
        Node stream = Assert.Single(report.Root.Children, n => n.Name == "TAK");
        Assert.Equal(manifest.GetProperty("end").GetInt64(), stream.End);
        Node[] objects = [.. stream.Children.Where(c => c.Name.EndsWith(" Object", StringComparison.Ordinal))];
        JsonElement[] expected = [.. manifest.GetProperty("objects").EnumerateArray()];
        Assert.Equal(expected.Length, objects.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            CheckObject(expected[i], objects[i], manifest.GetProperty("length").GetInt64(), $"{name}, object {i}");
        }

        Node? audio = stream.Children.SingleOrDefault(c => c.Name == "Audio Data");
        if (manifest.GetProperty("audio_start").ValueKind == JsonValueKind.Null)
        {
            Assert.Null(audio);
        }
        else
        {
            Assert.Equal(manifest.GetProperty("audio_start").GetInt64(), audio!.Offset);
        }

        string[] codes = [.. manifest.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()!).Order(StringComparer.Ordinal)];
        // The host's findings about a tag after the stream (Takc's APEv2 tag lists its items unsorted) are not the parser's.
        Assert.Equal(codes, report.Findings.Select(f => f.Code).Where(c => c.StartsWith("tak.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        Assert.Equal(manifest.GetProperty("stream_info_first").GetBoolean(), objects.Length > 0 && objects[0].Name == "Stream Info Object");
        if (!name.StartsWith("tak/external-", StringComparison.Ordinal))
        {
            return;
        }

        // The files Takc wrote: every CRC-24 holds, the figures are the manifest's, and nothing is a finding.
        Assert.Empty(codes);
        Assert.All(expected, e => Assert.True(e.GetProperty("crc_ok").ValueKind == JsonValueKind.Null || e.GetProperty("crc_ok").GetBoolean()));
        CheckStreamInfo(manifest, stream, objects, Assert.Single(report.Streams));
        CheckEncoder(manifest, objects, Assert.Single(report.Streams));
        CheckWaveData(manifest, objects);
        CheckLastFrame(manifest, objects);
        Node? md5 = objects.FirstOrDefault(o => o.Name == "MD5 Object");
        JsonElement md5Expected = manifest.GetProperty("md5");
        if (md5Expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(md5);
        }
        else
        {
            Assert.Equal(md5Expected.GetString(), md5!.Children.Single(c => c.Name == "MD5").Value?.ToString());
        }

        CheckVerification(manifest, report.Streams.Single().Verification!);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string name)
    {
        string actual = Fixtures.ToGoldenJson(name, Fixtures.AnalyzeMpeg(name));
        string path = Fixtures.MpegCompanion(name, ".report.json");

        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden report {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static string Hex(NodeValue? value, int digits) => ((long)value!.AsInteger()!).ToString("x" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static void CheckObject(JsonElement expected, Node node, long fileLength, string where)
    {
        Node Field(string field) => node.Children.Single(c => c.Name == field);
        Assert.True(expected.GetProperty("offset").GetInt64() == node.Offset, $"{where} is at {node.Offset}");
        Assert.Equal(expected.GetProperty("id").GetInt32(), Field("Object ID").Value?.AsInteger());
        Assert.Equal(expected.GetProperty("size").GetInt64(), Field("Object Size").Value?.AsInteger());
        Assert.Equal(Math.Min(expected.GetProperty("end").GetInt64(), fileLength) - node.Offset, node.Length);
        if (expected.GetProperty("crc_ok").ValueKind == JsonValueKind.Null)
        {
            Assert.DoesNotContain(node.Children, c => c.Name == "CRC-24");
            return;
        }

        Assert.Equal(expected.GetProperty("crc").GetInt64().ToString("x6", CultureInfo.InvariantCulture), Hex(Field("CRC-24").Value, 6));
        Assert.Equal(expected.GetProperty("crc_ok").GetBoolean(), Field("IsCrc24Valid").Value?.AsBoolean());
    }

    private static void CheckStreamInfo(JsonElement manifest, Node streamNode, Node[] objects, StreamInfo info)
    {
        JsonElement summary = manifest.GetProperty("summary");
        JsonElement stream = manifest.GetProperty("stream_info");
        Node node = objects.Single(o => o.Name == "Stream Info Object");
        Node Field(string field) => node.Children.Single(c => c.Name == field);
        Assert.Equal(Layout.CodecName(stream.GetProperty("codec").GetInt32()), Field("Codec").Value?.ToString());
        Assert.Equal(summary.GetProperty("codec_name").GetString(), Field("Codec").Value?.ToString());
        Assert.Equal(stream.GetProperty("profile").GetInt32(), Field("Profile").Value?.AsInteger());
        Assert.Equal(stream.GetProperty("frame_length").GetInt32(), Field("Frame Size Type").Children.Single().Value?.AsInteger());
        Assert.Equal(stream.GetProperty("samples").GetInt64(), Field("Samples").Value?.AsInteger());
        Assert.Equal(stream.GetProperty("sample_rate").GetInt32(), Field("Sample Rate").Value?.AsInteger());
        Assert.Equal(stream.GetProperty("bits_per_sample").GetInt32(), Field("Bits Per Sample").Value?.AsInteger());
        Assert.Equal(stream.GetProperty("channels").GetInt32(), Field("Channels").Value?.AsInteger());
        Assert.Equal(stream.GetProperty("has_extension").GetBoolean(), Field("HasExtension").Value?.AsBoolean());
        if (stream.TryGetProperty("speakers", out JsonElement speakers) && speakers.ValueKind == JsonValueKind.Array)
        {
            Node assignment = Field("Speaker Assignment");
            Assert.Equal(speakers.EnumerateArray().Select(v => Layout.SpeakerName(v.GetInt32())), assignment.Children.Select(c => c.Value?.ToString()));
        }

        Assert.Equal("TAK", info.Codec);
        Assert.Equal(summary.GetProperty("sample_rate").GetInt32(), info.SampleRate);
        Assert.Equal(summary.GetProperty("channels").GetInt32(), info.Channels);
        Assert.Equal(summary.GetProperty("bits_per_sample").GetInt32(), info.BitsPerSample);
        Assert.Equal(summary.GetProperty("samples").GetInt64(), info.Samples);
        Assert.Equal(summary.GetProperty("encoder").GetString(), info.Encoder);
        Node statistics = streamNode.Children.Single(c => c.Name == "Statistics");
        Assert.Equal(summary.GetProperty("preset").GetString(), statistics.Children.Single(c => c.Name == "Preset").Value?.ToString());
        Assert.Equal(stream.GetProperty("frame_length").GetInt32(), statistics.Children.Single(c => c.Name == "Frame Length").Value?.AsInteger());
    }

    private static void CheckEncoder(JsonElement manifest, Node[] objects, StreamInfo info)
    {
        JsonElement encoder = manifest.GetProperty("encoder_info");
        Node? node = objects.FirstOrDefault(o => o.Name == "Encoder Info Object");
        if (encoder.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(node);
            Assert.Equal("TAK 1.0", info.Encoder);
            Assert.Equal(Confidence.Medium, info.EncoderConfidence);
            return;
        }

        Assert.Equal(encoder.GetProperty("version").GetString(), node!.Children.Single(c => c.Name == "Encoder Version").Value?.ToString());
        Assert.Equal(encoder.GetProperty("preset").GetInt32(), node.Children.Single(c => c.Name == "Preset").Value?.AsInteger());
        Assert.Equal("TAK " + encoder.GetProperty("version").GetString(), info.Encoder);
        Assert.Equal(Confidence.High, info.EncoderConfidence);
    }

    private static void CheckWaveData(JsonElement manifest, Node[] objects)
    {
        JsonElement wave = manifest.GetProperty("wave_data");
        Node? node = objects.FirstOrDefault(o => o.Name == "Wave Data Object");
        if (wave.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(node);
            return;
        }

        Assert.True(wave.GetProperty("size_fits").GetBoolean());
        Assert.Equal(wave.GetProperty("header_size").GetInt64(), node!.Children.Single(c => c.Name == "Header Size").Value?.AsInteger());
        Assert.Equal(wave.GetProperty("footer_size").GetInt64(), node.Children.Single(c => c.Name == "Footer Size").Value?.AsInteger());
        foreach ((string key, string name) in new[] { ("header_chunks", "WAV Header Data"), ("footer_chunks", "WAV Terminating Data") })
        {
            string[] chunks = [.. wave.GetProperty(key).EnumerateArray().Select(c => c.GetString()!)];
            Node? data = node.Children.FirstOrDefault(c => c.Name == name);
            Node? list = data?.Children.FirstOrDefault(c => c.Name == "Chunks");
            Assert.Equal(chunks, list is null ? [] : list.Children.Select(c => c.Children[0].Value!.ToString()!));
        }
    }

    private static void CheckLastFrame(JsonElement manifest, Node[] objects)
    {
        JsonElement last = manifest.GetProperty("last_frame");
        Node? node = objects.FirstOrDefault(o => o.Name == "Last Frame Info Object");
        if (last.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(node);
            return;
        }

        Assert.Equal(last.GetProperty("position").GetInt64(), node!.Children.Single(c => c.Name == "Last Frame Position").Value?.AsInteger());
        Assert.Equal(last.GetProperty("size").GetInt64(), node.Children.Single(c => c.Name == "Last Frame Size").Value?.AsInteger());
    }

    private static void CheckVerification(JsonElement manifest, StreamVerification verification)
    {
        Assert.Equal(DecodeStatus.NoDecoder, verification.Decoding);
        JsonElement checksums = manifest.GetProperty("checksums");
        ChecksumTally tally = verification.Checksums.Single(c => c.Name == "metadata CRC-24");
        Assert.Equal(checksums.GetProperty("compared").GetInt64(), tally.Stored);
        Assert.Equal(checksums.GetProperty("compared").GetInt64(), tally.Checked);
        Assert.Equal(checksums.GetProperty("failed").GetInt64(), tally.Failed);
        Assert.Null(verification.IsVerified);
    }
}
