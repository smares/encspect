using Encspect.Core;
using TagLib;
using TagLib.Id3v2;

namespace Encspect.Tests;

// Cross-checks the tag parsers against TagLib#, an independent implementation, on every
// fixture: the same tag types must be found and the values both tools decode must agree.
// Disagreements fail the test unless they are listed in Explained with the reason.
public class TagLibCrossCheckTests
{
    private const string LyricsStack =
        "TagLib# does not skip a Lyrics3 tag at the end of the file, so it misses the tags in front of it.";

    private const string WxxxReason =
        "TagLib# 2.3 misreads WXXX frames (empty or merged description, no URL). mutagen 1.47 reads the " +
        "same bytes as Encspect does.";

    private const string CrcReason =
        "TagLib# 2.3 loses every frame after an extended header with a CRC in this layout, whether or not the CRC " +
        "matches (checked with a correct CRC as well); mutagen 1.47 reads the frame, as Encspect does.";

    private const string InnerId3v1Reason =
        "TagLib# reads a TAG 128 bytes before the ID3v1 tag as a second ID3v1 tag, here inside the tag in front " +
        "of it, and reports that one; mutagen 1.47 reads the ID3v1 tag at EOF-128, as Encspect does.";

    private const string ApeSizeReason =
        "The leading APE header's size points past the end of the file. TagLib# reads the tag anyway (mutagen 1.47 " +
        "reads its items when they are complete and fails otherwise); the plan rejects such a candidate because its " +
        "extent is unknown, and Encspect reports the rejection.";

    // Each entry: fixture, a substring of the difference, and why the tools legitimately differ.
    private static readonly (string Fixture, string Difference, string Reason)[] Explained =
    [
        ("id3v23-basic", "WXXX", WxxxReason),
        ("external-mutagen-id3v24-id3v1", "WXXX", WxxxReason),
        ("id3v24-plain-frame-sizes", "TIT2[0] text", "TagLib# reads the non-syncsafe size as syncsafe and cuts TIT2 short; mutagen 1.47 agrees with Encspect."),
        ("id3v24-plain-frame-sizes", "frame count", "TagLib# loses TPE1 after misreading the TIT2 size; mutagen 1.47 finds both frames."),
        ("id3v24-itunes-plain-sizes", "TIT2[0] text", "TagLib# reads plain frame sizes without a high bit as syncsafe and cuts TIT2 short; mutagen 1.47 agrees with Encspect."),
        ("id3v24-itunes-plain-sizes", "frame count", "TagLib# loses TPE1 after misreading the TIT2 size; mutagen 1.47 finds both frames."),
        ("id3v2-unknown-flags", "ID3v2:", "TagLib# (and mutagen 1.47) refuse an ID3v2 tag with undefined header flags; Encspect reports the tag with a finding."),
        ("ape-v1-flags", "APE Title", "TagLib# applies APEv2 item flags to an APEv1 item; APEv1 flags are \"ignored on reading\" per the specification."),
        ("id3v24-undefined-encoding", "TIT2[0] text", "No version defines text encoding $04. TagLib# decodes it as UTF-16, mutagen 1.48.1 drops the frame, Encspect shows ISO-8859-1 with a finding."),
        ("ape-v1-not-ascii", "APE Title value", "TagLib# reads every APE value as UTF-8; Encspect reads APEv1 values that are not valid UTF-8 as ISO-8859-1, with a finding."),
        ("ape-v1-zero-byte", "APE Title value", "TagLib# splits an APEv1 value at $00 into a list, which only APEv2 defines; mutagen 1.47 keeps one value, as Encspect does."),
        ("ape-leading-header-only", "TagLib# fails", "TagLib# throws on a header-only APE tag at the start of the file; mutagen 1.47 reads the same items as Encspect."),
        ("ape-leading-v1-block", "TagLib# fails", "TagLib# throws on an APEv1 block at the start of the file; Encspect rejects it with a finding."),
        ("id3v23-crc-mismatch", "frame count", CrcReason),
        ("id3v24-crc-mismatch", "frame count", CrcReason),
        ("ape-size-too-large", "TagLib# fails", "TagLib# seeks before the start of the file and throws; Encspect rejects the candidate with a finding."),
        ("id3v24-compressed-without-data-length", "frame count", "TagLib# drops a compressed ID3v2.4 frame without a data length indicator; mutagen 1.48 decompresses it, and Encspect keeps it as compressed data, with a finding."),
        ("id3v24-extended-header-size-and-flags", "frame count", "TagLib# finds no frame after an extended header that declares more bytes than its flags use; mutagen 1.48 reads TIT2 at the declared size, as Encspect does."),
        ("ape-header-before-file", "APE:", "TagLib# (and mutagen 1.48) refuse an APE footer whose header would start before the file; Encspect reports the tag with a finding."),
        ("id3v1-enhanced-tag-in-artist", "ID3v1 ", InnerId3v1Reason),
        ("lyrics3v1-tag-in-long-lyrics", "ID3v1 ", InnerId3v1Reason),
        ("ape-leading-truncated", "APE:", ApeSizeReason),
        ("ape-leading-size-past-eof", "APE:", ApeSizeReason),
        ("stack-id3v2-ape-lyrics3-id3v1", "APE:", LyricsStack),
        ("stack-id3v2-ape-id3v1-lyrics3", "APE:", LyricsStack),
        ("stack-id3v2-ape-id3v1-lyrics3", "ID3v1:", LyricsStack),
        ("stack-id3v2-id3v1-lyrics3-ape", "ID3v1:", LyricsStack),
        ("stack-id3v2-id3v1-ape-lyrics3", "APE:", LyricsStack),
        ("stack-id3v2-id3v1-ape-lyrics3", "ID3v1:", LyricsStack),
    ];

