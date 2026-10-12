namespace Encspect.Core;

// One instance serves every analysis, also on several threads at once: keep no per-call state in
// fields. An exception other than EndOfStreamException (truncation) and InvalidDataException
// (a message for the report) is a parser bug, which the host reports as a parse.failed error.
public interface IFormatParser
{
    string Name { get; }

    // Returns a confidence from 0 (not this format) to 100 (certain). The host resets the
    // reader position afterward, so implementations may move it freely. The extension hint may
    // only break ties between formats without a signature, never make a parser accept.
    int Sniff(SniffContext context);

    void Parse(ParseContext context);
}
