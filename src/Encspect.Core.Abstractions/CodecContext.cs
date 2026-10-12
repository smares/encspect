namespace Encspect.Core;

// What a codec parser gets from the container: either one contiguous payload (Payload) or packets
// (Packets), the node to add its nodes under, and what the container stores about the coding
// elsewhere (Configuration). Positions in all of them are file offsets.
public sealed class CodecContext : AnalysisContext
{
    public CodecContext(Node parent, ByteReader payload, IReadOnlyList<EncoderClaim>? encoderClaims = null, IParserRouter? router = null,
        CodecConfiguration? configuration = null)
        : base((payload ?? throw new ArgumentNullException(nameof(payload))).CancellationToken, encoderClaims, router)
    {
        ArgumentNullException.ThrowIfNull(parent);

        Parent = parent;
        Payload = payload;
        Configuration = configuration;
    }

    public CodecContext(Node parent, IEnumerable<MappedBytes> packets, CancellationToken cancellationToken, IReadOnlyList<EncoderClaim>? encoderClaims = null,
        IParserRouter? router = null, CodecConfiguration? configuration = null)
        : base(cancellationToken, encoderClaims, router)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(packets);

        Parent = parent;
        Packets = packets;
        Configuration = configuration;
    }

    // The container's node for the payload, which takes the codec's nodes as children.
    public Node Parent { get; }

    // The payload as one byte range, or null when the container hands packets.
    public ByteReader? Payload { get; }

    // The packets in stream order, or null when the container hands one payload. Enumerate them
    // once.
    public IEnumerable<MappedBytes>? Packets { get; }

    // What the container stores about the coding apart from the data, such as AAC's
    // AudioSpecificConfig in MP4, or null.
    public CodecConfiguration? Configuration { get; }
}
