using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// IEC 61937 data-bursts in the samples of a WAVE file: the fields of a burst, the routes to the codec parsers, what is no chain, and the bounds. The
// fixtures' manifests (wav_model.py) check the bursts and findings of each file; these tests check fields and cases a file cannot.
public class Iec61937Tests
{
    private static Report Analyze(byte[] data, string name = "iec.wav") => Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "wav", name));

    private static Node Burst(Report report, int index) => Fixtures.Resolve(report.Root, $"RIFF/Data Chunk/IEC 61937 Bursts/Burst {index}").Single();

    // A RIFF file of 16-bit stereo PCM whose data chunk is data.
    private static byte[] Wave(byte[] data, int rate = 48000)
    {
        byte[] file = new byte[12 + 24 + 8 + data.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(file.Length - 8));
        "WAVEfmt "u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), (uint)(rate * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(34), 16);
        "data"u8.CopyTo(file.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), (uint)data.Length);
        data.CopyTo(file.AsSpan(44));
        return file;
    }

    // The data chunk of a fixture.
    private static byte[] Samples(string name)
    {
        byte[] file = Fixture(name);
        int at = file.AsSpan().IndexOf("data"u8);
        return file[(at + 8)..(at + 8 + BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 4)))];
    }

    [Fact]
    public void ABurstIsReadFieldByField()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/external-iec61937-ac3-stereo-192k.wav");
        Node bursts = Fixtures.Resolve(report.Root, "RIFF/Data Chunk/IEC 61937 Bursts").Single();
        Assert.Equal(4, bursts.Value?.AsInteger());
        Node burst = Burst(report, 1);
        Assert.Equal("AC-3", burst.Value?.AsText());
        Assert.Equal(6144, burst.Offset - bursts.Offset);
        Assert.Equal(8 + 768, burst.Length);
        Assert.Equal("F872h 4E1Fh", burst.Children.Single(c => c.Name == "Sync Words").Value?.AsText());
        Node info = burst.Children.Single(c => c.Name == "Burst Info");
        Assert.Equal("0x0001", info.Value?.ToString());
        Assert.Equal(1, info.Children.Single(c => c.Name == "Data Type").Value?.AsInteger());
        Assert.Equal(0, info.Children.Single(c => c.Name == "Bitstream Number").Value?.AsInteger());
        Assert.Equal(6144, burst.Children.Single(c => c.Name == "Length Code").Value?.AsInteger());
        Node payload = burst.Children.Single(c => c.Name == "Burst Payload");
        Assert.Equal(burst.Offset + 8, payload.Offset);
        Assert.Equal(768, payload.Length);
    }

    [Fact]
    public void TheDataTypeBitsLieInTheLittleEndianWord()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/built-iec61937-error-flag.wav");
        Node info = Burst(report, 3).Children.Single(c => c.Name == "Burst Info");
        Node flag = info.Children.Single(c => c.Name == "Error Flag");
        Assert.True(flag.Value?.AsBoolean());
        Assert.Equal(info.Offset, flag.Offset);
        Assert.Equal(7, flag.BitOffset);
        Node type = info.Children.Single(c => c.Name == "Data Type");
        Assert.Equal(0, type.BitOffset);
        Assert.Equal(7, type.BitLength);
    }

    [Theory]
    [InlineData("wav/external-iec61937-ac3-stereo-192k.wav", "AC-3", 48000, 2, 4 * 1536 / 48000.0)]
    [InlineData("wav/external-iec61937-ac3-5.1-448k.wav", "AC-3", 44100, 6, 3 * 1536 / 44100.0)]
    [InlineData("wav/external-iec61937-eac3-stereo-192k.wav", "E-AC-3", 48000, 2, 4 * 1536 / 48000.0)]
    [InlineData("wav/external-iec61937-dts-stereo-768k.wav", "DTS", 48000, 2, 10 * 512 / 48000.0)]
    [InlineData("wav/external-iec61937-aac-stereo-128k.wav", "AAC LC", 48000, 2, 6 * 1024 / 48000.0)]
    public void TheStreamIsTheCodingsNotTheCarriers(string name, string codec, int rate, int channels, double seconds)
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg(name).Streams);
        Assert.Equal(codec, stream.Codec);
        Assert.Equal(rate, stream.SampleRate);
        Assert.Equal(channels, stream.Channels);
        Assert.Equal(seconds, stream.Duration, 3);
    }

    [Fact]
    public void ThePayloadsAreHandedToTheCodecParserAsFramesAtTheirFileBytes()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/external-iec61937-ac3-stereo-192k.wav");
        Node stream = Fixtures.Resolve(report.Root, "RIFF/Data Chunk/AC-3").Single();
        Assert.Equal(4, stream.Value?.AsInteger());
        Node first = stream.Children.First(c => c.Name.StartsWith("Frame", StringComparison.Ordinal));
        Node payload = Burst(report, 0).Children.Single(c => c.Name == "Burst Payload");
        Assert.True(first.Offset >= payload.Offset && first.End <= payload.End, $"The first frame lies at {first.Offset}..{first.End}, the payload at {payload.Offset}..{payload.End}.");
        Assert.DoesNotContain(report.Findings, f => f.Severity >= Severity.Warning);
    }

    [Fact]
    public void TheLinkRateOfEac3IsFourTimesTheContentRate()
    {
        Assert.DoesNotContain(Fixtures.AnalyzeMpeg("wav/external-iec61937-eac3-stereo-192k.wav").Findings, f => f.Code == "wav.iec61937-rate-mismatch");
        Finding finding = Assert.Single(Fixtures.AnalyzeMpeg("wav/built-iec61937-eac3-at-the-content-rate.wav").Findings, f => f.Code == "wav.iec61937-rate-mismatch");
        Assert.Contains("link rate of 192000 Hz", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wav/external-iec61937-mp2-stereo-192k.wav", "MPEG-1 Layer II or III", 5 * 1152 / 48000.0)]
    [InlineData("wav/external-iec61937-mp2-lsf-24000.wav", "MPEG-2 Layer II", 3 * 2304 / 48000.0)]
    public void ACodingWithoutAParserIsDescribedByItsBursts(string name, string codec, double seconds)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(codec, stream.Codec);
        Assert.Equal(seconds, stream.Duration, 9);
        Assert.Equal(0, stream.Channels);
        Assert.Equal(0, stream.SampleRate);
        Assert.Equal(0, stream.Samples);
        Assert.Contains(report.Findings, f => f.Code == "wav.iec61937-payload-not-analyzed" && f.Severity == Severity.Info);
        Assert.Equal(codec, Fixtures.Resolve(report.Root, "RIFF/Data Chunk/Statistics/Codec").Single().Value?.AsText());
    }

    [Theory]
    [InlineData("built-iec61937-two-bursts.wav")]
    [InlineData("built-iec61937-sync-word-alone.wav")]
    [InlineData("built-iec61937-in-mono.wav")]
    [InlineData("built-iec61937-in-24-bits.wav")]
    [InlineData("built-iec61937-in-rifx.wav")]
    [InlineData("built-iec61937-unknown-data-type.wav")]
    [InlineData("built-iec61937-payload-too-long-at-start.wav")]
    public void WhatIsNoChainOfBurstsIsPcm(string name)
    {
        Report report = Analyze(Fixture(name));
        Assert.Equal("PCM", Assert.Single(report.Streams).Codec);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("wav.iec61937-", StringComparison.Ordinal));
    }

    [Fact]
    public void SilenceBeforeTheFirstBurstIsSkippedUpToABound()
    {
        byte[] bursts = Samples("built-iec61937-ac3.wav");
        Assert.Equal("AC-3", Assert.Single(Analyze(Wave([.. new byte[4000], .. bursts])).Streams).Codec);
        Assert.Equal("PCM", Assert.Single(Analyze(Wave([.. new byte[(1 << 20) + 4096], .. bursts])).Streams).Codec);
        Assert.Equal("PCM", Assert.Single(Analyze(Wave([1, 0, 0, 0, .. bursts])).Streams).Codec);
    }

    [Fact]
    public void ABurstOffTheFrameGridIsNotTheStart()
    {
        byte[] bursts = Samples("built-iec61937-ac3.wav");
        Assert.Equal("PCM", Assert.Single(Analyze(Wave([0, 0, .. bursts])).Streams).Codec);
    }

    [Fact]
    public void OnlyTheFirstBurstsAreListedButAllAreCounted()
    {
        byte[] source = Samples("external-iec61937-dts-stereo-768k.wav");
        byte[] one = source[..2048];
        byte[] data = new byte[2048 * 1100];
        for (int i = 0; i < 1100; i++)
        {
            one.CopyTo(data, i * 2048);
        }

        Report report = Analyze(Wave(data));
        Node bursts = Fixtures.Resolve(report.Root, "RIFF/Data Chunk/IEC 61937 Bursts").Single();
        Assert.Equal(1100, bursts.Value?.AsInteger());
        Assert.Equal(1024, bursts.Children.Count);
        Assert.Equal("DTS", Assert.Single(report.Streams).Codec);
    }

    [Fact]
    public void ZerosAfterTheLastBurstAreStuffingAndOtherBytesAreNot()
    {
        byte[] bursts = Samples("built-iec61937-ac3.wav");
        Assert.DoesNotContain(Analyze(Wave([.. bursts, .. new byte[5000]])).Findings, f => f.Code == "wav.iec61937-data-after-bursts");
        Finding finding = Assert.Single(Analyze(Wave([.. bursts, .. new byte[5000], 1])).Findings, f => f.Code == "wav.iec61937-data-after-bursts");
        long at = 44 + bursts.Length + 5000;
        Assert.Contains($"from 0x{at:X} ", finding.Message, StringComparison.Ordinal);
        Assert.Contains("1 byte ", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANormalPcmFileCostsNoBurstWork()
    {
        Report report = Analyze(Wave(new byte[1 << 20]));
        Assert.Equal("PCM", Assert.Single(report.Streams).Codec);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void BurstsInAFileOfTheFormatTagForAc3OverSpdifAreReadToo()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/built-iec61937-ac3-fmt-tag-0092.wav");
        Assert.Equal("AC-3", Assert.Single(report.Streams).Codec);
        Assert.Contains(report.Findings, f => f.Code == "wav.iec61937-in-pcm");
    }

    [Theory]
    [InlineData("built-iec61937-ac3-fmt-tag-0240.wav", "AC-3")]
    [InlineData("built-iec61937-ac3-fmt-tag-0241.wav", "AC-3")]
    [InlineData("built-iec61937-dts-fmt-tag-0008.wav", "DTS")]
    public void BurstsUnderTheOtherTagsForSpdifDataAreReadToo(string name, string codec)
    {
        Report report = Fixtures.AnalyzeMpeg($"wav/{name}");
        Assert.Equal(codec, Assert.Single(report.Streams).Codec);
        Assert.Contains(report.Findings, f => f.Code == "wav.iec61937-in-pcm");
        Assert.DoesNotContain(report.Findings, f => f.Code is "dts.leading-junk" or "wav.coding-unknown");
    }

    [Fact]
    public void ABurstChainInWave64IsReadAsInWav()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/built-w64-iec61937-ac3.w64");
        Assert.Equal("AC-3", Assert.Single(report.Streams).Codec);
        Assert.Equal(5, Fixtures.Resolve(report.Root, "Wave64/Data Chunk/IEC 61937 Bursts").Single().Value?.AsInteger());
    }

    [Fact]
    public void ThePauseBurstsOfAGapAreNotMissingBursts()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/built-iec61937-ac3-pause-bursts.wav");
        Assert.DoesNotContain(report.Findings, f => f.Code == "wav.iec61937-burst-missing");
        Assert.Equal(5, Fixtures.Resolve(report.Root, "RIFF/Data Chunk/IEC 61937 Bursts").Single().Value?.AsInteger());
        Assert.Equal(5, Fixtures.Resolve(report.Root, "RIFF/Data Chunk/IEC 61937 Bursts").Single().Children.Count);
        Finding pauses = Assert.Single(report.Findings, f => f.Code == "wav.iec61937-pause-bursts");
        Assert.Equal(Severity.Info, pauses.Severity);
        Assert.Contains("2 pause or null bursts in 1 run", pauses.Message, StringComparison.Ordinal);
    }

    // A pause burst at its recommended period of 3 IEC 60958 frames (IEC 61937-3:2017 Table 2): the preamble and a payload of 32 bits.
    private static readonly byte[] Pause = [0x72, 0xF8, 0x1F, 0x4E, 3, 0, 32, 0, 0, 0, 0, 0];

    private static byte[] Repeat(byte[] unit, int count)
    {
        byte[] result = new byte[unit.Length * count];
        for (int i = 0; i < count; i++)
        {
            unit.CopyTo(result, i * unit.Length);
        }

        return result;
    }

    [Fact]
    public void AStreamThatStartsInAGapIsFoundOnceAChainFollows()
    {
        byte[] bursts = Samples("built-iec61937-ac3.wav");
        Report report = Analyze(Wave([.. Repeat(Pause, 100), .. bursts]));
        Assert.Equal("AC-3", Assert.Single(report.Streams).Codec);
        Node container = Fixtures.Resolve(report.Root, "RIFF/Data Chunk/IEC 61937 Bursts").Single();
        Assert.Equal(44, container.Offset);
        Assert.Equal(5, container.Value?.AsInteger());
        Assert.Equal(5, container.Children.Count);
        Assert.Contains("100 pause or null bursts in 1 run", Assert.Single(report.Findings, f => f.Code == "wav.iec61937-pause-bursts").Message, StringComparison.Ordinal);

        // Two bursts after the gap are no chain, and a gap beyond the bound is PCM, as silence is.
        Assert.Equal("PCM", Assert.Single(Analyze(Wave([.. Repeat(Pause, 100), .. bursts[..(2 * 6144)]])).Streams).Codec);
        Assert.Equal("PCM", Assert.Single(Analyze(Wave([.. Repeat(Pause, 100_000), .. bursts])).Streams).Codec);
    }

    [Fact]
    public void AGapOfPauseBurstsCostsLinearTimeAndNoListedBursts()
    {
        byte[] bursts = Samples("built-iec61937-ac3.wav");
        byte[] data = [.. bursts[..(3 * 6144)], .. Repeat(Pause, 960_000), .. bursts[(3 * 6144)..]];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Report report = Analyze(Wave(data));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Took {watch.Elapsed.TotalSeconds:F1} s");
        Node container = Fixtures.Resolve(report.Root, "RIFF/Data Chunk/IEC 61937 Bursts").Single();
        Assert.Equal(5, container.Value?.AsInteger());
        Assert.Equal(5, container.Children.Count);
        Assert.Contains("960000 pause or null bursts in 1 run", Assert.Single(report.Findings, f => f.Code == "wav.iec61937-pause-bursts").Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Code is "wav.iec61937-burst-missing" or "wav.iec61937-burst-spacing");
    }

    // The payload of an AAC burst is an ADTS frame (the file holds its bytes in swapped pairs), which the AAC parser reads as a packet: its header gives the
    // configuration and the raw data block follows it.
    [Fact]
    public void TheAacBurstsAreAdtsFramesReadByTheAacParser()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/external-iec61937-aac-stereo-128k.wav");
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("AAC LC", stream.Codec);
        Assert.Equal(48000, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
        Assert.Equal(6 * 1024, stream.Samples);
        Node units = Fixtures.Resolve(report.Root, "RIFF/Data Chunk/AAC").Single();
        Assert.Equal(6, units.Value?.AsInteger());
        Node payload = Burst(report, 0).Children.Single(c => c.Name == "Burst Payload");
        Node unit = units.Children.First(c => c.Name == "Access Unit");
        Assert.Equal(payload.Offset + 7, unit.Offset);
        Assert.DoesNotContain(report.Findings, f => f.Severity >= Severity.Warning);
    }

    // The byte of the stream (swapped back) at index in the payload of burst, as the file holds it.
    private static int FileIndex(int burst, int index) => (burst * 4096) + 8 + (index ^ 1);

    [Fact]
    public void AnAdtsFrameOfAnotherStreamOrOfSeveralBlocksIsNotRead()
    {
        byte[] samples = Samples("external-iec61937-aac-stereo-128k.wav");

        byte[] changed = [.. samples];
        changed[FileIndex(2, 2)] = 0x50;
        Report report = Analyze(Wave(changed));
        Assert.Equal(5, Fixtures.Resolve(report.Root, "RIFF/Data Chunk/AAC").Single().Value?.AsInteger());
        Assert.Contains(report.Findings, f => f.Code == "aac.adts-packet-invalid" && f.Severity == Severity.Warning && f.Message.StartsWith("A packet does not start", StringComparison.Ordinal));

        byte[] several = [.. samples];
        several[FileIndex(3, 6)] |= 1;
        report = Analyze(Wave(several));
        Assert.Equal(5, Fixtures.Resolve(report.Root, "RIFF/Data Chunk/AAC").Single().Value?.AsInteger());
        Assert.Contains(report.Findings, f => f.Code == "aac.adts-multiple-blocks" && f.Severity == Severity.Info);
    }

    [Fact]
    public void BurstsWithoutAnAdtsHeaderGiveNoConfiguration()
    {
        byte[] samples = Samples("external-iec61937-aac-stereo-128k.wav");
        for (int burst = 0; burst < 6; burst++)
        {
            samples[FileIndex(burst, 0)] = 0;
        }

        Report report = Analyze(Wave(samples));
        Assert.Contains(report.Findings, f => f.Code == "aac.no-configuration");
        Assert.Contains(report.Findings, f => f.Code == "aac.adts-packet-invalid");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }
}
