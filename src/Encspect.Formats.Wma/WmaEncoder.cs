using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// The encoder of a Windows Media Audio stream, from what the stream carries, how the container lays out its
// header and what its metadata says (EncoderClaim). Evidence of three strengths:
//
// - The stream's own trait: ffmpeg's wmav1 and wmav2 encoders pad every superframe with 'N' bytes
//   (wmaenc.c), which no other encoder at hand does; with two or more padded superframes, high.
// - A pattern the project's fixtures show Microsoft's encoders keep, or the layout of the header of the
//   Windows Media Format SDK's writer or Media Foundation's ASF sink: medium, since neither is in a
//   specification and the fixtures come from one Windows build.
// - A claim of the encoder role, which whatever software wrote the tags fills in and which survives a re-encode:
//   low. The attributes ASF files carry (WMFSDKVersion, MediaFoundationVersion, WM/EncodingSettings, WM/ToolName
//   and the like) are claims of the writer role, resolved apart (Writer): the Windows Media attribute lists
//   give them to no encoder, and ffmpeg's muxer writes its own version ("Lavf60.16.100") into them whatever
//   encoded the stream.
internal static partial class WmaEncoder
{
    private const int MaxClaimedName = 64;

    // What the stream itself shows.
    public sealed record Trait(string Name, string Family, Confidence Confidence, string Description);

    private sealed record Candidate(string Name, string Family, Confidence Confidence, string Description, EncoderClaim? Claim);

    [GeneratedRegex(@"ffmpeg|lavf|lavc|libav", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FFmpegWords();

    [GeneratedRegex(@"windows media|wmfsdk|media foundation|microsoft", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MicrosoftWords();

    public static string FamilyOf(string text) =>
        FFmpegWords().IsMatch(text) ? "FFmpeg" : MicrosoftWords().IsMatch(text) ? "Microsoft" : text.Trim().ToLowerInvariant();

    // Names the encoder under the stream's node: adds the Encoder node as its first child and returns the
    // name and confidence for the stream's figures, or nulls where nothing names one.
    public static Detected Apply(CodecContext context, Node stream, FindingCollector findings, IReadOnlyList<Trait> traits)
    {
        Node Cover(string name, NodeValue? value, string description) =>
            new(name, stream.Offset, stream.Length, value) { Description = description, Topic = ClaimRoles.TopicOf(name) };

        Node? encoder = Detect(
            traits,
            context.EncoderClaims,
            Cover,
            (claim, text, name) => findings.Add(Severity.Info, "wma.encoder-claim-differs", claim.Source, _ =>
                $"The {claim.Description} names \"{text}\", but the stream points to {name}; the file may have been written by other software, or kept its metadata when the audio was re-encoded."));
        if (encoder is not null)
        {
            stream.Add(encoder);
        }

        // Of claims of one confidence the first names the writer, and the claims are in file order. A muxer's own version goes first where it
        // stands as ffmpeg's ASF muxer leaves it: ffmpeg copies every attribute of the file it reads (asfdec_f.c, asfenc.c) and writes its
        // WM/EncodingSettings as the last of them (mux.c replaces "encoder" by removing it and appending it), so a remux of a Windows Media
        // file carries the earlier writer's WMFSDKVersion or MediaFoundationVersion before the stamp of the program that wrote this file.
        // The stamp is last after a plain remux. ffmpeg removes a creation_time given as metadata (asfenc.c) and any encoder-* key (mux.c), and
        // each removal moves the stamp into the removed entry's place, so an explicit creation_time or encoder-* key can leave attributes after it.
        // No rule in a source tells those files from ones other software wrote, so a stamp with attributes after it keeps its place and the
        // copied claim stays first. The copy stays as a claim that differs; a layout (medium) still outranks the stamp.
        IEnumerable<EncoderClaim> ordered = context.EncoderClaims.OrderBy(c => c.Role == ClaimRole.Writer && c.IsLastItem && ClaimRoles.IsMuxerVersion(c.Text) ? 0 : 1);

        // The writer's evidence lies in the container's header, not in the stream's bytes that every node here covers.
        Node? writer = WriterDetection.Detect(ordered, (name, value, description) => Cover(name, value, name == "Evidence" ? Explain.WriterEvidence : description),
            (claim, text, name) => findings.Add(Severity.Info, "wma.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
            familyOf: FamilyOf,
            format: new WriterDetection.StreamFormat(KnownWriters.WindowsMedia, "Windows Media Audio",
                (claim, message, node) => findings.Add(Severity.Info, "wma.writer-other-format", node, _ => message)));
        if (writer is not null)
        {
            stream.Add(writer);
        }

        ClaimRoles.CompleteVersion(encoder, context.EncoderClaims);
        (string? encoderName, Confidence? encoderConfidence) = ClaimRoles.Resolved(encoder);
        (string? writerName, Confidence? writerConfidence) = ClaimRoles.Resolved(writer);
        return new Detected(encoderName, encoderConfidence, writerName, writerConfidence);
    }

    // What the stream's figures take from the detection.
    public readonly record struct Detected(string? Encoder, Confidence? EncoderConfidence, string? Writer, Confidence? WriterConfidence);

    // The Encoder node, covering the stream, or null where nothing names one. differs reports a claim that
    // names another encoder than the evidence points to.
    public static Node? Detect(IReadOnlyList<Trait> traits, IReadOnlyList<EncoderClaim> claims, Func<string, NodeValue?, string, Node> cover, Action<EncoderClaim, string, string> differs)
    {
        var candidates = new List<Candidate>();
        foreach (Trait trait in traits)
        {
            candidates.Add(new Candidate(trait.Name, trait.Family, trait.Confidence, trait.Description, null));
        }

        bool ffmpegStream = traits.Any(t => t.Family == "FFmpeg");
        foreach (EncoderClaim claim in ClaimRoles.Encoders(claims))
        {
            string text = claim.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            string family = FamilyOf(text);
            // Only an encoder ID reaches here (a muxer's version is a writer claim), and ffmpeg's ID names the encoder only where the stream shows ffmpeg's.
            if (family == "FFmpeg" && !ffmpegStream)
            {
                continue;
            }

            string where = $"at 0x{claim.Source.Offset:X}";
            string description = claim.Confidence == Confidence.Low ? $"{claim.Description} {where} names \"{text}\"" : $"{claim.Description} ({where})";
            candidates.Add(new Candidate(text.Length > MaxClaimedName ? text[..MaxClaimedName] : text, family, claim.Confidence, description, claim));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // The strongest evidence names the encoder; among equals a claim's name, which is the more specific.
        Candidate primary = candidates
            .Select((c, i) => (Candidate: c, Index: i))
            .OrderByDescending(x => x.Candidate.Confidence)
            .ThenByDescending(x => x.Candidate.Claim is not null)
            .ThenBy(x => x.Index)
            .First().Candidate;

        Node encoder = cover("Encoder", primary.Name, Explain.Encoder);
        encoder.Add(cover("Confidence", primary.Confidence.ToString(), Explain.Confidence));
        encoder.Add(cover("Evidence", primary.Description, Explain.Evidence));
        foreach (Candidate other in candidates)
        {
            if (other == primary)
            {
                continue;
            }

            if (other.Family == primary.Family)
            {
                encoder.Add(cover("Evidence", other.Description, Explain.Evidence));
            }
            else if (other.Claim is not null)
            {
                differs(other.Claim, other.Name, primary.Name);
            }
        }

        return encoder;
    }
}
