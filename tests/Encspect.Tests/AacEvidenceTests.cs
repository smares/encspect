using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Host;

namespace Encspect.Tests;

// What turns an AAC LC stream into HE-AAC (SBR data after a channel element in at least half of the blocks of intact frames), and the bound on
// the graph of bitrate over time.
public class AacEvidenceTests
{
    public static TheoryData<int> Seeds => new(Enumerable.Range(0, 300));

    // ffmpeg's AAC LC stream with forty frames of random bytes under the stream's own header after its twentieth: about one random block
    // in fifty reads to its END element with a fill element of SBR data, and a few of those follow a channel element. A few blocks of the
    // stream's 106 do not make it HE-AAC: SBR data has to be in at least two blocks and half of them (the fixtures sbr-in-one-block and
    // sbr-in-three-blocks cover the rule with exact counts).
    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomFramesInsideAnLcStreamLeaveItLc(int seed)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "ffmpeg-adts.aac"));
        List<byte[]> frames = Frames(source);
        var random = new Random(seed);
        var stream = new MemoryStream();
        int count = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            if (i == 20)
            {
                for (int j = 0; j < 40; j++)
                {
                    byte[] frame = new byte[random.Next(60, 400)];
                    random.NextBytes(frame.AsSpan(7));
                    frames[0].AsSpan(0, 7).CopyTo(frame);
                    frame[3] = (byte)((frame[3] & 0xFC) | (frame.Length >> 11));
                    frame[4] = (byte)(frame.Length >> 3);
                    frame[5] = (byte)(((frame.Length & 7) << 5) | (frame[5] & 0x1F));
                    stream.Write(frame);
                    count++;
                }
            }

            stream.Write(frames[i]);
            count++;
        }

        Report report = Analyzer.CreateDefault().Analyze("garbage.aac", new MemoryStream(stream.ToArray()));

        StreamInfo info = Assert.Single(report.Streams);
        Assert.Equal("AAC LC", info.Codec);
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(count * 1024L, info.Samples);
    }

    // FAAC and fdkaac streams cut before their first SBR header stay HE-AAC, as ffmpeg, faad2 and fdk-aac decode them, and the finding does not
    // claim that ffmpeg plays the core alone: it takes SBR from the first payload, readable or not.
    [Theory]
    [InlineData("faac-2.2-he-adts.aac", 44100)]
    [InlineData("faac-2.2-he-mono-adts.aac", 48000)]
    [InlineData("fdkaac-he-adts.aac", 44100)]
    public void StreamsCutBeforeTheirFirstSbrHeaderStayHeAac(string name, int rate)
    {
        List<byte[]> frames = Frames(File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", name)));
        byte[] cut = [.. frames.Skip(1).SelectMany(f => f)];

        Report report = Analyzer.CreateDefault().Analyze("cut.aac", new MemoryStream(cut));

        StreamInfo info = Assert.Single(report.Streams);
        Assert.StartsWith("HE-AAC", info.Codec);
        Assert.Equal(rate, info.SampleRate);
        Finding implicitSbr = Assert.Single(report.Findings, f => f.Code == "aac.implicit-sbr");
        Assert.DoesNotContain("plays the core alone", implicitSbr.Message, StringComparison.Ordinal);
    }

    // The splitting of a long run of counted frames into records that fit a node's length, at frame boundaries.
    [Fact]
    public void RunsAreSplitIntoRecordsThatFit()
    {
        var splitter = new RunSplitter(100);
        var records = new List<(long Offset, long Length, long Frames)>();
        long at = 1000;
        foreach (int length in new[] { 40, 40, 40, 90, 10, 10, 100, 1 })
        {
            if (splitter.Add(at, length) is { } closed)
            {
                records.Add(closed);
            }

            at += length;
        }

        records.Add(splitter.Flush()!.Value);

        Assert.Equal([(1000L, 80L, 2L), (1080L, 40L, 1L), (1120L, 100L, 2L), (1220L, 10L, 1L), (1230L, 100L, 1L), (1330L, 1L, 1L)], records);
        Assert.Null(splitter.Flush());
        Assert.Equal([(5L, 4), (9L, 4), (13L, 2)], RunSplitter.Pieces(5, 10, 4).ToArray());
        Assert.Empty(RunSplitter.Pieces(5, 0));
    }

    // SBR data in one block of ten does not make the stream HE-AAC.
    [Fact]
    public void SbrDataInAFewBlocksOnlyIsReported()
    {
        Report report = Fixtures.AnalyzeMpeg("adts/sbr-in-one-block.aac");

        Assert.Equal("AAC LC", Assert.Single(report.Streams).Codec);
        Assert.Contains(report.Findings, f => f.Code == "aac.sbr-data-sparse");
        Assert.DoesNotContain(report.Findings, f => f.Code == "aac.implicit-sbr");
    }

    // The graph of bitrate over time keeps at most MaxSeconds entries whatever the sample rate; neighbors merge, and what they hold is
    // kept: the bits and the samples of the whole stream.
    [Theory]
    [InlineData(1, 100_000)]
    [InlineData(8000, 1_200_000)]
    public void BitrateGraphIsBoundedWhateverTheSampleRate(int sampleRate, int units)
    {
        var figures = new StreamFigures(sampleRate);
        for (int i = 0; i < units; i++)
        {
            figures.Add(i * 100L, 100, 1024);
        }

        List<Series> series = figures.Series(null, CancellationToken.None);
        Series overTime = series.Single(s => s.Name == "Bitrate Over Time");

        Assert.True(overTime.X.Length <= StreamFigures.MaxSeconds, $"{overTime.X.Length} entries");
        // Every unit has 100 bytes over 1024 samples: each entry that holds a unit has the stream's bitrate.
        double expected = Math.Round(100 * 8.0 * sampleRate / 1024 / 1000, MidpointRounding.ToEven);
        Assert.All(overTime.Values.ToArray().Where(v => v > 0), v => Assert.Equal(expected, v));
        double[] starts = overTime.X.ToArray();
        Assert.Equal(starts.Order(), starts);
    }

    private static List<byte[]> Frames(byte[] data)
    {
        var frames = new List<byte[]>();
        for (int at = 0; at + 7 <= data.Length;)
        {
            int length = ((data[at + 3] & 3) << 11) | (data[at + 4] << 3) | (data[at + 5] >> 5);
            frames.Add(data[at..(at + length)]);
            at += length;
        }

        return frames;
    }
}
