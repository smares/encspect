using Encspect.Core;
using Encspect.Formats.TrueHd;
using Encspect.Host;

namespace Encspect.Tests;

// One damaged or unusual file for each finding the TrueHD parser emits that no fixture's manifest lists, built from an encoder-written fixture
// with a bit flipped or bytes added, the checks that the change leaves alone worked out again, and the finding that must come out.
// FindingCoverageTests checks that every code the parser emits is asserted somewhere.
public class TrueHdFindingTests
{
    private const string Tiny = "built-tiny-truehd-stereo.thd";

    private const string OddLength = "external-truehd-stereo-48k-24-odd-length.thd";

    // The first substream of the first unit starts after the unit header (4 bytes), the 28 bytes of the major sync and the 2 of the directory.
    private const int FirstSubstream = 34;

    private static readonly Dictionary<string, Func<byte[]>> Crafts = new()
    {
        // The flips are found by trying every bit of the substream, and each is named for what the reader then meets.
        ["truehd.block-invalid"] = () => Flip(Tiny, 0, 140),
        ["truehd.channel-assignment"] = () => Flip(Tiny, 0, 120),
        ["truehd.substream-end-invalid"] = () => Flip(Tiny, 0, 34),
        ["truehd.unsupported"] = () => Flip(Tiny, 0, 15),
        ["truehd.major-sync-invalid"] = () =>
        {
            // The first of the stream's major syncs states a sampling rate code of 15; a later one lets the stream be read.
            byte[] data = Fixture(OddLength);
            data[8] |= 0xF0;
            Refresh(data, 0, Units(data)[0].Length, hasSync: true);
            return data;
        },
        ["truehd.no-units"] = () => new byte[100],
        // The end of stream marker of the last unit drops all it can: more samples than the unit holds.
        ["truehd.unit-short"] = () =>
        {
            byte[] data = Fixture(OddLength);
            (int offset, int length) = Units(data)[^1];
            data[offset + length - 6] = 0xD2;
            data[offset + length - 5] = 0x34;
            data[offset + length - 4] = 0xFF;
            data[offset + length - 3] = 0xFF;
            Refresh(data, offset, length, hasSync: false);
            return data;
        },
        ["truehd.units-after-end-of-stream"] = () =>
        {
            byte[] data = Fixture(OddLength);
            List<(int Offset, int Length)> units = Units(data);
            return [.. data, .. data[units[1].Offset..(units[2].Offset + units[2].Length)]];
        },
        ["truehd.data-after-tag"] = () =>
        {
            byte[] data = Fixture(Tiny);
            List<(int Offset, int Length)> units = Units(data);
            int first = units[4].Offset;
            byte[] seek = Id3v24(Frame("SEEK", [0, 0, (byte)(first >> 8), (byte)first]));
            byte[] second = Id3v24(Frame("TIT2", [3, .. "Second tag"u8]));
            return [.. seek, .. data[..first], .. second, .. data[first..]];
        },
    };

