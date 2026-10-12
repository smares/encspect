using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Formats.Mp4;
using Encspect.Formats.Mpeg;
using Encspect.Formats.Ogg;
using Encspect.Formats.Opus;
using Encspect.Formats.Speex;
using Encspect.Formats.Vorbis;
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

    // The host takes the format that sniffs most confident, and the first of equals. MP4 is certain
    // of an ftyp box (100) and sure of a run of top-level boxes (95), above MPEG audio's 90 for a
    // chain of frames anywhere in its first MiB, which an MP4's mdat may hold; an ADTS chain at the
    // start of the data (91) outranks MPEG audio too, one further in (50) does not. Ogg is certain
    // of a chain of pages from the start (100), which no MP4 or ADTS file starts with, and takes one
    // further in (60) only where nothing more certain claims the file.
    public static IReadOnlyList<IFormatParser> Formats { get; } = [Mpeg, new Mp4Parser(), new RawAacParser(), new OggParser()];

    // The codecs containers hand their payloads to, by ID: MPEG audio in WAV and MP4, AAC in MP4,
    // Speex, Vorbis and Opus in Ogg.
    public static IReadOnlyList<ICodecParser> Codecs { get; } = [Mpeg, new AacCodecParser(), new SpeexParser(), new VorbisParser(), new OpusParser()];
}
