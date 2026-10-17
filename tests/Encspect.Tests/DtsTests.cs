using Encspect.Core;
using Encspect.Formats.Dts;
using Encspect.Host;

namespace Encspect.Tests;

// What the DTS parser promises that no one fixture shows: the sync search against the other formats' files and
// the junk in front of its own, and the four forms of the core stream reading to the same fields.
public class DtsTests
{
    private static int Sniff(byte[] data) => new DtsParser().Sniff(new SniffContext(new ByteReader(new MemoryStream(data)), "dts")).Class;

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "dts", name));

    // Every other fixture sniffs 0, so DTS never competes for them (plan, "Format rollout"); the DTS fixtures are taken as DTS, or as MP4 when
    // an MP4 file holds them, which DtsFixtureTests checks with each manifest's format.
    [Fact]
    public void NoOtherFormatsFileSniffsAsDts()
    {
        string dts = Path.Combine(Fixtures.FixtureDirectory, "dts") + Path.DirectorySeparatorChar;
        string matroska = Path.Combine(Fixtures.FixtureDirectory, "matroska") + Path.DirectorySeparatorChar;
        var parser = new DtsParser();
        int checkedFiles = 0;
        foreach (string path in Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*", SearchOption.AllDirectories))
        {
            if (path.StartsWith(dts, StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal) || Path.GetFileName(path).StartsWith("dts-", StringComparison.Ordinal)
                // The DTS frames in a Matroska file are DTS to the DTS parser, as the ones in an MP4 file in dts/ are; the file is Matroska to the host.
                || (path.StartsWith(matroska, StringComparison.Ordinal) && Path.GetFileName(path).Contains("dts", StringComparison.Ordinal)))
            {
                if (path.StartsWith(matroska, StringComparison.Ordinal) && !path.EndsWith(".json", StringComparison.Ordinal))
                {
                    Assert.Equal("Matroska", Encspect.Host.Analyzer.CreateDefault().AnalyzeFile(path).Root.Value?.ToString());
                }

                continue;
            }

            using FileStream stream = File.OpenRead(path);
            Assert.True(parser.Sniff(new SniffContext(new ByteReader(stream))).Class == 0, path);
            checkedFiles++;
        }

        Assert.True(checkedFiles > 500, $"{checkedFiles} files");
    }

    [Theory]
    [InlineData("core-16be.dts")]
    [InlineData("core-16le.dts")]
    [InlineData("core-14be.dts")]
    [InlineData("core-14le.dts")]
    [InlineData("exss-lossless.dts")]
    [InlineData("external-ffmpeg-5.1-48k-768k.dts")]
    public void AChainAtTheStartTakes93(string name)
    {
        Assert.Equal(93, Sniff(Fixture(name)));
    }

    [Fact]
    public void LeadingZeroBytesAreSkippedAndOtherJunkTakes50()
    {
        byte[] data = Fixture("core-16be.dts");
        Assert.Equal(93, Sniff([.. new byte[100], .. data]));
        Assert.Equal(50, Sniff([.. Enumerable.Range(0, 30).Select(i => (byte)(0x80 | i)), .. data]));
    }

    private static string FormatOf(byte[] data, IEnumerable<IFormatParser>? parsers = null) =>
        (parsers is null ? Analyzer.CreateDefault() : new Analyzer(parsers)).Analyze("mixed", new MemoryStream(data)).Root.Value?.ToString() ?? string.Empty;

    private static byte[] Junk() => [.. Enumerable.Range(0, 30).Select(i => (byte)(0x80 | i))];

    // A DTS stream ranks below MPEG audio, ADTS and the containers when it starts further in, so it cannot take an MP3 file that has it
    // appended; and the free-format chains MPEG audio finds in DTS data are no frames, so junk in front of DTS data does not give the
    // file to MPEG audio.
    [Fact]
    public void MpegAudioFollowedByDtsStaysMpegAudio()
    {
        byte[] mp3 = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "mpeg", "external-blade-stereo.mp3"));
        foreach (string name in new[] { "external-ffmpeg-stereo-44k-256k.dts", "core-16be.dts" })
        {
            Assert.Equal("MPEG Audio", FormatOf([.. mp3, .. Fixture(name)]));
        }
    }

    [Theory]
    [InlineData("core-16be.dts")]
    [InlineData("junk-around.dts")]
    [InlineData("external-ffmpeg-5.1-48k-768k.dts")]
    [InlineData("external-ffmpeg-stereo-44k-256k.dts")]
    [InlineData("14le-x96.dts")]
    [InlineData("exss-lossless.dts")]
    public void DtsAfterJunkIsStillDts(string name)
    {
        Assert.Equal("DTS", FormatOf([.. Junk(), .. Fixture(name)]));
    }

    // Extension substream sync words with headers of a megabyte, which each point at the next one, are no units: the CRC16 of a substream
    // is checked wherever one follows a core frame or starts a unit, so the bytes after the last frame stay trailing data.
    [Fact]
    public void ExtensionSubstreamHeadersThatFailTheirCrcAfterTheLastFrameAreTrailingData()
    {
        byte[] header = (((new System.Numerics.BigInteger(0x64582025) << 43) | (System.Numerics.BigInteger.One << 32) | (4095L << 20) | 0xFFFFF) << 5).ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] flood = [.. Enumerable.Range(0, 1 << 17).SelectMany(_ => header.Concat(new byte[6]))];
        byte[] core = Fixture("core-16be.dts");
        Report report = Analyzer.CreateDefault().Analyze("flood.dts", new MemoryStream([.. core[..1536], .. flood]));
        Assert.Single(report.Streams);
        Assert.Equal(["dts.trailing-data"], report.Findings.Select(f => f.Code));
    }

    // A change of the configuration ends the stream and starts another, as for the other formats, also where the configuration returns; the
    // changes are one finding with their number.
    [Fact]
    public void AConfigurationThatReturnsStartsAStream()
    {
        byte[] frame = Fixture("core-16be.dts")[..512];
        var data = new List<byte>();
        for (int i = 0; i < 12; i++)
        {
            byte[] copy = [.. frame];
            SetBits(copy, 60, 6, i % 2 == 0 ? 2 : 0);
            data.AddRange(copy);
        }

        Report report = Analyzer.CreateDefault().Analyze("alternating.dts", new MemoryStream([.. data]));
        Assert.Equal(12, report.Streams.Count);
        Assert.Equal(["2/0", "1/0"], report.Streams.Take(2).Select(s => s.ChannelMode));
        Finding changed = Assert.Single(report.Findings, f => f.Code == "dts.stream-changed");
        Assert.Equal(11, changed.Count);
        Assert.DoesNotContain("limited", changed.Message, StringComparison.Ordinal);
        Assert.Equal(512L, report.Streams[0].Samples);
    }

    // Extension substream headers that omit the static fields before any header that states them have no figures of their own: they
    // belong to the stream the first header with the fields starts (a stream of its own would hold no samples), and the stream does not change.
    [Fact]
    public void UnitsBeforeTheFirstStaticFieldsJoinTheStreamAfterThem()
    {
        byte[] source = Fixture("exss-only-static-first-only.dts");
        byte[] with = source[..60];
        byte[] without = source[60..112];
        byte[] data = [.. without, .. without, .. with, .. without];
        Report report = Analyzer.CreateDefault().Analyze("late.dts", new MemoryStream(data));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(48000, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
        Assert.Equal(2 * 512L, stream.Samples);
        Assert.DoesNotContain(report.Findings, f => f.Code == "dts.stream-changed");
    }

    // A unit without asset figures matches the configuration of its core and structure only: core frames followed by units that add a lossless
    // extension (never stating the static fields) are two streams, and the units that then state the fields continue the second.
    [Fact]
    public void UnitsWithoutFiguresDoNotJoinAStreamOfAnotherStructure()
    {
        byte[] source = Fixture("exss-static-first-only.dts");
        byte[] with = source[..704];
        byte[] without = source[704..1400];
        byte[] core = Fixture("core-16be.dts")[..512];
        byte[] data = [.. core, .. core, .. core, .. without, .. without, .. without];
        Report report = Analyzer.CreateDefault().Analyze("core-then-partial.dts", new MemoryStream(data));
        Assert.Equal(["DTS", "DTS-HD Master Audio"], report.Streams.Select(s => s.Codec));
        Assert.Equal(3 * 512L, report.Streams[0].Samples);
        Assert.Equal(1, Assert.Single(report.Findings, f => f.Code == "dts.stream-changed").Count);

        data = [.. data, .. with, .. without];
        report = Analyzer.CreateDefault().Analyze("core-partial-static.dts", new MemoryStream(data));
        Assert.Equal(["DTS", "DTS-HD Master Audio"], report.Streams.Select(s => s.Codec));
        Assert.Equal(3 * 512L, report.Streams[0].Samples);
        Assert.Equal(5 * 512L, report.Streams[1].Samples);
        Assert.Equal(1, Assert.Single(report.Findings, f => f.Code == "dts.stream-changed").Count);
    }

    // The change that would start stream 65 starts none: that unit and every unit after it, also of the last stream's configuration, are in no
    // stream, so the last stream's figures are those of its own units, and `dts.streams-limited` counts them. The changes are still counted.
    [Fact]
    public void PastTheStreamLimitNoUnitIsInAStream()
    {
        byte[] frame = Fixture("core-16be.dts")[..512];
        var data = new List<byte>();
        for (int i = 0; i < 70; i++)
        {
            byte[] copy = [.. frame];
            SetBits(copy, 60, 6, i % 2 == 0 ? 2 : 0);
            data.AddRange(copy);
        }

        Report report = Analyzer.CreateDefault().Analyze("limit.dts", new MemoryStream([.. data]));
        Assert.Equal(64, report.Streams.Count);
        StreamInfo last = report.Streams[^1];
        Assert.Equal("1/0", last.ChannelMode);
        Assert.Equal(1, last.Channels);
        Assert.Equal(512L, last.Samples);
        Finding changed = Assert.Single(report.Findings, f => f.Code == "dts.stream-changed");
        Assert.Equal(Severity.Info, changed.Severity);
        Assert.Equal(69, changed.Count);
        Finding limited = Assert.Single(report.Findings, f => f.Code == "dts.streams-limited");
        Assert.Equal(Severity.Warning, limited.Severity);
        Assert.Equal(6, limited.Count);
    }

    // A last stream that has no asset figures yet is completed by a unit of its own structure only: here by none, so the unit of an LBR
    // asset is in no stream and the stream stays the lossless one.
    [Fact]
    public void ALastStreamWithoutFiguresIsNotCompletedByAnotherStructure()
    {
        byte[] core = Fixture("core-16be.dts")[..512];
        byte[] partial = Fixture("exss-static-first-only.dts")[704..1400];
        var data = new List<byte>();
        for (int i = 0; i < 63; i++)
        {
            byte[] copy = [.. core];
            SetBits(copy, 60, 6, i % 2 == 0 ? 2 : 0);
            data.AddRange(copy);
        }

        data.AddRange(partial);
        data.AddRange(partial);
        data.AddRange(Fixture("exss-only-static-first-only.dts")[..60]);
        Report report = Analyzer.CreateDefault().Analyze("limit-partial.dts", new MemoryStream([.. data]));
        Assert.Equal(64, report.Streams.Count);
        Assert.Equal("DTS-HD Master Audio", report.Streams[^1].Codec);
        Assert.Equal(Severity.Info, Assert.Single(report.Findings, f => f.Code == "dts.stream-changed").Severity);
        Finding limited = Assert.Single(report.Findings, f => f.Code == "dts.streams-limited");
        Assert.Equal(1, limited.Count);
    }

    // Units of an extension substream index that has not stated its static fields yet still give the rate of their LBR header: no stream
    // has a sampling frequency of 0 Hz.
    [Fact]
    public void NoStreamHasAZeroSampleRate()
    {
        byte[] data = Convert.FromBase64String("ZFggJQADYAd4AICAB8EAXwBwgQA/AAAAAADiTQqAGSECDAMAAAEbAAD6APoAAAAAAAAAAAAAAAAAAAAAZFggJQADYAd4AICAB8EAXwBwgQA/AAAAAADiTQqAGSECDAMAAAEbAAD6APoAAAAAAAAAAAAAAAAAAAAAZFggJQADYAd4AICAB8EAXwBwgQA/AAAAAADiTQqAGSECDAMAAAEbAAD6APoAAAAAAAAAAAAAAAAAAAAAZFggJQBCYAZgAfAgIAfgAAAA73cKgBkhAgwDAAABGwAA+gD6AAAAAAAAAAAAAAAAAAAAAGRYICUAQmAGYAHwICAH4AAAAO93CoAZIQIMAwAAARsAAPoA+gAAAAAAAAAAAAAAAAAAAABkWCAlAEJgBmAB8CAgB+AAAADvdwqAGSECDAMAAAEbAAD6APoAAAAAAAAAAAAAAAAAAAAA");
        Report report = Analyzer.CreateDefault().Analyze("index1.dts", new MemoryStream(data));
        Assert.NotEmpty(report.Streams);
        Assert.All(report.Streams, s => Assert.Equal(48000, s.SampleRate));
    }

    // Units without samples have no bitrate: they stay out of the minimum, the average and the series of the stream they join.
    [Fact]
    public void UnitsWithoutSamplesHaveNoBitrate()
    {
        byte[] source = Fixture("exss-only-static-first-only.dts");
        byte[] data = [.. source[60..112], .. source[60..112], .. source[..60], .. source[60..112]];
        Report report = Analyzer.CreateDefault().Analyze("late.dts", new MemoryStream(data));
        StreamInfo stream = Assert.Single(report.Streams);
        double[] values = stream.Series.First(x => x.Name == "Bitrate").Values.ToArray();
        Assert.Equal(2, values.Length);
        Assert.All(values, v => Assert.True(v > 0));
    }

    private static void SetBits(byte[] data, int start, int width, int value)
    {
        for (int i = 0; i < width; i++)
        {
            int bit = start + i;
            int on = (value >> (width - 1 - i)) & 1;
            data[bit >> 3] = (byte)((data[bit >> 3] & ~(0x80 >> (bit & 7))) | (on << (7 - (bit & 7))));
        }
    }

    // ADTS at the start takes 91 and DTS further in 50, whichever comes first in the list of parsers.
    [Fact]
    public void AdtsFollowedByDtsIsAdtsInEitherOrderOfTheParsers()
    {
        byte[] adts = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "adts", "cbr.aac"));
        byte[] data = [.. adts, .. Fixture("core-16be.dts")];
        Assert.Equal("AAC", FormatOf(data));
        Assert.Equal("AAC", FormatOf(data, DefaultParsers.Formats.Reverse()));
    }

    // Frames that end exactly at the end of the data are a chain, however few; a frame followed by the start of another that the data
    // does not finish is not, nor is a sync word in a stream whose frames do not follow it.
    [Fact]
    public void LessThanAChainOfFramesIsNoDts()
    {
        byte[] data = Fixture("core-16be.dts");
        Assert.Equal(93, Sniff(data[..512]));
        Assert.Equal(0, Sniff(data[..700]));
        Assert.Equal(0, Sniff([.. data[..12], .. new byte[1000]]));
        Assert.Equal(0, Sniff([]));
        Assert.Equal(93, Sniff(data[..1536]));
    }

    // The sync words of every form begin with a byte that the other formats' sync words allow only with a second byte DTS does not
    // use, so no chain is two formats': MPEG audio's is 0xFF and 0xFx with x from 2 to 15 in the first four bits of the second byte
    // (0xFF 0xFE or 0xFF 0xFA are MPEG headers, 0xFF 0x1F is not), AC-3's is 0x0B 0x77.
    [Fact]
    public void SyncWordsOfTheOtherFormatsAreNotDts()
    {
        foreach (string name in new[] { "ac3/external-ffmpeg-5.1-448k.ac3", "mpeg/external-blade-flags.mp3" })
        {
            Assert.Equal(0, Sniff(File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name))));
        }
    }

    // The same frame read from each form gives the same fields; the frame size and the targeted bit rate differ by the form's
    // padding, so they are left out.
    [Fact]
    public void TheFourFormsReadTheSameFields()
    {
        static List<string> Fields(string name)
        {
            Report report = Fixtures.AnalyzeMpeg("dts/" + name);
            Node frame = report.Streams.Single().Nodes.Single().Children.First(c => c.Kind == NodeKind.Frame);
            var lines = new List<string>();
            Walk(frame, "", lines);
            return lines;
        }

        static void Walk(Node node, string path, List<string> lines)
        {
            string here = path + "/" + node.Name;
            if (node.Name is "Frame Size" or "Bit Rate" or "Sync Word" or "Word Size" or "IsBigEndian" or "Synchronization" or "Core Audio Data")
            {
                return;
            }

            if (node.Children.Count == 0)
            {
                lines.Add($"{here}={node.Value}");
            }

            foreach (Node child in node.Children)
            {
                Walk(child, here, lines);
            }
        }

        List<string> expected = Fields("core-16be.dts");
        Assert.True(expected.Count > 40, $"{expected.Count} fields");
        foreach (string form in new[] { "core-16le.dts", "core-14be.dts", "core-14le.dts" })
        {
            Assert.Equal(expected, Fields(form));
        }
    }

    // Fixtures written in the little endian 16-bit or a 14-bit form, by their names.
    private static bool IsOtherForm(string name) =>
        new[] { "core-14", "core-16le", "14be-", "14le-", "16le-", "le16-", "core-odd-size-14", "core-odd-size-16le", "termination-14", "termination-16le" }.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    public static TheoryData<string> BigEndianInputs()
    {
        var data = new TheoryData<string>();
        string root = Fixtures.FixtureDirectory;
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "dts")).Concat(Directory.EnumerateFiles(Path.Combine(root, "fuzz"), "dts-*.bin"))
            .Where(p => !p.EndsWith(".json", StringComparison.Ordinal) && !IsOtherForm(Path.GetFileName(p)))
            .Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
        }

        return data;
    }

    // In a frame of the 16-bit big endian form, where a bit of the file is a bit of the stream, a field starts where the one before it ends or
    // later, whatever the sizes in a damaged header say. Fields that describe the same bytes again (the word size of a sync word, the flags
    // of a byte) are the exception, and so are the 14-bit and little endian forms, whose fields share words.
    [Theory]
    [MemberData(nameof(BigEndianInputs))]
    public void FieldsOfAFrameFollowOneAnother(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        foreach (Node frame in report.Streams.SelectMany(s => s.Nodes).Distinct().SelectMany(n => n.Children).Where(c => c.Kind == NodeKind.Frame))
        {
            CheckOrder(frame, frame);
        }
    }

    private static void CheckOrder(Node node, Node frame)
    {
        long previousStart = long.MinValue;
        long previousEnd = long.MinValue;
        foreach (Node child in node.Children.Where(c => c.Ranges is null))
        {
            long start = (child.Offset * 8) + (child.BitOffset ?? 0);
            long end = child.BitLength is { } bits ? start + bits : child.End * 8;
            bool sameBytes = node.Children.All(c => c.BitOffset is null && c.Offset == child.Offset) || (start == previousStart && end == previousEnd);
            Assert.True(start >= previousEnd || sameBytes, $"{child.Name} at bit {start} of the file starts before the field before it ends, at {previousEnd} (under {node.Name} of the frame at {frame.Offset}).");
            previousStart = start;
            previousEnd = end;
        }

        foreach (Node child in node.Children)
        {
            CheckOrder(child, frame);
        }
    }
}
