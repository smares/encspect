using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The route of Matroska's A_PCM tracks to the PCM parser: the fixtures ffmpeg wrote (make_external_matroska_fixtures.py) decode to what ffmpeg decodes them to (the MD5 of the
// canonical layout their manifests hold, which crosscheck_matroska_fixtures.py checks again), the decoding adds what the decode goldens hold and changes no node, the layout comes from
// the codec ID and the Audio element, and a track's samples are read only where the decoder runs.
public class MatroskaPcmRouteTests
{
    private static readonly string[] HandMade = ["matroska/pcm-three-layouts.mka", "matroska/pcm-default-rate.mka"];

    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "matroska");
            foreach (string path in Directory.EnumerateFiles(directory, "external-*.mka").Order(StringComparer.Ordinal))
            {
                string name = $"matroska/{Path.GetFileName(path)}";
                if (Fixtures.LoadMpegManifest(name).TryGetProperty("pcm", out _))
                {
                    data.Add(name);
                }
            }

            return data;
        }
    }

    public static TheoryData<string> GoldenNames
    {
        get
        {
            TheoryData<string> data = Names;
            foreach (string name in HandMade)
            {
                data.Add(name);
            }

            return data;
        }
    }

    private static (Report Report, PcmHashingSink Sink) Decode(string name)
    {
        var sink = new PcmHashingSink();
        using FileStream file = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), file, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare } });
        return (report, sink);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodedAudioMatchesWhatFfmpegDecodesTo(string name)
    {
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        JsonElement pcm = manifest.GetProperty("pcm");
        JsonElement stream = manifest.GetProperty("streams")[0];
        (Report report, PcmHashingSink sink) = Decode(name);

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed" or "decode.no-decoder");
        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.Equal(pcm.GetProperty("samples").GetInt64(), verification.DecodedSamples);
        Assert.Equal(verification.DecodedSamples, sink.Samples);
        Assert.Equal(pcm.GetProperty("md5").GetString(), sink.Hash());
        Assert.Equal(pcm.GetProperty("bits").GetInt32(), sink.Bits);
        Assert.Equal(pcm.GetProperty("format").GetString() == "float" ? (sink.Bits == 32 ? SampleFormat.Float32 : SampleFormat.Float64) : SampleFormat.Integer, sink.Format);
        Assert.Equal(stream.GetProperty("sample_rate").GetInt32(), sink.SampleRate);
        Assert.Equal(stream.GetProperty("channels").GetInt32(), sink.Channels);
        Assert.Equal(1, sink.Declared);

        // PCM stores no checksum.
        Assert.Null(verification.IsVerified);
    }

    [Theory]
    [MemberData(nameof(GoldenNames))]
    public void DecodingAddsWhatTheDecodeGoldenHolds(string name)
    {
        (Report report, _) = Decode(name);
        string actual = DecoderFixtureTests.Golden(report);
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
    [MemberData(nameof(GoldenNames))]
    public void DecodingChangesNoNode(string name)
    {
        Report plain = Fixtures.AnalyzeMpeg(name);
        (Report decoded, _) = Decode(name);

        Assert.Equal(Tree(plain.Root), Tree(decoded.Root));
    }

    private static string Tree(Node node) => $"{node.Name}|{node.Value}|{node.Offset}+{node.Length}({string.Concat(node.Children.Select(Tree))})";

    // The layout is the codec ID's and the BitDepth's: 8-bit samples are unsigned, the other integers signed, in the byte order the ID names.
    [Theory]
    [InlineData("A_PCM/INT/LIT", 16, "signed integer", "little-endian")]
    [InlineData("A_PCM/INT/BIG", 24, "signed integer", "big-endian")]
    [InlineData("A_PCM/INT/LIT", 8, "unsigned integer", "little-endian")]
    [InlineData("A_PCM/FLOAT/IEEE", 64, "IEEE 754 floating point", "little-endian")]
    public void TheLayoutComesFromTheCodecIdAndTheBitDepth(string codecId, int bits, string format, string order)
    {
        Report report = Analyzer.CreateDefault().Analyze("p.mka", new MemoryStream(PcmFile(codecId, bits, 2, 48000, 1024 * bits / 8)));

        Node statistics = Find(report.Root, "Statistics");
        Assert.Equal(format, statistics.Children.Single(c => c.Name == "Sample Format").Value?.ToString());
        Assert.Equal(order, statistics.Children.Single(c => c.Name == "Byte Order").Value?.ToString());
        Assert.Equal(bits, report.Streams.Single().BitsPerSample);
        Assert.Equal(1024 / 2, report.Streams.Single().Samples);
    }

    // A frame of a track whose bytes are not whole sample frames leaves the rest over, as in a WAV file.
    [Fact]
    public void BytesThatAreNoWholeSampleFrameAreReported()
    {
        Report report = Analyzer.CreateDefault().Analyze("p.mka", new MemoryStream(PcmFile("A_PCM/INT/LIT", 16, 2, 48000, 4 * 100 + 3)));

        Assert.Contains(report.Findings, f => f.Code == "pcm.partial-sample-frame");
        Assert.Equal(100, report.Streams.Single().Samples);
    }

    // The CodecPrivate of a PCM track is none the codec mapping defines, so it is not handed on.
    [Fact]
    public void AStrayCodecPrivateIsNotHandedToTheParser()
    {
        Report report = Analyzer.CreateDefault().Analyze("p.mka", new MemoryStream(PcmFile("A_PCM/INT/LIT", 16, 1, 8000, 160, [1, 2, 3])));

        Assert.Contains(report.Findings, f => f.Code == "matroska.codec-private-unexpected");
        Assert.Equal(80, report.Streams.Single().Samples);
    }

    // A sampling frequency that differs from the stream's cannot happen (the rate is the TrackEntry's), but an output sampling frequency can name another rate; it is not compared for PCM.
    [Fact]
    public void ATrackWithoutBitDepthIsReportedFromItsTrackEntry()
    {
        Report report = Analyzer.CreateDefault().Analyze("p.mka", new MemoryStream(PcmFile("A_PCM/INT/LIT", 0, 2, 48000, 400)));

        Assert.Contains(report.Findings, f => f.Code == "matroska.pcm-layout-not-read" && f.Message.Contains("no BitDepth", StringComparison.Ordinal));
        Assert.Equal(48000, report.Streams.Single().SampleRate);
        Assert.Equal(0, report.Streams.Single().Samples);
    }

    // The samples are counted from the bytes of the track's blocks, so the route reads none of them: analyzing the file reads what analyzing the same track without the route
    // does (the host's own scans of the file included), and decoding reads the samples on top of that.
    [Fact]
    public void ThePcmSamplesOfATrackAreReadOnlyWhereTheDecoderRuns()
    {
        const int blockBytes = 4096;
        const int blocks = 1024;
        byte[] media = new byte[blockBytes * blocks];
        for (int i = 0; i < media.Length; i++)
        {
            media[i] = (byte)(i * 31 >> 3);
        }

        byte[] file = PcmFileOf("A_PCM/INT/LIT", 16, 2, 48000, media, blockBytes);
        byte[] unrouted = PcmFileOf("A_PCM/INT/LIT", 0, 2, 48000, media, blockBytes);
        long start = file.Length - media.Length - (blocks * 12L);

        var control = new RecordingStream(unrouted);
        Analyzer.CreateDefault().Analyze("c.mka", control);
        var plain = new RecordingStream(file);
        Report report = Analyzer.CreateDefault().Analyze("p.mka", plain);
        Assert.Equal(media.Length / 4, report.Streams.Single().Samples);
        Assert.True(plain.BytesIn(start, file.Length) <= control.BytesIn(start, file.Length), $"{plain.BytesIn(start, file.Length)} bytes of the blocks were read, {control.BytesIn(start, file.Length)} without the route");

        var decoding = new RecordingStream(file);
        var sink = new PcmHashingSink();
        Analyzer.CreateDefault().Analyze("p.mka", decoding, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare } });
        Assert.Equal(media.Length / 4, sink.Samples);
        Assert.True(decoding.BytesIn(start, file.Length) >= plain.BytesIn(start, file.Length) + media.Length);
    }

    private static byte[] PcmFile(string codecId, int bits, int channels, int rate, int bytes, byte[]? codecPrivate = null)
    {
        byte[] media = new byte[bytes];
        for (int i = 0; i < media.Length; i++)
        {
            media[i] = (byte)(i * 7);
        }

        return PcmFileOf(codecId, bits, channels, rate, media, 1 << 20, codecPrivate);
    }

    private static byte[] PcmFileOf(string codecId, int bits, int channels, int rate, byte[] media, int blockBytes, byte[]? codecPrivate = null)
    {
        byte[] audio = MatroskaBuilder.Element(0xE1, MatroskaBuilder.Element(0xB5, Double(rate)),
            MatroskaBuilder.Unsigned(0x9F, (ulong)channels), bits > 0 ? MatroskaBuilder.Unsigned(0x6264, (ulong)bits) : []);
        byte[] entry = MatroskaBuilder.Element(0xAE, MatroskaBuilder.Unsigned(0xD7, 1), MatroskaBuilder.Unsigned(0x73C5, 1), MatroskaBuilder.Unsigned(0x83, 2), MatroskaBuilder.Text(0x86, codecId), codecPrivate is null ? [] : MatroskaBuilder.Element(0x63A2, codecPrivate), audio);
        var items = new List<byte[]>();
        for (int at = 0, n = 0; at < media.Length; at += blockBytes, n++)
        {
            items.Add(MatroskaBuilder.SimpleBlock(1, n, media[at..Math.Min(media.Length, at + blockBytes)]));
        }

        return MatroskaBuilder.File(MatroskaBuilder.Info(), MatroskaBuilder.Element(0x1654AE6B, entry), MatroskaBuilder.Cluster(0, [.. items]));
    }

    private static byte[] Double(double value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Array.Reverse(bytes);
        return bytes;
    }

    private static Node Find(Node node, string name) => node.Name == name ? node : node.Children.Select(c => FindOrNull(c, name)).First(n => n is not null)!;

    private static Node? FindOrNull(Node node, string name)
    {
        if (node.Name == name)
        {
            return node;
        }

        foreach (Node child in node.Children)
        {
            if (FindOrNull(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private sealed class RecordingStream(byte[] data) : MemoryStream(data)
    {
        private readonly List<(long Start, long End)> _reads = [];

        public long BytesIn(long start, long end) => _reads.Sum(r => Math.Max(0, Math.Min(r.End, end) - Math.Max(r.Start, start)));

        public override int Read(byte[] buffer, int offset, int count)
        {
            long at = Position;
            int read = base.Read(buffer, offset, count);
            _reads.Add((at, at + read));
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            long at = Position;
            int read = base.Read(buffer);
            _reads.Add((at, at + read));
            return read;
        }
    }
}
