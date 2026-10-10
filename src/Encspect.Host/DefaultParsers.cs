using Encspect.Core;
using Encspect.Tags.Ape;
using Encspect.Tags.Id3;
using Encspect.Tags.Lyrics3;

namespace Encspect.Host;

public static class DefaultParsers
{
    // The order is the evaluation order at each anchor (plan, "Tag detection and false
    // positives", rule 3): APE before Lyrics3 before ID3v1, so a validated APE tag claims the
    // "TAG" inside "APETAGEX" before ID3v1 can.
    public static IReadOnlyList<ITagParser> Tags { get; } =
    [
        new ApeTagParser(),
        new Lyrics3TagParser(),
        new Id3v1TagParser(),
        new Id3v2TagParser(),
    ];

    public static IReadOnlyList<IFormatParser> Formats { get; } = [];
}
