namespace Encspect.Core;

public enum Confidence
{
    Low,
    Medium,
    High,
}

// One reason a candidate was accepted, pointing at the bytes that support it.
public sealed record Evidence(string Description, long Offset, long Length);

// What the claim is evidence of. An encoder turns the audio into the coded stream; a writer puts
// that stream into a file (a muxer, a container writer, a tagger) and need not be the encoder: a
// file can be remuxed, or tagged by other software, without its audio being touched. The two are
// resolved apart, each with a confidence and evidence of its own. There is no default, so a claim
// that forgot its role is an error.
public enum ClaimRole
{
    Encoder = 1,
    Writer = 2,
}

// What a tag or a container says about the software behind the file (an ID3v2 TSSE frame, an MP4
// tool atom), and the node that holds it. Format parsers weigh it against what the audio itself
// shows, among the claims of its own role. A claim is low confidence, since whatever software wrote
// the tag fills it in. A container that recognizes the writer from the way it lays out its own
// structure (not from a name it was given) makes a layout claim (IsLayout), with the confidence the
// layout supports.
public sealed record EncoderClaim(string Text, Node Source, string Description, ClaimRole Role)
{
    // The role the claim is evidence of, given by the parser that makes it: where the specification
    // of the field puts it, and for a field the specification leaves open, the writer.
    public ClaimRole Role { get; } = Enum.IsDefined(Role) ? Role : throw new ArgumentOutOfRangeException(nameof(Role), Role, "A claim needs the role it is evidence of.");

    public Confidence Confidence { get; init; } = Confidence.Low;

    // Whether the claim comes from the way a file is laid out and not from a name some software wrote. It names the writer of the layout,
    // which need not be the encoder (a writer can lay a file out around packets it only copied), and its description reads
    // "what the layout is: the traces", the part before the colon being the subject of a finding.
    public bool IsLayout { get; init; }
}

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

    // The earliest offset at which the tag says another tag of its kind starts (an ID3v2.4 SEEK
    // frame), or null.
    public long? NextTagOffset { get; private init; }

    public IReadOnlyList<EncoderClaim> EncoderClaims { get; private init; } = [];

    public static TagProbe Accept(
        Node tag,
        long signatureOffset,
        Confidence confidence,
        IReadOnlyList<Evidence> evidence,
        IReadOnlyList<Finding> findings,
        long? nextTagOffset = null,
        IReadOnlyList<EncoderClaim>? encoderClaims = null)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(findings);
        if (tag.Length == 0)
        {
            throw new ArgumentException("An accepted tag must cover at least one byte.", nameof(tag));
        }

        return new TagProbe(signatureOffset, tag, confidence, evidence, findings, null)
        {
            NextTagOffset = nextTagOffset,
            EncoderClaims = encoderClaims ?? [],
        };
    }

    public static TagProbe Reject(long signatureOffset, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        return new TagProbe(signatureOffset, null, Confidence.Low, [], [], reason);
    }
}
