using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// "Container Samples": the File Properties Object's play duration less the preroll, in samples at the stream's rate, rounded to the nearest with halves up
// (docs/formats/asf-wma.md). Each case patches the play duration, the preroll or the flags of a committed Windows-written file and reads the statistic back.
public class AsfContainerSamplesTests
{
    private const string Standard = "external-windows-wma92-cbr-stereo-44k-128.wma";
    private static readonly byte[] FilePropertiesId = Guid.Parse("8CABDCA1-A947-11CF-8EE4-00C00C205365").ToByteArray();

    // File Properties Object: play duration at 64 (100 ns ticks), preroll at 80 (milliseconds), flags at 88 (bit 0: broadcast).
    private static byte[] Patched(string fixture, ulong playTicks, ulong prerollMilliseconds, uint? flags = null)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf", fixture));
        int at = data.AsSpan().IndexOf(FilePropertiesId);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at + 64), playTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at + 80), prerollMilliseconds);
        if (flags is { } value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 88), value);
        }

        return data;
    }

    private static Node? Find(Node node, string name) => node.Name == name ? node : node.Children.Select(c => Find(c, name)).FirstOrDefault(n => n is not null);

    private static long? ContainerSamples(byte[] data)
    {
        Report report = Analyzer.CreateDefault().Analyze("a.wma", new MemoryStream(data));
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed");
        return Find(report.Root, "Container Samples")?.Value?.AsInteger();
    }

    [Theory]
    [InlineData(1_050_000UL, 0UL, 4631L)] // 4,630.5 samples: halves up
    [InlineData(1_049_999UL, 0UL, 4630L)] // one tick less: 4,630.49559
    [InlineData(1_050_001UL, 0UL, 4631L)]
    [InlineData(61_050_000UL, 6000UL, 4631L)] // the preroll is milliseconds: 6,000 ms = 60,000,000 ticks
    [InlineData(10_000_000UL, 0UL, 44100L)]
    public void TheLengthIsRoundedToTheNearestSampleWithHalvesUp(ulong playTicks, ulong prerollMilliseconds, long expected)
    {
        Assert.Equal(expected, ContainerSamples(Patched(Standard, playTicks, prerollMilliseconds)));
    }

    // The largest play duration still converts in integers.
    [Fact]
    public void TheLargestPlayDurationConvertsWithoutLoss()
    {
        Assert.Equal(81_350_141_365_059_123L, ContainerSamples(Patched(Standard, ulong.MaxValue, 0)));
    }

    [Theory]
    [InlineData(0UL, 0UL, 0u)] // play duration 0
    [InlineData(60_000_000UL, 6000UL, 0u)] // play duration equal to the preroll
    [InlineData(50_000_000UL, 6000UL, 0u)] // play duration below the preroll
    [InlineData(10_000_000UL, ulong.MaxValue, 0u)] // a preroll too large to convert to ticks
    [InlineData(10_000_000UL, 0UL, 1u)] // broadcast flag set
    public void TheHeaderStatesNoLength(ulong playTicks, ulong prerollMilliseconds, uint flags)
    {
        Assert.Null(ContainerSamples(Patched(Standard, playTicks, prerollMilliseconds, flags)));
    }

    [Theory]
    [InlineData("external-windows-wma92-cbr-stereo-44k-128.wma", 4631L)]
    [InlineData("external-windows-wma10pro-cbr-stereo-44k-128.wma", 4631L)]
    [InlineData("external-windows-wma92lossless-stereo-44k-16.wma", 4631L)]
    [InlineData("external-windows-wmavoice-cbr-mono-16k-16.wma", 1680L)]
    public void EveryWindowsMediaAudioCodecStatesIt(string fixture, long expected)
    {
        byte[] data = Patched(fixture, 1_050_000UL, 0);
        Assert.Equal(expected, ContainerSamples(data));

        // The same length in seconds stands before it.
        Node statistics = Find(Analyzer.CreateDefault().Analyze("a.wma", new MemoryStream(data)).Root, "Container Duration")!;
        Assert.Equal(0.105, statistics.Value!.AsDouble()!.Value, 3);
        Assert.Equal("s", statistics.Value.Unit);
    }

    // An encrypted stream has no statistics, so no Container Samples; its Samples take the header's length instead.
    [Fact]
    public void AnEncryptedStreamHasNoStatisticAndTakesTheLengthAsItsSamples()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf", "protected-advanced-data-size.asf"));
        Report report = Analyzer.CreateDefault().Analyze("p.asf", new MemoryStream(data));
        Assert.Null(Find(report.Root, "Container Samples"));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal((long)Math.Round(stream.Duration * stream.SampleRate, MidpointRounding.AwayFromZero), stream.Samples);
    }

    // The streams whose Samples are the header's length (encrypted ones, and Voice with a configuration the description rejects) take the integer figure,
    // halves up: a length of exactly 4,630.5 (44.1 kHz) or 800.5 (8 kHz) samples is 4,631 or 801, where rounding a double to even would give 4,630 or 800.
    [Theory]
    [InlineData("protected-advanced-data-size.asf", 1_050_000UL, 4631L)]
    [InlineData("protected-wma-pro.asf", 1_050_000UL, 4631L)]
    [InlineData("protected-wma-lossless.asf", 1_050_000UL, 4631L)]
    [InlineData("protected-wma-voice.asf", 1_000_625UL, 801L)]
    [InlineData("voice-config-align.asf", 1_000_625UL, 801L)]
    public void SamplesTakenFromTheHeaderRoundHalvesUp(string fixture, ulong playTicks, long expected)
    {
        byte[] data = Patched(fixture, playTicks, 0);
        Report report = Analyzer.CreateDefault().Analyze("p.asf", new MemoryStream(data));
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed");
        Assert.Equal(expected, Assert.Single(report.Streams).Samples);
    }

    private static readonly byte[] StreamPropertiesId = Guid.Parse("B7DC0791-A9B7-11CF-8EE6-00C00C205365").ToByteArray();

    // Replaces the sampling rate (8,000) in the first stream's type-specific data, a WAVEFORMATEX.
    private static byte[] WithRate(byte[] data, uint rate)
    {
        int stream = data.AsSpan().IndexOf(StreamPropertiesId);
        byte[] old = BitConverter.GetBytes(8000u);
        int at = stream + data.AsSpan(stream).IndexOf(old);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), rate);
        return data;
    }

    // A length that does not fit a signed 64-bit integer in samples, and a sampling rate of 0, state no samples.
    [Theory]
    [InlineData(uint.MaxValue)] // 2^64 - 1 ticks x 4,294,967,295 samples per second / 10,000,000
    [InlineData(0u)]
    public void ALengthThatCannotBeConvertedIsNotStated(uint rate)
    {
        byte[] data = WithRate(Patched("voice-config-align.asf", ulong.MaxValue, 0), rate);
        Report report = Analyzer.CreateDefault().Analyze("a.asf", new MemoryStream(data));
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed");
        Assert.NotNull(Find(report.Root, "Container Duration"));
        Assert.Null(Find(report.Root, "Container Samples"));
    }
}
