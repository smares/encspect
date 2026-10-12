namespace Encspect.Core;

// Tag parsers only look where a tag's specification allows it to be: starting at a given
// offset (the start of the file or the end of a previous tag) or ending at a given offset (the
// end of the file or the start of a following tag). The host decides which anchors to probe,
// in which order, and which candidate owns the bytes.
//
// One instance serves every analysis, also on several threads at once: keep no per-call state in
// fields. A probe that throws anything but EndOfStreamException or InvalidDataException is a
// parser bug: the host rejects the candidate and reports a parse.failed error.
public interface ITagParser
{
    string Name { get; }

    // The name of the tag that the specification places directly after this one (ID3v1 after
    // Lyrics3 and after the Enhanced TAG). Without it the host still reports the tag, with a
    // layout finding and one level less confidence.
    string? MustPrecede => null;

    // Probes for a tag that starts exactly at start. Returns null when the signature is absent.
    TagProbe? ProbeAt(TagProbeContext context, long start) => null;

    // Probes for a tag that ends exactly at end. Returns null when the signature is absent.
    TagProbe? ProbeBefore(TagProbeContext context, long end) => null;

    // Searches [from, limit) for the first tag that starts there and validates with full
    // confidence. Only used where a previous tag points at a later one (TagProbe.NextTagOffset),
    // since it is the one search not tied to a fixed position.
    TagProbe? FindFrom(TagProbeContext context, long from, long limit) => null;

    // Parses a tag that a container holds whole (AnalysisContext.ParseTag), such as the ID3v2 tag
    // in a RIFF or AIFF chunk: the context's reader is limited to the container's bytes for it, and
    // the tag starts at the reader's start. Returns null when the signature is absent.
    TagProbe? ParseWhole(TagProbeContext context) => ProbeAt(context, context.Reader.Start);

    // Parses a tag that a container or codec took apart (AnalysisContext.ParseTag with mapped
    // bytes), such as a Vorbis comment packet that spans Ogg pages: the tag starts at the first
    // byte, and its nodes point at the file through the bytes' map. Returns null when the parser
    // does not read tags from mapped bytes or the signature is absent.
    TagProbe? ParseWhole(MappedBytes tag, CancellationToken cancellationToken) => null;
}
