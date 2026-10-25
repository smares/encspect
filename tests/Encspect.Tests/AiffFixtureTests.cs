using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Checks every AIFF and AIFF-C fixture against its manifest (make_external_aiff_fixtures.py, make_aiff_fixtures.py, both worked out by aiff_model.py, a
// reading of the specifications apart from the analyzer, and confirmed by crosscheck_wav_fixtures.py), and the fields of some of them against the
// values they were built with.
public class AiffFixtureTests
{
    public static TheoryData<string> Names => ChunkFixtureChecks.Names("aiff");

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name) => ChunkFixtureChecks.ReportMatchesManifest(name);

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string name) => ChunkFixtureChecks.ReportMatchesGoldenFile(name);

    [Theory]
    [MemberData(nameof(Names))]
    public void TheFormatIsDetectedAsAiff(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Equal("AIFF", report.Root.Value?.ToString());
        Assert.Null(report.Detection);
    }

    private static Node Field(string name, string path) => Fixtures.Resolve(Fixtures.AnalyzeMpeg(name).Root, path).Single();

    private static string Text(string name, string path) => Field(name, path).Value!.ToString();

    [Fact]
    public void TheCommonChunkIsReadFieldByField()
    {
        const string name = "aiff/external-pcm-s24-stereo-48000.aiff";
        Assert.Equal(2, Field(name, "FORM/Common Chunk/Channels").Value?.AsInteger());
        Assert.Equal(12000, Field(name, "FORM/Common Chunk/Sample Frames").Value?.AsInteger());
        Assert.Equal(24, Field(name, "FORM/Common Chunk/Sample Size").Value?.AsInteger());
        Assert.Equal(48000, Field(name, "FORM/Common Chunk/Sample Rate").Value?.AsInteger());
        Assert.Equal("PCM, 2 channels, 48000 Hz, 24 bits", Text(name, "FORM/Common Chunk"));
        Assert.Equal(0x400EUL, Field(name, "FORM/Common Chunk/Sample Rate/Sign and Exponent").Value?.AsUnsigned());
        Assert.Equal(0xBB80000000000000UL, Field(name, "FORM/Common Chunk/Sample Rate/Mantissa").Value?.AsUnsigned());
    }

    [Theory]
    [InlineData("aiff/built-rate-22254-54.aiff", 22255, "22254.5400 Hz")]
    [InlineData("aiff/built-rate-192000.aiff", 192000, "192000 Hz")]
    [InlineData("aiff/built-rate-11025.aiff", 11025, "11025 Hz")]
    public void TheExtendedRateIsDecodedExactly(string name, int rate, string shown)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Equal(rate, Assert.Single(report.Streams).SampleRate);
        Assert.Equal(shown, Field(name, "FORM/Common Chunk/Sample Rate").Value?.ToString());
    }

    [Theory]
    [InlineData("aiff/external-sowt-stereo-44100.aifc", "sowt", "PCM", "little-endian")]
    [InlineData("aiff/external-fl32-stereo-44100.aifc", "fl32", "PCM (floating-point)", "big-endian")]
    [InlineData("aiff/external-fl64-mono-48000.aifc", "fl64", "PCM (floating-point)", "big-endian")]
    [InlineData("aiff/external-alaw-mono-8000.aifc", "alaw", "A-law", null)]
    [InlineData("aiff/external-ulaw-mono-8000.aifc", "ulaw", "Mu-law", null)]
    [InlineData("aiff/external-ima4-stereo-44100.aifc", "ima4", "IMA ADPCM", null)]
    [InlineData("aiff/external-raw-u8-mono-22050.aifc", "raw ", "PCM", "big-endian")]
    [InlineData("aiff/built-aifc-mac3.aifc", "MAC3", "MACE 3-to-1", null)]
    [InlineData("aiff/built-aifc-ace8.aifc", "ACE8", "ACE 8-to-3", null)]
    public void ACompressionTypeIsNamedByItsCoding(string name, string type, string codec, string? byteOrder)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Equal("AIFC", Fixtures.Resolve(report.Root, "FORM").Single().Value?.ToString());
        Assert.Equal(type, Field(name, "FORM/Common Chunk/Compression Type").Value?.AsText());
        Assert.Equal(codec, Assert.Single(report.Streams).Codec);
        Node[] order = [.. Fixtures.Resolve(report.Root, "FORM/Sound Data Chunk/Statistics/Byte Order")];
        Assert.Equal(byteOrder, order.SingleOrDefault()?.Value?.ToString());
    }

    // Appendix C of the AIFF-C draft: the sample frame of a compressed coding is 2 bytes for MACE 3:1 (6 samples), 1 byte for MACE 6:1 (6), 1 byte
    // for ACE 2:1 (2) and 3 bytes for ACE 8:3 (8), for each channel; the Common Chunk counts those frames.
    [Theory]
    [InlineData("aiff/built-aifc-mac3.aifc", 20 * 6)]
    [InlineData("aiff/built-aifc-mac6.aifc", 20 * 6)]
    [InlineData("aiff/built-aifc-ace2.aifc", 20 * 2)]
    [InlineData("aiff/built-aifc-ace8.aifc", 20 * 8)]
    [InlineData("aiff/built-aifc-mac3-short.aifc", 30 * 6)]
    public void ACompressedSampleFrameHoldsTheSamplesAppendixCGives(string name, int samples)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(samples, stream.Samples);
        Assert.True(stream.Duration > 0);
        Assert.DoesNotContain(report.Findings, f => f.Code == "aiff.length-unknown");
    }

    [Fact]
    public void AFloatingPointTypeHasItsWidthAsTheSampleBits()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg("aiff/external-fl64-mono-48000.aifc").Streams);
        Assert.Equal(64, stream.BitsPerSample);
        Assert.Equal("Mono", stream.ChannelMode);
    }

    [Fact]
    public void TheFormatVersionChunkIsVersionOneOfAiffC()
    {
        const string name = "aiff/built-aifc-none.aifc";
        Assert.Equal("2726318400 (version 1 of AIFF-C)", Text(name, "FORM/Format Version Chunk/Timestamp"));
        Assert.Equal("not compressed", Text(name, "FORM/Common Chunk/Compression Name"));
    }

    [Fact]
    public void TextChunksShowTheirText()
    {
        const string name = "aiff/external-pcm-s16-stereo-text.aiff";
        Assert.Equal("Fixture title", Text(name, "FORM/Name Chunk"));
        Assert.Equal("Fixture author", Text(name, "FORM/Author Chunk"));
        Assert.Equal("Public domain", Text(name, "FORM/Copyright Chunk"));
        Assert.Equal("A comment", Text(name, "FORM/Annotation Chunk"));
        Assert.Equal(["First note", "Odd"], Fixtures.Resolve(Fixtures.AnalyzeMpeg("aiff/built-text-chunks.aiff").Root, "FORM/Annotation Chunk").Select(c => c.Value?.ToString()));
    }

    [Fact]
    public void MarkersCommentsAndTheInstrumentAreReadWithTheirFields()
    {
        const string name = "aiff/built-markers-comments-instrument.aiff";
        Assert.Equal(30, Field(name, "FORM/Marker Chunk/Markers/Marker 1/Position").Value?.AsInteger());
        Assert.Equal("Middle", Text(name, "FORM/Marker Chunk/Markers/Marker 1/Name"));
        Assert.Equal("1990-05-23 14:40:00 UTC", Text(name, "FORM/Comments Chunk/Comments/Comment 0/Time Stamp"));
        Assert.Equal("About start", Text(name, "FORM/Comments Chunk/Comments/Comment 0/Text"));
        Assert.Equal("0 (no marker)", Text(name, "FORM/Comments Chunk/Comments/Comment 1/Marker ID"));
        Assert.Equal(60, Field(name, "FORM/Instrument Chunk/Base Note").Value?.AsInteger());
        Assert.Equal(-6, Field(name, "FORM/Instrument Chunk/Detune").Value?.AsInteger());
        Assert.Equal(-6, Field(name, "FORM/Instrument Chunk/Gain").Value?.AsInteger());
        Assert.Equal("1 (forward looping)", Text(name, "FORM/Instrument Chunk/Sustain Loop/Play Mode"));
        Assert.Equal(2, Field(name, "FORM/Instrument Chunk/Sustain Loop/End Marker").Value?.AsInteger());
    }

    [Fact]
    public void ApplicationAudioRecordingAndMidiChunksAreShown()
    {
        const string name = "aiff/built-application-chunks.aiff";
        Report report = Fixtures.AnalyzeMpeg(name);
        Node[] appl = [.. Fixtures.Resolve(report.Root, "FORM/Application Specific Chunk")];
        Assert.Equal(["pdos, Fixture", "stoc, Tool", "ABCD"], appl.Select(a => a.Value?.ToString()));
        Assert.Equal(24, Field(name, "FORM/Audio Recording Chunk/Channel Status Data").Value?.AsInteger());
        Assert.Equal(2, Fixtures.Resolve(report.Root, "FORM/MIDI Data Chunk").Count());
    }

    [Fact]
    public void BlockAlignedSoundDataIsReadFromItsOffset()
    {
        Report report = Fixtures.AnalyzeMpeg("aiff/built-sound-offset.aiff");
        Assert.Empty(report.Findings);
        Assert.Equal(50, Assert.Single(report.Streams).Samples);
        Assert.Equal(6, Field("aiff/built-sound-offset.aiff", "FORM/Sound Data Chunk/Offset").Value?.AsInteger());
        Report aligned = Fixtures.AnalyzeMpeg("aiff/built-block-aligned.aiff");
        Assert.Empty(aligned.Findings);
        Assert.Equal(512, Field("aiff/built-block-aligned.aiff", "FORM/Sound Data Chunk/Block Size").Value?.AsInteger());
    }

    [Fact]
    public void SomeLayoutsAreNamedAndOthersAreNot()
    {
        Assert.Equal(["front left", "front right", "front center"], Fixtures.AnalyzeMpeg("aiff/external-pcm-s16-3ch-48000.aiff").Streams[0].ChannelLayout!.Select(ChannelLayouts.Name));
        Assert.Equal(["front center"], Fixtures.AnalyzeMpeg("aiff/external-pcm-s16-mono-8000.aiff").Streams[0].ChannelLayout!.Select(ChannelLayouts.Name));
        Assert.Equal("3.0", Fixtures.AnalyzeMpeg("aiff/built-32-bit-surround-3ch.aiff").Streams[0].ChannelLayoutName);
    }

    [Fact]
    public void AnId3ChunkHoldsAnId3v2TagThatKeepsItsVersion()
    {
        const string v4 = "aiff/external-pcm-s16-stereo-id3-v4.aiff";
        Report report = Fixtures.AnalyzeMpeg(v4);
        Assert.Equal(["FORM"], report.Root.Children.Select(c => c.Name));
        Node chunk = Fixtures.Resolve(report.Root, "FORM/ID3v2 Chunk").Single();
        Assert.Equal("ID3 ", chunk.Children.First(c => c.Name == "Chunk ID").Value?.AsText());
        Node tag = chunk.Children.Single(c => c.Kind == NodeKind.Tag);
        Assert.Equal("2.4.0", tag.Value?.ToString());
        Assert.Equal("2.3.0", Fixtures.Resolve(Fixtures.AnalyzeMpeg("aiff/external-pcm-s16-stereo-id3-v3.aiff").Root, "FORM/ID3v2 Chunk").Single().Children.Single(c => c.Kind == NodeKind.Tag).Value?.ToString());
    }

    [Fact]
    public void ATagAfterTheFormChunkIsTheFilesAndOneInsideItIsTheChunks()
    {
        Assert.Equal(["FORM", "ID3v1"], Fixtures.AnalyzeMpeg("aiff/built-id3v1-after-form.aiff").Root.Children.Select(c => c.Name));
        Assert.Equal(["ID3v2", "FORM"], Fixtures.AnalyzeMpeg("aiff/built-leading-id3v2.aiff").Root.Children.Select(c => c.Name));
        Report inside = Fixtures.AnalyzeMpeg("aiff/built-id3v1-inside-sound.aiff");
        Assert.Equal(["FORM"], inside.Root.Children.Select(c => c.Name));
        Assert.Contains(inside.Findings, f => f.Code == "tag.near-miss");
    }

    [Fact]
    public void NodesCoverTheirBytesAndTheChunksTileTheFormChunk()
    {
        foreach (string name in new[] { "aiff/built-markers-comments-instrument.aiff", "aiff/built-application-chunks.aiff", "aiff/built-text-chunks.aiff", "aiff/external-pcm-s16-stereo-id3-v4.aiff", "aiff/built-aifc-none.aifc", "aiff/built-chan-descriptions-coordinates.aiff", "aiff/external-pcm-s16-5.1-48000.aiff" })
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
    public void TheChannelLayoutChunkIsReadFieldByField()
    {
        const string name = "aiff/external-pcm-s16-5.1-48000.aiff";
        Assert.Equal("MPEG_5_1_A: L R C LFE Ls Rs (0x00790006)", Text(name, "FORM/Channel Layout Chunk/Layout Tag"));
        Assert.Equal(0, Field(name, "FORM/Channel Layout Chunk/Channel Bitmap").Value?.AsInteger());
        Assert.Equal(0, Field(name, "FORM/Channel Layout Chunk/Channel Descriptions").Value?.AsInteger());
        Assert.Equal("5.1 (front left, front right, front center, LFE, back left, back right)", Text(name, "FORM/Channel Layout Chunk"));
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg(name).Streams);
        Assert.Equal("5.1", stream.ChannelLayoutName);
        Assert.Equal([Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight], stream.ChannelLayout);
    }

    [Theory]
    [InlineData("aiff/external-pcm-s24-7.1-96000.aiff", "7.1")]
    [InlineData("aiff/external-pcm-s16-quad-48000.aiff", "quadraphonic")]
    [InlineData("aiff/external-pcm-s16-6.1-48000.aiff", "6.1")]
    [InlineData("aiff/external-pcm-s16-hexagonal-48000.aiff", "6.0")]
    [InlineData("aiff/built-chan-descriptions-rear-labels.aiff", "7.1")]
    [InlineData("aiff/built-chan-channel-bitmap-sides.aiff", "5.1")]
    public void TheLayoutOfTheChunkNamesTheStreamsSpeakers(string name, string layout) =>
        Assert.Equal(layout, Assert.Single(Fixtures.AnalyzeMpeg(name).Streams).ChannelLayoutName);

    [Fact]
    public void TheTagMayPutTheCenterFirst()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg("aiff/built-chan-layout-tag-center-first.aiff").Streams);
        Assert.Equal([Speaker.FrontCenter, Speaker.FrontLeft, Speaker.FrontRight], stream.ChannelLayout);
    }

    [Fact]
    public void ChannelDescriptionsShowTheirLabelsFlagsAndCoordinates()
    {
        const string name = "aiff/built-chan-descriptions-coordinates.aiff";
        Assert.Equal("UseCoordinates (100)", Text(name, "FORM/Channel Layout Chunk/Descriptions/Description 0/Label"));
        Assert.Equal(1, Field(name, "FORM/Channel Layout Chunk/Descriptions/Description 0/Flags").Value?.AsInteger());
        Assert.Equal("-1, 1, 0", Text(name, "FORM/Channel Layout Chunk/Descriptions/Description 0/Coordinates"));
        Assert.Equal("1, 1, 0", Text(name, "FORM/Channel Layout Chunk/Descriptions/Description 1/Coordinates"));
        Assert.Equal("not used", Text("aiff/built-chan-descriptions.aiff", "FORM/Channel Layout Chunk/Descriptions/Description 0/Coordinates"));
        Assert.Null(Assert.Single(Fixtures.AnalyzeMpeg(name).Streams).ChannelLayoutName);
    }

    [Fact]
    public void ADiscreteLayoutHasNoSpeakers()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg("aiff/built-chan-discrete-in-order.aiff").Streams);
        Assert.Equal([Speaker.Discrete, Speaker.Discrete, Speaker.Discrete, Speaker.Discrete], stream.ChannelLayout);
    }

    [Theory]
    [InlineData("aiff/built-chan-differs-tag.aiff", "layout tag states 6 channels, but the Common Chunk states 2")]
    [InlineData("aiff/built-chan-differs-bitmap.aiff", "channel bitmap states 6 channels, but the Common Chunk states 2")]
    [InlineData("aiff/built-chan-differs-descriptions.aiff", "channel descriptions states 3 channels, but the Common Chunk states 2")]
    public void AChannelCountThatDisagreesWithTheCommonChunkIsReportedAndItsLayoutIsNotUsed(string name, string message)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Finding finding = Assert.Single(report.Findings, f => f.Code == "aiff.channel-layout-differs");
        Assert.Contains(message, finding.Message, StringComparison.Ordinal);
        Assert.Equal([Speaker.FrontLeft, Speaker.FrontRight], Assert.Single(report.Streams).ChannelLayout);
    }

    [Fact]
    public void AnUnknownLayoutTagGivesNoLayoutAndTheCommonChunkStands()
    {
        Report report = Fixtures.AnalyzeMpeg("aiff/built-chan-layout-tag-unknown.aiff");
        Assert.Equal("not known (0x012C0002)", Text("aiff/built-chan-layout-tag-unknown.aiff", "FORM/Channel Layout Chunk/Layout Tag"));
        Assert.Equal("stereo", Assert.Single(report.Streams).ChannelLayoutName);
    }
}
