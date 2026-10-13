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
//
// exhale writes no text into a USAC stream, but from release 1.1.2 on, which added the audio
// pre-roll element, its UsacConfig has a layout of its own, as exhale 1.2.2 writes it
// (bitStreamWriter.cpp, createAudioConfig, and exhaleApp.cpp):
// - coreSbrFrameLengthIndex 1, or 3 with SBR: the app's presets 0 to 9 and a to g;
// - an audio pre-roll extension element first, with no configuration, default length or
//   fragmentation, then only channel elements (exhaleLibPch.h, elementTypeConfig), never a fill
//   element;
// - no time-warped MDCT (exhaleEnc.cpp); with SBR, neither harmonic SBR nor PVC, a default start
//   frequency of 15, a stop frequency of 8 to 10, no second default header extra and
//   stereoConfigIndex 0;
// - one loudness info configuration extension of 7 to 9 bytes with one loudnessInfo() and one
//   measurement, or no configuration extension at all for 7 or more channels.
// bs_interTes is left out: exhale writes 0, but a build with ENABLE_INTERTES writes 1.
// Fraunhofer's demonstration files and libxaac's output end the element list with a fill element
// (the newer Fraunhofer files and libxaac also carry a stream ID extension), and the eight
// ISO/IEC 23003-3 conformance streams checked have a fill element and no pre-roll element. The
// layout names exhale with medium confidence, without a version, which an agreeing claim supplies.
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
    public static Node? Detect(Signature? signature, IReadOnlyList<EncoderClaim> claims, Func<string, NodeValue?, string, Node> cover, Action<EncoderClaim, string, string> differs,
        UsacLayout? usac = null)
    {
        Node? encoder = null;
        bool versionless = false;
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
        else if (usac is not null && IsExhaleLayout(usac))
        {
            encoder = cover("Encoder", "exhale", Explain.Encoder);
            encoder.Add(cover("Confidence", "Medium", Explain.Confidence));
            string sbr = usac.CoreSbrFrameLengthIndex == 3 ? ", SBR defaults of start frequency 15 without harmonic SBR or PVC" : string.Empty;
            string extension = usac.ConfigExtensions.Count == 0
                ? "no configuration extension"
                : $"one loudness info extension of {usac.ConfigExtensions[0].Length} bytes with one measurement";
            encoder.Add(cover("Evidence",
                $"The UsacConfig at 0x{usac.Config.Offset:X} has the layout exhale writes: an audio pre-roll element first, then only channel elements without time-warped MDCT and no fill element{sbr}, and {extension}",
                Explain.Evidence));
            versionless = true;
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
                if (versionless && claimed.SetEquals(named))
                {
                    encoder.Value = ClaimedName(text);
                    versionless = false;
                }
            }
            else
            {
                differs(claim, text, encoder.Value!.ToString());
            }
        }

        return encoder;
    }

    private static bool IsExhaleLayout(UsacLayout usac)
    {
        if (usac.CoreSbrFrameLengthIndex is not (1 or 3) || usac.Elements.Count < 2
            || usac.Elements[0] is not { Kind: "EXT", ExtensionType: 3, ConfigLength: 0, HasDefaultLength: false, Fragmented: false })
        {
            return false;
        }

        bool sbr = usac.CoreSbrFrameLengthIndex == 3;
        int channels = 0;
        foreach (UsacElementLayout element in usac.Elements.Skip(1))
        {
            channels += element.Channels;
            bool fits = element.Kind switch
            {
                "LFE" => true,
                "SCE" or "CPE" => !element.TimeWarpedMdct && (!sbr || (!element.HarmonicSbr && !element.Pvc && element.StartFrequency == 15
                    && element.StopFrequency is >= 8 and <= 10 && !element.DefaultHeaderExtra2 && element.StereoConfigIndex == 0)),
                _ => false,
            };
            if (!fits)
            {
                return false;
            }
        }

        return usac.ConfigExtensions switch
        {
            [] => channels >= 7,
            [{ Type: 2, Length: >= 7 and <= 9, SingleLoudnessMeasurement: true }] => true,
            _ => false,
        };
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
