using Encspect.Core;
using Encspect.Tags.Ape;
using Encspect.Tags.Id3;
using Encspect.Tags.Lyrics3;

namespace Encspect.Host;

public static class DefaultParsers
{
    // The order is the evaluation order at each anchor (plan, "Tag detection and false
    // positives", rule 3): APE, Lyrics3, ID3v2 found through its footer, then ID3v1. ID3v1 has
    // almost nothing to validate, so it goes last; otherwise a "TAG" at EOF-128 inside a validated
    // APE or appended ID3v2.4 tag would be claimed as an ID3v1 tag.
    public static IReadOnlyList<ITagParser> Tags { get; } =
    [
        new ApeTagParser(),
        new Lyrics3TagParser(),
        new Id3v2TagParser(),
        new Id3v1TagParser(),
    ];

    public static IReadOnlyList<IFormatParser> Formats { get; } = [];
}
