namespace Encspect.Core;

public sealed class ParseContext : AnalysisContext
{
    public ParseContext(ByteReader reader, Node root, IReadOnlyList<Node>? embeddedTags = null, IReadOnlyList<EncoderClaim>? encoderClaims = null,
        IParserRouter? router = null, AnalysisContext? decodingOwner = null)
        : base((reader ?? throw new ArgumentNullException(nameof(reader))).CancellationToken, encoderClaims, router, decodingOwner)
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

    // Asks the host for the file that belongs with this one by its extension (no dot, lower case: "wvc", "ofc"): the companion's name is the input's
    // without its last extension and then this one, in the folder of the input, which the host opens as a stream. A parser asks once per stream and
    // only for a stream that may have a companion; the host, never the parser, opens it. Found gives the parser a context over the companion to build
    // its nodes, findings and streams in; they become a report of their own in the report (Report.Companions). Missing and Unchecked (no folder
    // to look in: the input is a stream, or the caller passed none) give no context. A context the host does not serve gives Unchecked too.
    public Companion OpenCompanion(string extension)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);

        return Router?.OpenCompanion(this, extension) ?? Companion.Unchecked(string.Empty, "the analysis has no host to look for it");
    }
}
