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

    // MPEG audio whose layer a container does not state, or whose frames are not read.
    public const string MpegAudio = "MPEG audio";

    public const string MpegAudioLayerI = "MPEG audio Layer I";

    public const string MpegAudioLayerII = "MPEG audio Layer II";

    public const string MpegAudioLayerIAndII = "MPEG audio Layer I and II";

    public const string MpegAudioLayerIII = "MPEG audio Layer III";

    public const string DtsHdMasterAudio = "DTS-HD Master Audio";

    public const string EncryptedAudio = "Encrypted audio";

    public const string EncryptedVideo = "Encrypted video";
}
