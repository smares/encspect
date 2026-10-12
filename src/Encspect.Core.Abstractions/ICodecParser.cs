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
}
