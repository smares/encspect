namespace Encspect.Core;

public sealed class ParseContext : AnalysisContext
{
    public ParseContext(ByteReader reader, Node root, IReadOnlyList<Node>? embeddedTags = null, IReadOnlyList<EncoderClaim>? encoderClaims = null,
        IParserRouter? router = null)
        : base((reader ?? throw new ArgumentNullException(nameof(reader))).CancellationToken, encoderClaims, router)
    {
        ArgumentNullException.ThrowIfNull(root);

        Reader = reader;
        Root = root;
        EmbeddedTags = embeddedTags ?? [];
    }

    public ByteReader Reader { get; }

    public Node Root { get; }

    // Tags inside the reader's range (found through an ID3v2.4 SEEK frame) that the format
    // parser must skip; they are already in the report.
    public IReadOnlyList<Node> EmbeddedTags { get; }
}