    public static TheoryData<string> Names => Fixtures.TagFixtures();

    [Theory]
    [MemberData(nameof(Names))]
    public void AgreesWithTagLib(string name)
    {
        Report report = Fixtures.Analyze(name);
        List<string> differences = Compare(name, report);
        var explanations = Explained.Where(e => e.Fixture == name).ToList();

        List<string> unexplained = differences
            .Where(d => !explanations.Any(e => d.Contains(e.Difference, StringComparison.Ordinal)))
            .ToList();
        Assert.True(unexplained.Count == 0, string.Join(Environment.NewLine, unexplained));

        foreach ((_, string difference, string reason) in explanations)
        {
            Assert.True(
                differences.Any(d => d.Contains(difference, StringComparison.Ordinal)),
                $"The explained difference \"{difference}\" ({reason}) no longer occurs; remove the entry.");
        }
    }

    private static List<string> Compare(string name, Report report)
    {
        var differences = new List<string>();
        Node[] regions = report.Root.Children.ToArray();

        TagLib.File file;
        try
        {
            file = TagLib.File.Create(Fixtures.PathOf(name, ".mp3"), "taglib/mp3", ReadStyle.None);
        }
        catch (Exception ex) when (ex is CorruptFileException or IOException or ArgumentOutOfRangeException)
        {
            differences.Add($"TagLib# fails on the file: {ex.Message}");
            return differences;
        }

        using var dispose = file;
        TagTypes onDisk = file.TagTypesOnDisk;

        CompareType(differences, onDisk, TagTypes.Id3v1, regions, "ID3v1");
        CompareType(differences, onDisk, TagTypes.Id3v2, regions, "ID3v2");
        CompareType(differences, onDisk, TagTypes.Ape, regions, "APE");

        if ((onDisk & TagTypes.Id3v1) != 0 && regions.LastOrDefault(r => r.Name == "ID3v1") is { } id3v1)
        {
            CompareId3v1(differences, (TagLib.Id3v1.Tag)file.GetTag(TagTypes.Id3v1), id3v1);
        }

        if ((onDisk & TagTypes.Id3v2) != 0 && regions.FirstOrDefault(r => r.Name == "ID3v2") is { } id3v2)
        {
            CompareId3v2(differences, (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2), id3v2);
        }

        if ((onDisk & TagTypes.Ape) != 0 && regions.LastOrDefault(r => r.Name == "APE") is { } ape)
        {
            CompareApe(differences, (TagLib.Ape.Tag)file.GetTag(TagTypes.Ape), ape);
        }

        return differences;
    }

