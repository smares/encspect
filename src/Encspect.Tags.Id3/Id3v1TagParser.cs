using System.Text;
using Encspect.Core;

namespace Encspect.Tags.Id3;

// ID3v1 and ID3v1.1 (id3.org, "ID3v1"). ID3v1 has no size, version or checksum, so the only
// structure to validate is that the fields hold text; a match is never more than Medium. The
// Enhanced TAG in front of it is a separate candidate (Id3v1EnhancedTagParser), so its 4-byte
// signature cannot claim bytes on its own.
public sealed class Id3v1TagParser : ITagParser
{
    public const int TagSize = 128;

    private static readonly byte[] Signature = "TAG"u8.ToArray();

    public string Name => "ID3v1";

    public TagProbe? ProbeBefore(TagProbeContext context, long end)
    {
        ByteReader reader = context.Reader;
        long start = end - TagSize;
        if (!reader.MatchesAt(start, Signature))
        {
            return null;
        }

        byte[] data = reader.ReadBytesAt(start, TagSize);
        var findings = new List<Finding>();
        var evidence = new List<Evidence> { new("Signature \"TAG\" 128 bytes before the anchor", start, 3) };

        // ID3v1.1: a zero byte at comment offset 28 followed by a non-zero track number.
        bool isV11 = data[125] == 0 && data[126] != 0;

        var tag = new Node(Name, start, TagSize, isV11 ? "1.1" : "1.0");
        var nonText = new List<string>();

        tag.Add("Signature", start, 3, "TAG");
        AddText(tag, "Title", data, start, 3, 30, nonText);
        AddText(tag, "Artist", data, start, 33, 30, nonText);
        AddText(tag, "Album", data, start, 63, 30, nonText);
        Node year = AddText(tag, "Year", data, start, 93, 4, nonText);
        if (year.Value!.Length > 0 && !year.Value.All(char.IsAsciiDigit))
        {
            findings.Add(new Finding(Severity.Warning, "id3v1.year-not-numeric", $"The ID3v1 year \"{year.Value}\" is not a number.", year));
        }

        if (isV11)
        {
            AddText(tag, "Comment", data, start, 97, 28, nonText);
            tag.Add("Track", start + 126, 1, data[126].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            AddText(tag, "Comment", data, start, 97, 30, nonText);
        }

        tag.Add("Genre", start + 127, 1, GenreName(data[127]));

        Confidence confidence = Confidence.Medium;
        if (nonText.Count > 0)
        {
            confidence = Confidence.Low;
            findings.Add(new Finding(
                Severity.Warning,
                "id3v1.not-text",
                $"ID3v1 fields contain control characters, so this may not be a real tag: {string.Join(", ", nonText)}.",
                tag));
        }
        else
        {
            evidence.Add(new Evidence("All text fields hold printable text", start + 3, 124));
        }

        return TagProbe.Accept(tag, start, confidence, evidence, findings);
    }

    public static string GenreName(byte genre)
    {
        if (genre < Id3v1Genres.Names.Length)
        {
            return $"{genre} ({Id3v1Genres.Names[genre]})";
        }

        return genre == 255 ? "255 (none)" : $"{genre} (unknown)";
    }

    // Fields are ISO-8859-1, padded with zero bytes or spaces.
    internal static Node AddText(Node parent, string name, byte[] data, long baseOffset, int offset, int length, List<string> nonText, string? label = null)
    {
        ReadOnlySpan<byte> field = data.AsSpan(offset, length);
        int terminator = field.IndexOf((byte)0);
        ReadOnlySpan<byte> text = terminator < 0 ? field : field[..terminator];

        foreach (byte b in text)
        {
            if (b < 0x20 || b == 0x7F)
            {
                nonText.Add(label ?? name);
                break;
            }
        }

        string value = Encoding.Latin1.GetString(text).TrimEnd(' ');
        return parent.Add(name, baseOffset + offset, length, value);
    }
}
