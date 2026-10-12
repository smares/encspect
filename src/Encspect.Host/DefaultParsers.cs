using Encspect.Core;
using Encspect.Formats.Mpeg;
using Encspect.Tags.Ape;
using Encspect.Tags.Id3;
using Encspect.Tags.Lyrics3;
using Encspect.Tags.VorbisComment;

namespace Encspect.Host;

public static class DefaultParsers
{
    // The order is the evaluation order at each anchor (plan, "Tag detection and false
    // positives", rule 3): APE, Lyrics3, ID3v2 found through its footer, then ID3v1. ID3v1 has
    // almost nothing to validate, so it goes after the others; otherwise a "TAG" at EOF-128 inside
    // a validated APE or appended ID3v2.4 tag would be claimed as an ID3v1 tag. The Enhanced TAG
    // has only a 4-byte signature and goes last for the same reason.
    public static IReadOnlyList<ITagParser> Tags { get; } =
    [
        new ApeTagParser(),
        new Lyrics3TagParser(),
        new Id3v2TagParser(),
        new Id3v1TagParser(),
        new Id3v1EnhancedTagParser(),

        // Never found in a file, only where a container or codec says one is (ParseTag).
        new VorbisCommentTagParser(),
    ];

    private static readonly MpegAudioParser Mpeg = new();

    public static IReadOnlyList<IFormatParser> Formats { get; } = [Mpeg];

    // The codecs containers hand their payloads to, by ID: MPEG audio in WAV and MP4.
    public static IReadOnlyList<ICodecParser> Codecs { get; } = [Mpeg];
}
