namespace Encspect.Core;

// The names of codings that more than one container reports for a stream its codec parser did not read (a
// coding without a parser, an encrypted stream), so that one coding has one name in every container. A name a
// codec parser gives is its own and stays: a container's name is for the coding when only the container knows it.
public static class CodecNames
{
    public const string AppleLossless = "Apple Lossless";

    // Integer PCM; a container that states a byte order keeps it in its own nodes.
    public const string Pcm = "PCM";

    public const string PcmFloat = "PCM (floating-point)";

    // MPEG audio the codec parser did not read, named in the parser's form ("MPEG-1 Layer III") with what the container states: the layer
    // alone where it states no version, the version alone where it states no layer. Where it states both, the parser's own name is used.
    public const string MpegAudioLayerI = "MPEG Layer I";

    public const string MpegAudioLayerII = "MPEG Layer II";

    public const string MpegAudioLayerIAndII = "MPEG Layer I and II";

    public const string MpegAudioLayerIII = "MPEG Layer III";

    public const string Mpeg1Audio = "MPEG-1 audio";

    public const string Mpeg2Audio = "MPEG-2 audio";

    public const string Mpeg4Audio = "MPEG-4 audio";

    public const string DtsHdMasterAudio = "DTS-HD Master Audio";

    public const string EncryptedAudio = "Encrypted audio";

    public const string EncryptedVideo = "Encrypted video";
}
