using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// What the file's tags say about the encoder (an ID3v2 TSSE frame), weighed against the frames.
// The software that writes the tag fills it in, which may be the encoder, a later tagger or
// ffmpeg's muxer, and it is not updated when the audio is re-encoded, so it is weaker evidence than
// the stream's own: alone it names the encoder with low confidence; agreeing with traits that name
// the same encoder, the two together make medium confidence; a different name is reported, not
// taken. ffmpeg writes its own version ("Lavf60.16.100") whatever encoder it used, so its claim
// supports the stream's encoder without naming one. A node stays inside its parent's bytes, so the
// claim's evidence spans the encoder node and names where the tag frame is; the claim-differs
// finding points to the frame itself.
public sealed partial class MpegAudioParser
{
    private const int MaxClaimedName = 64;

    // Encoders by the words their names and claims hold, lowercase: a marker counts where no
    // letter comes before it, and none after it but where it is a prefix (a "*"), so "Flame" does
    // not name LAME nor "Remixing" Xing. "TwoLAME" and Helix's "LAMEH" do not name LAME.
    private static readonly (string Family, string[] Markers)[] Families =
    [
        ("TwoLAME", ["twolame"]),
        ("Helix", ["helix", "lameh*"]),
        ("LAME", ["lame"]),
        ("Xing", ["xing"]),
        ("BladeEnc", ["blade*"]),
        ("Shine", ["shine*"]),
        ("Gogo", ["gogo*"]),
        ("Fraunhofer", ["fraunhofer", "fhg", "mp3senc", "l3enc", "mp3enc", "mp3pro"]),
        ("FFmpeg", ["ffmpeg", "lavf", "lavc"]),
    ];

    private static readonly Dictionary<string, Regex> MarkerPatterns = Families.SelectMany(f => f.Markers).ToDictionary(m => m, m =>
        new Regex(m.EndsWith('*') ? $"(?<![a-z]){m[..^1]}" : $"(?<![a-z]){m}(?![a-z])", RegexOptions.CultureInvariant));

    [GeneratedRegex(@"(?<![a-z])LAME(?![a-z])(?:\s*\d+bits)?(?:\s*version)?\s*(\d+\.\d+[\w.]*)", RegexOptions.IgnoreCase)]
    private static partial Regex LameVersion();

    private static Node? WeighClaims(StreamState state, RegionState region, Node? encoder, IReadOnlyList<EncoderClaim> claims)
    {
        foreach (EncoderClaim claim in claims)
        {
            string text = claim.Text.Trim();
            HashSet<string> claimed = FamiliesOf(text);
            string evidence = $"{claim.Description} at 0x{claim.Source.Offset:X} names \"{text}\"";
            if (encoder is null)
            {
                // ffmpeg's muxer writes its own version whatever encoded the stream.
                if (claimed.SetEquals(["FFmpeg"]))
                {
                    continue;
                }

                if (region.AudioFrames > 0)
                {
                    long offset = region.AudioStart;
                    long length = region.AudioEnd - offset;
                    encoder = EncoderNode(offset, length, ClaimedName(text, claimed));
                    encoder.Add("Confidence", offset, length, "Low", Explain.Confidence);
                    encoder.Add("Evidence", offset, length, evidence, Explain.Evidence);
                }

                continue;
            }

            HashSet<string> named = FamiliesOf(encoder.Value?.ToString() ?? string.Empty);
            if (claimed.Overlaps(named))
            {
                encoder.Add("Evidence", encoder.Offset, encoder.Length, evidence, Explain.Evidence);
                Node confidence = encoder.Children.First(c => c.Name == "Confidence");
                if (confidence.Value == "Low")
                {
                    confidence.Value = "Medium";
                }
            }
            else if (claimed.Contains("FFmpeg"))
            {
                encoder.Add("Evidence", encoder.Offset, encoder.Length, evidence, Explain.Evidence);
            }
            else
            {
                string name = encoder.Value!.ToString();
                state.Findings.Add(Severity.Info, "mpeg.encoder-claim-differs", claim.Source, _ =>
                    $"The {claim.Description} names \"{text}\", but the stream points to {name}; the tag may have been written by other software, or kept when the audio was re-encoded.");
            }
        }

        return encoder;
    }

    private static HashSet<string> FamiliesOf(string text)
    {
        string lower = text.ToLowerInvariant();
        return Families.Where(f => f.Markers.Any(m => MarkerPatterns[m].IsMatch(lower))).Select(f => f.Family).ToHashSet();
    }

    // LAME's front end writes "LAME 64bits version 3.100 (http://lame.sf.net)", named here as the
    // LAME tag names it; a libavcodec ID as every parser names it ("FFmpeg (Lavc60.31.102 libmp3lame)"); anything else is kept as written.
    private static string ClaimedName(string text, HashSet<string> families)
    {
        if (families.Contains("LAME") && LameVersion().Match(text) is { Success: true } match)
        {
            return $"LAME {match.Groups[1].Value}";
        }

        if (ClaimRoles.IsEncoderId(text))
        {
            return ClaimRoles.WriterName(text, MaxClaimedName);
        }

        return text.Length > MaxClaimedName ? text[..MaxClaimedName] : text;
    }
}
