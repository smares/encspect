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
