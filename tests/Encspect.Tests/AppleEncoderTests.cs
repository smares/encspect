using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Apple's AudioToolbox is named from the way its files are written (AacEncoder, Mp4Parser.AppleEvidence): the encoder with medium confidence where
// an sbtd box records the PCM source of an encode, the writer with low confidence otherwise, since the writer lays its layout around packets it only copied: the files afconvert wrote
// on the macOS 15.7 runners are in tests/fixtures/aac (apple-*, with manifests and golden reports) and tests/fixtures/apple (the rest), and here each piece of the evidence is taken away in turn.
public class AppleEncoderTests
{
    // The writer, named from a layout; the encoder, named when an sbtd box records the PCM source of an encode.
    private const string Apple = "Apple AudioToolbox";
    private const string Encoder = "Apple AAC encoder (AudioToolbox)";

    private static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));

    private static Report Analyze(string name, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return Analyzer.CreateDefault().Analyze(name, stream);
    }

    private static int Find(byte[] bytes, string text, int from = 0) => bytes.AsSpan(from).IndexOf(Encoding.ASCII.GetBytes(text)) is var at and >= 0 ? at + from : -1;

    private static void Replace(byte[] bytes, string from, string to)
    {
        int at = Find(bytes, from);
        Assert.True(at >= 0, $"\"{from}\" is not in the file");
        Encoding.ASCII.GetBytes(to).CopyTo(bytes, at);
    }

    // The byte after the object type 0x40 in the DecoderConfigDescriptor of the esds box: stream type, upstream flag, reserved bit.
    private static int StreamTypeByte(byte[] bytes)
    {
        int esds = Find(bytes, "esds");
        int at = bytes.AsSpan(esds).IndexOf(new byte[] { 0x04, 0x80, 0x80, 0x80 });
        Assert.True(at >= 0);
        return esds + at + 6;
    }

    [Fact]
    public void M4aLayoutWithAnSbtdBoxNamesTheEncoderWithMediumConfidence()
    {
        Report report = Analyze("apple.m4a", Read("aac/apple-lc-m4a-cbr-st44.m4a"));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(Encoder, stream.Encoder);
        Assert.Equal(Confidence.Medium, stream.EncoderConfidence);
    }

    [Fact]
    public void ReservedStreamTypeBitSetNamesNothing()
    {
        byte[] bytes = Read("aac/apple-lc-m4a-cbr-st44.m4a");
        Assert.Equal(0x14, bytes[StreamTypeByte(bytes)]);
        bytes[StreamTypeByte(bytes)] = 0x15;
        Assert.Null(Assert.Single(Analyze("apple.m4a", bytes).Streams).Encoder);
    }

    [Fact]
    public void WithoutSbtdBoxAlignedAudioDataNamesTheWriterWithLowConfidence()
    {
        byte[] bytes = Read("aac/apple-lc-m4a-cbr-st44.m4a");
        Replace(bytes, "sbtd", "sbtX");
        StreamInfo stream = Assert.Single(Analyze("apple.m4a", bytes).Streams);
        Assert.Equal((Apple, Confidence.Low), (stream.Encoder, stream.EncoderConfidence));
    }

    [Fact]
    public void WithoutSbtdBoxOrAlignedAudioDataNamesNothing()
    {
        byte[] bytes = Read("aac/apple-lc-m4a-cbr-st44.m4a");
        Replace(bytes, "sbtd", "sbtX");
        Replace(bytes, "free", "skip");
        Assert.Null(Assert.Single(Analyze("apple.m4a", bytes).Streams).Encoder);
    }

    [Theory]
    [InlineData("aac/apple-he-m4a-cbr-st44.m4a")]
    [InlineData("aac/apple-hev2-m4a-cbr-st44.m4a")]
    [InlineData("aac/apple-aacl-m4a-cbr-mono44.m4a")]
    [InlineData("apple/apple-intel-he-m4a-cbr-st44.m4a")]
    [InlineData("apple/apple-intel-lc-m4a-cbr-mono44.m4a")]
    [InlineData("apple/apple-lc-m4a-cbr-mono44.m4a")]
    [InlineData("apple/apple-lc-m4a-cbr-st44-64.m4a")]
    [InlineData("apple/apple-lc-m4a-vbr-q64-st44.m4a")]
    [InlineData("apple/apple-lc-m4a-music-st44.m4a")]
    [InlineData("apple/apple-lc-m4b-cbr-st44.m4b")]
    [InlineData("apple/apple-he-m4a-cbr-st44-32.m4a")]
    [InlineData("apple/apple-he-m4a-cbr-mono22.m4a")]
    [InlineData("apple/apple-lc-m4a-cbr-six48-lout.m4a")]
    [InlineData("apple/apple-lc-m4a-soundcheck-st44.m4a")]
    [InlineData("apple/apple-lc-m4a-condensed-st44.m4a")]
    public void EveryM4aAndM4bEncodeIsNamedAsApplesEncoder(string name)
    {
        StreamInfo stream = Assert.Single(Analyze(name, Read(name)).Streams);
        Assert.Equal((Encoder, Confidence.Medium), (stream.Encoder, stream.EncoderConfidence));
    }

    // MP4 and 3GP files carry the stream type byte and the padding, and no sbtd box.
    [Fact]
    public void A3gpEncodeIsNamedAsTheWriter()
    {
        StreamInfo stream = Assert.Single(Analyze("3gp", Read("apple/apple-lc-3gp-cbr-st44.3gp")).Streams);
        Assert.Equal((Apple, Confidence.Low), (stream.Encoder, stream.EncoderConfidence));
    }

    [Theory]
    [InlineData("aac/apple-lc-adts-cbr-st44.aac")]
    [InlineData("apple/apple-intel-lc-adts-cbr-st44.aac")]
    [InlineData("aac/apple-hev2-adts-cbr-st44.aac")]
    [InlineData("apple/apple-he-adts-cbr-st44.aac")]
    public void AdtsHeadersNameAppleWithLowConfidence(string name)
    {
        StreamInfo stream = Assert.Single(Analyze(name, Read(name)).Streams);
        Assert.Equal((Apple, Confidence.Low), (stream.Encoder, stream.EncoderConfidence));
    }

    // Every ADTS frame header of the file, as the offsets where they start.
    private static IEnumerable<int> Frames(byte[] bytes)
    {
        for (int at = 0; at + 7 <= bytes.Length;)
        {
            yield return at;
            at += ((bytes[at + 3] & 3) << 11) | (bytes[at + 4] << 3) | (bytes[at + 5] >> 5);
        }
    }

    [Fact]
    public void AdtsWithTheOriginalBitClearNamesNothing()
    {
        byte[] bytes = Read("aac/apple-lc-adts-cbr-st44.aac");
        foreach (int at in Frames(bytes))
        {
            bytes[at + 3] &= 0xDF;
        }

        Assert.Null(Assert.Single(Analyze("apple.aac", bytes).Streams).Encoder);
    }

    [Fact]
    public void AdtsWithAVariableBufferFullnessNamesNothing()
    {
        byte[] bytes = Read("aac/apple-lc-adts-cbr-st44.aac");
        foreach (int at in Frames(bytes))
        {
            bytes[at + 5] |= 0x1F;
            bytes[at + 6] |= 0xFC;
        }

        Assert.Null(Assert.Single(Analyze("apple.aac", bytes).Streams).Encoder);
    }

    [Fact]
    public void AdtsWithOneOtherFrameNamesNothing()
    {
        byte[] bytes = Read("aac/apple-lc-adts-cbr-st44.aac");
        bytes[Frames(bytes).ElementAt(10) + 3] &= 0xDF;
        Assert.Null(Assert.Single(Analyze("apple.aac", bytes).Streams).Encoder);
    }

    // ALAC is phase 3: the container is shown and the codec is not analyzed, and iTunSMPB's total, which counts the padding
    // that Apple's writer takes off the last packet's duration, is not a finding.
    [Theory]
    [InlineData("alac/apple-alac-m4a-st44q16.m4a")]
    [InlineData("alac/apple-alac-m4a-st44q24.m4a")]
    public void AlacInM4aShowsTheContainerOnly(string name)
    {
        Report report = Analyze(name, Read(name));
        Assert.Empty(report.Streams);
        Assert.Contains(report.Findings, f => f.Code == "mp4.codec-not-analyzed");
        Assert.DoesNotContain(report.Findings, f => f.Code == "mp4.itunsmpb-total");
        Assert.All(report.Findings, f => Assert.True(f.Severity < Severity.Warning, f.Message));
    }

    [Theory]
    [InlineData("alac/apple-alac-caf-st44q16.caf")]
    [InlineData("aac/apple-lc-adts-cbr-st44.aac")]
    public void AppleFilesWithoutAParserOrWithOneReportNoErrors(string name)
    {
        Report report = Analyze(name, Read(name));
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.True(f.Severity < Severity.Error, f.Message));
    }

    // Part of the HE-AAC files carry SBR frames of the FIXVAR class, whose frequency resolutions are sent last envelope first (ISO/IEC
    // 14496-3 4.4.2.8, sbr_grid); read forwards, the payloads of he-m4a-cbr-st44-32 and he-m4a-cbr-mono22 (both runners) and of the model
    // fixture, whose envelopes take as many bits as their resolutions say, ran past their fill elements (aac.sbr-past-fill).
    [Theory]
    [InlineData("aac/apple-he-m4a-cbr-st44.m4a")]
    [InlineData("apple/apple-he-m4a-cbr-st44-32.m4a")]
    [InlineData("apple/apple-he-m4a-cbr-mono22.m4a")]
    [InlineData("apple/apple-intel-he-m4a-cbr-st44-32.m4a")]
    [InlineData("apple/apple-intel-he-m4a-cbr-mono22.m4a")]
    [InlineData("apple/apple-intel-he-m4a-cbr-st44.m4a")]
    [InlineData("apple/apple-he-adts-cbr-st44.aac")]
    [InlineData("adts/sbr-fixvar-resolutions.aac")]
    public void SbrPayloadsOfFixvarFramesFitTheirFillElements(string name)
    {
        Report report = Analyze(name, Read(name));
        Assert.DoesNotContain(report.Findings, f => f.Code is "aac.sbr-past-fill" or "aac.sbr-data-invalid");
    }

    // Sound Check adds an iTunNORM item and a loudness box, afconvert's 16-bit framing a compact sample size box, and 5.1 a channel configuration.
    [Theory]
    [InlineData("apple/apple-lc-m4a-soundcheck-st44.m4a")]
    [InlineData("apple/apple-lc-m4a-condensed-st44.m4a")]
    [InlineData("apple/apple-lc-m4a-cbr-six48-lout.m4a")]
    [InlineData("apple/apple-lc-3gp-cbr-st44.3gp")]
    [InlineData("apple/apple-lc-m4a-vbr-q64-st44.m4a")]
    [InlineData("apple/apple-lc-m4a-music-st44.m4a")]
    [InlineData("apple/apple-lc-m4b-cbr-st44.m4b")]
    public void OtherAppleFilesReportNothingBeyondInformation(string name)
    {
        Report report = Analyze(name, Read(name));
        Assert.All(report.Findings, f => Assert.True(f.Severity < Severity.Warning, f.Message));
        Assert.Single(report.Streams);
    }

    // iTunes uses Apple's encoder, so its tool atom agrees with the layout and is no disagreement; the tool atom's name is the one shown.
    [Fact]
    public void AnITunesToolClaimAgreesWithTheLayout()
    {
        var unit = new Node("unit", 0, 1);
        var layout = new EncoderClaim(Apple, unit, "MP4 layout of Apple's AudioToolbox file writer: x") { IsLayout = true };
        var tool = new EncoderClaim("iTunes 12.13.2.3", unit, "MP4 ©too atom (encoding tool)");
        var differing = new List<string>();
        Node? encoder = Encspect.Formats.Aac.AacEncoder.Detect(null, [tool, layout], (name, value, description) => new Node(name, 0, 1, value) { Description = description },
            (claim, text, winner) => differing.Add(text));
        Assert.Empty(differing);
        Assert.Equal("iTunes 12.13.2.3", encoder?.Value?.ToString());
        Assert.Equal(2, encoder!.Children.Count(c => c.Name == "Evidence"));

        encoder = Encspect.Formats.Aac.AacEncoder.Detect(null, [new EncoderClaim("FAAC 1.40.0", unit, "MP4 ©too atom (encoding tool)"), layout],
            (name, value, description) => new Node(name, 0, 1, value) { Description = description }, (claim, text, winner) => differing.Add(winner));
        // a name some software wrote outranks the writer's layout, which then disagrees; the finding says what won
        Assert.Equal(["the MP4 ©too atom (encoding tool) names \"FAAC 1.40.0\""], differing);
        Assert.Equal("FAAC 1.40.0", encoder?.Value?.ToString());
        Assert.Equal(
            "The MP4 layout of Apple's AudioToolbox file writer points to \"Apple AudioToolbox\", but the MP4 ©too atom (encoding tool) names \"FAAC 1.40.0\"; the writer also lays out streams it only copied in the same way, and other software may have rewritten the file.",
            Encspect.Formats.Aac.AacEncoder.Disagreement(layout, Apple, differing[0], "unused"));
    }

    // iTunSMPB's total counts the whole track; only Apple Lossless's writer leaves the padding off the last packet's duration.
    private static byte[] WithSmpbSamples(string hex)
    {
        byte[] bytes = Read("aac/apple-lc-m4a-cbr-st44.m4a");
        Replace(bytes, "0000000000005622", hex);
        return bytes;
    }

    [Fact]
    public void AacWhoseSmpbTotalIsNotTheTrackDurationIsWarnedAbout()
    {
        Report original = Analyze("apple.m4a", Read("aac/apple-lc-m4a-cbr-st44.m4a"));
        Assert.DoesNotContain(original.Findings, f => f.Code == "mp4.itunsmpb-total");
        // 22,050 samples and a padding of 414 plus a delay of 2,112 are the track's 24,576 units; 22,464 samples make delay plus samples the duration.
        Assert.Contains(Analyze("apple.m4a", WithSmpbSamples("00000000000057C0")).Findings, f => f.Code == "mp4.itunsmpb-total");
        Assert.Contains(Analyze("apple.m4a", WithSmpbSamples("0000000000005623")).Findings, f => f.Code == "mp4.itunsmpb-total");
    }

    // The writer's claim is about its own track: a second AAC track without the layout is not named, whatever the order of the tracks.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheLayoutNamesOnlyTheTrackThatHasIt(bool foreignFirst)
    {
        byte[] file = Read("aac/apple-lc-m4a-cbr-st44.m4a");
        int moov = Find(file, "moov") - 4;
        int trak = Find(file, "trak", moov) - 4;
        int trakSize = ReadBox(file, trak);
        int free = Find(file, "free", moov) - 4;
        int freeSize = ReadBox(file, free);
        Assert.True(freeSize > trakSize + 8, "the free box must have room for a second track");
        byte[] copy = file[trak..(trak + trakSize)];
        copy[StreamTypeByte(copy)] = 0x15;
        // track ID 2 in the copy's tkhd (version 0: header 8, version and flags 4, two times 8)
        int tkhd = Find(copy, "tkhd") + 4;
        copy[tkhd + 4 + 8 + 3] = 2;
        var output = new List<byte>(file[..(trak + (foreignFirst ? 0 : trakSize))]);
        if (foreignFirst)
        {
            output.AddRange(copy);
            output.AddRange(file[trak..(trak + trakSize)]);
        }
        else
        {
            output.AddRange(copy);
        }

        output.AddRange(file[(trak + trakSize)..free]);
        // the free box shrinks by what the track adds, so the audio data stays where the sample tables say
        byte[] header = [(byte)((freeSize - trakSize) >> 24), (byte)((freeSize - trakSize) >> 16), (byte)((freeSize - trakSize) >> 8), (byte)(freeSize - trakSize), (byte)'f', (byte)'r', (byte)'e', (byte)'e'];
        output.AddRange(header);
        output.AddRange(file[(free + 8)..(free + freeSize - trakSize)]);
        output.AddRange(file[(free + freeSize)..]);
        byte[] bytes = [.. output];
        int size = ReadBox(bytes, moov) + trakSize;
        bytes[moov] = (byte)(size >> 24);
        bytes[moov + 1] = (byte)(size >> 16);
        bytes[moov + 2] = (byte)(size >> 8);
        bytes[moov + 3] = (byte)size;
        Assert.Equal(file.Length, bytes.Length);
        Report report = Analyze("two.m4a", bytes);
        Assert.Equal(2, report.Streams.Count);
        string?[] encoders = [.. report.Streams.Select(s => s.Encoder)];
        Assert.Equal(foreignFirst ? new string?[] { null, Encoder } : new string?[] { Encoder, null }, encoders);
        Assert.Equal(foreignFirst ? new Confidence?[] { null, Confidence.Medium } : new Confidence?[] { Confidence.Medium, null }, report.Streams.Select(s => s.EncoderConfidence));
    }

    private static int ReadBox(byte[] bytes, int at) => (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];

    // Each provenance record states the SHA-256 of the file it describes.
    public static TheoryData<string> Provenance()
    {
        var data = new TheoryData<string>();
        foreach (string directory in new[] { "aac", "apple", "alac" })
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, directory), "apple-*.provenance.json").Order(StringComparer.Ordinal))
            {
                data.Add($"{directory}/{Path.GetFileName(path)}");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Provenance))]
    public void ProvenanceStatesTheFilesHash(string name)
    {
        string directory = Path.GetDirectoryName(name)!;
        string stem = Path.GetFileName(name)[..^".provenance.json".Length];
        string file = Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, directory), stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, name)));
        JsonElement record = document.RootElement;
        byte[] bytes = File.ReadAllBytes(file);
        Assert.Equal(record.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal(record.GetProperty("bytes").GetInt64(), bytes.Length);
        Assert.NotNull(record.GetProperty("encoder").GetProperty("afinfo").GetProperty("format_id").GetString());
    }

    // afconvert -d 0 copies packets. An ADTS target gets Apple's headers whatever the source; an M4A target always gets the padding and gets the
    // stream type byte 0x14 when the source is ADTS. A copy of an fdkaac stream, which writes no text, is therefore named for the writer, with low
    // confidence; an M4A copied into M4A keeps its esds (stream type byte 0x15) and shows no layout.
    [Theory]
    [InlineData("apple/apple-passthru-m4a-from-fdkaac-he-adts.m4a")]
    [InlineData("apple/apple-passthru-adts-from-fdkaac-m4a.aac")]
    [InlineData("apple/apple-intel-passthru-m4a-from-fdkaac-he-adts.m4a")]
    [InlineData("apple/apple-intel-passthru-adts-from-fdkaac-m4a.aac")]
    public void CopiedStreamsWithoutASignatureAreNamedAfterTheWriterWithLowConfidence(string name)
    {
        Report report = Analyze(name, Read(name));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal((Apple, Confidence.Low), (stream.Encoder, stream.EncoderConfidence));
    }

    [Theory]
    [InlineData("apple/apple-passthru-m4a-from-fdkaac-m4a.m4a")]
    [InlineData("apple/apple-intel-passthru-m4a-from-fdkaac-m4a.m4a")]
    public void ACopyOfAnM4aKeepsItsEsdsAndShowsNoLayout(string name)
    {
        Assert.Null(Assert.Single(Analyze(name, Read(name)).Streams).Encoder);
    }

    // A signature in the stream beats the writer's layout, which is then an information finding worded as the writer's: through the MP4 parser's
    // claim (M4A made from ffmpeg's ADTS) and through the ADTS parser's (ADTS from ffmpeg's ADTS), on both runners' files.
    [Theory]
    [InlineData("apple/apple-passthru-m4a-from-ffmpeg-adts.m4a", "MP4 layout of Apple's AudioToolbox file writer")]
    [InlineData("apple/apple-intel-passthru-m4a-from-ffmpeg-adts.m4a", "MP4 layout of Apple's AudioToolbox file writer")]
    [InlineData("apple/apple-passthru-adts-from-ffmpeg-adts.aac", "ADTS header layout of Apple's AudioToolbox writer")]
    public void ASignatureInTheStreamBeatsTheWritersLayout(string name, string subject)
    {
        Report report = Analyze(name, Read(name));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(("FFmpeg (Lavc60.31.102)", Confidence.High), (stream.Encoder, stream.EncoderConfidence));
        Finding differing = Assert.Single(report.Findings, f => f.Code == "aac.encoder-claim-differs");
        Assert.Equal(Severity.Info, differing.Severity);
        Assert.Equal(
            $"The {subject} points to \"Apple AudioToolbox\", but the stream points to \"FFmpeg (Lavc60.31.102)\"; the writer also lays out streams it only copied in the same way, and other software may have rewritten the file.",
            differing.Message);
    }

    // The ADTS copy was byte for byte the same on both runners, so one file and one record stand for both.
    [Fact]
    public void TheAdtsCopyOfFfmpegsStreamIsOneFileForBothRunners()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, "apple/apple-passthru-adts-from-ffmpeg-adts.provenance.json")));
        JsonElement other = Assert.Single(document.RootElement.GetProperty("identical_on").EnumerateArray());
        Assert.Contains("X86_64", other.GetProperty("uname").GetString(), StringComparison.Ordinal);
    }

    // With an sbtd box the claim names the encoder; the finding for a stream that disagrees says what an sbtd box records.
    [Fact]
    public void ADisagreeingSbtdClaimIsWordedForTheSbtdBox()
    {
        var claim = new EncoderClaim("Apple AAC encoder (AudioToolbox)", new Node("sbtd", 0, 1), "MP4 layout of Apple's AudioToolbox encoder: x") { Confidence = Confidence.Medium, IsLayout = true };
        Assert.Equal(
            "The MP4 layout of Apple's AudioToolbox encoder points to \"Apple AAC encoder (AudioToolbox)\", but the stream points to \"FFmpeg (Lavc60.31.102)\"; the sbtd box records the source of an encode, so other software may have replaced the stream in the file.",
            Encspect.Formats.Aac.AacEncoder.Disagreement(claim, claim.Text, "the stream points to \"FFmpeg (Lavc60.31.102)\"", "unused"));
    }

    // FAAC's copy came from an M4A: the esds is kept, there is no layout, and the version text names the encoder.
    [Theory]
    [InlineData("apple/apple-passthru-m4a-from-faac-m4a.m4a")]
    [InlineData("apple/apple-intel-passthru-m4a-from-faac-m4a.m4a")]
    public void ACopyOfAnM4aKeepsItsVersionText(string name)
    {
        Report report = Analyze(name, Read(name));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(("FAAC 1.40.0", Confidence.High), (stream.Encoder, stream.EncoderConfidence));
        Assert.DoesNotContain(report.Findings, f => f.Code == "aac.encoder-claim-differs");
    }
}
