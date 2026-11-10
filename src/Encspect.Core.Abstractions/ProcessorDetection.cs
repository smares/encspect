namespace Encspect.Core;

// The processor of a stream: software that changed the audio between its source and the encoder (lossyWAV turns PCM into PCM), resolved from the
// claims of the processor role and independent of the encoder and the writer. The strongest claim names it; claims that name the same software add
// evidence, and a claim that names other software adds none. A claim from behavior names the software without its version, so it adds evidence to a claim that names it with one.
public static class ProcessorDetection
{
    public const string ProcessorDescription =
        "Which software changed the audio before it was encoded, as the analyzer concludes from the evidence below. It is neither the encoder nor the writer of the file.";

    public const string ConfidenceDescription = "How sure the naming is: high for text that the software itself writes into the file, which its own check takes for its output; low for a pattern in the coded counts that its output shows, which other software could also make.";

    public const string EvidenceDescription = "What points to the processor, at the bytes that hold it.";

    // The Processor node, made through cover (which gives each node the extent the parser's other detail nodes have, so the Processor node holds its Evidence), or null where no
    // claim names a processor. The evidence text gives the offset of the bytes that hold the claim.
    public static Node? Detect(IEnumerable<EncoderClaim> claims, Func<string, NodeValue?, string, Node> cover, Action<EncoderClaim, string, string>? differs = null)
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
        foreach (EncoderClaim claim in candidates.Where(c => Names(c, name)))
        {
            processor.Add(cover("Evidence", Evidence(claim), EvidenceDescription));
        }

        // A text that names other software (or another version of it) is reported, as the writer role reports its disagreements; a claim from behavior names no version and disagrees with none.
        if (differs is not null)
        {
            string winnerText = $"the processor is taken from {Evidence(winner)}";
            foreach (EncoderClaim claim in candidates.Where(c => !c.IsBehavior && !Names(c, name)))
            {
                differs(claim, claim.Text.Trim(), winnerText);
            }
        }

        return processor;
    }

    // The finding for a text that names other processor software than the stronger claim does.
    public static string Disagreement(EncoderClaim claim, string text, string winner) =>
        $"The {claim.Description} names \"{text}\", but {winner}; other software may have processed the audio again, and kept one of them.";

    // The claim of the processor role that a pattern in the coded syntax makes (lossyWAV's counts of wasted bits): low confidence, the evidence as the
    // description. wording names the unit the counts are of; source is the node the claim points to.
    public static EncoderClaim BehaviorClaim(LossyWavPattern pattern, Node source, WastedBitsWording wording)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return new EncoderClaim("lossyWAV", source, pattern.Evidence(wording.Plural, wording.Between), ClaimRole.Processor) { Confidence = Confidence.Low, IsBehavior = true };
    }

    // Whether the claim names the software name does: the same text, or a claim from behavior, which cannot know a version, for the software
    // whose name the winner starts with ("lossyWAV" for "lossyWAV 1.4.2").
    private static bool Names(EncoderClaim claim, string name)
    {
        string text = claim.Text.Trim();
        return string.Equals(text, name, StringComparison.Ordinal) || (claim.IsBehavior && name.StartsWith(text + " ", StringComparison.Ordinal));
    }

    private static string Evidence(EncoderClaim claim) => claim.IsBehavior ? claim.Description : claim.Settings is { Length: > 0 } settings
        ? $"{claim.Description} at 0x{claim.Source.Offset:X} names \"{claim.Text.Trim()}\" with the settings \"{settings}\""
        : $"{claim.Description} at 0x{claim.Source.Offset:X} names \"{claim.Text.Trim()}\"";
}