    public static TheoryData<string> Codes => [.. Crafts.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Codes))]
    public void CraftedFileRaisesTheFinding(string code)
    {
        var options = new AnalysisOptions { ForcedFormat = "thd" };
        Report report = Analyzer.CreateDefault().Analyze("crafted.thd", new MemoryStream(Crafts[code]()), options);

        Assert.Contains(report.Findings, f => f.Code == code);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        TestFiles.CheckInsideParents(report.Root);
    }

    // A unit that waits for a major sync after damage and is cut by the end of the data is a cut unit as well: its node ends where the data does.
    [Fact]
    public void ACutUnitAfterDamageEndsWhereTheDataDoes()
    {
        byte[] data = Flip(Tiny, 0, 34);
        List<(int Offset, int Length)> units = Units(data);
        byte[] cut = data[..(units[^1].Offset + 20)];

        Report report = Analyzer.CreateDefault().Analyze("cut.thd", new MemoryStream(cut));

        Assert.Contains(report.Findings, f => f.Code == "truehd.unit-cut");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        TestFiles.CheckInsideParents(report.Root);
        Node last = report.Root.Children.Single(c => c.Kind == NodeKind.Stream).Children[^1];
        Assert.Equal(cut.Length, last.End);
    }

    // The lossless checks a decoder reports are the ones the parser declared: for the substreams of the sync the decoder was opened with, which is
    // not the stream's first sync where its first units cannot be read, and not a later one, which the decoder does not follow.
    [Theory]
    [InlineData("truehd-lossless-checks-of-a-stream-that-adds-a-substream.bin")]
    [InlineData("truehd-lossless-checks-of-a-stream-whose-first-unit-is-not-readable.bin")]
    public void TheLosslessChecksADecoderReportsAreTheOnesTheParserDeclared(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", name));

        Report report = Analyzer.CreateDefault().Analyze("changes.thd", new MemoryStream(data), new AnalysisOptions { Decoding = new DecodeOptions() });

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Contains(report.Streams.Single().Verification!.Checksums, c => c.Name == "lossless check, substream 0");
    }

    private static Analyzer With(Func<TrueHdParser> parser) =>
        new(DefaultParsers.Tags, [.. DefaultParsers.Formats.Select(f => f is TrueHdParser ? parser() : f)], DefaultParsers.Codecs, DefaultParsers.Decoders);

    // More units than the analysis keeps as records (the limit is lowered to 20 here, 2,097,152 in all): the rest are read, decoded and counted, have one
    // node that counts them, and what the parser says about them points to it (the decoder's lossless check finding, at unit 32, to the stream).
    [Fact]
    public void UnitsPastTheLimitOfRecordsAreCountedByOneNodeAndFindingsAboutThemPointToIt()
    {
        byte[] data = Fixture("built-lossless-check-mismatch.thd");
        var options = new AnalysisOptions { Decoding = new DecodeOptions() };
        Report whole = Analyzer.CreateDefault().Analyze("whole.thd", new MemoryStream(data), options);
        Report limited = With(() => new TrueHdParser { RecordLimit = 20 }).Analyze("limited.thd", new MemoryStream(data), options);

        Node stream = limited.Root.Children.Single(c => c.Kind == NodeKind.Stream);
        Assert.Equal(20, stream.Children.Count(c => c.Name == "Access Unit"));
        Node counted = stream.Children.Single(c => c.Name == "Counted Access Units");
        Assert.Equal(Units(data).Count - 20, counted.Value!.AsInteger());
        Assert.Equal(Units(data)[20].Offset, counted.Offset);
        Assert.Equal(data.Length, counted.End);
        TestFiles.CheckInsideParents(limited.Root);

        Finding limit = Assert.Single(limited.Findings, f => f.Code == "truehd.units-limit");
        Assert.Equal(Severity.Info, limit.Severity);
        Assert.Equal("Counted Access Units", limit.Node!.Name);
        Assert.NotNull(limit.Path);
        // The decoder's finding about such a unit points to the stream, which no unit's record is needed for.
        Finding failed = Assert.Single(limited.Findings, f => f.Code == "truehd.lossless-check-failed");
        Assert.Equal("Dolby TrueHD", failed.Node!.Name);
        Assert.NotNull(failed.Path);
        Assert.Equal(whole.Findings.Count, limited.Findings.Count - 1);
        Assert.Equal(whole.Streams.Single().Samples, limited.Streams.Single().Samples);
        Assert.All(limited.Findings, f => Assert.NotNull(f.Path));
    }

    // A unit with a record that keeps no detail (the tree lists the detail of the first units and of some units that carry a major sync): a finding about
    // its restart header's lossless check points to the unit instead of to a field the tree does not hold.
    [Fact]
    public void AFindingAboutAUnitWithoutDetailPointsToTheUnit()
    {
        byte[] data = Fixture("built-lossless-check-mismatch.thd");
        var options = new AnalysisOptions { Decoding = new DecodeOptions() };
        Report whole = Analyzer.CreateDefault().Analyze("whole.thd", new MemoryStream(data), options);
        Report limited = With(() => new TrueHdParser { FullUnitLimit = 8, SparseUnitLimit = 1 }).Analyze("limited.thd", new MemoryStream(data), options);

        Assert.Equal("Lossless Check", whole.Findings.Single(f => f.Code == "truehd.lossless-check-failed").Node!.Name);
        Finding failed = limited.Findings.Single(f => f.Code == "truehd.lossless-check-failed");
        Assert.NotNull(failed.Path);
        Assert.Equal("Access Unit", failed.Node!.Name);
        Assert.Equal(Units(data)[32].Offset, failed.Node.Offset);
        Assert.Equal(Units(data)[32].Length, failed.Node.Length);
        Assert.Equal(whole.Findings.Count, limited.Findings.Count);
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "truehd", name));

    private static List<(int Offset, int Length)> Units(byte[] data)
    {
        var units = new List<(int, int)>();
        for (int at = 0; at + 4 <= data.Length;)
        {
            int length = (((data[at] & 0xF) << 8) | data[at + 1]) * 2;
            units.Add((at, length));
            at += length;
        }

        return units;
    }

    // The file with one bit of the first substream of a unit flipped and the unit's checks worked out again.
    internal static byte[] Flip(string name, int unit, int bit)
    {
        byte[] data = Fixture(name);
        (int offset, int length) = Units(data)[unit];
        bool hasSync = data[offset + 4] == 0xF8;
        int start = offset + 4 + (hasSync ? 28 : 0) + 2;
        data[start + (bit >> 3)] ^= (byte)(0x80 >> (bit & 7));
        Refresh(data, offset, length, hasSync);
        return data;
    }

    // Works out the major sync CRC, the substream parity byte and CRC-8, and the parity nibble of a unit of one substream with check data.
    internal static void Refresh(byte[] data, int offset, int length, bool hasSync)
    {
        int syncLength = hasSync ? 28 : 0;
        if (hasSync)
        {
            ushort crc = Checks.MajorSync(data.AsSpan(offset + 4, syncLength));
            data[offset + 4 + syncLength - 2] = (byte)(crc >> 8);
            data[offset + 4 + syncLength - 1] = (byte)crc;
        }

        int start = offset + 4 + syncLength + 2;
        int end = offset + length;
        ReadOnlySpan<byte> body = data.AsSpan(start, end - 2 - start);
        byte parity = 0xA9;
        foreach (byte b in body)
        {
            parity ^= b;
        }

        data[end - 2] = parity;
        data[end - 1] = Checks.Substream(body);

        data[offset] &= 0x0F;
        int x = 0;
        foreach (byte b in data.AsSpan(offset, 4).ToArray().Concat(data.AsSpan(offset + 4 + syncLength, 2).ToArray()))
        {
            x ^= b;
        }

        data[offset] |= (byte)((((x >> 4) ^ (x & 0xF)) ^ 0xF) << 4);
    }

    private static byte[] Syncsafe(int value) => [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    private static byte[] Frame(string id, byte[] data) => [.. System.Text.Encoding.ASCII.GetBytes(id), .. Syncsafe(data.Length), 0, 0, .. data];

    private static byte[] Id3v24(byte[] frames) => [.. "ID3"u8, 4, 0, 0, .. Syncsafe(frames.Length), .. frames];
}
