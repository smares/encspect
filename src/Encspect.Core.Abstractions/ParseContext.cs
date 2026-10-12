namespace Encspect.Core;

public sealed class ParseContext
{
    private readonly List<Finding> _findings = [];
    private readonly List<StreamInfo> _streams = [];

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

    // Reads check it already; loops that only compute, over every frame or packet, check it every
    // few thousand iterations.
    public CancellationToken CancellationToken => Reader.CancellationToken;

    public Node Root { get; }

    // Tags inside the reader's range (found through an ID3v2.4 SEEK frame) that the format
    // parser must skip; they are already in the report.
    public IReadOnlyList<Node> EmbeddedTags { get; }

    // What the file's tags say about the encoder, in file order.
    public IReadOnlyList<EncoderClaim> EncoderClaims { get; }

    public IReadOnlyList<Finding> Findings => _findings;

    public IReadOnlyList<StreamInfo> Streams => _streams;

    // count: how many cases a finding reported once per stream stands for.
    public void AddFinding(Severity severity, string code, string message, Node? node = null, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        _findings.Add(new Finding(severity, code, message, node) { Count = count });
    }

    public void AddStream(StreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _streams.Add(stream);
    }
}
