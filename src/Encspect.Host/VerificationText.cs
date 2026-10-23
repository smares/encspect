using Encspect.Core;

namespace Encspect.Host;

// How a stream's verification reads in the text output and in the front ends: "yes (frame CRC-16 40 of 40, STREAMINFO MD5
// 1 of 1)", "no (STREAMINFO MD5 1 of 1 differ)" or "unknown (STREAMINFO MD5 not compared: decoding was not asked for)".
// "n of m" is the values compared of the values stored; a failed checksum reads "n of m differ", the differing values of
// those compared.
public static class VerificationText
{
    // The text after "Verified: ".
    public static string Describe(StreamVerification verification)
    {
        ArgumentNullException.ThrowIfNull(verification);

        var parts = new List<string>();
        if (verification.IsVerified == true && verification.IsLossy)
        {
            parts.Add("lossy stream");
        }

        bool anyProblem = false;
        foreach (ChecksumTally tally in verification.Checksums.Where(t => t.Stored > 0))
        {
            if (tally.Failed > 0)
            {
                parts.Add($"{tally.Name} {tally.Failed} of {tally.Checked} differ");
            }
            else if (tally.NotChecked > 0)
            {
                anyProblem = true;
                string reason = tally.Covers == ChecksumCoverage.DecodedAudio ? $": {Reason(verification, tally)}" : string.Empty;
                parts.Add(tally.Checked == 0 ? $"{tally.Name} not compared{reason}" : $"{tally.Name} {tally.Checked} of {tally.Stored} compared{reason}");
            }
            else
            {
                parts.Add($"{tally.Name} {tally.Checked} of {tally.Stored}");
            }
        }

        if (verification.IsVerified is null && !anyProblem && verification.Checksums.All(t => t.Failed == 0))
        {
            parts.Add(verification.Checksums.Any(t => t.Stored > 0) ? "no checksum that was compared covers all of the audio" : "the stream stores no checksum");
        }

        string details = string.Join(", ", parts);
        if (verification.IsVerified == true && verification.IsLossy)
        {
            details = "lossy stream; " + string.Join(", ", parts.Skip(1));
        }

        string verdict = verification.IsVerified switch { true => "yes", false => "no", null => "unknown" };
        return details.Length == 0 ? verdict : $"{verdict} ({details})";
    }

    private static string Reason(StreamVerification verification, ChecksumTally tally) =>
        verification is { Decoding: DecodeStatus.Decoded, IsLossy: true } && tally.Checked == 0
        && verification.Checksums.Where(t => t.Stored > 0 && t.Covers == ChecksumCoverage.DecodedAudio && t != tally).All(t => t.NotChecked == 0)
            ? "the stream decodes to lossy audio, and the checksum is of the lossless audio"
            : Reason(verification.Decoding);

    private static string Reason(DecodeStatus status) => status switch
    {
        DecodeStatus.NotRequested => "decoding was not asked for",
        DecodeStatus.NoDecoder => "no decoder handles this coding",
        DecodeStatus.NotPossible => "the stream cannot be decoded",
        DecodeStatus.Stopped => "decoding was stopped",
        DecodeStatus.Failed => "the decoder failed",
        _ => "the decoded stream is incomplete: a frame was not decoded, a frame is missing, or the samples differ from the stated count",
    };
}
