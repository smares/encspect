using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Formats.Ac3;
using Encspect.Formats.Asf;
using Encspect.Formats.Mp4;
using Encspect.Formats.Mpeg;
using Encspect.Formats.Musepack;
using Encspect.Formats.Ogg;
using Encspect.Formats.Opus;
using Encspect.Formats.Speex;
using Encspect.Formats.Vorbis;
using Encspect.Formats.WavPack;
using Encspect.Formats.Wma;
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
    // further in (60) only where nothing more certain claims the file. Musepack is certain of its
    // magic number (100); a stream of version 4 to 6 has none and takes 60. An AC-3 chain at the
    // start of the data (92) outranks MPEG audio and ADTS, whose sync words cannot start with its
    // 0x0B; one further in (50) ranks below them all. ASF is certain of the GUID of its Header
    // Object at the start (100). WavPack is certain of a block header that passes libwavpack's checks
    // and is followed by another one at the start of the data (100), and takes a chain of them
    // further in (60), below MPEG audio.
    public static IReadOnlyList<IFormatParser> Formats { get; } = [Mpeg, new Mp4Parser(), new RawAacParser(), new OggParser(), new MusepackParser(), new Ac3Parser(), new AsfParser(), new WavPackParser()];

    // The codecs containers hand their payloads to, by ID: MPEG audio in WAV and MP4, AAC in MP4,
    // Speex, Vorbis and Opus in Ogg, AC-3 and E-AC-3 in MP4, Windows Media Audio in ASF.
    public static IReadOnlyList<ICodecParser> Codecs { get; } =
        [Mpeg, new AacCodecParser(), new SpeexParser(), new VorbisParser(), new OpusParser(), new Ac3CodecParser(Ac3CodecParser.Ac3), new Ac3CodecParser(Ac3CodecParser.Eac3), new WmaStandardParser(), new WmaProParser(), new WmaLosslessParser(), new WmaVoiceParser()];
}
