using System.Text.RegularExpressions;

namespace Encspect.Core;

// What the format parsers share when they weigh claims by role: which role a text plays, which
// software family a text names, and the Encoder and Writer nodes' name and confidence.
public static partial class ClaimRoles
{
    // The version text of ffmpeg's muxer ("Lavf60.16.100"), which libavformat writes into whichever tag its
    // output format has, whatever encoded the audio.
    [GeneratedRegex(@"^\s*Lavf\d", RegexOptions.CultureInvariant)]
    private static partial Regex MuxerVersion();

    // Whether the text is a muxer's version and so names the writer, not the encoder, in whichever field it stands.
    public static bool IsMuxerVersion(string text) => MuxerVersion().IsMatch(text);

    // The role of a claim whose field the specification gives to role: a muxer's version text goes to the
    // writer in any field.
    public static ClaimRole Of(ClaimRole role, string text) => IsMuxerVersion(text) ? ClaimRole.Writer : role;

    // The IDs of the background articles on detecting the encoder and the writer.
    public const string EncoderTopic = "encoder-detection";

    public const string WriterTopic = "writer-detection";

    // The article an Encoder or Writer node names, or null for any other node.
    public static string? TopicOf(string nodeName) => nodeName switch
    {
        "Encoder" => EncoderTopic,
        "Writer" => WriterTopic,
        _ => null,
    };

    public static IEnumerable<EncoderClaim> Encoders(IEnumerable<EncoderClaim> claims) => claims.Where(c => c.Role == ClaimRole.Encoder);

    public static IEnumerable<EncoderClaim> Writers(IEnumerable<EncoderClaim> claims) => claims.Where(c => c.Role == ClaimRole.Writer);

    public static IReadOnlyList<EncoderClaim> Encoders(IReadOnlyList<EncoderClaim> claims) => [.. claims.Where(c => c.Role == ClaimRole.Encoder)];

    // The name and the confidence an Encoder or Writer node states, or nulls for no node.
    public static (string? Name, Confidence? Confidence) Resolved(Node? node) =>
        node is null
            ? (null, null)
            : (node.Value?.ToString(), node.Children.FirstOrDefault(c => c.Name == "Confidence")?.Value?.ToString() is { } text && Enum.TryParse(text, out Confidence parsed) ? parsed : null);

    // How a writer's name is shown: ffmpeg's version texts name FFmpeg, as the encoder is named where its
    // library wrote the text ("FFmpeg (Lavc60.31.102)"); anything else as written, at most maxLength characters.
    public static string WriterName(string text, int maxLength = 64)
    {
        text = text.Trim();
        if (IsMuxerVersion(text) || text.StartsWith("Lavc", StringComparison.Ordinal))
        {
            return $"FFmpeg ({(text.Length > maxLength ? text[..maxLength] : text)})";
        }

        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private static readonly (string Family, string[] Markers)[] Families =
    [
        ("FFmpeg", ["ffmpeg", "lavf", "lavc", "libav"]),
        ("Apple", ["apple", "audiotoolbox", "itunes", "quicktime"]),
        ("Microsoft", ["windows media", "wmfsdk", "media foundation", "microsoft"]),
        ("Nero", ["nero"]),
        ("GPAC", ["gpac", "mp4box"]),
        ("LAME", ["lame"]),
    ];

    private static readonly Regex[] FamilyPatterns = [.. Families.Select(f =>
        new Regex($"(?<![a-z])(?:{string.Join('|', f.Markers.Select(Regex.Escape))})(?![a-z])", RegexOptions.CultureInvariant))];

    // The software a writer's text names, as a family: the well-known names share one (Lavf and ffmpeg are
    // FFmpeg; iTunes and AudioToolbox are Apple), and any other text is its first word, lowercase, so
    // "Foo 1.0" and "foo 2.0" are one family and "Bar" another. Two texts of one family agree.
    public static string FamilyOf(string text)
    {
        string lower = text.ToLowerInvariant();
        for (int i = 0; i < Families.Length; i++)
        {
            if (FamilyPatterns[i].IsMatch(lower))
            {
                return Families[i].Family;
            }
        }

        int start = 0;
        while (start < lower.Length && !char.IsLetterOrDigit(lower[start]))
        {
            start++;
        }

        int end = start;
        while (end < lower.Length && char.IsLetter(lower[end]))
        {
            end++;
        }

        return end > start ? lower[start..end] : lower.Trim();
    }
}
