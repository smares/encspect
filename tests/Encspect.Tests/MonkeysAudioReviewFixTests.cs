using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.MonkeysAudio;
using Encspect.Formats.MonkeysAudio.Decoding;
using Encspect.Host;

namespace Encspect.Tests;

// The Gate 3 review of Monkey's Audio (monkeys-audio-1 to -11): inputs built from a fixture's frames, each of which failed before the fix.
public class MonkeysAudioReviewFixTests
{
    private const string Stereo = "external-level-normal-16-stereo.ape";

    private static Report Verify(byte[] file, Analyzer? analyzer = null) =>
        (analyzer ?? Analyzer.CreateDefault()).Analyze("built.ape", new MemoryStream(file), new AnalysisOptions { Decoding = new DecodeOptions() });

    // monkeys-audio-1: the SDK counts the words of the frames from the first seek table entry, not from where the descriptor's sizes put the first frame.
    [Theory]
    [InlineData(-3)]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WordsAreCountedFromTheFirstSeekEntryWhenTheDescriptorDiffersByLessThanAWord(int difference)
    {
        byte[] file = File.ReadAllBytes(MonkeysAudioBuilder.FixturePath(Stereo));
        uint headerData = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), (uint)(headerData + difference));

        Report report = Verify(file);

        Assert.Contains(report.Findings, f => f.Code == "monkeys-audio.seek-table-start");
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.frame-invalid" or "monkeys-audio.frame-crc-mismatch" or "monkeys-audio.frame-misplaced" or "parse.failed" or "decode.failed");
        StreamVerification verification = report.Streams.Single().Verification!;
        ChecksumTally crc = verification.Checksums.Single(c => c.Name == "frame CRC-32");
        Assert.True(crc.Checked > 0);
        Assert.Equal(crc.Stored, crc.Checked);
        Assert.Equal(0, crc.Failed);
    }

    // monkeys-audio-2: findings about frames after the first 1,048,576 point at the node that stands for them.
    [Fact]
    public void FindingsOfFramesTheReportDoesNotListPointToFurtherFrames()
    {
        (byte[] header, byte[] headerData, _) = MonkeysAudioBuilder.Take(Stereo);
        const int count = (1 << 20) + 6;
        byte[] silent = MonkeysAudioBuilder.SilentFrame(64);
        byte[] damaged = [.. silent];
        damaged[3] ^= 1;
        var frames = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            frames.Add(i == count - 3 ? damaged : silent);
        }

        byte[] file = MonkeysAudioBuilder.Build(header, headerData, frames, 64, 64);
        // The seek entry of the last frame puts it far outside the data.
        int seekAt = MonkeysAudioBuilder.DescriptorLength + MonkeysAudioBuilder.HeaderLength + (4 * (count - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(seekAt), 0x7FFFFFF0);

        Report report = Verify(file);

        Finding misplaced = Assert.Single(report.Findings, f => f.Code == "monkeys-audio.frame-misplaced");
        Assert.NotNull(misplaced.Path);
        Assert.Equal("Further Frames", NodeAt(report, misplaced.Path).Name);
        Finding crc = Assert.Single(report.Findings, f => f.Code == "monkeys-audio.frame-crc-mismatch");
        Assert.NotNull(crc.Path);
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    private static Node NodeAt(Report report, IReadOnlyList<int> path)
    {
        Node node = report.Root;
        foreach (int index in path)
        {
            node = node.Children[index];
        }

        return node;
    }

    // monkeys-audio-3: "Bitrate Over Time" holds a bounded number of entries when every frame starts a new second.
    [Fact]
    public void TheBitrateOverTimeSeriesIsBoundedAtALowSampleRate()
    {
        (byte[] header, byte[] headerData, _) = MonkeysAudioBuilder.Take(Stereo);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 1);
        const int count = StreamFigures.MaxSeconds + 20_000;
        byte[] frame = MonkeysAudioBuilder.SilentFrame(64);
        byte[] file = MonkeysAudioBuilder.Build(header, headerData, [.. Enumerable.Repeat(frame, count)], 64, 64);

        Report report = Analyzer.CreateDefault().Analyze("slow.ape", new MemoryStream(file));

        Series perSecond = report.Streams.Single().Series.Single(s => s.Name == "Bitrate Over Time");
        Assert.InRange(perSecond.Values.Length, StreamFigures.MaxSeconds / 2, StreamFigures.MaxSeconds);
        Assert.Equal(0, perSecond.X.Span[0]);
        double[] x = perSecond.X.ToArray();
        Assert.Equal(x.Order(), x);
    }

    [Fact]
    public void TheSecondsOfTheSeriesMergeIntoWiderStepsAndKeepEveryBit()
    {
        var figures = new StreamFigures(1000);
        long total = 0;
        const int count = (3 * StreamFigures.MaxSeconds) + 5;
        for (int i = 0; i < count; i++)
        {
            long bits = 1000 * ((i % 7) + 1);
            figures.Add(i, bits, 1000);
            total += bits;
        }

        Series perSecond = figures.Series(null, CancellationToken.None).Single(s => s.Name == "Bitrate Over Time");

        Assert.True(perSecond.Values.Length <= StreamFigures.MaxSeconds);
        Assert.Equal(total, figures.Bits);
        double[] x = perSecond.X.ToArray();
        Assert.Equal(0, x[0]);
        Assert.Equal(4, x[1]);
        Assert.Equal(x.Order(), x);
    }

    // monkeys-audio-5: a 24-bit frame too large to hold is decoded again from its bytes, so the interim retry has no size limit.
    [Theory]
    [InlineData("external-interim-24-stereo.ape", 0L)]
    [InlineData("built-24-bit-frame-crc-changed.ape", 0L)]
    [InlineData("external-loud-24-stereo.ape", 0L)]
    public void ALargeFrameOf24BitsIsDecodedTwiceAndGivesTheResultOfAHeldOne(string name, long maxHeld)
    {
        byte[] file = File.ReadAllBytes(MonkeysAudioBuilder.FixturePath(name));
        Report held = Verify(file);
        var twice = new Analyzer(DefaultParsers.Tags, DefaultParsers.Formats, DefaultParsers.Codecs, [new MonkeysAudioDecoder(maxHeld)]);

        Report report = Verify(file, twice);

        StreamVerification expected = held.Streams.Single().Verification!;
        StreamVerification actual = report.Streams.Single().Verification!;
        Assert.Equal(expected.IsVerified, actual.IsVerified);
        Assert.Equal(expected.Checksums.Select(c => (c.Name, c.Stored, c.Checked, c.Failed)), actual.Checksums.Select(c => (c.Name, c.Stored, c.Checked, c.Failed)));
        Assert.Equal(held.Findings.Select(f => (f.Code, f.Count)), report.Findings.Select(f => (f.Code, f.Count)));
        Assert.Equal(held.Streams.Single().Verification!.DecodedSamples, actual.DecodedSamples);
    }

    // monkeys-audio-4: flagged silence is charged its fixed work only, so a silent file verifies where the old charge of 112 units a stereo sample stopped it.
    [Fact]
    public void LongSilenceVerifiesWithinABudgetThatTheOldChargeExceeded()
    {
        (byte[] header, byte[] headerData, _) = MonkeysAudioBuilder.Take("external-level-insane-16-stereo.ape");
        const int blocks = 10_000_000;
        byte[] frame = MonkeysAudioBuilder.SilentFrame(blocks);
        // 10 frames of 10 million samples: 3.2 billion units at 32 a sample, 11.2 billion at the old 112.
        byte[] file = MonkeysAudioBuilder.Build(header, headerData, [.. Enumerable.Repeat(frame, 10)], blocks, blocks);

        Report report = Analyzer.CreateDefault().Analyze("silent.ape", new MemoryStream(file), new AnalysisOptions { Decoding = new DecodeOptions { MaxWork = 1L << 32 } });

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.limit" or "decode.failed" or "parse.failed");
        Assert.True(report.Streams.Single().Verification!.IsVerified);
    }

    // monkeys-audio-10: an old header keeps the byte count it states, and the flag for a header created on decompression is reported there too.
    [Fact]
    public void AnOldHeaderWithTheCreatedHeaderFlagShowsTheStoredCountAndReportsTheUnusedHeaderData()
    {
        byte[] file = File.ReadAllBytes(MonkeysAudioBuilder.FixturePath("legacy-3860-normal-16-stereo-frames.ape"));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8)) | 32));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 100);

        Report report = Analyzer.CreateDefault().Analyze("old.ape", new MemoryStream(file));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "monkeys-audio.wav-header-unused");
        Assert.NotNull(finding.Path);
        Node node = NodeAt(report, finding.Path);
        Assert.Equal("WAV Header Data Bytes", node.Name);
        Assert.Equal(100L, node.Value!.AsInteger());
    }

    // monkeys-audio-9: versions 3.89x are Rice-coded.
    [Fact]
    public void AFrameOfVersion3891IsNotRangeCoded()
    {
        byte[] file = File.ReadAllBytes(MonkeysAudioBuilder.FixturePath("legacy-3890-normal-16-mono-overflow-exactly-16.ape"));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 3895);

        Report report = Analyzer.CreateDefault().Analyze("old.ape", new MemoryStream(file));

        Assert.DoesNotContain(AllNodes(report.Root), n => n.Name == "Range-Coded Data");
        Assert.Contains(AllNodes(report.Root), n => n.Name == "Coded Data");
    }

    private static IEnumerable<Node> AllNodes(Node node)
    {
        yield return node;
        foreach (Node child in node.Children)
        {
            foreach (Node descendant in AllNodes(child))
            {
                yield return descendant;
            }
        }
    }
}
