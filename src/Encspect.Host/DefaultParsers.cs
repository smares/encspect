using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Formats.Ac3;
using Encspect.Formats.Aiff;
using Encspect.Formats.Asf;
using Encspect.Formats.Dts;
using Encspect.Formats.DtsHd;
using Encspect.Formats.Flac;
using Encspect.Formats.Matroska;
using Encspect.Formats.MonkeysAudio;
using Encspect.Formats.Mp4;
using Encspect.Formats.Mpeg;
using Encspect.Formats.Musepack;
using Encspect.Formats.Ogg;
using Encspect.Formats.Opus;
using Encspect.Formats.Speex;
using Encspect.Formats.TrueHd;
using Encspect.Formats.Tta;
using Encspect.Formats.Vorbis;
using Encspect.Formats.Wav;
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

    // The host takes the format whose sniff has the best evidence: the highest class, then the
    // earliest start of the matched structure, then the most checksum-verified frames, then the most
    // consistent frames (SniffResult.Compare; plan, "Format detection"). The order of this list only
    // breaks a tie in all four, which the report names as format.ambiguous, so no format depends on
    // its place here.
    public static IReadOnlyList<IFormatParser> Formats { get; } = [Mpeg, new Mp4Parser(), new RawAacParser(), new OggParser(), new MusepackParser(), new Ac3Parser(), new AsfParser(), new WavPackParser(), new DtsParser(), new DtsHdParser(), new FlacParser(), new MonkeysAudioParser(), new TtaParser(), new TrueHdParser(), new MatroskaParser(), new WavParser(), new Wave64Parser(), new AiffParser()];

    // The codecs containers hand their payloads to, by ID: MPEG audio in WAV and MP4, AAC in MP4 and, as ADTS frames, in WAV bursts,
    // Speex, Vorbis and Opus in Ogg and Matroska, AC-3, E-AC-3 and DTS in MP4 and Matroska, AAC in Matroska, FLAC in Ogg, WavPack and TTA in Matroska, Windows Media Audio in ASF, Dolby TrueHD and MLP in MP4 and Matroska once their containers route to them.
    public static IReadOnlyList<ICodecParser> Codecs { get; } =
        [Mpeg, new AacCodecParser(), new AdtsPacketParser(), new SpeexParser(), new VorbisParser(), new OpusParser(), new Ac3CodecParser(Ac3CodecParser.Ac3), new Ac3CodecParser(Ac3CodecParser.Eac3), new WmaStandardParser(), new WmaProParser(), new WmaLosslessParser(), new WmaVoiceParser(), new DtsCodecParser(), new FlacCodecParser(), new WavPackCodecParser(), new TtaCodecParser(), TrueHdCodecParser.TrueHd, TrueHdCodecParser.Mlp];

    // The decoders of the codecs the formats above read, by the same IDs the parsers start their sessions with. Each
    // lives in its format's project; a format without one is not listed, and its streams report that no decoder exists.
    public static IReadOnlyList<IDecoder> Decoders { get; } = [new Encspect.Formats.Flac.Decoding.FlacDecoder(), new Encspect.Formats.WavPack.Decoding.WavPackDecoder(), new Encspect.Formats.MonkeysAudio.Decoding.MonkeysAudioDecoder(), Encspect.Formats.TrueHd.Decoding.TrueHdDecoder.TrueHd, Encspect.Formats.TrueHd.Decoding.TrueHdDecoder.Mlp];
}
