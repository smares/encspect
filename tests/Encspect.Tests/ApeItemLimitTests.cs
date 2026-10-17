using System.Buffers.Binary;
using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The first 1,024 items of an APE tag become nodes, the next ones are listed in blocks from the bytes kept for them (up to a
// budget), and the others are checked and counted (ape.items-limited). The findings about items are one per kind with a count.
public class ApeItemLimitTests
{
    private const int LongText = 5000;

    // An APEv2 tag with a footer and the declared item count, behind Layer III frames, holding these items (key, value, flags).
    private static byte[] File(int declaredCount, params (string Key, byte[] Value, uint Flags)[] items)
    {
        var body = new List<byte>();
        foreach ((string key, byte[] value, uint flags) in items)
        {
            body.AddRange(BitConverter.GetBytes((uint)value.Length));
            body.AddRange(BitConverter.GetBytes(flags));
            body.AddRange(Encoding.ASCII.GetBytes(key));
            body.Add(0);
            body.AddRange(value);
        }

        byte[] footer = new byte[32];
        "APETAGEX"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(8), 2000);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(12), (uint)(body.Count + 32));
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(16), (uint)declaredCount);
        byte[] frame = [0xFF, 0xFB, 0x10, 0x00, 0x00, 0x00, 0x01, 0x90, .. new byte[96]];
        return [.. Enumerable.Repeat(frame, 8).SelectMany(b => b), .. body, .. footer];
    }

    private static (string, byte[], uint) Small(int i) => (MemoryTests.ApeKey(i), Encoding.ASCII.GetBytes("v" + i), 0u);

    private static (string, byte[], uint)[] Smalls(int count) => [.. Enumerable.Range(0, count).Select(Small)];

    private static (string, byte[], uint) Long(string key) => (key, Encoding.ASCII.GetBytes(new string('x', LongText)), 0u);

    private static Report Analyze(byte[] data) => Analyzer.CreateDefault().Analyze("items.mp3", new MemoryStream(data));

    private static Node ItemsOf(Report report) => report.Root.Children.Single(c => c.Kind == NodeKind.Tag).Children.Single(c => c.Name == "Items");

    // Items after the first 1,024 stay reachable, in blocks, with the nodes the first ones have.
    [Fact]
    public void LaterItemsAreListedInBlocks()
    {
        Report report = Analyze(File(3000, Smalls(3000)));

        Node items = ItemsOf(report);
        Assert.Equal(3000, items.Children.Count);
        Node late = items.Children[2500];
        Assert.Equal(MemoryTests.ApeKey(2500), late.Name);
        Assert.Equal("v2500", late.Value?.AsText());
        Assert.Equal(["Size", "Flags", "Key", "Value"], late.Children.Select(c => c.Name));
        Assert.DoesNotContain(report.Findings, f => f.Code == "ape.items-limited");
    }

    // A text value too long to keep is not listed: it is checked and counted, with one finding.
    [Fact]
    public void ALongValuePastTheFirstItemsIsCountedNotListed()
    {
        Report report = Analyze(File(1027, [.. Smalls(1024), Long("zzlong1"), Small(1025), Long("zzlong2")]));

        Assert.Equal(1025, ItemsOf(report).Children.Count);
        Finding limited = Assert.Single(report.Findings, f => f.Code == "ape.items-limited");
        Assert.Equal(2, limited.Count);
        Assert.Contains("2 items are checked and counted but not listed", limited.Message, StringComparison.Ordinal);
        Assert.NotNull(limited.Path);
    }

    [Fact]
    public void OneItemThatIsNotListedIsSingular()
    {
        Report report = Analyze(File(1025, [.. Smalls(1024), Long("zzlong")]));

        Finding limited = Assert.Single(report.Findings, f => f.Code == "ape.items-limited");
        Assert.Equal(1, limited.Count);
        Assert.StartsWith("1 item is checked", limited.Message, StringComparison.Ordinal);
    }

    // The loop stops at an item that does not fit, and the items before it that were not listed are still reported.
    [Fact]
    public void TheFindingIsMadeWhenTheItemLoopStopsEarly()
    {
        (string, byte[], uint) bad = ("a\u0001", [], 0u);
        Report report = Analyze(File(1030, [.. Smalls(1024), Long("zzlong1"), Long("zzlong2"), bad]));

        Assert.Contains(report.Findings, f => f.Code == "ape.items-invalid");
        Assert.Equal(2, Assert.Single(report.Findings, f => f.Code == "ape.items-limited").Count);
    }

    [Fact]
    public void TheFindingIsMadeWhenTheCountIsOverstated()
    {
        Report report = Analyze(File(5000, [.. Smalls(1024), Long("zzlong1")]));

        Assert.Contains(report.Findings, f => f.Code == "ape.items-invalid");
        Assert.Equal(1, Assert.Single(report.Findings, f => f.Code == "ape.items-limited").Count);
    }

    // An Encoder item past the first 1,024 is still the writer's claim, whether the item is listed or not.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEncoderItemPastTheFirstItemsStillNamesTheWriter(bool isLong)
    {
        (string, byte[], uint) encoder = ("Encoder", Encoding.ASCII.GetBytes("Lavf60.16.100" + (isLong ? new string('\0', LongText) : string.Empty)), 0u);
        Report report = Analyze(File(1025, [.. Smalls(1024), encoder]));

        Assert.Contains("Lavf60.16.100", Assert.Single(report.Streams).Writer, StringComparison.Ordinal);
    }

    // Only the first few Encoder items become claims, and the writer is still named once.
    [Fact]
    public void ManyEncoderItemsStillGiveOneWriter()
    {
        (string, byte[], uint)[] items = [.. Enumerable.Range(0, 40).Select(i => ($"Encoder", Encoding.ASCII.GetBytes("Lavf60.16." + i), 0u))];
        Report report = Analyze(File(40, items));

        Assert.Single(report.Streams);
        Assert.Equal(39, Assert.Single(report.Findings, f => f.Code == "ape.duplicate-key").Count);
    }

    // The merged finding names the first few cases, so the second duplicate key is still shown.
    [Fact]
    public void AMergedFindingNamesItsFirstCases()
    {
        Report report = Analyze(File(6, ("alpha", "1"u8.ToArray(), 0u), ("beta", "2"u8.ToArray(), 0u), ("ALPHA", "3"u8.ToArray(), 0u), ("Beta", "4"u8.ToArray(), 0u),
            ("gamma", "5"u8.ToArray(), 0u), ("GAMMA", "6"u8.ToArray(), 0u)));

        Finding duplicates = Assert.Single(report.Findings, f => f.Code == "ape.duplicate-key");
        Assert.Equal(3, duplicates.Count);
        Assert.Contains("\"ALPHA\", \"Beta\", \"GAMMA\"", duplicates.Message, StringComparison.Ordinal);
    }
}
