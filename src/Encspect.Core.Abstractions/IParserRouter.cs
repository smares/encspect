namespace Encspect.Core;

// The host's side of the routes in AnalysisContext: it finds the codec or tag parser, runs it
// inside the exception boundary and merges what it reports into the caller's context. Parsers call
// the routes on their context, never this interface.
public interface IParserRouter
{
    bool ParseStream(AnalysisContext caller, string codecId, Node parent, ByteReader payload, CodecConfiguration? configuration);

    bool ParsePackets(AnalysisContext caller, string codecId, Node parent, IEnumerable<MappedBytes> packets, CodecConfiguration? configuration);

    Node? ParseTag(AnalysisContext caller, string tagName, ByteReader slice, Node parent);

    Node? ParseTag(AnalysisContext caller, string tagName, MappedBytes tag, Node parent);
}
