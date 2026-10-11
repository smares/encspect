namespace Encspect.Core;

public sealed class ParseContext
{
    private readonly List<Finding> _findings = [];

    public ParseContext(ByteReader reader, Node root, IReadOnlyList<Node>? embeddedTags = null, IReadOnlyList<EncoderClaim>? encoderClaims = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(root);

        Reader = reader;
        Root = root;
        EmbeddedTags = embeddedTags ?? [];
        EncoderClaims = encoderClaims ?? [];
    }

    public ByteReader Reader { get; }

    public Node Root { get; }

    // Tags inside the reader's range (found through an ID3v2.4 SEEK frame) that the format
    // parser must skip; they are already in the report.
    public IReadOnlyList<Node> EmbeddedTags { get; }

    // What the file's tags say about the encoder, in file order.
    public IReadOnlyList<EncoderClaim> EncoderClaims { get; }

    public IReadOnlyList<Finding> Findings => _findings;

    public void AddFinding(Severity severity, string code, string message, Node? node = null)
    {
        _findings.Add(new Finding(severity, code, message, node));
    }
}
