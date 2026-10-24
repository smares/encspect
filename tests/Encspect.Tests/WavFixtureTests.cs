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
    public void TheFormatIsDetectedAsWav(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Equal("WAV", report.Root.Value?.ToString());
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
        foreach (string name in new[] { "wav/external-pcm-s16-stereo-bwf.wav", "wav/built-cue-labels.wav", "wav/built-info-list.wav", "wav/built-unknown-chunks.wav", "wav/external-pcm-s24-stereo-rf64.wav" })
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
}
