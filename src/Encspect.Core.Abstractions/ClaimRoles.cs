using System.Text.RegularExpressions;

namespace Encspect.Core;

// What the format parsers share when they weigh claims by role: which role a text plays, which
// software family a text names, and the Encoder and Writer nodes' name and confidence.
public static partial class ClaimRoles
{
    // The text of ffmpeg's muxer ("Lavf60.16.100", a bare "Lavf" or "Lavf lame"), which libavformat writes into whichever tag its
    // output format has, whatever encoded the audio (FFmpeg n6.1.1 libavformat/oggenc.c:281 writes LIBAVFORMAT_IDENT as the
    // vendor string, movenc.c:4203-4204 into ©too). In the LAME tag mp3enc.c:250-252 replaces the bit-exact encoder ID "Lavc libmp3lame"
    // with "Lavf lame" and mp3enc.c:258 writes a bare "Lavf" without any: the muxer wrote them, so they name the writer.
    // This pattern tolerates leading white space and EncoderId does not: the muxer version is matched where tag text is taken as stored (an ID3v2
    // TSSE or an APE item may be padded), a role decision that must not miss a padded muxer; an encoder ID only counts exactly as ffmpeg writes it,
    // and the codecs' own patterns (VorbisParser, OpusParser, SpeexParser) are anchored the same way.
    [GeneratedRegex(@"^\s*Lavf(?![A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex MuxerVersion();

    // The libavcodec identification of an encoder ("Lavc60.31.102 libvorbis", or "Lavc libvorbis" when bit-exact):
    // fftools/ffmpeg_enc.c:157-160 set_encoder_id sets the stream's "encoder" metadata to it only when ffmpeg encodes, mp3enc.c copies
    // it into the LAME tag and the Vorbis comment writer (ff_vorbiscomment_write) into the ENCODER comment. It names an encoder
    // in whichever field. "Lavc" is followed by a version digit or, bit-exact, a single space, so "Lavc-foo" is no ID; the match is anchored
    // at the start like the codecs' own patterns (VorbisParser, OpusParser), which agree with it.
    [GeneratedRegex(@"^Lavc(?:\d| )", RegexOptions.CultureInvariant)]
    private static partial Regex EncoderId();

    // Whether the text is a muxer's version and so names the writer, not the encoder, in whichever field it stands.
    public static bool IsMuxerVersion(string text) => MuxerVersion().IsMatch(text);

    // Whether the text is a libavcodec encoder ID and so names the encoder in whichever field it stands.
    public static bool IsEncoderId(string text) => EncoderId().IsMatch(text);

    // The role of a claim whose field the specification gives to role: a muxer's version text goes to the writer and a
    // libavcodec encoder ID to the encoder, in any field.
    public static ClaimRole Of(ClaimRole role, string text) => IsMuxerVersion(text) ? ClaimRole.Writer : IsEncoderId(text) ? ClaimRole.Encoder : role;

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
        if (text == "ffmpeg")
        {
            return "FFmpeg";
        }

        if (IsMuxerVersion(text) || IsEncoderId(text))
        {
            return $"FFmpeg ({(text.Length > maxLength ? text[..maxLength] : text)})";
        }

        return text.Length > maxLength ? text[..maxLength] : text;
    }

    // The version a writer claim carries, for an encoder the stream's own evidence named without one (FAAC 2.x's layout names "FAAC";
    // the front end's tool atom reads "FAAC 2.2.0 (6acbe23)"). Only a name without a digit is completed, and only by a writer claim
    // of the same family that is no muxer's version and whose text is the encoder's name, white space, and a version: digits and dots
    // (letters and signs may follow), optionally one parenthesized build. Anything else after the version ("LAME 3.100 via foobar")
    // completes nothing, and so does a remux by MP4Box or ffmpeg. Of several such claims the strongest counts, and if the strongest
    // differ in name (compared without regard to case) nothing is completed. A writer claim never creates an encoder or raises its
    // confidence; the evidence says where the version came from.
    public static void CompleteVersion(Node? encoder, IEnumerable<EncoderClaim> claims, Func<string, string>? nameOf = null)
    {
        if (encoder?.Value?.ToString() is not { Length: > 0 } named || named.Any(char.IsAsciiDigit))
        {
            return;
        }

        string family = FamilyOf(named);
        nameOf ??= t => WriterName(t);
        var candidates = Writers(claims)
            .Where(c => !IsMuxerVersion(c.Text) && FamilyOf(c.Text) == family)
            .Select(c => (Claim: c, Name: nameOf(c.Text)))
            .Where(x => x.Name.Length > named.Length && x.Name.StartsWith(named, StringComparison.OrdinalIgnoreCase)
                && VersionPart().IsMatch(x.Name.AsSpan(named.Length)))
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        Confidence strongest = candidates.Max(x => x.Claim.Confidence);
        var top = candidates.Where(x => x.Claim.Confidence == strongest).ToList();
        if (top.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            return;
        }

        (EncoderClaim claim, string name) = top.OrderBy(x => x.Claim.Source.Offset).ThenBy(x => x.Name, StringComparer.Ordinal).First();
        encoder.Value = name;
        encoder.Add(new Node("Evidence", encoder.Offset, encoder.Length,
            $"The version comes from the {claim.Description} at 0x{claim.Source.Offset:X}, which names \"{claim.Text.Trim()}\" as the writer: software of the same name")
        {
            Description = WriterDetection.EvidenceDescription,
            Ranges = encoder.Ranges,
        });
    }

    [GeneratedRegex(@"^\s+[0-9][0-9.]*[A-Za-z0-9+-]*(\s\([^)]*\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPart();

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
