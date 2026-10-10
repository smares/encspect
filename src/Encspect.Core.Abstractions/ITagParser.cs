namespace Encspect.Core;

// Tag parsers only look where a tag's specification allows it to be: starting at a given
// offset (the start of the file or the end of a previous tag) or ending at a given offset (the
// end of the file or the start of a following tag). The host decides which anchors to probe,
// in which order, and which candidate owns the bytes.
public interface ITagParser
{
    string Name { get; }

    // The name of the tag that must directly follow this one, for blocks that only extend
    // another tag (the Enhanced TAG before ID3v1). The host rejects the candidate otherwise.
    string? MustPrecede => null;

    // Probes for a tag that starts exactly at start. Returns null when the signature is absent.
    TagProbe? ProbeAt(ByteReader reader, long start) => null;

    // Probes for a tag that ends exactly at end. Returns null when the signature is absent.
    TagProbe? ProbeBefore(ByteReader reader, long end) => null;
}
