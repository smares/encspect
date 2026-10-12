namespace Encspect.Core;

// What the context of every parser that reports data offers: findings, streams, what tags say about
// the encoder, cancellation, and the routes through which a container hands bytes to the codec and
// tag parsers that know them. Parsers never reference each other; they reach other parsers only
// through these routes, which the host serves.
public abstract class AnalysisContext
{
    private readonly List<Finding> _findings = [];
    private readonly List<StreamInfo> _streams = [];
    private readonly List<EncoderClaim> _encoderClaims;
    private readonly IParserRouter? _router;

    private protected AnalysisContext(CancellationToken cancellationToken, IReadOnlyList<EncoderClaim>? encoderClaims, IParserRouter? router)
    {
        CancellationToken = cancellationToken;
        _encoderClaims = [.. encoderClaims ?? []];
        _router = router;
    }

    // Reads check it already; loops that only compute, over every frame or packet, check it every
    // few thousand iterations.
    public CancellationToken CancellationToken { get; }

    // What the file's tags say about the encoder, in file order, including tags parsed through
    // ParseTag so far.
    public IReadOnlyList<EncoderClaim> EncoderClaims => _encoderClaims;

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

    // Kept in file order, after claims from the same offset.
    public void AddEncoderClaim(EncoderClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        int index = _encoderClaims.FindLastIndex(c => c.Source.Offset <= claim.Source.Offset);
        _encoderClaims.Insert(index + 1, claim);
    }

    // Hands one contiguous payload, such as a WAV data chunk of MPEG audio, to the codec parser
    // with that ID, which adds its nodes under parent; its findings and streams join this
    // context's. Returns false when no codec parser has the ID, and reports nothing then. Damage
    // and parser failures inside the payload become findings on parent, so the container carries
    // on; only cancellation passes through. configuration is what the container stores about the
    // coding elsewhere, such as AAC's AudioSpecificConfig, or null.
    public bool ParseStream(string codecId, Node parent, ByteReader payload, CodecConfiguration? configuration = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(codecId);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(payload);

        return _router?.ParseStream(this, codecId, parent, payload, configuration) ?? false;
    }

    // As ParseStream, for a codec whose data the container hands over as packets, such as Ogg
    // pages or MP4 samples. The packets are enumerated once, while the codec parser runs, so a
    // container may read them lazily; it reports damage in its own pages and ends the sequence
    // rather than throwing.
    public bool ParsePackets(string codecId, Node parent, IEnumerable<MappedBytes> packets, CodecConfiguration? configuration = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(codecId);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(packets);

        return _router?.ParsePackets(this, codecId, parent, packets, configuration) ?? false;
    }

    // Hands a tag that a container holds whole, such as the ID3v2 tag in a RIFF or AIFF chunk, to
    // the tag parser of that name (ITagParser.Name). The tag must start at the start of slice and
    // fit in it. An accepted tag is added under parent as the host shows top-level tags, with its
    // Detection; its findings and encoder claims join this context's. Returns the added node, or
    // null when no tag parser has the name (nothing is reported then) or no tag was accepted (a
    // finding on parent says why).
    public Node? ParseTag(string tagName, ByteReader slice, Node parent)
    {
        ArgumentException.ThrowIfNullOrEmpty(tagName);
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(parent);

        return _router?.ParseTag(this, tagName, slice, parent);
    }

    // As ParseTag, for a tag that a container or codec took apart, such as the Vorbis comment
    // packet of an Ogg stream, which may span pages: the tag starts at the first byte and its nodes
    // point at the file through the bytes' map (ITagParser.ParseWhole with mapped bytes).
    public Node? ParseTag(string tagName, MappedBytes tag, Node parent)
    {
        ArgumentException.ThrowIfNullOrEmpty(tagName);
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(parent);

        return _router?.ParseTag(this, tagName, tag, parent);
    }
}
