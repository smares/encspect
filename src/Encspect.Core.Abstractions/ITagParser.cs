namespace Encspect.Core;

// Tag parsers only look where a tag's specification allows it to be: starting at a given
// offset (the start of the file or the end of a previous tag) or ending at a given offset (the
// end of the file or the start of a following tag). The host decides which anchors to probe,
// in which order, and which candidate owns the bytes.
public interface ITagParser
{
    string Name { get; }

    // The name of the tag that the specification places directly after this one (ID3v1 after
    // Lyrics3 and after the Enhanced TAG). Without it the host still reports the tag, with a
    // layout finding and one level less confidence.
    string? MustPrecede => null;

    // Probes for a tag that starts exactly at start. Returns null when the signature is absent.
    TagProbe? ProbeAt(ByteReader reader, long start) => null;

    // Probes for a tag that ends exactly at end. Returns null when the signature is absent.
    TagProbe? ProbeBefore(ByteReader reader, long end) => null;

    // Searches [from, limit) for the first tag that starts there and validates with full
    // confidence. Only used where a previous tag points at a later one (TagProbe.NextTagOffset),
    // since it is the one search not tied to a fixed position.
    TagProbe? FindFrom(ByteReader reader, long from, long limit) => null;
}