    private static void CompareType(List<string> differences, TagTypes onDisk, TagTypes type, Node[] regions, string name)
    {
        bool theirs = (onDisk & type) != 0;
        bool ours = regions.Any(r => r.Name == name);
        if (theirs != ours)
        {
            differences.Add($"{name}: TagLib# {(theirs ? "finds" : "does not find")} it, Encspect {(ours ? "does" : "does not")}.");
        }
    }

    private static void CompareId3v1(List<string> differences, TagLib.Id3v1.Tag tag, Node node)
    {
        Same(differences, "ID3v1 Title", tag.Title ?? string.Empty, Value(node, "Title"));
        Same(differences, "ID3v1 Artist", string.Join(";", tag.Performers), Value(node, "Artist"));
        Same(differences, "ID3v1 Album", tag.Album ?? string.Empty, Value(node, "Album"));
        Same(differences, "ID3v1 Comment", tag.Comment ?? string.Empty, Value(node, "Comment"));
        // TagLib# exposes year and track as numbers, 0 when absent or not numeric.
        string year = Value(node, "Year");
        Same(differences, "ID3v1 Year", tag.Year == 0 ? string.Empty : tag.Year.ToString(), year.All(char.IsAsciiDigit) ? year : string.Empty);
        string track = Value(node, "Track");
        Same(differences, "ID3v1 Track", tag.Track == 0 ? string.Empty : tag.Track.ToString(), track == "<missing>" ? string.Empty : track);

        // Ours reads "17 (Rock)" or "255 (none)"; TagLib# lists the name, or nothing.
        string genre = Value(node, "Genre");
        string ourName = genre.EndsWith("(none)", StringComparison.Ordinal) ? string.Empty : genre[(genre.IndexOf('(') + 1)..^1];
        Same(differences, "ID3v1 Genre", tag.Genres.FirstOrDefault() ?? string.Empty, ourName);
    }

    private static void CompareId3v2(List<string> differences, TagLib.Id3v2.Tag tag, Node node)
    {
        int major = node.Value![2] - '0';
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Frame frame in tag.GetFrames())
        {
            string id = frame.FrameId.ToString();
            int index = seen.GetValueOrDefault(id);
            seen[id] = index + 1;

            Node? ours = node.Children.Where(c => FrameId(c.Name, major) == id).Skip(index).FirstOrDefault();
            if (ours is null)
            {
                differences.Add($"ID3v2 frame {id}[{index}]: TagLib# has it, Encspect does not.");
                continue;
            }

            string label = $"ID3v2 {id}[{index}]";
            switch (frame)
            {
                case UserTextInformationFrame user:
                    Same(differences, label + " description", user.Description, Value(ours, "Description"));
                    Same(differences, label + " text", Join(user.Text), Join(Values(ours, "Text")));
                    break;
                case TextInformationFrame text:
                    Same(differences, label + " text", Join(text.Text), Join(Values(ours, "Text")));
                    break;
                case CommentsFrame comment:
                    Same(differences, label + " language", comment.Language, Value(ours, "Language"));
                    Same(differences, label + " description", comment.Description, Value(ours, "Description"));
                    Same(differences, label + " text", comment.Text, Value(ours, "Text"));
                    break;
                case UnsynchronisedLyricsFrame lyrics:
                    Same(differences, label + " language", lyrics.Language, Value(ours, "Language"));
                    Same(differences, label + " description", lyrics.Description, Value(ours, "Description"));
                    Same(differences, label + " text", lyrics.Text, Value(ours, "Text"));
                    break;
                case AttachmentFrame picture:
                    Same(differences, label + " picture type", ((int)picture.Type).ToString(), Value(ours, "Picture Type").Split(' ')[0]);
                    Same(differences, label + " description", picture.Description, Value(ours, "Description"));
                    Same(differences, label + " data", $"{picture.Data.Count} bytes", Value(ours, "Picture Data"));
                    if (major > 2)
                    {
                        Same(differences, label + " MIME type", picture.MimeType, Value(ours, "MIME Type"));
                    }

                    break;
                case UserUrlLinkFrame userUrl:
                    Same(differences, label + " description", userUrl.Description, Value(ours, "Description"));
                    Same(differences, label + " URL", Join(userUrl.Text), Value(ours, "URL"));
                    break;
                case UrlLinkFrame url:
                    Same(differences, label + " URL", Join(url.Text), Value(ours, "URL"));
                    break;
                case UniqueFileIdentifierFrame ufid:
                    Same(differences, label + " owner", ufid.Owner, Value(ours, "Owner"));
                    Same(differences, label + " identifier", ufid.Identifier.ToString(StringType.Latin1), Value(ours, "Identifier"));
                    break;
                case PrivateFrame priv:
                    Same(differences, label + " owner", priv.Owner, Value(ours, "Owner"));
                    Same(differences, label + " data", $"{priv.PrivateData.Count} bytes", Value(ours, "Data"));
                    break;
                case PopularimeterFrame popm:
                    Same(differences, label + " email", popm.User, Value(ours, "Email"));
                    Same(differences, label + " rating", popm.Rating.ToString(), Value(ours, "Rating"));
                    Same(differences, label + " counter", popm.PlayCount.ToString(), Value(ours, "Counter"));
                    break;
                case PlayCountFrame pcnt:
                    Same(differences, label + " counter", pcnt.PlayCount.ToString(), Value(ours, "Counter"));
                    break;
            }
        }

