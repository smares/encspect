using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Checks every WAV fixture against its manifest (make_external_wav_fixtures.py, make_wav_fixtures.py, both worked out by wav_model.py, a reading of
// the specifications apart from the analyzer, and confirmed by crosscheck_wav_fixtures.py), and the fields of some of them against the values they
// were built with.
public class WavFixtureTests
{
    public static TheoryData<string> Names => ChunkFixtureChecks.Names("wav");

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name) => ChunkFixtureChecks.ReportMatchesManifest(name);

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string name) => ChunkFixtureChecks.ReportMatchesGoldenFile(name);

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodedAudioMatchesManifest(string name) => ChunkFixtureChecks.DecodedAudioMatchesManifest(name);

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodingAddsWhatTheDecodeGoldenHolds(string name) => ChunkFixtureChecks.DecodeGoldenMatches(name);

    [Theory]
    [MemberData(nameof(Names))]
    public void TheFormatIsDetected(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Equal(name.EndsWith(".w64", StringComparison.Ordinal) ? "Wave64" : "WAV", report.Root.Value?.ToString());
        Assert.Null(report.Detection);
    }

    private static Node Field(string name, string path) => Fixtures.Resolve(Fixtures.AnalyzeMpeg(name).Root, path).Single();

    private static string Text(string name, string path) => Field(name, path).Value!.ToString();

    [Fact]
    public void TheFormatChunkIsReadFieldByField()
    {
        const string name = "wav/external-pcm-s16-stereo-44100.wav";
        Assert.Equal("0x0001 (PCM)", Text(name, "RIFF/Format Chunk/Format Tag"));
        Assert.Equal(2, Field(name, "RIFF/Format Chunk/Channels").Value?.AsInteger());
        Assert.Equal(44100, Field(name, "RIFF/Format Chunk/Samples Per Second").Value?.AsInteger());
        Assert.Equal(176400, Field(name, "RIFF/Format Chunk/Average Bytes Per Second").Value?.AsInteger());
        Assert.Equal(4, Field(name, "RIFF/Format Chunk/Block Alignment").Value?.AsInteger());
        Assert.Equal(16, Field(name, "RIFF/Format Chunk/Bits Per Sample").Value?.AsInteger());
        Assert.Equal("PCM, 2 channels, 44100 Hz, 16 bits", Text(name, "RIFF/Format Chunk"));
    }

    [Fact]
    public void AnExtensibleFormatShowsItsMaskSpeakersAndSubFormat()
    {
        const string name = "wav/external-pcm-s16-5.1-48000.wav";
        Assert.Equal("0xFFFE (extensible)", Text(name, "RIFF/Format Chunk/Format Tag"));
        Assert.Equal(16, Field(name, "RIFF/Format Chunk/Valid Bits Per Sample").Value?.AsInteger());
        Node mask = Field(name, "RIFF/Format Chunk/Channel Mask");
        Assert.Equal(0x3FUL, mask.Value?.AsUnsigned());
        Assert.Equal("front left, front right, front center, LFE, back left, back right", mask.Children.Single().Value?.ToString());
        Assert.Equal("00000001-0000-0010-8000-00aa00389b71 (PCM)", Text(name, "RIFF/Format Chunk/Sub-Format"));
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg(name).Streams);
        Assert.Equal("5.1", stream.ChannelLayoutName);
        Assert.Equal([Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight], stream.ChannelLayout);
    }

    [Fact]
    public void ABitDepthInAWiderContainerIsTheValidBitsOfTheStream()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/built-pcm-24-bit-extensible-in-32.wav");
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(24, stream.BitsPerSample);
        Assert.Equal(32, Fixtures.Resolve(report.Root, "RIFF/Data Chunk/Statistics/Container Bits Per Sample").Single().Value?.AsInteger());
    }

    [Fact]
    public void TheEbuMaskBitsAreDiscreteChannelsAfterTheSpeakers()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg("wav/built-extensible-ebu-mask.wav").Streams);
        Assert.Equal([Speaker.FrontLeft, Speaker.FrontRight, Speaker.Discrete, Speaker.Discrete], stream.ChannelLayout);
        Assert.Null(stream.ChannelLayoutName);
    }

    [Fact]
    public void ARifxFileIsBigEndian()
    {
        const string name = "wav/built-riffx-pcm-16.wav";
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Equal("RIFX", report.Root.Children.Single().Name);
        Assert.Equal(44100, Field(name, "RIFX/Format Chunk/Samples Per Second").Value?.AsInteger());
        Assert.Equal("big-endian", Text(name, "RIFX/Data Chunk/Statistics/Byte Order"));
        Assert.Equal(20, Assert.Single(report.Streams).Samples * 2);
    }

    [Fact]
    public void TheBroadcastExtensionIsReadFieldByField()
    {
        const string name = "wav/built-bext-v2-loudness.wav";
        string path = "RIFF/Broadcast Extension Chunk";
        Assert.Equal("A description", Text(name, $"{path}/Description"));
        Assert.Equal("Fixture originator", Text(name, $"{path}/Originator"));
        Assert.Equal("REF-0001", Text(name, $"{path}/Originator Reference"));
        Assert.Equal("2026-10-24", Text(name, $"{path}/Origination Date"));
        Assert.Equal("12:34:56", Text(name, $"{path}/Origination Time"));
        Assert.Equal(44100, Field(name, $"{path}/Time Reference").Value?.AsInteger());
        Assert.Equal(2, Field(name, $"{path}/Version").Value?.AsInteger());
        Assert.Equal(-23.0, Field(name, $"{path}/Integrated Loudness").Value?.AsDouble());
        Assert.Equal("LUFS", Field(name, $"{path}/Integrated Loudness").Value?.Unit);
        Assert.Equal(8.0, Field(name, $"{path}/Loudness Range").Value?.AsDouble());
        Assert.Equal(-1.0, Field(name, $"{path}/Maximum True Peak Level").Value?.AsDouble());
        Assert.Equal(-18.0, Field(name, $"{path}/Maximum Momentary Loudness").Value?.AsDouble());
        Assert.Equal("not used", Text(name, $"{path}/Maximum Short-Term Loudness"));
        Assert.StartsWith("A=PCM,F=44100", Text(name, $"{path}/Coding History"), StringComparison.Ordinal);
        Assert.Equal("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20" + new string('0', 64), Text(name, $"{path}/UMID"));
    }

    [Fact]
    public void LoudnessValuesOutsideTheirRangeAreIgnored()
    {
        const string name = "wav/built-bext-v2-out-of-range.wav";
        string path = "RIFF/Broadcast Extension Chunk";
        Assert.Equal("12000 (out of range, ignored)", Text(name, $"{path}/Integrated Loudness"));
        Assert.Equal("-1 (out of range, ignored)", Text(name, $"{path}/Loudness Range"));
        Assert.Equal("10000 (out of range, ignored)", Text(name, $"{path}/Maximum True Peak Level"));
        Assert.Equal("not used", Text(name, $"{path}/Maximum Momentary Loudness"));
        Assert.Equal("-10000 (out of range, ignored)", Text(name, $"{path}/Maximum Short-Term Loudness"));
    }

    [Fact]
    public void AVersionZeroBroadcastExtensionShowsNoUmid()
    {
        const string name = "wav/built-bext-v0.wav";
        Assert.Equal("not set", Text(name, "RIFF/Broadcast Extension Chunk/UMID"));
        Assert.Equal(0, Field(name, "RIFF/Broadcast Extension Chunk/Version").Value?.AsInteger());
    }

    [Fact]
    public void InfoTextIsShownAndTheSoftwareNamesTheWriter()
    {
        const string name = "wav/built-info-list.wav";
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Equal("Fixture title", Text(name, "RIFF/List Chunk/Name Chunk"));
        Assert.Equal("Fixture artist", Text(name, "RIFF/List Chunk/Artist Chunk"));
        Assert.Equal("Fixture Tagger 1.2", Text(name, "RIFF/List Chunk/Software Chunk/Text"));
        Assert.Equal("3", Text(name, "RIFF/List Chunk/'ITRK' Chunk"));
        Assert.Equal("INFO", Text(name, "RIFF/List Chunk"));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("Fixture Tagger 1.2", stream.Writer);
        Assert.Equal(Confidence.Low, stream.WriterConfidence);
    }

    [Fact]
    public void FfmpegsMuxerVersionNamesTheWriter()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg("wav/external-pcm-s16-stereo-info.wav").Streams);
        Assert.StartsWith("FFmpeg (Lavf", stream.Writer, StringComparison.Ordinal);
    }

    [Fact]
    public void CuePointsLabelsAndPlaylistsAreReadWithTheirFields()
    {
        const string name = "wav/built-cue-labels.wav";
        Assert.Equal(30, Field(name, "RIFF/Cue Points Chunk/Cue Points/Cue Point 1/Position").Value?.AsInteger());
        Assert.Equal(2, Field(name, "RIFF/Cue Points Chunk/Cue Points/Cue Point 1/Name").Value?.AsInteger());
        Assert.Equal("data", Text(name, "RIFF/Cue Points Chunk/Cue Points/Cue Point 0/Chunk ID"));
        Assert.Equal(20, Field(name, "RIFF/Playlist Chunk/Segments/Segment 1/Length").Value?.AsInteger());
        Assert.Equal(2, Field(name, "RIFF/Playlist Chunk/Segments/Segment 1/Loops").Value?.AsInteger());
        Assert.Equal("Start", Text(name, "RIFF/List Chunk/Label Chunk"));
        Assert.Equal("A note", Text(name, "RIFF/List Chunk/Note Chunk"));
        Assert.Equal("Caption", Text(name, "RIFF/List Chunk/Labeled Text Chunk"));
        Assert.Equal("capt", Text(name, "RIFF/List Chunk/Labeled Text Chunk/Purpose"));
        Assert.Equal("RDIB", Text(name, "RIFF/List Chunk/Embedded File Chunk/Media Type"));
    }

    [Fact]
    public void SampleLoopsAndTheInstrumentAreReadWithTheirFields()
    {
        const string name = "wav/built-smpl-inst.wav";
        Assert.Equal(0x01000013UL, Field(name, "RIFF/Sample Chunk/Manufacturer").Value?.AsUnsigned());
        Assert.Equal(22675, Field(name, "RIFF/Sample Chunk/Sample Period").Value?.AsInteger());
        Assert.Equal(60, Field(name, "RIFF/Sample Chunk/MIDI Unity Note").Value?.AsInteger());
        Assert.Equal("25 frames per second", Text(name, "RIFF/Sample Chunk/SMPTE Format"));
        Assert.Equal("1 (alternating forward and backward)", Text(name, "RIFF/Sample Chunk/Sample Loops/Sample Loop 1/Type"));
        Assert.Equal("0 (infinite sustain loop)", Text(name, "RIFF/Sample Chunk/Sample Loops/Sample Loop 0/Play Count"));
        Assert.Equal(40, Field(name, "RIFF/Sample Chunk/Sample Loops/Sample Loop 0/End").Value?.AsInteger());
        Assert.Equal(4, Field(name, "RIFF/Sample Chunk/Sampler Data").Value?.AsInteger());
        Assert.Equal(60, Field(name, "RIFF/Instrument Chunk/Unshifted Note").Value?.AsInteger());
        Assert.Equal(-6, Field(name, "RIFF/Instrument Chunk/Fine Tune").Value?.AsInteger());
        Assert.Equal(-3, Field(name, "RIFF/Instrument Chunk/Gain").Value?.AsInteger());
        Assert.Equal(127, Field(name, "RIFF/Instrument Chunk/High Velocity").Value?.AsInteger());
    }

    [Fact]
    public void TheChnaChunkListsItsTrackUids()
    {
        const string name = "wav/built-chna.wav";
        Assert.Equal(2, Field(name, "RIFF/Channel Allocation Chunk/Track Count").Value?.AsInteger());
        Assert.Equal("ATU_00000001", Text(name, "RIFF/Channel Allocation Chunk/Track UIDs/Track UID 0/UID"));
        Assert.Equal("AT_00010002_01", Text(name, "RIFF/Channel Allocation Chunk/Track UIDs/Track UID 1/Track Format Reference"));
        Assert.Equal("unused", Text(name, "RIFF/Channel Allocation Chunk/Track UIDs/Track UID 2"));
    }

    [Fact]
    public void XmlChunksShowTheirText()
    {
        const string name = "wav/built-xml-chunks.wav";
        Assert.Equal("<?xml version=\"1.0\"?><ebuCoreMain/>", Text(name, "RIFF/XML Chunk/XML Text"));
        Assert.Contains("IXML_VERSION", Text(name, "RIFF/iXML Chunk/XML Text"), StringComparison.Ordinal);
        Assert.Equal("Peak Envelope Chunk", Field(name, "RIFF/Peak Envelope Chunk").Name);
    }

    [Fact]
    public void TheExtraFieldsOfTheWaveTypesAreRead()
    {
        const string ms = "wav/external-adpcm-ms.wav";
        Assert.Equal(7, Field(ms, "RIFF/Format Chunk/Coefficient Sets").Value?.AsInteger());
        Assert.Equal("256, 0", Text(ms, "RIFF/Format Chunk/Coefficients/Coefficient Set 0"));
        Assert.Equal("460, -208", Text(ms, "RIFF/Format Chunk/Coefficients/Coefficient Set 5"));
        const string ima = "wav/external-adpcm-ima.wav";
        Assert.Equal(1017, Field(ima, "RIFF/Format Chunk/Samples Per Block").Value?.AsInteger());
        const string mp3 = "wav/external-mp3-in-wav-stereo.wav";
        Assert.Equal("1 (MPEG)", Text(mp3, "RIFF/Format Chunk/Identifier"));
        Assert.Equal(1152, Field(mp3, "RIFF/Format Chunk/Block Size").Value?.AsInteger());
        const string mp2 = "wav/external-mp2-in-wav.wav";
        Assert.Equal("0x0002 (Layer II)", Text(mp2, "RIFF/Format Chunk/Layer"));
        Assert.Equal("0x0001 (stereo)", Text(mp2, "RIFF/Format Chunk/Mode"));
        Assert.Equal(192000, Field(mp2, "RIFF/Format Chunk/Bitrate").Value?.AsInteger());
    }

    [Theory]
    [InlineData("wav/external-mp2-in-wav.wav", "MPEG-1 Layer II")]
    [InlineData("wav/external-mp3-in-wav-stereo.wav", "MPEG-1 Layer III")]
    [InlineData("wav/external-mp3-in-wav-mono.wav", "MPEG-2 Layer III")]
    [InlineData("wav/external-ac3-in-wav.wav", "AC-3")]
    [InlineData("wav/external-dts-in-wav.wav", "DTS")]
    [InlineData("wav/external-eac3-in-wav.wav", "E-AC-3")]
    [InlineData("wav/built-extensible-dolby-ac3.wav", "AC-3")]
    [InlineData("wav/built-extensible-dolby-digital-plus.wav", "E-AC-3")]
    [InlineData("wav/built-extensible-mpeg2-audio.wav", "MPEG-1 Layer II")]
    [InlineData("wav/built-dts-format-tag-0008.wav", "DTS")]
    public void TheDataChunkOfAnotherCodingGoesToItsParser(string name, string codec)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(codec, stream.Codec);
        Node data = report.Root.Children.Single().Children.Single(c => c.Name == "Data Chunk");
        Assert.Equal(NodeKind.Detail, data.Kind);
        Assert.Contains(data.Children, c => c.Name is "MPEG Audio" or "AC-3" or "DTS" or "Audio" || c.Kind == NodeKind.Stream);
        Assert.All(stream.NodePaths, p => Assert.NotNull(p));
    }

    // DTS music CDs and the WAVE files made from them hold the DTS stream in the 16-bit samples of a stereo PCM file; the merge base's DTS parser found
    // the chain behind the header and reported a DTS file of 48 kHz, which the PCM format chunk must not hide. The format chunk describes the carrier.
    [Theory]
    [InlineData("wav/built-dts-in-pcm-14le.wav")]
    [InlineData("wav/built-dts-in-pcm-16be.wav")]
    [InlineData("wav/built-dts-in-pcm-after-silence.wav")]
    public void ADtsStreamInPcmSamplesIsReadAsDts(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("DTS", stream.Codec);
        Assert.Equal(48000, stream.SampleRate);
        Assert.Equal(0.064, stream.Duration, 3);
        Assert.Equal(name.EndsWith(".w64", StringComparison.Ordinal) ? "Wave64" : "WAV", report.Root.Value?.ToString());
        Assert.Single(report.Findings, f => f.Code == "wav.dts-in-pcm" && f.Severity == Severity.Info);
        Assert.DoesNotContain(report.Findings, f => f.Code == "wav.format-differs-from-stream");
        Node data = report.Root.Children.Single().Children.Single(c => c.Name == "Data Chunk");
        Assert.Equal(NodeKind.Detail, data.Kind);
        Assert.Contains(data.Children, c => c.Name == "DTS" && c.Kind == NodeKind.Stream);
    }

    // The sync rule: a sync word alone is no stream.
    [Fact]
    public void PcmThatOnlyStartsWithADtsSyncWordStaysPcm()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/built-pcm-with-dts-sync-word.wav");
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("PCM", stream.Codec);
        Assert.Equal(2, stream.Channels);
        Assert.DoesNotContain(report.Findings, f => f.Code == "wav.dts-in-pcm");
    }

    [Fact]
    public void AMaskOfFewerSpeakersLeavesTheOtherChannelsWithoutAPositionAndOneOfMoreIgnoresItsExtraBits()
    {
        StreamInfo fewer = Assert.Single(Fixtures.AnalyzeMpeg("wav/built-extensible-mask-fewer.wav").Streams);
        Assert.Equal([Speaker.FrontLeft, Speaker.FrontRight, Speaker.Discrete], fewer.ChannelLayout);
        StreamInfo more = Assert.Single(Fixtures.AnalyzeMpeg("wav/built-extensible-mask-mismatch.wav").Streams);
        Assert.Equal([Speaker.FrontLeft, Speaker.FrontRight], more.ChannelLayout);
        Assert.Equal("stereo", more.ChannelLayoutName);
        Finding finding = Assert.Single(Fixtures.AnalyzeMpeg("wav/built-extensible-mask-fewer.wav").Findings, f => f.Code == "wav.channel-mask-mismatch");
        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void AFormatChunkOf14BytesStillHasItsStream()
    {
        Report pcm = Fixtures.AnalyzeMpeg("wav/built-format-14-bytes-pcm.wav");
        StreamInfo stream = Assert.Single(pcm.Streams);
        Assert.Equal("PCM", stream.Codec);
        Assert.Equal(44100, stream.SampleRate);
        Assert.Equal(50, stream.Samples);
        Assert.Null(stream.BitsPerSample);
        Assert.Equal("wav.format-short", Assert.Single(pcm.Findings).Code);
        Assert.Equal("A-law", Assert.Single(Fixtures.AnalyzeMpeg("wav/built-format-14-bytes-alaw.wav").Streams).Codec);
    }

    [Fact]
    public void ADs64ChunkWithoutATableLengthStillGivesItsSizes()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/built-rf64-ds64-too-short.wav");
        Assert.Equal("wav.ds64-too-short", Assert.Single(report.Findings).Code);
        Assert.Equal(30, Assert.Single(report.Streams).Samples);
        Assert.Equal(report.Root.Length, report.Root.Children.Single().Length);
    }

    [Fact]
    public void TheMicrosoftSubFormatsOfAc3AndMpegAudioAreNamedInTheFormatChunk()
    {
        Assert.Equal("e06d802c-db46-11cf-b4d1-00805f6cbbea (AC-3)", Text("wav/built-extensible-dolby-ac3.wav", "RIFF/Format Chunk/Sub-Format"));
        Assert.Equal("a7fb87af-2d02-42fb-a4d4-05cd93843bdd (E-AC-3)", Text("wav/built-extensible-dolby-digital-plus.wav", "RIFF/Format Chunk/Sub-Format"));
        Assert.Equal("e06d802b-db46-11cf-b4d1-00805f6cbbea (MPEG-2 audio)", Text("wav/built-extensible-mpeg2-audio.wav", "RIFF/Format Chunk/Sub-Format"));
        Assert.Equal("Samples Per Block", Field("wav/built-extensible-samples-per-block.wav", "RIFF/Format Chunk/Samples Per Block").Name);
    }

    [Fact]
    public void AnMpegStreamWithAnotherChannelCountThanTheFormatNamesIsReported()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "wav", "external-mp3-in-wav-stereo.wav"));
        // The fmt chunk of the muxer's output: channels at offset 12 + 8 + 2.
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(22), 1);
        Report report = Analyzer.CreateDefault().Analyze("mono.wav", new MemoryStream(file));
        Finding finding = Assert.Single(report.Findings, f => f.Code == "wav.format-differs-from-stream");
        Assert.Contains("states 1 channel, but the MPEG-1 Layer III stream has 2", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARf64FileResolvesItsSizesThroughTheDs64Chunk()
    {
        const string name = "wav/external-pcm-s24-stereo-rf64.wav";
        Assert.Equal("RF64", Fixtures.AnalyzeMpeg(name).Root.Children.Single().Name);
        Assert.Equal(12000 * 6, Field(name, "RF64/Data Size 64 Chunk/Data Size").Value?.AsInteger());
        Assert.Equal(12000, Field(name, "RF64/Data Size 64 Chunk/Sample Count").Value?.AsInteger());
        Node data = Field(name, "RF64/Data Chunk");
        Assert.Equal(12000 * 6, data.Value?.AsInteger());
        Assert.Equal(uint.MaxValue, Field(name, "RF64/Data Chunk/Chunk Size").Value?.AsInteger());
        Assert.Equal(12000, Assert.Single(Fixtures.AnalyzeMpeg(name).Streams).Samples);
        Assert.Single(Fixtures.Resolve(Fixtures.AnalyzeMpeg(name).Root, "RF64/Data Size 64 Chunk"));
    }

    [Fact]
    public void ADs64TableResolvesAChunkSizeOfAnotherChunk()
    {
        const string name = "wav/built-rf64-table.wav";
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Empty(report.Findings);
        Assert.Equal("bext", Fixtures.Resolve(report.Root, "RF64/Broadcast Extension Chunk/Chunk ID").Single().Value?.AsText());
        Assert.Equal("bext, 641 bytes", Text(name, "RF64/Data Size 64 Chunk/Table/Entry 0"));
    }

    [Fact]
    public void TheJunkChunkThatReservesDs64SpaceIsAJunkChunk()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/external-pcm-s16-stereo-rf64-auto.wav");
        Assert.Equal("RIFF", report.Root.Children.Single().Name);
        Node junk = Fixtures.Resolve(report.Root, "RIFF/Junk Chunk").Single();
        Assert.Equal(28 + 8, junk.Length);
    }

    [Fact]
    public void APipeWrittenFileReadsToItsEnd()
    {
        const string name = "wav/external-pcm-s16-stereo-pipe.wav";
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(11025, stream.Samples);
        Assert.Equal(report.Root.Length, report.Root.Children.Single().Length);
    }

    [Fact]
    public void AWavDataChunkHoldingAnId3v1LookalikeStaysAudio()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/built-id3v1-inside-data.wav");
        Assert.Equal(["RIFF"], report.Root.Children.Select(c => c.Name));
        Assert.Single(report.Findings, f => f.Code == "tag.near-miss" && f.Message.Contains("WAV data", StringComparison.Ordinal));
    }

    [Fact]
    public void ATagAfterTheRiffChunkIsTheFilesAndAnId3ChunkIsTheChunks()
    {
        Report trailing = Fixtures.AnalyzeMpeg("wav/built-id3v1-after-riff.wav");
        Assert.Equal(["RIFF", "ID3v1"], trailing.Root.Children.Select(c => c.Name));
        Report chunk = Fixtures.AnalyzeMpeg("wav/built-id3-chunk.wav");
        Assert.Equal(["RIFF"], chunk.Root.Children.Select(c => c.Name));
        Node tag = Fixtures.Resolve(chunk.Root, "RIFF/ID3v2 Chunk").Single().Children.Single(c => c.Kind == NodeKind.Tag);
        Assert.Equal("ID3v2", tag.Name);
        Assert.Contains(chunk.Root.Children.Single().Children, c => c.Name == "ID3v2 Chunk" && c.Value?.AsText() == "ID3v2 tag");
        Report leading = Fixtures.AnalyzeMpeg("wav/built-leading-id3v2.wav");
        Assert.Equal(["ID3v2", "RIFF"], leading.Root.Children.Select(c => c.Name));
    }

    [Fact]
    public void AnId3TagInAChunkNamesItsEncoderForTheClaims()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/built-id3-chunk.wav");
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Null(stream.Encoder);
        Assert.Equal("PCM", stream.Codec);
    }

    [Fact]
    public void NodesCoverTheirBytesAndTheChunksTileTheRiffChunk()
    {
        foreach (string name in new[] { "wav/external-pcm-s16-stereo-bwf.wav", "wav/built-cue-labels.wav", "wav/built-info-list.wav", "wav/built-unknown-chunks.wav", "wav/external-pcm-s24-stereo-rf64.wav", "wav/built-bw64.wav", "wav/built-bw64-xml-chunks.wav" })
        {
            Node container = Fixtures.AnalyzeMpeg(name).Root.Children.Single();
            long position = container.Offset + 12;
            foreach (Node chunk in container.Children.Skip(3))
            {
                Assert.True(chunk.Offset == position, $"{name}: {chunk.Name} starts at {chunk.Offset}, the chunk before ends at {position}.");
                position = chunk.End;
                Assert.All(chunk.Children, c => Assert.True(c.Offset >= chunk.Offset && c.End <= chunk.End, $"{name}: {chunk.Name}/{c.Name} lies outside its chunk."));
            }

            Assert.Equal(container.End, position);
        }
    }

    [Fact]
    public void ABw64FileResolvesItsSizesFromTheDs64ChunkAndHasADummyValue()
    {
        const string name = "wav/built-bw64.wav";
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Empty(report.Findings);
        Assert.Equal("BW64", report.Root.Children.Single().Name);
        Assert.Equal(uint.MaxValue, Field(name, "BW64/Chunk Size").Value?.AsInteger());
        Assert.Equal(476, Field(name, "BW64/Data Size 64 Chunk/BW64 Size").Value?.AsInteger());
        Assert.Equal(60, Field(name, "BW64/Data Size 64 Chunk/Data Size").Value?.AsInteger());
        Assert.Equal(0, Field(name, "BW64/Data Size 64 Chunk/Dummy").Value?.AsInteger());
        Assert.Equal(60, Field(name, "BW64/Data Chunk").Value?.AsInteger());
        Assert.Equal(10, Assert.Single(report.Streams).Samples);
    }

    [Fact]
    public void ABw64FilesChnaAndXmlChunksAreRead()
    {
        const string name = "wav/built-bw64.wav";
        string chna = "BW64/Channel Allocation Chunk";
        Assert.Equal(2, Field(name, $"{chna}/Track Count").Value?.AsInteger());
        Assert.Equal("ATU_00000002", Text(name, $"{chna}/Track UIDs/Track UID 1/UID"));
        Assert.Equal("AP_00010002", Text(name, $"{chna}/Track UIDs/Track UID 0/Pack Format Reference"));
        Assert.Contains("audioFormatExtended", Text(name, "BW64/XML Chunk/XML Text"), StringComparison.Ordinal);
        const string xml = "wav/built-bw64-xml-chunks.wav";
        Assert.Equal("1 (gzip)", Text(xml, "BW64/Compressed XML Chunk/Compression Method"));
        Assert.Equal(4, Field(xml, "BW64/Serial XML Chunk/Table Size Low").Value?.AsInteger());
    }

    [Fact]
    public void ABw64DsTableGivesTheSizeOfAnotherChunk()
    {
        const string name = "wav/built-bw64-table.wav";
        Assert.Empty(Fixtures.AnalyzeMpeg(name).Findings);
        Assert.Equal("axml, 244 bytes", Text(name, "BW64/Data Size 64 Chunk/Table/Entry 0"));
        Assert.Equal(244, Field(name, "BW64/XML Chunk").Value?.AsInteger());
    }

    [Fact]
    public void UnusedChnaStructuresAreNoFindings()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/built-bw64-unused-chna-entries.wav");
        Assert.Empty(report.Findings);
        Assert.Equal("unused", Text("wav/built-bw64-unused-chna-entries.wav", "BW64/Channel Allocation Chunk/Track UIDs/Track UID 5"));
    }

    [Fact]
    public void AnAxmlChunkWithoutTheAudioDefinitionModelNeedsNoChnaChunk() =>
        Assert.Empty(Fixtures.AnalyzeMpeg("wav/built-axml-without-adm.wav").Findings);

    [Fact]
    public void AWave64ChunkSizeCountsItsHeaderAndChunksAreAlignedToEightBytes()
    {
        const string name = "wav/built-w64-pcm-8-bit-mono-odd.w64";
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Empty(report.Findings);
        Assert.Equal("Wave64", report.Root.Children.Single().Name);
        Assert.Equal(40 + 40 + 128, Field(name, "Wave64/Chunk Size").Value?.AsInteger());
        Assert.Equal(24 + 16, Field(name, "Wave64/Format Chunk/Chunk Size").Value?.AsInteger());
        Node data = Field(name, "Wave64/Data Chunk");
        Assert.Equal(101, data.Value?.AsInteger());
        Assert.Equal(24 + 101, Field(name, "Wave64/Data Chunk/Chunk Size").Value?.AsInteger());
        Assert.Equal(128, data.Length);
        Node padding = Field(name, "Wave64/Data Chunk/Padding");
        Assert.Equal(3, padding.Length);
        Assert.Equal(data.End - 3, padding.Offset);
        Assert.Equal(101, Assert.Single(report.Streams).Samples);
    }

    [Fact]
    public void AWave64ChunkIsNamedByTheCharactersOfItsGuid()
    {
        const string name = "wav/built-w64-unknown-chunks.w64";
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Empty(report.Findings);
        Assert.Equal("20746d66-acf3-11d3-8cd1-00c04f8edb8a (fmt )", Text(name, "Wave64/Format Chunk/Chunk ID"));
        Assert.Equal("'abcd' Chunk", Field(name, "Wave64/'abcd' Chunk").Name);
        Assert.Equal("List Chunk", Fixtures.Resolve(report.Root, "Wave64/List Chunk").Single().Name);
        Assert.Contains(report.Root.Children.Single().Children, c => c.Name == "Chunk 03020100-0504-0706-0809-0a0b0c0d0e0f");
    }

    [Fact]
    public void AWave64FileIsNotAWavFileAndAWavFileIsNotWave64()
    {
        Analyzer analyzer = Analyzer.CreateDefault();
        byte[] w64 = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "wav", "built-w64-minimal.w64"));
        byte[] wav = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "wav", "built-minimal-pcm.wav"));
        Assert.Equal("Wave64", analyzer.Analyze("a.wav", new MemoryStream(w64)).Root.Value?.ToString());
        Assert.Equal("WAV", analyzer.Analyze("a.w64", new MemoryStream(wav)).Root.Value?.ToString());
        // The riff GUID alone is no Wave64 file: the wave GUID must follow.
        byte[] half = [.. w64[..24], .. new byte[16], .. w64[40..]];
        Assert.NotEqual("Wave64", analyzer.Analyze("a.w64", new MemoryStream(half)).Root.Value?.ToString());
    }

    [Fact]
    public void AWave64HeaderWithoutAChunkIsLikelyAndWithOneIsCertain()
    {
        var parser = new Encspect.Formats.Wav.Wave64Parser();
        byte[] w64 = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "wav", "built-w64-minimal.w64"));
        SniffResult Sniff(byte[] data) => parser.Sniff(new SniffContext(new ByteReader(new MemoryStream(data))));
        Assert.Equal(100, Sniff(w64).Class);
        Assert.Equal(2, Sniff(w64).Frames);
        Assert.Equal(80, Sniff(w64[..40]).Class);
        Assert.Equal(80, Sniff(w64[..50]).Class);
        Assert.Equal(90, Sniff(w64[..70]).Class);
        Assert.Equal(0, Sniff(w64[..39]).Class);
        byte[] zeroSize = [.. w64];
        zeroSize[56] = 0;
        Assert.Equal(80, Sniff(zeroSize).Class);
        Assert.Null(parser.DeclaredEnd(new SniffContext(new ByteReader(new MemoryStream(w64[..20])))));
        Assert.Equal(w64.Length, parser.DeclaredEnd(new SniffContext(new ByteReader(new MemoryStream(w64)))));
    }
}
