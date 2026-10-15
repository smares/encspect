namespace Encspect.Core;

// The writer of a stream: the software that put the file together around the audio (the muxer, the container
// writer, a tagger), resolved from the claims of the writer role and independent of the encoder. The strongest
// claim names it, a name some software wrote outranking a layout of equal confidence, since a layout may be
// the traces of a writer that only wrapped the packets. Claims that name the same software add evidence, not
// confidence; a claim that names other software is a conflict within the role, reported through differs.
public static class WriterDetection
{
    // The descriptions of the nodes, in wording that holds for every format, since the writer is the software behind the file and not the coding.
    public const string WriterDescription =
        "Which software wrote the file around the audio (the muxer, the container writer or a tagger), as the analyzer concludes from the evidence below. It need not be the software that encoded the audio.";

    public const string ConfidenceDescription =
        "How sure the naming is: medium when the file's layout is the writer's, low when only text in the file, which other software may have written, names it.";

    public const string EvidenceDescription = "What points to the writer, at the bytes that hold it.";

    // The Writer node, made through cover (which gives each node the extent the parser's other detail nodes
    // have), or null where no claim names a writer. differs gets a claim that names other software than the
    // winner, its text, and a phrase for the winner ("the ... names "x"") for the finding; Disagreement words it.
    // nameOf shows a claim's text as the writer's name where the format knows better than ClaimRoles.WriterName, and
    // familyOf says which software a text names where it knows better than ClaimRoles.FamilyOf.
    // With isEvidenceAtSource, each Evidence node covers the bytes of its claim's node, which the Writer node must
    // contain; otherwise it covers what cover gives every node.
    // A stream's format for KnownWriters: format is the kind it passes to KnownWriters and formatLabel the name the finding gives it;
    // otherFormat gets each claim that names software known to write another kind of stream, with the finding's message and the
    // node to point at (the claim's Evidence node where the Writer node has one, else the claim's own node).
    public sealed record StreamFormat(string Kind, string Label, Action<EncoderClaim, string, Node> OtherFormat);

    public static Node? Detect(IEnumerable<EncoderClaim> claims, Func<string, NodeValue?, string, Node> cover,
        Action<EncoderClaim, string, string> differs, Func<string, string>? nameOf = null, bool isEvidenceAtSource = false, Func<string, string>? familyOf = null,
        StreamFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentNullException.ThrowIfNull(differs);

        var candidates = ClaimRoles.Writers(claims)
            .Where(c => c.Text.Trim().Length > 0)
            .Select((claim, index) => (Claim: claim, Index: index))
            .OrderByDescending(x => x.Claim.Confidence)
            .ThenBy(x => x.Claim.IsLayout)
            .ThenBy(x => x.Index)
            .Select(x => x.Claim)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var evidenceOf = new Dictionary<EncoderClaim, Node>();
        EncoderClaim winner = candidates[0];
        nameOf ??= text => ClaimRoles.WriterName(text);
        string name = nameOf(winner.Text);
        familyOf ??= ClaimRoles.FamilyOf;
        string family = familyOf(winner.Text);
        Node writer = cover("Writer", name, WriterDescription);
        writer.Add(cover("Confidence", winner.Confidence.ToString(), ConfidenceDescription));
        writer.Add(EvidenceNode(winner));
        foreach (EncoderClaim claim in candidates.Skip(1))
        {
            if (familyOf(claim.Text) == family)
            {
                writer.Add(EvidenceNode(claim));
            }
            else
            {
                differs(claim, claim.Text.Trim(), Phrase(winner, name));
            }
        }

        if (format is not null)
        {
            foreach (EncoderClaim claim in candidates)
            {
                if (KnownWriters.WritesInstead(claim.Text, format.Kind) is { } writes)
                {
                    string subject = claim.IsLayout ? $"The {Subject(claim)} points to" : $"The {claim.Description} names";
                    format.OtherFormat(claim, $"{subject} \"{Quoted(claim)}\", which writes {writes}, not {format.Label}.", evidenceOf.GetValueOrDefault(claim) ?? claim.Source);
                }
            }
        }

        return writer;

        string Quoted(EncoderClaim claim) => claim.Text.Trim();

        Node EvidenceNode(EncoderClaim claim)
        {
            Node node = isEvidenceAtSource
                ? new Node("Evidence", claim.Source.Offset, claim.Source.Length, Evidence(claim)) { Description = EvidenceDescription }
                : cover("Evidence", Evidence(claim), EvidenceDescription);
            evidenceOf[claim] = node;
            return node;
        }
    }

    private static string Evidence(EncoderClaim claim) => claim.IsLayout
        ? $"{claim.Description} (at 0x{claim.Source.Offset:X})"
        : $"{claim.Description} at 0x{claim.Source.Offset:X} names \"{claim.Text.Trim()}\"";

    // What won, as "but ..." in a finding.
    private static string Phrase(EncoderClaim winner, string name) =>
        winner.IsLayout ? $"the {Subject(winner)} points to \"{name}\"" : $"the {winner.Description} names \"{name}\"";

    // A layout claim's description reads "what it is: the traces", and its subject is the part before the colon.
    public static string Subject(EncoderClaim claim)
    {
        int colon = claim.Description.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? claim.Description[..colon] : claim.Description;
    }

    // The finding for a claim that names other software than the writer the stronger claim named: both are
    // facts about the file, and when they differ one of them was kept when other software rewrote the file.
    public static string Disagreement(EncoderClaim claim, string text, string winner) => claim.IsLayout
        ? $"The {Subject(claim)} points to \"{ClaimRoles.WriterName(text)}\", but {winner}; other software may have rewritten the file, and kept one of them."
        : $"The {claim.Description} names \"{text}\", but {winner}; other software may have rewritten the file, and kept one of them.";
}