        int theirCount = seen.Values.Sum();
        int ourCount = node.Children.Count(c => c.Name.Length is 3 or 4 && c.Name.All(ch => ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9')));
        if (theirCount != ourCount)
        {
            differences.Add($"ID3v2 frame count: TagLib# {theirCount}, Encspect {ourCount}.");
        }
    }

    private static void CompareApe(List<string> differences, TagLib.Ape.Tag tag, Node node)
    {
        Node items = node.Children.Single(c => c.Name == "Items");
        var keys = new List<string>();
        foreach (string key in tag)
        {
            keys.Add(key);
            TagLib.Ape.Item item = tag.GetItem(key);
            Node? ours = items.Children.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));
            if (ours is null)
            {
                differences.Add($"APE item {key}: TagLib# has it, Encspect does not.");
                continue;
            }

            string type = item.Type switch
            {
                TagLib.Ape.ItemType.Text => "Text",
                TagLib.Ape.ItemType.Binary => "Binary",
                TagLib.Ape.ItemType.Locator => "External",
                _ => item.Type.ToString(),
            };
            Same(differences, $"APE {key} type", type, Value(ours, "Flags/Item Type"));
            Same(differences, $"APE {key} read-only", item.ReadOnly ? "true" : "false", Value(ours, "Flags/IsReadOnly"));
            string theirs = item.Type == TagLib.Ape.ItemType.Binary ? $"{item.Value.Count} bytes" : Join(item.ToStringArray());
            Same(differences, $"APE {key} value", theirs, Join(Values(ours, "Value")));
        }

        if (keys.Count != items.Children.Count)
        {
            differences.Add($"APE item count: TagLib# {keys.Count}, Encspect {items.Children.Count}.");
        }
    }

    // TagLib# upgrades ID3v2.2 frame IDs to their 2.4 equivalents and turns the 2.3 year frame
    // into the 2.4 recording time frame.
    private static string FrameId(string id, int major) => (major, id) switch
    {
        (2, "TT2") => "TIT2",
        (2, "TP1") => "TPE1",
        (2, "TAL") => "TALB",
        (2, "COM") => "COMM",
        (2, "PIC") => "APIC",
        (3, "TYER") => "TDRC",
        _ => id,
    };

    private static string Value(Node node, string path)
    {
        Node? current = node;
        foreach (string segment in path.Split('/'))
        {
            current = current?.Children.FirstOrDefault(c => c.Name == segment);
        }

        return current?.Value ?? "<missing>";
    }

    private static IEnumerable<string> Values(Node node, string name) =>
        node.Children.Where(c => c.Name == name).Select(c => c.Value ?? string.Empty);

    private static string Join(IEnumerable<string> values) => string.Join(" | ", values);

    private static void Same(List<string> differences, string label, string theirs, string ours)
    {
        if (!string.Equals(theirs, ours, StringComparison.Ordinal))
        {
            differences.Add($"{label}: TagLib# \"{theirs}\", Encspect \"{ours}\".");
        }
    }
}
