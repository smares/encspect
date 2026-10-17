using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks every Musepack fixture against its manifest (make_musepack_fixtures.py,
// make_external_musepack_fixtures.py, both worked out by musepack_model.py and confirmed by
// crosscheck_musepack_fixtures.py): the top-level regions, the header fields, every SV4 to SV7 frame
// with its length, used subbands, mid/side subbands and noise channels, the 11 bits after the last
// frame of SV6, every SV8 packet with its key and bytes, the stream info and exactly the findings
// listed.
public class MusepackFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string manifest in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "musepack"), "*.expected.json").Order(StringComparer.Ordinal))
            {
                data.Add("musepack/" + Path.GetFileName(manifest)[..^".expected.json".Length] + ".mpc");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        JsonElement manifest = Fixtures.LoadMpegManifest(name);

        Assert.Equal(manifest.GetProperty("length").GetInt64(), report.Root.Length);
        Assert.Equal(
            manifest.GetProperty("regions").EnumerateArray().Select(r => $"{r.GetProperty("name").GetString()}@{r.GetProperty("offset").GetInt64()}+{r.GetProperty("length").GetInt64()}"),
            report.Root.Children.Select(n => $"{n.Name}@{n.Offset}+{n.Length}"));
        Assert.Equal("Musepack", report.Root.Value?.ToString());

        Node stream = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        if (manifest.TryGetProperty("header", out JsonElement header))
        {
            Node fields = stream.Children.FirstOrDefault(c => c.Name == "Header") ?? stream.Children.First(c => c.Name == "Stream Header");
            foreach (JsonProperty field in header.EnumerateObject())
            {
                Assert.True(field.Value.GetString() == fields.Children.Single(c => c.Name == field.Name).Value?.ToString(),
                    $"Header field {field.Name} is \"{fields.Children.Single(c => c.Name == field.Name).Value}\", expected \"{field.Value.GetString()}\"");
            }
        }

        int version = manifest.GetProperty("version").GetInt32();
        if (version is >= 4 and <= 7 && manifest.TryGetProperty("frames", out JsonElement listed) && listed.GetArrayLength() > 0)
        {
            List<Node> frames = stream.Children.Where(c => c.Kind == NodeKind.Frame && c.Value?.ToString() is not "cut short" and not "not decoded").ToList();
            JsonElement[] expected = manifest.GetProperty("frames").EnumerateArray().ToArray();
            int whole = frames.Count(f => f.Children.Any(c => c.Name == "Frame Data"));
            Assert.True(expected.Length <= frames.Count, $"{expected.Length} frames expected, {frames.Count} found");
            for (int i = 0; i < expected.Length; i++)
            {
                JsonElement[] item = expected[i].EnumerateArray().ToArray();
                Node frame = frames[i];
                Node data = frame.Children.Single(c => c.Name == "Frame Data");
                string actual = $"{frame.Children.Single(c => c.Name == "Frame Length").Value?.AsInteger()} {data.Children.Single(c => c.Name == "Used Subbands").Value} "
                    + $"{Mask(data.Children.Single(c => c.Name == "Mid/Side Subbands").Value!.ToString())} {data.Children.SingleOrDefault(c => c.Name == "Noise-Substituted Channels")?.Value?.ToString() ?? "0"}";
                Assert.Equal($"{item[0].GetInt64()} {item[1].GetInt32()} {item[2].GetString()} {item[3].GetInt32()}", actual);
            }

            Assert.True(whole >= expected.Length);

            // Version 6 keeps its last counted frame and follows it with how many of its samples
            // belong to the audio; versions 4 and 5 leave that frame undecoded.
            Node? final = frames[expected.Length - 1].Children.SingleOrDefault(c => c.Name == "Last Frame Samples");
            if (version is 4 or 5 or 6)
            {
                Assert.Equal(manifest.TryGetProperty("last_frame_samples", out JsonElement samples) ? samples.GetInt64() : null, final?.Value?.AsInteger());
                Assert.Equal(version == 6 ? 0 : 1, stream.Children.Count(c => c.Kind == NodeKind.Frame && c.Value?.ToString() == "not decoded"));
            }
        }

        if (version == 8)
        {
            List<Node> packets = stream.Children.Where(c => c.Children.Any(k => k.Name == "Key")).ToList();
            Assert.Equal(
                manifest.GetProperty("packets").EnumerateArray().Select(p => $"{p[2].GetString()}@{p[0].GetInt64()}+{p[1].GetInt64()}"),
                packets.Select(p => $"{p.Children.Single(k => k.Name == "Key").Value}@{p.Offset}+{p.Length}"));
            Assert.All(packets.Where(p => p.Name == "Audio Packet"), p => Assert.Equal(NodeKind.Frame, p.Kind));
            Assert.Equal(
                manifest.TryGetProperty("packet_runs", out JsonElement runs) ? runs.EnumerateArray().Select(r => $"{r[2].GetInt32()}@{r[0].GetInt64()}+{r[1].GetInt64()}") : [],
                stream.Children.Where(c => c.Name == "Undetailed Packets").Select(c => $"{c.Value?.ToString()?.Split(' ')[0]}@{c.Offset}+{c.Length}"));
        }

        CheckStreamInfos(manifest.GetProperty("stream_infos"), report.Streams);
        CheckFindings(manifest.GetProperty("findings"), report.Findings);
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

    // A stream of version 4 to 6 has no magic number; no fixture of another format passes for one.
    [Fact]
    public void OldStreamsAreNotFoundInOtherFormats()
    {
        var parser = new Encspect.Formats.Musepack.MusepackParser();
        string musepack = Path.Combine(Fixtures.FixtureDirectory, "musepack");
        foreach (string path in Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*", SearchOption.AllDirectories))
        {
            if (path.StartsWith(musepack, StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            using FileStream stream = File.OpenRead(path);
            var reader = new ByteReader(stream);
            if (reader.Length >= 4 && !reader.MatchesAt(0, "MP+"u8) && !reader.MatchesAt(0, "MPCK"u8))
            {
                Assert.True(parser.Sniff(new SniffContext(reader)).Class == 0, path);
            }
        }
    }

    // The sniff takes no SV6 stream cut short, since it needs the last frame and the 11 bits after it,
    // but the parser reads one where it is handed it without the sniff: cut in the 11 bits, in the last
    // frame and in the middle, the stream says so, its last frame claims no 11 bits, and every finding
    // points into the tree.
    [Theory]
    [InlineData("musepack/derived-mppsv5-left-right-vbr-as-sv6.mpc", 4, 27)]
    [InlineData("musepack/derived-mppsv5-vbr-as-sv6.mpc", 4, 27)]
    [InlineData("musepack/derived-mppsv5-vbr-as-sv6.mpc", 4000, 19)]
    public void CutSv6StreamsParseWithoutTheSniff(string name, int cut, int whole)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        Assert.Equal(0, new Encspect.Formats.Musepack.MusepackParser().Sniff(new SniffContext(new ByteReader(new MemoryStream(data[..^cut])))).Class);
        Report report = new Encspect.Host.Analyzer([new ForcedMusepack()]).Analyze("cut.mpc", new MemoryStream(data[..^cut]));

        Node stream = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        Finding missing = Assert.Single(report.Findings, f => f.Code == "musepack.frames-missing");
        Assert.Equal($"The header counts 28 frames, but the data holds {whole} whole ones: the stream is cut short.", missing.Message);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        Assert.Equal(whole, report.Streams.Single().Samples is { } samples ? (samples + 481) / 1152 : -1);
        List<Node> frames = stream.Children.Where(c => c.Kind == NodeKind.Frame).ToList();
        Assert.Equal(whole + 1, frames.Count);
        Assert.DoesNotContain(frames.SelectMany(f => f.Children), c => c.Name == "Last Frame Samples");
        Assert.All(frames, f => Assert.DoesNotContain("followed by 11 bits", f.Description, StringComparison.Ordinal));
        Assert.Equal(whole == 27, frames[^1].Description!.Contains("the data ends before", StringComparison.Ordinal));
        TestFiles.CheckInsideParents(report.Root);
    }

    // The Musepack parser with a sniff that takes anything.
    private sealed class ForcedMusepack : IFormatParser
    {
        private readonly Encspect.Formats.Musepack.MusepackParser _parser = new();

        public string Name => _parser.Name;

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context) => _parser.Parse(context);
    }

    // Frames whose subband resolutions run past their length, which no encoder writes (a length of 0
    // among them), stop being followed after a run of them, so 30 MB of zero bytes do not become 12
    // million frame records.
    [Fact]
    public void RunsOfEmptyFramesStop()
    {
        byte[] data = new byte[1 << 20];
        "MP+"u8.CopyTo(data);
        data[3] = 0x07;
        WriteBits(data, 32, 32, -1);
        WriteBits(data, 66, 6, 31);
        WriteBits(data, 192, 8, 116);
        Report report = Encspect.Host.Analyzer.CreateDefault().Analyze("empty.mpc", new MemoryStream(data));
        Assert.Contains(report.Findings, f => f.Code == "musepack.frames-damaged");
        Node stream = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        Assert.Equal(4096, stream.Children.Count(c => c.Kind == NodeKind.Frame));
    }

    // The same for frames that are not empty: a length of 1 bit leaves no room for the resolutions
    // either, in SV7 and in versions 4 to 6, which are read here without the sniff.
    [Theory]
    [InlineData(7)]
    [InlineData(5)]
    public void RunsOfTinyFramesStop(int version)
    {
        const int frames = 20000;
        byte[] data = new byte[(8 + (frames * 21 / 8)) + 64];
        int first;
        if (version == 7)
        {
            "MP+"u8.CopyTo(data);
            data[3] = 0x07;
            WriteBits(data, 32, 32, frames);
            WriteBits(data, 66, 6, 31);
            WriteBits(data, 192, 8, 116);
            first = 200;
        }
        else
        {
            WriteBits(data, 21, 5, 31);
            WriteBits(data, 11, 10, 5);
            WriteBits(data, 26, 6, 1);
            WriteBits(data, 32, 32, frames);
            first = 64;
        }

        for (int i = 0; i < frames && first + (i * 21) + 21 <= data.Length * 8; i++)
        {
            WriteBits(data, first + (i * 21), 20, 1);
        }

        Report report = new Encspect.Host.Analyzer([new ForcedMusepack()]).Analyze("tiny.mpc", new MemoryStream(data));
        Assert.Contains(report.Findings, f => f.Code == "musepack.frames-damaged");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Node stream = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        Assert.Equal(4096, stream.Children.Count(c => c.Kind == NodeKind.Frame));
    }

    // Any first words, forced into the SV4 to SV6 parser: a stream version outside 4 to 6, a constant
    // bitrate, intensity stereo and blocks of frames all end in a finding and no exception.
    [Fact]
    public void ForcedOldStreamsOfAnyVersionGiveFindings()
    {
        foreach (int length in new[] { 4, 8, 12, 64 })
        {
            for (int version = 0; version < 1024; version++)
            {
                foreach (int variant in new[] { 0, 1, 2, 3 })
                {
                    byte[] data = new byte[length];
                    WriteBits(data, 11, 10, version);
                    WriteBits(data, 26, 6, variant >= 2 ? 2 : 1);
                    WriteBits(data, 0, 9, variant % 2 == 1 ? 128 : 0);
                    if (length >= 8)
                    {
                        WriteBits(data, 32, 32, 3);
                    }

                    Report report = new Encspect.Host.Analyzer([new ForcedMusepack()]).Analyze("forced.mpc", new MemoryStream(data));
                    Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
                    if (version is < 4 or > 6)
                    {
                        Assert.Contains(report.Findings, f => f.Code == "musepack.frames-not-read" && f.Message.Contains("not of stream version 4 to 6", StringComparison.Ordinal));
                    }
                }
            }
        }
    }

    // SV8 audio packets without a payload, which no encoder writes, stop being followed after a run
    // of them, as SV7 frames of length 0 do.
    [Fact]
    public void RunsOfEmptyAudioPacketsStop()
    {
        byte[] stream = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "musepack/external-mpcenc-no-ei-no-st.mpc"));
        byte[] data = [.. stream[..^3], .. Enumerable.Repeat("AP\x03"u8.ToArray(), 5000).SelectMany(p => p), .. stream[^3..]];
        Report report = Encspect.Host.Analyzer.CreateDefault().Analyze("empty.mpc", new MemoryStream(data));
        Assert.Contains(report.Findings, f => f.Code == "musepack.packets-damaged");
        Node stream8 = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        Assert.Equal(4097, stream8.Children.Count(c => c.Kind == NodeKind.Frame));
    }

    // An audio packet holds at least one bit for each of its frames, which the stream header gives as
    // 64 here: packets of one byte are no more than empty ones, and the walk stops after a run of them.
    [Fact]
    public void RunsOfTinyAudioPacketsStop()
    {
        byte[] stream = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "musepack/external-mpcenc-no-ei-no-st.mpc"));
        byte[] data = [.. stream[..^3], .. Enumerable.Repeat("AP\x04\x55"u8.ToArray(), 5000).SelectMany(p => p), .. stream[^3..]];
        Report report = Encspect.Host.Analyzer.CreateDefault().Analyze("tiny.mpc", new MemoryStream(data));
        Assert.Contains(report.Findings, f => f.Code == "musepack.packets-damaged");
        Node stream8 = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        Assert.InRange(stream8.Children.Count(c => c.Kind == NodeKind.Frame), 4096, 4200);
    }

    // Seek table offset and seek table packets by the tens of thousands cost time in the number of
    // packets, not in its square: every offset used to be compared with every table, which took
    // 25 s for 80,000 of each. The offsets point at tables that start in the part not read, which
    // are not reported.
    [Fact]
    public void ManySeekPacketsAreCheckedInLinearTime()
    {
        byte[] stream = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "musepack/external-mpcenc-no-ei-no-st.mpc"));
        int at = stream.Length - 3;
        var pairs = new List<byte>();
        for (int i = 0; i < 80_000; i++)
        {
            pairs.AddRange("SO\x04\x03"u8.ToArray());
            pairs.AddRange("ST\x05\x01\x00"u8.ToArray());
        }

        byte[] data = [.. stream[..at], .. pairs, .. stream[at..]];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Report report = Encspect.Host.Analyzer.CreateDefault().Analyze("seek.mpc", new MemoryStream(data));
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The analysis took {watch.Elapsed.TotalSeconds:F1} s.");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings.Where(f => f.Code.StartsWith("musepack.seek", StringComparison.Ordinal)), f => Assert.True(f.Count >= 1));
        Assert.True(report.Findings.Count < 40, $"{report.Findings.Count} findings.");
    }

    // A stream header cut before its two coding bytes reports no sample rate and no channels, which
    // it never read, and libmpcdec refuses it; the packets after it are still followed.
    [Theory]
    [InlineData("musepack/sv8-header-cut-after-crc.mpc")]
    [InlineData("musepack/sv8-header-cut-before-coding.mpc")]
    public void CutStreamHeaderReportsNoRateOrChannels(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo info = report.Streams.Single();
        Assert.Equal(0, info.SampleRate);
        Assert.Equal(0, info.Channels);
        Assert.Null(info.ChannelMode);
        Assert.Null(info.ChannelLayout);
        Assert.Contains(report.Findings, f => f.Code == "musepack.packet-fields-truncated");
        Assert.DoesNotContain(report.Findings, f => f.Code == "musepack.header-missing");
    }

    // A sample count near 2^63 needs more frames than fit in the sum the frame count is worked out
    // from: the findings and the stream keep the true figures.
    [Fact]
    public void HugeSampleCountDoesNotOverflow()
    {
        Report report = Fixtures.AnalyzeMpeg("musepack/sv8-sample-count-huge.mpc");
        Finding missing = Assert.Single(report.Findings, f => f.Code == "musepack.audio-packets-missing");
        Assert.DoesNotContain("-", missing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Code == "musepack.audio-packets-extra");
        Assert.True(report.Streams.Single().Samples > 0);
    }

    // The name libmpcdec gives an SV7 version byte above 116 is its SV8 reading with the byte shifted
    // out, 0.0.0, and no encoder writes such a byte: low confidence, as the description says.
    [Fact]
    public void Sv7EncoderByteAbove116IsNamedLikeLibmpcdec()
    {
        Report report = Fixtures.AnalyzeMpeg("musepack/sv7-encoder-above-116.mpc");
        Node stream = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        Node version = stream.Children.Single(c => c.Name == "Header").Children.Single(c => c.Name == "Encoder Version");
        Assert.Equal("stable 0.0.0", version.Children.Single(c => c.Name == "Name").Value?.ToString());
        Assert.Equal("Low", report.Streams.Single().EncoderConfidence?.ToString());
    }

    // A tag that names the encoder the stream's own fields name agrees with it, whichever family of
    // builds wrote the stream: Buschmann's MP+ builds among them, whose name has none of the words
    // the others share but "MP+".
    [Theory]
    [InlineData("musepack/sv7-claim-mppenc.mpc", "mppenc 1.16")]
    [InlineData("musepack/sv7-claim-buschmann.mpc", "MP+ 1.7.8 to 1.7.9c (Buschmann)")]
    [InlineData("musepack/sv7-claim-klemm.mpc", "mppenc 0.90f to 1.0 (Klemm)")]
    public void AgreeingEncoderClaimIsEvidenceForEveryFamily(string name, string encoder)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.DoesNotContain(report.Findings, f => f.Code == "musepack.encoder-claim-differs");
        StreamInfo info = report.Streams.Single();
        Assert.Equal(encoder, info.Encoder);
        Assert.Equal("High", info.EncoderConfidence?.ToString());
        Node stream = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        Node node = stream.Children.Single(c => c.Name == "Encoder");
        Assert.Equal(2, node.Children.Count(c => c.Name == "Evidence"));
    }

    // The SV7 encoder table against what each encoder at hand wrote (sv7-encoders.json, written by
    // make_external_musepack_fixtures.py): the fields the table reads, written into an SV7 stream,
    // name that encoder with high confidence.
    [Fact]
    public void Sv7EncoderTableMatchesEncodes()
    {
        byte[] stream = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "musepack/external-mppenc-standard.mpc"));
        using JsonDocument rows = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, "musepack/sv7-encoders.json")));
        foreach (JsonElement row in rows.RootElement.EnumerateArray())
        {
            byte[] data = (byte[])stream.Clone();
            WriteBits(data, 72, 4, row.GetProperty("profile").GetInt32());
            WriteBits(data, 80, 16, row.GetProperty("max_level").GetInt32());
            WriteBits(data, 160, 1, row.GetProperty("gapless").GetBoolean() ? 1 : 0);
            WriteBits(data, 161, 11, row.GetProperty("last").GetInt32());
            WriteBits(data, 192, 8, row.GetProperty("encoder").GetInt32());
            StreamInfo info = Encspect.Host.Analyzer.CreateDefault().Analyze("encoder.mpc", new MemoryStream(data)).Streams.Single();
            Assert.True(row.GetProperty("detected").GetString() == info.Encoder, $"{row.GetProperty("build").GetString()}: {info.Encoder}");
            Assert.Equal("High", info.EncoderConfidence?.ToString());

            // The same build at other settings writes the profile asked for, or 1 whatever is asked
            // for: either way the table names it as before.
            foreach (JsonProperty setting in row.GetProperty("profiles").EnumerateObject())
            {
                byte[] other = (byte[])data.Clone();
                WriteBits(other, 72, 4, setting.Value.GetInt32());
                info = Encspect.Host.Analyzer.CreateDefault().Analyze("encoder.mpc", new MemoryStream(other)).Streams.Single();
                Assert.True(row.GetProperty("detected").GetString() == info.Encoder, $"{row.GetProperty("build").GetString()} {setting.Name}: {info.Encoder}");
                Assert.Equal("High", info.EncoderConfidence?.ToString());
            }

            // ReplayGain tools fill Max Level too, so with ReplayGain written it tells Klemm's builds
            // from Buschmann's no longer.
            if (row.GetProperty("encoder").GetInt32() == 0 && !row.GetProperty("gapless").GetBoolean())
            {
                WriteBits(data, 96, 16, 1234);
                WriteBits(data, 80, 16, 20000);
                info = Encspect.Host.Analyzer.CreateDefault().Analyze("encoder.mpc", new MemoryStream(data)).Streams.Single();
                Assert.Equal("MP+ 1.7.8 to 1.7.9c (Buschmann) or mppenc 0.90f to 1.0 (Klemm)", info.Encoder);
                Assert.Equal("Medium", info.EncoderConfidence?.ToString());
            }
        }
    }

    // Writes value into count bits at bit of the 32-bit little-endian words, read from their most
    // significant bit.
    private static void WriteBits(byte[] data, int bit, int count, int value)
    {
        for (int i = 0; i < count; i++)
        {
            int position = bit + i;
            int index = ((position >> 5) * 4) + 3 - ((position & 31) >> 3);
            int mask = 0x80 >> (position & 7);
            data[index] = (byte)(((value >> (count - 1 - i)) & 1) == 1 ? data[index] | mask : data[index] & ~mask);
        }
    }

    // A truncated copy of every encoder-made fixture, at every 97th byte, yields a report without a
    // parser failure, and its findings and streams point into the tree.
    [Theory]
    [InlineData("musepack/external-mppenc-extra-frame.mpc")]
    [InlineData("musepack/external-mpcenc-packets.mpc")]
    [InlineData("musepack/external-mpcenc-chapters.mpc")]
    [InlineData("musepack/external-mppsv5-vbr.mpc")]
    [InlineData("musepack/derived-mppsv5-vbr-as-sv6.mpc")]
    [InlineData("musepack/external-mppsv4-superframes.mpc")]
    public void TruncatedCopiesYieldFindings(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        bool magic = data.AsSpan().StartsWith("MP"u8);
        for (int length = 1; length < data.Length; length += 97)
        {
            Report report = Encspect.Host.Analyzer.CreateDefault().Analyze("cut.mpc", new MemoryStream(data[..length]));

            // SV7 and SV8 have a magic number, so a cut stream is still one, with a finding that says
            // it is cut; SV4 to SV6 have none and are recognized only where their frames end where the
            // encoders end them, so a cut copy is no Musepack stream.
            if (magic && length >= 4)
            {
                Assert.Equal("Musepack", report.Root.Value?.ToString());
                Assert.Contains(report.Findings, f => f.Code.StartsWith("musepack.", StringComparison.Ordinal) && f.Severity >= Severity.Warning);
            }
            else if (!magic && length < data.Length - 4)
            {
                Assert.NotEqual("Musepack", report.Root.Value?.ToString());
            }

            Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
            Assert.All(report.Findings, f => Assert.NotNull(f.Path));
            Assert.All(report.Streams, s => Assert.All(s.NodePaths, p => Assert.NotNull(p)));
            TestFiles.CheckInsideParents(report.Root);
        }
    }

    // The mid/side subbands as the manifest writes them: a hexadecimal mask.
    private static string Mask(string list)
    {
        if (list == "none")
        {
            return "0";
        }

        uint mask = 0;
        foreach (string part in list.Split(", "))
        {
            string[] range = part.Split(" to ");
            int first = int.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture);
            int last = int.Parse(range[^1], System.Globalization.CultureInfo.InvariantCulture);
            for (int band = first; band <= last; band++)
            {
                mask |= 1u << band;
            }
        }

        return mask.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void CheckStreamInfos(JsonElement expected, IReadOnlyList<StreamInfo> streams)
    {
        JsonElement info = expected.EnumerateArray().Single();
        if (streams.Count == 0)
        {
            Assert.Equal(0, info.GetProperty("samples").GetInt64());
            return;
        }

        StreamInfo stream = streams.Single();
        Assert.Equal(info.GetProperty("codec").GetString(), stream.Codec);
        Assert.Equal(info.GetProperty("sample_rate").GetInt32(), stream.SampleRate);
        Assert.Equal(info.GetProperty("channels").GetInt32(), stream.Channels);
        Assert.Equal(info.GetProperty("samples").GetInt64(), stream.Samples);
        Assert.Equal(info.GetProperty("duration").GetDouble(), stream.Duration, 9);
        if (info.GetProperty("average_bitrate").ValueKind == JsonValueKind.Null)
        {
            Assert.Null(stream.AverageBitrate);
        }
        else
        {
            Assert.Equal(info.GetProperty("average_bitrate").GetDouble(), stream.AverageBitrate!.Value, 9);
        }

        Assert.Equal(stream.Channels switch { 1 => ["front center"], 2 => ["front left", "front right"], _ => Array.Empty<string>() },
            stream.ChannelLayout?.Select(ChannelLayouts.Name) ?? []);
        Assert.All(stream.NodePaths, path => Assert.NotNull(path));
        if (info.TryGetProperty("encoder", out JsonElement encoder))
        {
            Assert.Equal(encoder.ValueKind == JsonValueKind.Null ? null : encoder.GetString(), stream.Encoder);
            if (info.TryGetProperty("encoder_confidence", out JsonElement confidence))
            {
                Assert.Equal(confidence.GetString(), stream.EncoderConfidence?.ToString());
            }
        }
    }

    private static void CheckFindings(JsonElement expected, IReadOnlyList<Finding> findings)
    {
        var remaining = findings.ToList();
        foreach (JsonElement item in expected.EnumerateArray())
        {
            string code = item.GetProperty("code").GetString()!;
            string contains = item.GetProperty("contains").GetString()!;
            Finding? match = remaining.FirstOrDefault(f => f.Code == code && f.Message.Contains(contains, StringComparison.Ordinal));
            Assert.True(match is not null, $"Missing finding {code} containing \"{contains}\". Findings: {string.Join("; ", findings.Select(f => $"{f.Code}: {f.Message}"))}");
            remaining.Remove(match);
        }

        Assert.True(remaining.Count == 0, $"Unexpected findings: {string.Join("; ", remaining.Select(f => $"{f.Code}: {f.Message}"))}");
        Assert.All(findings, f => Assert.True(f.Path is not null, $"The node of {f.Code} is not in the tree."));
    }
}
