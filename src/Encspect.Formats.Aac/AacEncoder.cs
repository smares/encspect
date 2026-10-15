using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.Aac;

// The encoder of an AAC stream, from what the stream itself carries and what the container's
// metadata and the file's tags say (EncoderClaim). ffmpeg's AAC encoder writes its version
// ("Lavc60.31.102") into a fill element at the start of an access unit, unless told to be
// bit-exact, and FAAC 1.30 to 1.50 their library's ("libfaac 1.31.1") into one in the first access unit; either names
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
//
// Apple's AAC encoder (AudioToolbox, as afconvert, iTunes and QuickTime use it) writes no text into the
// stream. Its file writer lays files out in a way of its own: an M4A, MP4 or 3GP track whose DecoderConfigDescriptor
// has its reserved stream type bit clear, with an sbtd box or a free box that pads the audio data to a page (the MP4
// parser), and ADTS headers with the original/copy bit set and a buffer fullness of 0. They are claims that name
// "Apple AudioToolbox", the writer, with low confidence, because afconvert's -d 0 gives the layout to packets it only
// copied, such as an fdkaac stream (macOS 15.7, run 4): an ADTS target always gets Apple's headers, whatever the source,
// and an M4A or MP4 target always gets the padding and, from an ADTS source, the stream type byte (an M4A source keeps its
// own). With an sbtd box in
// the sample entry, which records the PCM source of an encode and which no copy had, the MP4 claim names the encoder at
// medium confidence. A signature in the stream (ffmpeg's, FAAC's) wins over a claim, and a name some software wrote
// (a tool atom) outranks a layout of equal confidence.
//
// FAAC 1.30, 1.31.1, 1.40 and 1.50 sign their streams with text (FaacVersion). FAAC 2.0, 2.1 and 2.2
// write none, and their HE-AAC encoder lays out its SBR data in ways of its own (FaacSbr): the layout
// names FAAC with medium confidence, without a version, which an agreeing claim supplies. 2.2 writes
// the SBR data of each channel element in a fill element after it; 2.0 and 2.1 write one payload after the
// whole frame, typed as a channel pair element for any channel count above one, so more than two
// channels give a payload without the channel element it belongs to (aac.sbr-without-channel-element)
// and no name. Their LC streams carry neither text nor layout and stay unnamed.
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
        ("Apple", ["apple", "audiotoolbox", "itunes"]),
    ];

    private static readonly Dictionary<string, Regex> MarkerPatterns = Families.SelectMany(f => f.Markers).ToDictionary(m => m, m =>
        new Regex(m.EndsWith('*') ? $"(?<![a-z]){Regex.Escape(m[..^1])}" : $"(?<![a-z]){Regex.Escape(m)}(?![a-z])", RegexOptions.CultureInvariant));

    [GeneratedRegex(@"^fdkaac\s+(\S+?),\s*libfdk-aac\s+(\S+?)(?:,|$)", RegexOptions.IgnoreCase)]
    private static partial Regex FdkaacTool();

    [GeneratedRegex(@"^Lavc\d+\.\d+\.\d+$")]
    private static partial Regex LavcVersion();

    // FAAC 1.30, 1.31.1, 1.40 and 1.50 write "libfaac 1.30", "libfaac 1.31.1", "libfaac 1.40.0" and
    // "libfaac 1.50.0" into a fill element of the first access unit, in M4A and ADTS alike, as their
    // fixtures show.
    [GeneratedRegex(@"^libfaac (\d+\.\d+(?:\.\d+)?)$")]
    private static partial Regex FaacVersion();

    // The stream's own signature: text from a fill element at the start of an access unit, the
    // access unit's node for the evidence, and its index.
    public sealed record Signature(string Text, Node Unit, int Index);

    // The Encoder node, covering the stream, or null where nothing names one. differs reports a
    // claim that names another encoder than the evidence that won, with a phrase that says what that was.
    public static Node? Detect(Signature? signature, IReadOnlyList<EncoderClaim> claims, Func<string, NodeValue?, string, Node> cover, Action<EncoderClaim, string, string> differs,
        UsacLayout? usac = null, CodingTally? tally = null)
    {
        Node? encoder = null;
        EncoderClaim? winner = null;
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
        else if (tally is not null && FaacSbr.IsLayout(tally))
        {
            encoder = cover("Encoder", "FAAC", Explain.Encoder);
            encoder.Add(cover("Confidence", "Medium", Explain.Confidence));
            SbrHeader header = tally.FirstHeader!;
            string period = header.FrequencyScale == 0 ? string.Empty : $", none off FAAC's {FaacSbr.HeaderPeriod}-access-unit header period";
            encoder.Add(cover("Evidence",
                $"The {tally.SbrHeaders} SBR headers and {tally.SbrPayloads} SBR payloads have the layout FAAC 2.x writes: headers that send bs_header_extra_1 and not bs_header_extra_2, with start frequency 15, crossover band 0, bs_noise_bands 0 (one noise floor band) and stop frequency {header.StopFrequency}{period}, "
                + "and data that codes every envelope and noise floor across frequency, with no coupling, sinusoids or extended data",
                Explain.Evidence));
            versionless = true;
        }

        // A stronger claim outranks a weaker one, and a name some software wrote outranks a layout of equal confidence: the
        // layout may only be a copy's wrapper.
        foreach (EncoderClaim claim in claims.OrderByDescending(c => c.Confidence).ThenBy(c => c.IsLayout))
        {
            string text = claim.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            HashSet<string> claimed = FamiliesOf(text);
            string evidence = claim.IsLayout
                ? $"{claim.Description} (at 0x{claim.Source.Offset:X})"
                : $"{claim.Description} at 0x{claim.Source.Offset:X} names \"{text}\"";
            if (encoder is null)
            {
                // ffmpeg's muxer writes its own version whatever encoded the stream.
                if (claimed.SetEquals(["FFmpeg"]))
                {
                    continue;
                }

                encoder = cover("Encoder", ClaimedName(text), Explain.Encoder);
                winner = claim;
                encoder.Add(cover("Confidence", claim.Confidence.ToString(), Explain.Confidence));
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
                differs(claim, text, Phrase(winner, encoder.Value!.ToString()));
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

    // What won, as "but ..." in a finding: the stream, or the claim that named the encoder. The name is the Encoder node's value, quoted for both.
    private static string Phrase(EncoderClaim? winner, string name) => winner is null
        ? $"the stream points to \"{name}\""
        : winner.IsLayout ? $"the {Subject(winner)} points to \"{name}\"" : $"the {winner.Description} names \"{name}\"";

    // A layout claim's description reads "what it is: the traces", and its subject is the part before the colon.
    private static string Subject(EncoderClaim claim)
    {
        int colon = claim.Description.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? claim.Description[..colon] : claim.Description;
    }

    // The finding for a claim that names another encoder than the winner (the phrase Detect gives). A name some software wrote is a claim;
    // a layout is the traces of the writer that laid the file out, which also writes them around packets it only copied.
    public static string Disagreement(EncoderClaim claim, string text, string winner, string cause)
    {
        if (!claim.IsLayout)
        {
            return $"The {claim.Description} names \"{text}\", but {winner}; {cause}";
        }

        // An sbtd box records the PCM source of an encode, and no copy had one; a layout without it is the writer's, which also lays it around copies.
        return claim.Confidence >= Confidence.Medium
            ? $"The {Subject(claim)} points to \"{text}\", but {winner}; the sbtd box records the source of an encode, so other software may have replaced the stream in the file."
            : $"The {Subject(claim)} points to \"{text}\", but {winner}; the writer also lays out streams it only copied in the same way, and other software may have rewritten the file.";
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
