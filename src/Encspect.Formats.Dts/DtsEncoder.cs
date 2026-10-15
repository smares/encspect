using Encspect.Core;

namespace Encspect.Formats.Dts;

// The encoder of a DTS stream. The bit stream carries the revision of the encoder software
// (ETSI TS 102 114 5.4.2, VERNUM), which the frames show as a field, and no name, so only what the
// file's tags claim as the encoder's names one: as written, with low confidence, as an
// ID3v2 TSSE frame does for MPEG audio. What names the writer (the BUILDVER chunk of a .dtshd file, an
// APEv2 Encoder item, ffmpeg's muxer version "Lavf60.16.100") is resolved apart (WriterDetection).
internal static class DtsEncoder
{
    private const int MaxClaimedName = 64;

    public static Node? Detect(IReadOnlyList<EncoderClaim> claims, Func<string, NodeValue?, string, Node> cover, Action<EncoderClaim, string> other)
    {
        Node? encoder = null;
        foreach (EncoderClaim claim in ClaimRoles.Encoders(claims))
        {
            string text = claim.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (encoder is null)
            {
                encoder = cover("Encoder", text.Length > MaxClaimedName ? text[..MaxClaimedName] : text, Explain.Encoder);
                encoder.Add(cover("Confidence", "Low", Explain.Confidence));
                encoder.Add(cover("Evidence", $"{claim.Description} at 0x{claim.Source.Offset:X} names \"{text}\"", Explain.EvidenceText));
            }
            else
            {
                other(claim, text);
            }
        }

        return encoder;
    }
}
