namespace Encspect.Core;

public enum Confidence
{
    Low,
    Medium,
    High,
}

// One reason a candidate was accepted, pointing at the bytes that support it.
public sealed record Evidence(string Description, long Offset, long Length);

// The outcome of probing one anchor: either a validated tag with its node tree, or a signature
// that was found but failed structural validation.
public sealed class TagProbe
{
    private TagProbe(
        long signatureOffset,
        Node? tag,
        Confidence confidence,
        IReadOnlyList<Evidence> evidence,
        IReadOnlyList<Finding> findings,
        string? rejectionReason)
    {
        SignatureOffset = signatureOffset;
        Tag = tag;
        Confidence = confidence;
        Evidence = evidence;
        Findings = findings;
        RejectionReason = rejectionReason;
    }

    // Where the signature that triggered the probe sits, which is not always the tag start (an
    // APE footer, a Lyrics3 end marker).
    public long SignatureOffset { get; }

    public Node? Tag { get; }

    public bool IsAccepted => Tag is not null;

    public Confidence Confidence { get; }

    public IReadOnlyList<Evidence> Evidence { get; }

    public IReadOnlyList<Finding> Findings { get; }

    public string? RejectionReason { get; }

    public static TagProbe Accept(
        Node tag,
        long signatureOffset,
        Confidence confidence,
        IReadOnlyList<Evidence> evidence,
        IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(findings);
        if (tag.Length == 0)
        {
            throw new ArgumentException("An accepted tag must cover at least one byte.", nameof(tag));
        }

        return new TagProbe(signatureOffset, tag, confidence, evidence, findings, null);
    }

    public static TagProbe Reject(long signatureOffset, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        return new TagProbe(signatureOffset, null, Confidence.Low, [], [], reason);
    }
}
