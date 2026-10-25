namespace Encspect.Core;

// The parser of one coding inside a container, which the container reaches through
// AnalysisContext.ParseStream or ParsePackets: Ogg hands Vorbis and Opus packets on, MP4 its AAC
// and ALAC samples, WAV and MP4 their MPEG audio. One instance serves every analysis, also on
// several threads at once: keep no per-call state in fields. Like a format parser it throws only
// EndOfStreamException (truncation), InvalidDataException (a message for the report) and
// OperationCanceledException.
public interface ICodecParser
{
    // The ID containers route by, such as "mpeg-audio": lower case, named after the coding, not
    // after a container's code for it.
    string Id { get; }

    void Parse(CodecContext context);

    // Whether the payload starts with a stream of this coding that Parse would read, by the evidence the coding's own raw files are
    // recognized by (a chain of consistent frames, not a sync word alone). A container asks where its own fields do not say what
    // the data holds: the 16-bit samples of a PCM WAVE file may carry a DTS stream. It reads at most the start of the payload and
    // reports nothing. A parser that has no such evidence leaves the default, which says no.
    bool Recognizes(ByteReader payload) => false;
}
