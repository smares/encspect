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

    // Encoders by what their names and claims contain, lowercase.
    private static readonly (string Family, string[] Markers)[] Families =
    [
        ("TwoLAME", ["twolame"]),
        ("Helix", ["helix", "lameh"]),
        ("LAME", ["lame"]),
        ("Xing", ["xing"]),
        ("BladeEnc", ["blade"]),
        ("Shine", ["shine"]),
        ("Gogo", ["gogo"]),
        ("Fraunhofer", ["fraunhofer", "fhg", "mp3senc", "l3enc", "mp3enc", "mp3pro"]),
        ("FFmpeg", ["ffmpeg", "lavf", "lavc"]),
    ];

    [GeneratedRegex(@"LAME(?:\s*\d+bits)?(?:\s*version)?\s*(\d+\.\d+[\w.]*)", RegexOptions.IgnoreCase)]
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
                List<Entry> audio = region.Entries.Where(IsAudio).ToList();
                if (audio.Count > 0)
                {
                    long offset = audio[0].Offset;
                    long length = audio[^1].End - offset;
                    encoder = new Node("Encoder", offset, length, ClaimedName(text, claimed));
                    encoder.Add("Confidence", offset, length, "Low");
                    encoder.Add("Evidence", offset, length, evidence);
                }

                continue;
            }

            HashSet<string> named = FamiliesOf(encoder.Value ?? string.Empty);
            if (claimed.Overlaps(named))
            {
                encoder.Add("Evidence", encoder.Offset, encoder.Length, evidence);
                Node confidence = encoder.Children.First(c => c.Name == "Confidence");
                if (confidence.Value == "Low")
                {
                    confidence.Value = "Medium";
                }
            }
            else if (claimed.Contains("FFmpeg"))
            {
                encoder.Add("Evidence", encoder.Offset, encoder.Length, evidence);
            }
            else
            {
                string name = encoder.Value!;
                state.Findings.Add(Severity.Info, "mpeg.encoder-claim-differs", claim.Source, _ =>
                    $"The {claim.Description} names \"{text}\", but the stream points to {name}; the tag may have been written by other software, or kept when the audio was re-encoded.");
            }
        }

        return encoder;
    }

    private static HashSet<string> FamiliesOf(string text)
    {
        string lower = text.ToLowerInvariant();
        var families = Families.Where(f => f.Markers.Any(m => lower.Contains(m, StringComparison.Ordinal))).Select(f => f.Family).ToHashSet();

        // "TwoLAME" and Helix's "LAMEH" contain "lame" without naming LAME.
        if (!lower.Replace("twolame", string.Empty, StringComparison.Ordinal).Replace("lameh", string.Empty, StringComparison.Ordinal).Contains("lame", StringComparison.Ordinal))
        {
            families.Remove("LAME");
        }

        return families;
    }

    // LAME's front end writes "LAME 64bits version 3.100 (http://lame.sf.net)", named here as the
    // LAME tag names it; ffmpeg's version names the muxer; anything else is kept as written.
    private static string ClaimedName(string text, HashSet<string> families)
    {
        if (families.Contains("LAME") && LameVersion().Match(text) is { Success: true } match)
        {
            return $"LAME {match.Groups[1].Value}";
        }

        if (families.Contains("FFmpeg") && families.Count == 1)
        {
            return $"FFmpeg ({text})";
        }

        return text.Length > MaxClaimedName ? text[..MaxClaimedName] : text;
    }
}
