using Encspect.Core;

namespace Encspect.Tags.Id3;

// The Enhanced TAG ("TAG+", 227 bytes) that some players write directly before an ID3v1 tag:
// title, artist and album of 60 bytes each, a speed byte, a free-text genre, and start and end
// times as "mmm:ss". No authoritative specification exists, so beyond the signature only the
// text fields are checked. Without an ID3v1 tag after it, the host lowers its confidence.
public sealed class Id3v1EnhancedTagParser : ITagParser
{
    public const int TagSize = 227;

    private static readonly byte[] Signature = "TAG+"u8.ToArray();
    private static readonly string[] SpeedNames = ["unset", "slow", "medium", "fast", "hardcore"];

    public string Name => "Enhanced TAG";

    public string? MustPrecede => "ID3v1";

    public TagProbe? ProbeBefore(TagProbeContext context, long end)
    {
        ByteReader reader = context.Reader;
        long start = end - TagSize;
        if (!reader.MatchesAt(start, Signature))
        {
            return null;
        }

        byte[] data = reader.ReadBytesAt(start, TagSize);
        var tag = new Node(Name, start, TagSize);
        var nonText = new List<string>();

        tag.Add("Signature", start, 4, "TAG+");
        Id3v1TagParser.AddText(tag, "Title", data, start, 4, 60, nonText);
        Id3v1TagParser.AddText(tag, "Artist", data, start, 64, 60, nonText);
        Id3v1TagParser.AddText(tag, "Album", data, start, 124, 60, nonText);

        byte speed = data[184];
        string speedName = speed < SpeedNames.Length ? SpeedNames[speed] : "unknown";
        tag.Add("Speed", start + 184, 1, $"{speed} ({speedName})");

        Id3v1TagParser.AddText(tag, "Genre", data, start, 185, 30, nonText);
        Id3v1TagParser.AddText(tag, "Start Time", data, start, 215, 6, nonText);
        Id3v1TagParser.AddText(tag, "End Time", data, start, 221, 6, nonText);

        var evidence = new List<Evidence> { new("Signature \"TAG+\" 227 bytes before the anchor", start, 4) };
        var findings = new List<Finding>();
        Confidence confidence = Confidence.Medium;
        if (nonText.Count > 0)
        {
            confidence = Confidence.Low;
            findings.Add(new Finding(
                Severity.Warning,
                "id3v1.not-text",
                $"Enhanced TAG fields contain control characters, so this may not be a real tag: {string.Join(", ", nonText)}.",
                tag));
        }
        else
        {
            evidence.Add(new Evidence("All text fields hold printable text", start + 4, TagSize - 4));
        }

        return TagProbe.Accept(tag, start, confidence, evidence, findings);
    }
}
