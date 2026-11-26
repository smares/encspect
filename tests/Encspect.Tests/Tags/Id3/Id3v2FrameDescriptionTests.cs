using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Tags.Id3;

// A frame cites the specification of the tag's own version, with the section that version gives it
// (checked against the documents id3.org serves, which scripts/fixtures/id3_specs.py pins).
public class Id3v2FrameDescriptionTests
{
    private static byte[] Syncsafe(int value) => [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    private static byte[] Frame(int major, string id, byte[] data) => major switch
    {
        2 => [.. Encoding.ASCII.GetBytes(id), 0, (byte)(data.Length >> 8), (byte)data.Length, .. data],
        3 => [.. Encoding.ASCII.GetBytes(id), 0, 0, (byte)(data.Length >> 8), (byte)data.Length, 0, 0, .. data],
        _ => [.. Encoding.ASCII.GetBytes(id), .. Syncsafe(data.Length), 0, 0, .. data],
    };

    private static readonly byte[] Text = [0, (byte)'x'];

    private static Dictionary<string, string?> Descriptions(int major, params string[] ids)
    {
        byte[] body = [.. ids.SelectMany(id => Frame(major, id, Text)), .. new byte[8]];
        byte[] tag = [.. "ID3"u8.ToArray(), (byte)major, 0, 0, .. Syncsafe(body.Length), .. body];
        Report report = Analyzer.CreateDefault().Analyze("test.mp3", new MemoryStream(tag));
        Node tagNode = report.Root.Children.Single(n => n.Kind == NodeKind.Tag && n.Name == "ID3v2");
        return tagNode.Children.Where(n => ids.Contains(n.Name)).ToDictionary(n => n.Name, n => n.Description);
    }

    [Fact]
    public void AnId3v22FrameCitesId3v22AndItsThreeLetterIdsSection()
    {
        Dictionary<string, string?> d = Descriptions(2, "TT2", "TYE", "TXX", "WAF", "COM", "XYZ");

        Assert.Equal("The ID3v2.2 form of TIT2. Title of the track or piece (ID3v2.2 section 4.2.1).", d["TT2"]);
        Assert.Equal("The ID3v2.2 form of TYER. Year of the recording; ID3v2.4 replaced it with TDRC (ID3v2.2 section 4.2.1; ID3v2.4.0 changes section 4).", d["TYE"]);
        Assert.Contains("(ID3v2.2 section 4.2.2)", d["TXX"]);
        Assert.Contains("(ID3v2.2 section 4.3.1)", d["WAF"]);
        Assert.Contains("(ID3v2.2 section 4.11)", d["COM"]);
        Assert.Contains("(ID3v2.2 section 3.2)", d["XYZ"]);
        Assert.All(d.Values, v => Assert.DoesNotContain("ID3v2.4.0 frames", v));
    }

    [Fact]
    public void AnId3v23FrameCitesId3v23AndTheFramesThatVersionDoesNotDefineSaySo()
    {
        Dictionary<string, string?> d = Descriptions(3, "TIT2", "TXXX", "COMM", "AENC", "TYER", "TDRC", "XYZ1");

        Assert.Equal("Title of the track or piece (ID3v2.3.0 section 4.2.1).", d["TIT2"]);
        Assert.Contains("(ID3v2.3.0 section 4.2.2)", d["TXXX"]);
        Assert.Contains("(ID3v2.3.0 section 4.11)", d["COMM"]);
        Assert.Contains("(ID3v2.3.0 section 4.20)", d["AENC"]);
        Assert.Equal("Year of the recording; ID3v2.4 replaced it with TDRC (ID3v2.3.0 section 4.2.1; ID3v2.4.0 changes section 4).", d["TYER"]);
        Assert.Equal("Recording time, the date ID3v2.4 uses for the year (ID3v2.4.0 frames section 4.2.5; not part of ID3v2.3).", d["TDRC"]);
        Assert.Contains("(ID3v2.3.0 section 3.3)", d["XYZ1"]);
        Assert.DoesNotContain("ID3v2.4.0 frames section 4.2.1", d["TIT2"]);
    }

    [Fact]
    public void AnId3v24FrameCitesTheId3v24FramesDocumentAndTheFramesItDroppedSaySo()
    {
        Dictionary<string, string?> d = Descriptions(4, "TIT2", "TXXX", "COMM", "AENC", "TDRC", "TYER", "XYZ1");

        Assert.Equal("Title of the track or piece (ID3v2.4.0 frames section 4.2.1).", d["TIT2"]);
        Assert.Contains("(ID3v2.4.0 frames section 4.2.6)", d["TXXX"]);
        Assert.Contains("(ID3v2.4.0 frames section 4.10)", d["COMM"]);
        Assert.Contains("(ID3v2.4.0 frames section 4.19)", d["AENC"]);
        Assert.Contains("(ID3v2.4.0 frames section 4.2.5)", d["TDRC"]);
        Assert.Equal("Year of the recording (ID3v2.3.0 section 4.2.1; not part of ID3v2.4, which replaced it with TDRC, ID3v2.4.0 changes section 4).", d["TYER"]);
        Assert.Contains("(ID3v2.4.0 structure section 4)", d["XYZ1"]);
    }

    [Theory]
    [InlineData(3, "TDRC", "Frame TDRC is defined in ID3v2.4 (frames section 4.2.5), not in ID3v2.3, the version of this tag.")]
    [InlineData(4, "IPLS", "Frame IPLS is defined in ID3v2.3 (section 4.4), not in ID3v2.4, the version of this tag; ID3v2.4 replaced it with TIPL and TMCL (ID3v2.4.0 changes section 4).")]
    [InlineData(4, "TSIZ", "Frame TSIZ is defined in ID3v2.3 (section 4.2.1), not in ID3v2.4, the version of this tag; ID3v2.4 deprecated it (ID3v2.4.0 changes section 4).")]
    [InlineData(4, "TIT2", null)]
    [InlineData(3, "TIT2", null)]
    [InlineData(4, "PRIV", null)]
    [InlineData(3, "XYZ1", null)]
    public void AFrameTheTagsVersionDoesNotDefineIsAnInfoFinding(int major, string id, string? message)
    {
        byte[] body = [.. Frame(major, id, Text), .. new byte[8]];
        byte[] tag = [.. "ID3"u8.ToArray(), (byte)major, 0, 0, .. Syncsafe(body.Length), .. body];
        Report report = Analyzer.CreateDefault().Analyze("test.mp3", new MemoryStream(tag));

        Finding[] found = [.. report.Findings.Where(f => f.Code == "id3v2.frame-not-in-version")];
        if (message is null)
        {
            Assert.Empty(found);
            return;
        }

        Finding finding = Assert.Single(found);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal(message, finding.Message);
        Assert.Equal(id, finding.Node!.Name);
    }
}
