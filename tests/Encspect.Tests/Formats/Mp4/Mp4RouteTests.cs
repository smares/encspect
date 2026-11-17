using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The routes of MP4 to FLAC, Dolby TrueHD and PCM: the fixtures ffmpeg's muxers wrote (make_external_mp4_fixtures.py) decode to what ffmpeg decodes them to (the MD5 of the
// canonical layout their manifests hold, which crosscheck_mp4_fixtures.py checks again), the decoding adds what the decode goldens hold and changes no node, and a PCM track's samples
// are read only where the decoder runs.
public class Mp4RouteTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in Fixtures.Mp4Fixtures())
            {
                if (name.StartsWith("mp4/external-ffmpeg-", StringComparison.Ordinal) && Fixtures.LoadMpegManifest(name).TryGetProperty("pcm", out _))
                {
                    data.Add(name);
                }
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
        StreamVerification verification = report.Streams[0].Verification!;
        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.Equal(pcm.GetProperty("samples").GetInt64(), verification.DecodedSamples);
        Assert.Equal(verification.DecodedSamples, sink.Samples);
        Assert.Equal(pcm.GetProperty("md5").GetString(), sink.Hash());
        Assert.Equal(pcm.GetProperty("bits").GetInt32(), sink.Bits);
        Assert.Equal(pcm.GetProperty("format").GetString() == "float" ? (sink.Bits == 32 ? SampleFormat.Float32 : SampleFormat.Float64) : SampleFormat.Integer, sink.Format);
        Assert.Equal(stream.GetProperty("sample_rate").GetInt32(), sink.SampleRate);
        Assert.Equal(stream.GetProperty("channels").GetInt32(), sink.Channels);
        Assert.Equal(1, sink.Declared);

        // FLAC and Dolby TrueHD store checksums over the audio, which the decoding compares; PCM stores none.
        string codec = stream.GetProperty("codec").GetString()!;
        Assert.Equal(codec is "FLAC" or "Dolby TrueHD" ? true : null, verification.IsVerified);
    }

    [Theory]
    [MemberData(nameof(Names))]
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
    [MemberData(nameof(Names))]
    public void DecodingChangesNoNode(string name)
    {
        Report plain = Fixtures.AnalyzeMpeg(name);
        (Report decoded, _) = Decode(name);

        Assert.Equal(Tree(plain.Root), Tree(decoded.Root));
    }

    private static string Tree(Node node) => $"{node.Name}|{node.Value}|{node.Offset}+{node.Length}({string.Concat(node.Children.Select(Tree))})";

    // The samples of a PCM track are counted from the bytes the sample tables place, so the route reads none of them: analyzing the file reads what analyzing the same track
    // without the route does (the host's own scans of the file included), and decoding reads the samples on top of that.
    [Fact]
    public void ThePcmSamplesOfATrackAreReadOnlyWhereTheDecoderRuns()
    {
        const int frames = 1 << 20;
        byte[] media = new byte[frames * 4];
        for (int i = 0; i < media.Length; i += 4)
        {
            media[i] = (byte)(i >> 8);
            media[i + 1] = (byte)(i >> 16);
        }

        byte[] Track(bool isStated)
        {
            byte[] entry = Mp4Builder.Box("ipcm", [new byte[6], [0, 1], new byte[8], [0, 2, 0, 16, 0, 0, 0, 0, 0x1F, 0x40, 0, 0], .. isStated ? new[] { Mp4Builder.FullBox("pcmC", [1, 16]) } : []]);
            return Mp4Builder.FileWithMoov(offset => Mp4Builder.Trak(1, entry, Mp4Builder.Stts(frames, 1), Mp4Builder.Stsc(frames), Mp4Builder.ConstantSizes(4, frames), Mp4Builder.ChunkOffsets([offset])), media);
        }

        byte[] file = Track(true);
        byte[] unrouted = Track(false);
        long start = file.Length - media.Length;

        var control = new RecordingStream(unrouted);
        Assert.Empty(Analyzer.CreateDefault().Analyze("c.mp4", control).Streams);
        var plain = new RecordingStream(file);
        Report report = Analyzer.CreateDefault().Analyze("p.mp4", plain);
        Assert.Equal(frames, report.Streams.Single().Samples);
        Assert.True(plain.BytesIn(start, file.Length) <= control.BytesIn(start, file.Length), $"{plain.BytesIn(start, file.Length)} bytes of the samples were read, {control.BytesIn(start, file.Length)} without the route");

        var decoding = new RecordingStream(file);
        Analyzer.CreateDefault().Analyze("p.mp4", decoding, new AnalysisOptions { Decoding = new DecodeOptions { Output = new PcmHashingSink().Declare } });
        Assert.True(decoding.BytesIn(start, file.Length) >= plain.BytesIn(start, file.Length) + media.Length);
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

    // The metadata blocks of the dfLa box are nodes of the box, in the movie box; the frames and the statistics are the track's samples'.
    [Fact]
    public void TheMetadataBlocksOfAFlacTrackLieUnderTheDfLaBox()
    {
        Report report = Fixtures.AnalyzeMpeg("mp4/flac-vorbis-comment-in-mp4.m4a");

        Node box = Find(report.Root, "dfLa");
        Assert.Contains(box.Children, c => c.Name.Contains("STREAMINFO", StringComparison.Ordinal) || c.Name == "Metadata Block");
        Node samples = Find(report.Root, "Track 1 Samples");
        Assert.Contains(samples.Children, c => c.Name == "Statistics");
        Assert.Contains(samples.Children, c => c.Name == "Frames");
        TestFiles.CheckInsideParents(report.Root);
    }

    private static Node Find(Node node, string name) =>
        node.Name == name ? node : node.Children.Select(c => Find(c, name)).FirstOrDefault(n => n is not null)!;
}
