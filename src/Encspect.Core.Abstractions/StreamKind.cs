namespace Encspect.Core;

// What a stream carries. Most streams are audio; a container reports a stream of another kind only so that what it says of the stream, such as its
// protection, is not lost. The summary of a report describes the file by its audio streams.
public enum StreamKind
{
    Audio,

    Video,

    // Any other kind of stream, such as commands or data.
    Other,
}
