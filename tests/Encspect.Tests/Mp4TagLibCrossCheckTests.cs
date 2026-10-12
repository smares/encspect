using System.Text;
using Encspect.Core;
using TagLib;

namespace Encspect.Tests;

// Cross-checks the iTunes metadata the MP4 parser reads against TagLib#'s AppleTag on every MP4
// fixture that has some: every item whose data is text, the track and disc numbers, the tempo, the
// cover art and the freeform atoms must agree. Disagreements fail unless Explained lists them.
public class Mp4TagLibCrossCheckTests
{
    private static readonly (string Fixture, string Item, string Reason)[] Explained =
    [
        ("ilst-all-types", "soar", "TagLib# 2.3 reads only data atoms of type 1 (UTF-8) as text; this item's data atom is of type 4 (UTF-8 sort), which the analyzer decodes as UTF-8."),
        ("ilst-all-types", "©ART", "TagLib# 2.3 reads only data atoms of type 1 (UTF-8) as text; this item's data atom is of type 2 (UTF-16), which the analyzer decodes as UTF-16."),
        ("ilst-all-types", "sonm", "TagLib# 2.3 reads only data atoms of type 1 (UTF-8) as text; this item's data atom is of type 5 (UTF-16 sort), which the analyzer decodes as UTF-16."),
        ("ilst-all-types", "xsjs", "TagLib# 2.3 reads only data atoms of type 1 (UTF-8) as text; this item's data atom is of type 3 (S/JIS)."),
    ];

    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in Fixtures.Mp4FixtureNames().Where(n => Path.GetExtension(n) is ".m4a" or ".mp4" or ".mov"))
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ItunesMetadataMatchesTagLib(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Node? list = Find(report.Root, n => n.Kind == NodeKind.Tag && n.Name is "iTunes Metadata");
        string fixture = Path.GetFileNameWithoutExtension(name);
        var differences = new List<string>();

        TagLib.Mpeg4.AppleTag? tag;
        try
        {
            using var file = TagLib.File.Create(Path.Combine(Fixtures.FixtureDirectory, name));
            tag = file.GetTag(TagTypes.Apple) as TagLib.Mpeg4.AppleTag;
            if (list is null)
            {
                Assert.True(tag is null || tag.IsEmpty, $"TagLib# finds iTunes metadata the analyzer does not in {name}.");
                return;
            }

            Assert.NotNull(tag);
            foreach (Node item in list.Children.Where(c => c.Children.Any(f => f.Name == "Type")))
            {
                string type = item.Children.First(f => f.Name == "Type").Value!.ToString();
                Node[] atoms = item.Children.Where(c => c.Children.Any(f => f.Name == "Type" && f.Value?.ToString() == "data")).ToArray();
                Node? first = atoms.FirstOrDefault();
                string? dataType = first?.Children.FirstOrDefault(c => c.Name == "Data Type")?.Value?.ToString();
                string? value = first?.Children.FirstOrDefault(c => c.Name == "Value")?.Value?.ToString();
                string? expected = null;
                string? actual = null;
                switch (type)
                {
                    case "trkn":
                        (expected, actual) = (value, $"{tag.Track} of {tag.TrackCount}");
                        break;
                    case "disk":
                        (expected, actual) = (value, $"{tag.Disc} of {tag.DiscCount}");
                        break;
                    case "tmpo":
                        (expected, actual) = (value, tag.BeatsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case "covr":
                        (expected, actual) = (string.Join(", ", atoms.Select(a => a.Children.First(c => c.Name == "Value").Value!.ToString().Split(' ')[0])),
                            string.Join(", ", tag.Pictures.Select(p => p.MimeType switch { "image/jpeg" => "JPEG", "image/png" => "PNG", "image/bmp" => "BMP", _ => p.MimeType })));
                        break;
                    case "----":
                        string? mean = item.Children.FirstOrDefault(c => c.Name == "mean")?.Value?.ToString();
                        if (dataType?.StartsWith("1 ", StringComparison.Ordinal) == true && mean is not null)
                        {
                            (expected, actual) = (value, tag.GetDashBox(mean, item.Name));
                        }

                        break;
                    default:
                        if (dataType is not null && dataType.StartsWith("0 ", StringComparison.Ordinal) is false && first is not null && dataType.Contains("UTF", StringComparison.Ordinal)
                            || dataType?.Contains("S/JIS", StringComparison.Ordinal) == true)
                        {
                            byte[] code = type.Select(c => c == '©' ? (byte)0xA9 : (byte)c).ToArray();
                            (expected, actual) = (value, tag.GetText(new ByteVector(code)).FirstOrDefault());
                        }

                        break;
                }

                if (expected != actual && !Explained.Any(e => e.Fixture == fixture && e.Item == type))
                {
                    differences.Add($"{type}: analyzer \"{expected}\", TagLib# \"{actual}\"");
                }
            }
        }
        catch (Exception ex) when (ex is CorruptFileException or UnsupportedFormatException)
        {
            Assert.True(list is null || report.Findings.Any(f => f.Severity >= Severity.Warning),
                $"TagLib# cannot read {name} ({ex.Message}), which the analyzer reads without a warning.");
            return;
        }

        Assert.True(differences.Count == 0, $"{name}: {string.Join("; ", differences)}");
    }

    private static Node? Find(Node node, Func<Node, bool> match, int depth = 0)
    {
        if (match(node))
        {
            return node;
        }

        if (depth >= 12 || node.Kind is NodeKind.Stream or NodeKind.Frame)
        {
            return null;
        }

        foreach (Node child in node.Children)
        {
            if (Find(child, match, depth + 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
