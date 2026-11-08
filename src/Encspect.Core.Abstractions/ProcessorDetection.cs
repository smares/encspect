namespace Encspect.Core;

// The processor of a stream: software that changed the audio between its source and the encoder (lossyWAV turns PCM into PCM), resolved from the
// claims of the processor role and independent of the encoder and the writer. The strongest claim names it; claims that name the same software add
// evidence, and a claim that names other software adds none.
public static class ProcessorDetection
{
    public const string ProcessorDescription =
        "Which software changed the audio before it was encoded, as the analyzer concludes from the evidence below. It is neither the encoder nor the writer of the file.";

    public const string ConfidenceDescription = "How sure the naming is: high for text that the software itself writes into the file, which its own check takes for its output.";

    public const string EvidenceDescription = "What points to the processor, at the bytes that hold it.";

    // The Processor node, made through cover (which gives each node the extent the parser's other detail nodes have), or null where no claim names a
    // processor. Each Evidence node covers the bytes of its claim's node.
    public static Node? Detect(IEnumerable<EncoderClaim> claims, Func<string, NodeValue?, string, Node> cover)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(cover);

        var candidates = ClaimRoles.Processors(claims)
            .Where(c => c.Text.Trim().Length > 0)
            .Select((claim, index) => (Claim: claim, Index: index))
            .OrderByDescending(x => x.Claim.Confidence)
            .ThenBy(x => x.Index)
            .Select(x => x.Claim)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        EncoderClaim winner = candidates[0];
        string name = winner.Text.Trim();
        Node processor = cover("Processor", name, ProcessorDescription);
        processor.Add(cover("Confidence", winner.Confidence.ToString(), ConfidenceDescription));
        foreach (EncoderClaim claim in candidates.Where(c => string.Equals(c.Text.Trim(), name, StringComparison.Ordinal)))
        {
            processor.Add(new Node("Evidence", claim.Source.Offset, claim.Source.Length, Evidence(claim)) { Description = EvidenceDescription, Ranges = claim.Source.Ranges });
        }

        return processor;
    }

    private static string Evidence(EncoderClaim claim) => claim.Settings is { Length: > 0 } settings
        ? $"{claim.Description} at 0x{claim.Source.Offset:X} names \"{claim.Text.Trim()}\" with the settings \"{settings}\""
        : $"{claim.Description} at 0x{claim.Source.Offset:X} names \"{claim.Text.Trim()}\"";
}
