using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.Aac;

// The encoder of an AAC stream, from what the stream itself carries and what the container's
// metadata and the file's tags say (EncoderClaim). ffmpeg's AAC encoder writes its version
// ("Lavc60.31.102") into a fill element at the start of an access unit, unless told to be
// bit-exact, and FAAC its library's ("libfaac 1.30") into one in the first access unit; either names
// the encoder with high confidence. A claim, such as MP4's tool atom (©too) or an
// ID3v2 TSSE frame, is written by whatever software wrote the file and survives a re-encode, so
// alone it names the encoder as written with low confidence, as the MPEG parser weighs a TSSE
// frame; agreeing with the stream's evidence it is listed as more evidence. ffmpeg's muxer writes its own version
// ("Lavf60.16.100") whatever encoder it used, so that names none. fdkaac writes "fdkaac 1.0.0,
// libfdk-aac 4.0.1, CBR 48kbps", named as "fdkaac 1.0.0 (libfdk-aac 4.0.1)".
internal static partial class AacEncoder
{
    private const int MaxClaimedName = 64;

    // Families by the words their names hold, lowercase, matched where no letter comes before them
    // (and none after, but for a prefix marked "*").
    private static readonly (string Family, string[] Markers)[] Families =
    [
        ("FFmpeg", ["ffmpeg", "lavf", "lavc"]),
        ("fdkaac", ["fdkaac", "libfdk*", "fdk-aac", "fdk aac"]),
        ("FAAC", ["faac", "libfaac"]),
        ("exhale", ["exhale"]),
        ("Nero", ["nero"]),
        ("qaac", ["qaac"]),
        ("iTunes", ["itunes"]),
    ];

    private static readonly Dictionary<string, Regex> MarkerPatterns = Families.SelectMany(f => f.Markers).ToDictionary(m => m, m =>
        new Regex(m.EndsWith('*') ? $"(?<![a-z]){Regex.Escape(m[..^1])}" : $"(?<![a-z]){Regex.Escape(m)}(?![a-z])", RegexOptions.CultureInvariant));

    [GeneratedRegex(@"^fdkaac\s+(\S+?),\s*libfdk-aac\s+(\S+?)(?:,|$)", RegexOptions.IgnoreCase)]
    private static partial Regex FdkaacTool();

    [GeneratedRegex(@"^Lavc\d+\.\d+\.\d+$")]
    private static partial Regex LavcVersion();

    // FAAC 1.30 writes "libfaac 1.30" into a fill element of the first access unit, in M4A and ADTS
    // alike, as its fixtures show.
    [GeneratedRegex(@"^libfaac (\d+\.\d+(?:\.\d+)?)$")]
    private static partial Regex FaacVersion();

    // The stream's own signature: text from a fill element at the start of an access unit, the
    // access unit's node for the evidence, and its index.
    public sealed record Signature(string Text, Node Unit, int Index);

    // The Encoder node, covering the stream, or null where nothing names one. differs reports a
    // claim that names another encoder than the stream's evidence.
    public static Node? Detect(Signature? signature, IReadOnlyList<EncoderClaim> claims, Func<string, NodeValue?, string, Node> cover, Action<EncoderClaim, string, string> differs)
    {
        Node? encoder = null;
        if (signature is not null && LavcVersion().IsMatch(signature.Text))
        {
            encoder = cover("Encoder", $"FFmpeg ({signature.Text})", Explain.Encoder);
            encoder.Add(cover("Confidence", "High", Explain.Confidence));
            encoder.Add(cover("Evidence",
                $"A fill element of access unit {signature.Index + 1} at 0x{signature.Unit.Offset:X} holds \"{signature.Text}\", which ffmpeg's AAC encoder writes", Explain.Evidence));
        }
        else if (signature is not null && FaacVersion().Match(signature.Text) is { Success: true } faac)
        {
            encoder = cover("Encoder", $"FAAC {faac.Groups[1].Value}", Explain.Encoder);
            encoder.Add(cover("Confidence", "High", Explain.Confidence));
            encoder.Add(cover("Evidence",
                $"A fill element of access unit {signature.Index + 1} at 0x{signature.Unit.Offset:X} holds \"{signature.Text}\", which libfaac writes", Explain.Evidence));
        }

        foreach (EncoderClaim claim in claims)
        {
            string text = claim.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            HashSet<string> claimed = FamiliesOf(text);
            string evidence = $"{claim.Description} at 0x{claim.Source.Offset:X} names \"{text}\"";
            if (encoder is null)
            {
                // ffmpeg's muxer writes its own version whatever encoded the stream.
                if (claimed.SetEquals(["FFmpeg"]))
                {
                    continue;
                }

                encoder = cover("Encoder", ClaimedName(text), Explain.Encoder);
                encoder.Add(cover("Confidence", "Low", Explain.Confidence));
                encoder.Add(cover("Evidence", evidence, Explain.Evidence));
                continue;
            }

            HashSet<string> named = FamiliesOf(encoder.Value?.ToString() ?? string.Empty);
            // Two claims that agree are still claims: they add evidence, not confidence.
            if (claimed.Overlaps(named) || claimed.Contains("FFmpeg"))
            {
                encoder.Add(cover("Evidence", evidence, Explain.Evidence));
            }
            else
            {
                differs(claim, text, encoder.Value!.ToString());
            }
        }

        return encoder;
    }

    private static HashSet<string> FamiliesOf(string text)
    {
        string lower = text.ToLowerInvariant();
        return Families.Where(f => f.Markers.Any(m => MarkerPatterns[m].IsMatch(lower))).Select(f => f.Family).ToHashSet();
    }

    private static string ClaimedName(string text)
    {
        if (FdkaacTool().Match(text) is { Success: true } match)
        {
            return $"fdkaac {match.Groups[1].Value} (libfdk-aac {match.Groups[2].Value})";
        }

        return text.Length > MaxClaimedName ? text[..MaxClaimedName] : text;
    }
}
