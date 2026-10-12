using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.Ac3;

// The encoder of an AC-3 or E-AC-3 stream. The bit stream carries no encoder name, and the habits
// of the encoders at hand (ffmpeg's ac3, ac3_fixed and eac3, and Aften) are not told apart by
// anything their fixtures show every time and the others never, so only what the file's tags and
// the container's metadata claim names one: as written, with low confidence, as an ID3v2 TSSE
// frame does for MPEG audio. ffmpeg's muxer writes its own version ("Lavf60.16.100") whatever
// encoded the stream, so that names none.
internal static partial class Ac3Encoder
{
    private const int MaxClaimedName = 64;

    [GeneratedRegex(@"^Lavf\d", RegexOptions.CultureInvariant)]
    private static partial Regex MuxerVersion();

    public static Node? Detect(IReadOnlyList<EncoderClaim> claims, Func<string, NodeValue?, string, Node> cover, Action<EncoderClaim, string> other)
    {
        Node? encoder = null;
        foreach (EncoderClaim claim in claims)
        {
            string text = claim.Text.Trim();
            if (text.Length == 0 || MuxerVersion().IsMatch(text))
            {
                continue;
            }

            if (encoder is null)
            {
                encoder = cover("Encoder", text.Length > MaxClaimedName ? text[..MaxClaimedName] : text, Explain.Encoder);
                encoder.Add(cover("Confidence", "Low", Explain.Confidence));
                encoder.Add(cover("Evidence", $"{claim.Description} at 0x{claim.Source.Offset:X} names \"{text}\"", Explain.Evidence));
            }
            else
            {
                other(claim, text);
            }
        }

        return encoder;
    }
}
