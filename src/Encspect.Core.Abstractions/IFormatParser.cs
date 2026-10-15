namespace Encspect.Core;

// One instance serves every analysis, also on several threads at once: keep no per-call state in
// fields. An exception other than EndOfStreamException (truncation) and InvalidDataException
// (a message for the report) is a parser bug, which the host reports as a parse.failed error.
public interface IFormatParser
{
    string Name { get; }

    // Returns the evidence for this format: SniffResult.None (class 0) when the data is not this
    // format, otherwise its class, where the matched structure starts and how many consistent and
    // checksum-verified frames were found. The host ranks the parsers' results. It resets the
    // reader position afterward, so implementations may move it freely. The extension hint may
    // only break ties between formats without a signature, never make a parser accept.
    SniffResult Sniff(SniffContext context);

    // For a container that states its own size (RIFF, FORM, a run of top-level MP4 boxes): the file
    // offset where it ends by its own account, which may lie past the end of a truncated file. The
    // host asks the parser that sniffs best, on the data after the leading tags up to the end of
    // the file, before it looks for trailing tags: a candidate that starts before this end is the
    // container's own chunk, not a tag of the file, and stays in the data. Null for a format
    // without a declared size.
    long? DeclaredEnd(SniffContext context) => null;

    void Parse(ParseContext context);
}
