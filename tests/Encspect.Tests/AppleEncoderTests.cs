using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Apple's AAC encoder is named from the way its files are written (AacEncoder, Mp4Parser.AddAppleEvidence): the files afconvert wrote
// on the macOS 15.7 runners are in tests/fixtures/aac (apple-*), and here each piece of the evidence is taken away in turn.
public class AppleEncoderTests
{
    private const string Apple = "Apple AAC encoder (AudioToolbox)";

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
    public void M4aLayoutNamesAppleWithMediumConfidence()
    {
        Report report = Analyze("apple.m4a", Read("aac/apple-lc-m4a-cbr-st44.m4a"));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(Apple, stream.Encoder);
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
    public void WithoutSbtdBoxAlignedAudioDataStillNamesApple()
    {
        byte[] bytes = Read("aac/apple-lc-m4a-cbr-st44.m4a");
        Replace(bytes, "sbtd", "sbtX");
        Assert.Equal(Apple, Assert.Single(Analyze("apple.m4a", bytes).Streams).Encoder);
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
    [InlineData("aac/apple-lc-3gp-cbr-st44.3gp")]
    [InlineData("aac/apple-he-m4a-cbr-st44.m4a")]
    [InlineData("aac/apple-hev2-m4a-cbr-st44.m4a")]
    [InlineData("aac/apple-aacl-m4a-cbr-mono44.m4a")]
    [InlineData("aac/apple-intel-he-m4a-cbr-st44.m4a")]
    public void EveryContainerAndProfileIsNamed(string name)
    {
        StreamInfo stream = Assert.Single(Analyze(name, Read(name)).Streams);
        Assert.Equal((Apple, Confidence.Medium), (stream.Encoder, stream.EncoderConfidence));
    }

    [Theory]
    [InlineData("aac/apple-lc-adts-cbr-st44.aac")]
    [InlineData("aac/apple-intel-lc-adts-cbr-st44.aac")]
    [InlineData("aac/apple-hev2-adts-cbr-st44.aac")]
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
        bytes[Frames(bytes).ElementAt(40) + 3] &= 0xDF;
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

    // The stereo HE-AAC files carry SBR frames of the FIXVAR class, whose frequency resolutions are sent last envelope first (ISO/IEC
    // 14496-3 4.4.2.2); read forwards, the payloads ran past their fill elements.
    [Theory]
    [InlineData("aac/apple-he-m4a-cbr-st44.m4a")]
    [InlineData("aac/apple-intel-he-m4a-cbr-st44.m4a")]
    [InlineData("aac/apple-he-adts-cbr-st44.aac")]
    public void StereoSbrPayloadsFitTheirFillElements(string name)
    {
        Report report = Analyze(name, Read(name));
        Assert.DoesNotContain(report.Findings, f => f.Code is "aac.sbr-past-fill" or "aac.sbr-data-invalid");
    }
}
