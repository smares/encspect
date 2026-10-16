using System.Text.RegularExpressions;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The encoder and the writer of a stream are resolved apart, each from the claims of its own role: a writer that
// differs from the encoder is a fact, only two claims of one role can conflict, and each tag goes where its
// specification puts it (the writer where the specification leaves the field open).
public partial class WriterRoleTests
{
    private static Node At(string name = "claim") => new(name, 0, 1);

    private static Node Cover(string name, NodeValue? value, string description) => new(name, 0, 1, value) { Description = description };

    private static byte[] Fixture(string path) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, path));

    private static Report Analyze(string name, byte[] data) => Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));

    // ---- the contract

    [Fact]
    public void AClaimNeedsARole()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncoderClaim("x", At(), "d", default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncoderClaim("x", At(), "d", (ClaimRole)7));
        Assert.Equal(ClaimRole.Writer, new EncoderClaim("x", At(), "d", ClaimRole.Writer).Role);
    }

    [GeneratedRegex(@"new EncoderClaim\(")]
    private static partial Regex Construction();

    // The compiler demands the argument; this keeps a construction from passing a role that is not one of the two by spelling.
    [Fact]
    public void EveryClaimTheParsersMakeStatesItsRole()
    {
        string root = Path.GetFullPath(Path.Combine(Fixtures.FixtureDirectory, "..", ".."));
        string separator = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        int found = 0;
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains(separator, StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(path);
            foreach (Match match in Construction().Matches(text))
            {
                int depth = 1;
                int at = match.Index + match.Length;
                bool inString = false;
                int start = at;
                while (depth > 0 && at < text.Length)
                {
                    char c = text[at++];
                    if (c == '"' && text[at - 2] != '\\')
                    {
                        inString = !inString;
                    }
                    else if (!inString && c == '(')
                    {
                        depth++;
                    }
                    else if (!inString && c == ')')
                    {
                        depth--;
                    }
                }

                string call = text[start..at];
                Assert.True(call.Contains("ClaimRole.", StringComparison.Ordinal) || call.Contains("ClaimRoles.Of(", StringComparison.Ordinal), $"{Path.GetFileName(path)}: {call}");
                found++;
            }
        }

        Assert.True(found >= 12, $"Found only {found} constructions.");
    }

    [Fact]
    public void AMuxerVersionIsTheWritersInAnyField()
    {
        Assert.True(ClaimRoles.IsMuxerVersion("Lavf60.16.100"));
        Assert.False(ClaimRoles.IsMuxerVersion("Lavc60.31.102 libvorbis"));
        Assert.False(ClaimRoles.IsMuxerVersion("LAME 3.100"));
        Assert.Equal(ClaimRole.Writer, ClaimRoles.Of(ClaimRole.Encoder, "Lavf60.16.100"));
        Assert.Equal(ClaimRole.Encoder, ClaimRoles.Of(ClaimRole.Encoder, "LAME 3.100"));
        Assert.Equal("FFmpeg (Lavf60.16.100)", ClaimRoles.WriterName("Lavf60.16.100"));
        Assert.Equal("FFmpeg (Lavf)", ClaimRoles.WriterName("Lavf"));
        Assert.Equal("FFmpeg (Lavf lame)", ClaimRoles.WriterName("Lavf lame"));
        Assert.True(ClaimRoles.IsMuxerVersion("Lavf"));
        Assert.True(ClaimRoles.IsEncoderId("Lavc60.31.102 libvorbis"));
        Assert.True(ClaimRoles.IsEncoderId("Lavc60.31"));
        Assert.True(ClaimRoles.IsEncoderId("Lavc libvorbis"));
        Assert.False(ClaimRoles.IsEncoderId("Lavc-foo"));
        Assert.False(ClaimRoles.IsEncoderId("Lavcodec"));
        Assert.False(ClaimRoles.IsEncoderId(" Lavc60.31"));
        Assert.Equal(ClaimRole.Encoder, ClaimRoles.Of(ClaimRole.Writer, "Lavc60.31.102 libvorbis"));
        Assert.Equal(ClaimRole.Writer, ClaimRoles.Of(ClaimRole.Writer, "opusenc from opus-tools 0.2"));
    }

    // ---- resolving a role

    [Fact]
    public void TheWriterIsResolvedFromTheWriterClaimsOnly()
    {
        var claims = new[]
        {
            new EncoderClaim("LAME 3.100", At(), "ID3v2 TSSE frame", ClaimRole.Encoder),
            new EncoderClaim("Foo Muxer 1.0", At(), "MP4 ©too atom", ClaimRole.Writer),
        };
        var differing = new List<string>();
        Node? writer = WriterDetection.Detect(claims, Cover, (claim, text, winner) => differing.Add(text));
        Assert.Equal("Foo Muxer 1.0", writer?.Value?.ToString());
        Assert.Equal("Low", writer!.Children.Single(c => c.Name == "Confidence").Value?.ToString());
        Assert.Single(writer.Children, c => c.Name == "Evidence");
        Assert.Empty(differing);
        Assert.Equal(["LAME 3.100"], ClaimRoles.Encoders(claims).Select(c => c.Text));
        Assert.Null(WriterDetection.Detect(claims.Take(1), Cover, (_, _, _) => Assert.Fail("no conflict")));
    }

    [Fact]
    public void TheStrongestWriterClaimWinsAndANameOutranksALayoutOfEqualConfidence()
    {
        var layout = new EncoderClaim("Media Foundation 12", At(), "ASF header layout of Media Foundation's ASF sink: x", ClaimRole.Writer) { Confidence = Confidence.Medium, IsLayout = true };
        var name = new EncoderClaim("Media Foundation 12.0", At(), "ASF Extended Content descriptor MediaFoundationVersion", ClaimRole.Writer);
        Node? writer = WriterDetection.Detect([name, layout], Cover, (_, _, _) => Assert.Fail("one family"));
        Assert.Equal("Media Foundation 12", writer?.Value?.ToString());
        Assert.Equal(2, writer!.Children.Count(c => c.Name == "Evidence"));

        var equalLayout = layout with { Confidence = Confidence.Low };
        Assert.Equal("Media Foundation 12.0", WriterDetection.Detect([equalLayout, name], Cover, (_, _, _) => { })?.Value?.ToString());
    }

    [Fact]
    public void ClaimsThatNameOtherSoftwareConflictWithinTheRoleOnly()
    {
        var lavf = new EncoderClaim("Lavf60.16.100", At(), "ID3v2 TSSE frame", ClaimRole.Writer);
        var other = new EncoderClaim("Foo Muxer 1.0", At(), "APE Encoder item", ClaimRole.Writer);
        var again = new EncoderClaim("ffmpeg 6.1", At(), "MP4 ©too atom", ClaimRole.Writer);
        var differing = new List<(string Text, string Winner)>();
        Node? writer = WriterDetection.Detect([lavf, other, again], Cover, (claim, text, winner) => differing.Add((text, winner)));
        Assert.Equal("FFmpeg (Lavf60.16.100)", writer?.Value?.ToString());
        Assert.Equal([("Foo Muxer 1.0", "the ID3v2 TSSE frame names \"FFmpeg (Lavf60.16.100)\"")], differing);
        Assert.Equal(2, writer!.Children.Count(c => c.Name == "Evidence"));
        Assert.Equal(
            "The APE Encoder item names \"Foo Muxer 1.0\", but the ID3v2 TSSE frame names \"FFmpeg (Lavf60.16.100)\"; other software may have rewritten the file, and kept one of them.",
            WriterDetection.Disagreement(other, "Foo Muxer 1.0", differing[0].Winner));
    }

    [Fact]
    public void ResolvedReadsTheNodeTheDetectionMade()
    {
        Node? writer = WriterDetection.Detect([new EncoderClaim("Foo 1", At(), "x", ClaimRole.Writer) { Confidence = Confidence.Medium }], Cover, (_, _, _) => { });
        Assert.Equal(("Foo 1", Confidence.Medium), ClaimRoles.Resolved(writer));
        Assert.Equal((null, null), ClaimRoles.Resolved(null));
    }

    // ---- the version a writer of the same name supplies

    private static Node EncoderNode(string name) => Cover("Encoder", name, "d");

    [Fact]
    public void AWriterOfTheSameNameCompletesTheEncodersVersion()
    {
        Node encoder = EncoderNode("FAAC");
        encoder.Add(Cover("Confidence", "Medium", "d"));
        ClaimRoles.CompleteVersion(encoder, [new EncoderClaim("FAAC 2.2.0 (6acbe23)", At("©too"), "MP4 ©too atom", ClaimRole.Writer)]);
        Assert.Equal("FAAC 2.2.0 (6acbe23)", encoder.Value?.ToString());
        Assert.Equal("Medium", encoder.Children.Single(c => c.Name == "Confidence").Value?.ToString());
        Assert.Contains("MP4 ©too atom", encoder.Children.Single(c => c.Name == "Evidence").Value!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FFmpeg (Lavc60.31.102)", "Lavf60.16.100")]
    [InlineData("FAAC", "GPAC-2.2.1-rev2.2.1")]
    [InlineData("FAAC 1.30", "FAAC 1.30")]
    [InlineData("Apple AAC encoder (AudioToolbox)", "Apple AudioToolbox")]
    [InlineData("FAAC", "FAACX 2.0")]
    [InlineData("FAAC", "FAAC-based tool")]
    [InlineData("FAAC 1.2", "FAAC 1.28")]
    [InlineData("LAME 3.100", "LAME 3.100.1")]
    [InlineData("FFmpeg", "Lavf60.16.100")]
    [InlineData("FFmpeg", "Lavf")]
    [InlineData("LAME", "LAME 3.100 via foobar")]
    [InlineData("FAAC", "FAAC 2\u0663.0")]
    [InlineData("FAAC", "FAAC")]
    public void AWriterOfAnotherNameOrWithoutMoreLendsNoVersion(string encoderName, string writerText)
    {
        Node encoder = EncoderNode(encoderName);
        ClaimRoles.CompleteVersion(encoder, [new EncoderClaim(writerText, At(), "claim", ClaimRole.Writer)]);
        Assert.Equal(encoderName, encoder.Value?.ToString());
        Assert.Empty(encoder.Children);
    }

    [Fact]
    public void SeveralWritersOfTheSameNameCompleteOnlyWhenTheStrongestAgree()
    {
        Node Fresh() => EncoderNode("FAAC");
        EncoderClaim Claim(string text, Confidence confidence) => new(text, At(), "claim", ClaimRole.Writer) { Confidence = confidence };

        Node encoder = Fresh();
        ClaimRoles.CompleteVersion(encoder, [Claim("FAAC 2.1.0", Confidence.Low), Claim("FAAC 2.2.0", Confidence.Low)]);
        Assert.Equal("FAAC", encoder.Value?.ToString());

        encoder = Fresh();
        ClaimRoles.CompleteVersion(encoder, [Claim("FAAC 2.1.0", Confidence.Low), Claim("FAAC 2.2.0", Confidence.Medium)]);
        Assert.Equal("FAAC 2.2.0", encoder.Value?.ToString());

        // the order of the claims does not matter
        encoder = Fresh();
        ClaimRoles.CompleteVersion(encoder, [Claim("FAAC 2.2.0", Confidence.Medium), Claim("FAAC 2.1.0", Confidence.Low)]);
        Assert.Equal("FAAC 2.2.0", encoder.Value?.ToString());

        encoder = Fresh();
        ClaimRoles.CompleteVersion(encoder, [Claim("FAAC 2.2.0", Confidence.Low), Claim("faac 2.2.0", Confidence.Low)]);
        Assert.Equal("FAAC 2.2.0", encoder.Value?.ToString(), ignoreCase: true);
    }

    [Fact]
    public void AnEncoderClaimIsNoWriterAndAWriterAloneCreatesNoEncoder()
    {
        Node encoder = EncoderNode("FAAC");
        ClaimRoles.CompleteVersion(encoder, [new EncoderClaim("FAAC 2.2.0", At(), "TSSE", ClaimRole.Encoder)]);
        Assert.Equal("FAAC", encoder.Value?.ToString());
        ClaimRoles.CompleteVersion(null, [new EncoderClaim("FAAC 2.2.0", At(), "©too", ClaimRole.Writer)]);

        StreamInfo stream = Assert.Single(Analyze("faac.m4a", Fixture("aac/faac-2.2.m4a")).Streams);
        Assert.Null(stream.Encoder);
        Assert.Equal("FAAC 2.2.0 (6acbe23)", stream.Writer);
    }

    [Fact]
    public void FaacTwoPointXHeAacTakesTheVersionFromItsToolAtom()
    {
        StreamInfo stream = Assert.Single(Analyze("faac.m4a", Fixture("aac/faac-2.2-he.m4a")).Streams);
        Assert.Equal(("FAAC 2.2.0 (6acbe23)", Confidence.Medium), (stream.Encoder, stream.EncoderConfidence));
        Assert.Equal(("FAAC 2.2.0 (6acbe23)", Confidence.Low), (stream.Writer, stream.WriterConfidence));

        // a remux by ffmpeg's muxer lends no version
        StreamInfo ffmpeg = Assert.Single(Analyze("ffmpeg.m4a", Fixture("aac/ffmpeg-lc.m4a")).Streams);
        Assert.Equal("FFmpeg (Lavc60.31.102)", ffmpeg.Encoder);
    }

    // ---- the tags, where the specification puts them

    [Fact]
    public void AnId3TsseFrameIsTheEncodersAndFfmpegsMuxerVersionTheWriters()
    {
        byte[] ac3 = Fixture("ac3/annex-d.ac3");
        StreamInfo encoder = Assert.Single(Analyze("tsse.ac3", [.. TestFiles.Id3v23(("TSSE", "Aften 0.0.8")), .. ac3]).Streams);
        Assert.Equal(("Aften 0.0.8", Confidence.Low), (encoder.Encoder, encoder.EncoderConfidence));
        Assert.Null(encoder.Writer);

        StreamInfo writer = Assert.Single(Analyze("lavf.ac3", [.. TestFiles.Id3v23(("TSSE", "Lavf60.16.100")), .. ac3]).Streams);
        Assert.Null(writer.Encoder);
        Assert.Equal(("FFmpeg (Lavf60.16.100)", Confidence.Low), (writer.Writer, writer.WriterConfidence));
    }

    [Fact]
    public void AnApeEncoderItemIsTheWriters()
    {
        StreamInfo stream = Assert.Single(Analyze("ape.ac3", [.. Fixture("ac3/annex-d.ac3"), .. TestFiles.ApeText("Encoder", "Aften 0.0.8")]).Streams);
        Assert.Null(stream.Encoder);
        Assert.Equal(("Aften 0.0.8", Confidence.Low), (stream.Writer, stream.WriterConfidence));
    }

    [Fact]
    public void ClaimsOfTheTwoRolesDoNotConflictAndClaimsOfOneRoleDo()
    {
        byte[] ac3 = Fixture("ac3/annex-d.ac3");
        Report apart = Analyze("apart.ac3", [.. TestFiles.Id3v23(("TSSE", "Aften 0.0.8")), .. ac3, .. TestFiles.ApeText("Encoder", "Foo Muxer 1.0")]);
        Assert.DoesNotContain(apart.Findings, f => f.Code is "ac3.encoder-claim" or "ac3.writer-claim-differs");
        StreamInfo stream = Assert.Single(apart.Streams);
        Assert.Equal(("Aften 0.0.8", "Foo Muxer 1.0"), (stream.Encoder, stream.Writer));

        Report conflict = Analyze("conflict.ac3", [.. TestFiles.Id3v23(("TSSE", "Lavf60.16.100")), .. ac3, .. TestFiles.ApeText("Encoder", "Foo Muxer 1.0")]);
        Finding finding = Assert.Single(conflict.Findings, f => f.Code == "ac3.writer-claim-differs");
        Assert.Contains("APE Encoder item names \"Foo Muxer 1.0\"", finding.Message, StringComparison.Ordinal);
        Assert.NotNull(finding.Path);
    }

    [Fact]
    public void AnMp4ToolAtomIsTheWritersAndTheEncoderComesFromTheStream()
    {
        StreamInfo stream = Assert.Single(Analyze("faac.m4a", Fixture("aac/faac.m4a")).Streams);
        Assert.Equal(("FAAC 1.30", Confidence.High), (stream.Encoder, stream.EncoderConfidence));
        Assert.Equal(("FAAC 1.30", Confidence.Low), (stream.Writer, stream.WriterConfidence));

        StreamInfo ffmpeg = Assert.Single(Analyze("ffmpeg.m4a", Fixture("aac/ffmpeg-lc.m4a")).Streams);
        Assert.Equal("FFmpeg (Lavc60.31.102)", ffmpeg.Encoder);
        Assert.Equal("FFmpeg (Lavf60.16.100)", ffmpeg.Writer);
    }

    [Fact]
    public void AVorbisCommentVendorStringIsTheEncodersAndTheEncoderFieldTheWriters()
    {
        StreamInfo opusenc = Assert.Single(Analyze("opusenc.opus", Fixture("ogg/external-opusenc-96.opus")).Streams);
        Assert.Equal(("libopus 1.4", Confidence.Medium), (opusenc.Encoder, opusenc.EncoderConfidence));
        Assert.Equal(("opusenc 0.2", Confidence.Low), (opusenc.Writer, opusenc.WriterConfidence));

        StreamInfo oggenc = Assert.Single(Analyze("oggenc.ogg", Fixture("ogg/external-oggenc-q5.ogg")).Streams);
        Assert.Equal("libvorbis 1.3.7", oggenc.Encoder);
        Assert.Null(oggenc.Writer);

        // ffmpeg rewrites the vendor string with its muxer's version, which names the writer, and writes its libavcodec encoder ID
        // as the ENCODER comment, which names the encoder.
        StreamInfo ffmpeg = Assert.Single(Analyze("ffmpeg.ogg", Fixture("ogg/external-ffmpeg-libvorbis.ogg")).Streams);
        Assert.Equal(("FFmpeg (Lavc60.31.102 libvorbis)", Confidence.Medium), (ffmpeg.Encoder, ffmpeg.EncoderConfidence));
        Assert.Equal("FFmpeg (Lavf60.16.100)", ffmpeg.Writer);
    }

    [Fact]
    public void AWindowsWritersLayoutNamesTheWriterAndNotTheEncoder()
    {
        StreamInfo foundation = Fixtures.AnalyzeMpeg("asf/external-windows-wma92-cbr-stereo-44k-128.wma").Streams[0];
        Assert.StartsWith("Media Foundation ", foundation.Writer, StringComparison.Ordinal);
        Assert.Equal(Confidence.Medium, foundation.WriterConfidence);
        Assert.NotEqual(foundation.Writer, foundation.Encoder);
    }

    // ---- a writer that cannot write the format

    [Theory]
    [InlineData("LAME 3.100", KnownWriters.WavPack, "MPEG audio")]
    [InlineData("lame 3.99", KnownWriters.Aac, "MPEG audio")]
    [InlineData("FAAC 1.30", KnownWriters.Usac, "AAC")]
    [InlineData("fdkaac 1.0.0 (libfdk-aac 4.0.1)", KnownWriters.Opus, "AAC")]
    [InlineData("exhale 1.2.2", KnownWriters.Aac, "xHE-AAC (USAC)")]
    [InlineData("opusenc 0.2", KnownWriters.Vorbis, "Opus")]
    [InlineData("oggenc 1.4.2", KnownWriters.Speex, "Vorbis")]
    [InlineData("speexenc 1.2.1", KnownWriters.Musepack, "Speex")]
    [InlineData("WavPack 5.6.0", KnownWriters.MpegAudio, "WavPack")]
    [InlineData("LAME3.100", KnownWriters.WavPack, "MPEG audio")]
    [InlineData("FAAC2", KnownWriters.Usac, "AAC")]
    [InlineData("opusenc.exe", KnownWriters.Vorbis, "Opus")]
    public void SoftwareThatWritesAnotherFormatIsKnown(string writer, string kind, string writes) =>
        Assert.Equal(writes, KnownWriters.WritesInstead(writer, kind));

    [Theory]
    [InlineData("LAME 3.100", KnownWriters.MpegAudio)]
    [InlineData("FAAC 2.2.0 (6acbe23)", KnownWriters.Aac)]
    [InlineData("exhale 1.2.2", KnownWriters.Usac)]
    [InlineData("WavPack 5.6.0", KnownWriters.WavPack)]
    [InlineData("FFmpeg (Lavf60.16.100)", KnownWriters.WavPack)]
    [InlineData("Lavf60.16.100", KnownWriters.MpegAudio)]
    [InlineData("GPAC-2.2.1", KnownWriters.Aac)]
    [InlineData("MP4Box 2.2", KnownWriters.Opus)]
    [InlineData("Foo Muxer 1.0", KnownWriters.WavPack)]
    [InlineData("TwoLAME 0.4.0", KnownWriters.Aac)]
    [InlineData("WavPack-based tool", KnownWriters.MpegAudio)]
    [InlineData("FAAC-based tool", KnownWriters.Usac)]
    [InlineData("exhale_wrapper", KnownWriters.Aac)]
    [InlineData("Lame-o-matic", KnownWriters.WavPack)]
    [InlineData("foobar2000 (LAME 3.100)", KnownWriters.WavPack)]
    [InlineData("LAMEenc", KnownWriters.WavPack)]
    [InlineData("LameXP", KnownWriters.WavPack)]
    [InlineData("LAME-3.100", KnownWriters.WavPack)]
    [InlineData("RazorLame", KnownWriters.WavPack)]
    [InlineData("Windows Media Format SDK 12", KnownWriters.MpegAudio)]
    public void SoftwareThatWritesTheFormatOrIsNotKnownRaisesNothing(string writer, string kind) =>
        Assert.Null(KnownWriters.WritesInstead(writer, kind));

    [Fact]
    public void AWriterClaimOfSoftwareThatCannotWriteTheFormatIsAnInfoFindingAtItsEvidence()
    {
        var found = new List<(string Message, string Node)>();
        var format = new WriterDetection.StreamFormat(KnownWriters.WavPack, "WavPack", (claim, message, node) => found.Add((message, node.Name)));
        Node? writer = WriterDetection.Detect([new EncoderClaim("LAME 3.100", At("item"), "APE Encoder item", ClaimRole.Writer)], Cover, (_, _, _) => { }, format: format);
        Assert.Equal("LAME 3.100", writer?.Value?.ToString());
        Assert.Equal([("The APE Encoder item names \"LAME 3.100\", which writes MPEG audio, not WavPack.", "item")], found);

        // a differing writer is no finding by itself, and neither is FFmpeg on any format or unknown software
        found.Clear();
        WriterDetection.Detect([new EncoderClaim("Lavf60.16.100", At(), "x", ClaimRole.Writer), new EncoderClaim("Foo 1.0", At("item"), "y", ClaimRole.Writer)], Cover, (_, _, _) => { }, format: format);
        Assert.Empty(found);
    }

    private static Report WithApe(string fixture, string name, string key, string value) => Analyze(name, [.. Fixture(fixture), .. TestFiles.ApeText(key, value)]);

    [Fact]
    public void EachFormatsWriterNamingSoftwareOfAnotherFormatIsAFinding()
    {
        Finding Only(Report report, string code) => Assert.Single(report.Findings, f => f.Code == code);

        Finding mpeg = Only(WithApe("mpeg/external-lame-cbr128-joint.mp3", "a.mp3", "Encoder", "FAAC 1.30"), "mpeg.writer-other-format");
        Assert.Equal("The APE Encoder item names \"FAAC 1.30\", which writes AAC, not MPEG audio.", mpeg.Message);
        Assert.NotNull(mpeg.Path);
        // the finding points at the tag item that states the claim, not at the encoder's evidence
        Report pointed = WithApe("wavpack/external-lossless-16-stereo.wv", "p.wv", "Encoder", "LAME 3.100");
        Finding other = Only(pointed, "wavpack.writer-other-format");
        Node at = pointed.Root;
        foreach (int index in other.Path!)
        {
            at = at.Children[index];
        }

        Assert.Equal("Encoder", at.Name);
        Assert.Equal("LAME 3.100", at.Value?.ToString());
        Assert.Equal("APE", pointed.Root.Children[other.Path![0]].Name);
        Assert.Equal(Severity.Info, mpeg.Severity);
        Only(WithApe("musepack/external-mpcenc-standard.mpc", "a.mpc", "Encoder", "LAME 3.100"), "musepack.writer-other-format");
        Only(WithApe("ac3/annex-d.ac3", "a.ac3", "Encoder", "LAME 3.100"), "ac3.writer-other-format");
        Only(WithApe("wavpack/external-lossless-16-stereo.wv", "a.wv", "Encoder", "LAME 3.100"), "wavpack.writer-other-format");

        // matching format, FFmpeg and unknown software raise nothing
        Assert.DoesNotContain(WithApe("mpeg/external-lame-cbr128-joint.mp3", "b.mp3", "Encoder", "LAME 3.100").Findings, f => f.Code.EndsWith("other-format", StringComparison.Ordinal));
        Assert.DoesNotContain(WithApe("wavpack/external-lossless-16-stereo.wv", "b.wv", "Encoder", "Lavf60.16.100").Findings, f => f.Code.EndsWith("other-format", StringComparison.Ordinal));
        Assert.DoesNotContain(WithApe("wavpack/external-lossless-16-stereo.wv", "c.wv", "Encoder", "Foo Muxer 1.0").Findings, f => f.Code.EndsWith("other-format", StringComparison.Ordinal));
        Assert.DoesNotContain(Analyze("d.m4a", Fixture("aac/mp4box-mux.m4a")).Findings, f => f.Code.EndsWith("other-format", StringComparison.Ordinal));
    }

    // ---- bit-exact ffmpeg and the LAME tag

    [Theory]
    [InlineData("ogg/external-ffmpeg-libvorbis-bitexact.ogg", "FFmpeg (Lavc libvorbis)")]
    [InlineData("ogg/external-ffmpeg-libopus-bitexact.opus", "FFmpeg (Lavc libopus)")]
    [InlineData("ogg/external-ffmpeg-libspeex-bitexact.spx", "FFmpeg (Lavc libspeex)")]
    public void ABitExactFfmpegKeepsItsEncoderIdWithoutAVersion(string path, string encoder)
    {
        StreamInfo stream = Assert.Single(Analyze("bitexact", Fixture(path)).Streams);
        Assert.Equal((encoder, Confidence.Medium), (stream.Encoder, stream.EncoderConfidence));
        Assert.Equal(("FFmpeg", Confidence.Low), (stream.Writer, stream.WriterConfidence));
    }

    [Fact]
    public void AnFfmpegStyleLameTagNamesTheMuxerAsTheWriterWithoutAVersion()
    {
        StreamInfo bitExact = Assert.Single(Analyze("bitexact.mp3", Fixture("mpeg/external-ffmpeg-bitexact.mp3")).Streams);
        Assert.Equal(("LAME 3.100", "FFmpeg", Confidence.Low), (bitExact.Encoder, bitExact.Writer, bitExact.WriterConfidence));

        // ffmpeg's tag CRC over the frame's first 190 bytes is the muxer's layout, whatever the encoder string says
        StreamInfo quirk = Assert.Single(Fixtures.AnalyzeMpeg("vbr/lame-ffmpeg-crc.mp3").Streams);
        Assert.Equal(("FFmpeg", Confidence.Low), (quirk.Writer, quirk.WriterConfidence));
        Assert.StartsWith("FFmpeg (Lavc", quirk.Encoder, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEncoderIdOfAnotherCodecNeverBecomesTheEncoder()
    {
        Report opus = Analyze("other.opus", Fixture("ogg/opus-encoder-other-format.opus"));
        Assert.Null(Assert.Single(opus.Streams).Encoder);
        Finding finding = Assert.Single(opus.Findings, f => f.Code == "opus.encoder-other-format");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("an encoder of another codec, not of Opus", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ogg/vorbis-encoder-vendor-wins.ogg", "vorbis", "libvorbis 1.3.7")]
    [InlineData("ogg/opus-encoder-vendor-wins.opus", "opus", "libopus 1.4")]
    public void AVendorThatNamesTheEncoderWinsOverAForeignEncoderId(string path, string code, string encoder)
    {
        Report report = Analyze(Path.GetFileName(path), Fixture(path));
        Assert.Equal(encoder, Assert.Single(report.Streams).Encoder);
        Assert.Single(report.Findings, f => f.Code == code + ".encoder-other-format");
    }

    // ---- the front ends

    [Fact]
    public void TheSummaryTheCsvAndTheJsonCarryTheWriter()
    {
        Report report = Analyze("faac.m4a", Fixture("aac/faac.m4a"));
        ReportSummary summary = ReportSummary.From(report);
        Assert.Equal(("FAAC 1.30", Confidence.High, "FAAC 1.30", Confidence.Low), (summary.Encoder, summary.EncoderConfidence, summary.Writer, summary.WriterConfidence));

        var csv = new StringWriter();
        ReportCsvWriter.Write([report], csv);
        string[] lines = csv.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(",Encoder,Encoder Confidence,Writer,Writer Confidence,Tags,", lines[0], StringComparison.Ordinal);
        Assert.Contains(",FAAC 1.30,High,FAAC 1.30,Low,", lines[1], StringComparison.Ordinal);

        var json = new MemoryStream();
        ReportJsonWriter.Write([report], json);
        using var document = System.Text.Json.JsonDocument.Parse(json.ToArray());
        System.Text.Json.JsonElement stream = document.RootElement[0].GetProperty("streams")[0];
        Assert.Equal("FAAC 1.30", stream.GetProperty("encoder").GetString());
        Assert.Equal("FAAC 1.30", stream.GetProperty("writer").GetString());
        Assert.Equal("Low", stream.GetProperty("writerConfidence").GetString());
    }

    [Fact]
    public void AStreamWithoutAWriterHasNoWriterColumnsOrFields()
    {
        Report report = Analyze("oggenc.ogg", Fixture("ogg/external-oggenc-q5.ogg"));
        var csv = new StringWriter();
        ReportCsvWriter.Write([report], csv);
        Assert.Contains(",libvorbis 1.3.7,High,,,Vorbis Comment,", csv.ToString(), StringComparison.Ordinal);

        var json = new MemoryStream();
        ReportJsonWriter.Write([report], json);
        using var document = System.Text.Json.JsonDocument.Parse(json.ToArray());
        System.Text.Json.JsonElement stream = document.RootElement[0].GetProperty("streams")[0];
        Assert.False(stream.TryGetProperty("writer", out _));
        Assert.False(stream.TryGetProperty("writerConfidence", out _));
    }
}
