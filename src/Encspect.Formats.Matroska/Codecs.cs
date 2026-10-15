namespace Encspect.Formats.Matroska;

// The audio codec IDs of the Matroska codec mapping (draft-ietf-cellar-codec, section 3.4) and what the analysis does with them: the
// IDs of the codec parsers that read their frames, and the names of the codings it does not read yet.
internal static class Codecs
{
    // The ID the host's codec parser answers to, for a codec ID the mapping defines, or null.
    public static string? RouteOf(string codecId) => codecId switch
    {
        "A_AAC" or "A_AAC/MPEG2/MAIN" or "A_AAC/MPEG2/LC" or "A_AAC/MPEG2/LC/SBR" or "A_AAC/MPEG2/SSR"
            or "A_AAC/MPEG4/MAIN" or "A_AAC/MPEG4/LC" or "A_AAC/MPEG4/LC/SBR" or "A_AAC/MPEG4/SSR" or "A_AAC/MPEG4/LTP" => "aac",
        "A_MPEG/L1" or "A_MPEG/L2" or "A_MPEG/L3" => "mpeg-audio",
        "A_AC3" or "A_AC3/BSID9" or "A_AC3/BSID10" => "ac-3",
        "A_EAC3" => "e-ac-3",
        "A_DTS" or "A_DTS/EXPRESS" or "A_DTS/LOSSLESS" => "dts",
        "A_VORBIS" => "vorbis",
        "A_OPUS" or "A_OPUS/EXPERIMENTAL" => "opus",
        _ => null,
    };

    // The coding's name, for a stream the container reports itself, or null for an ID the mapping does not define.
    public static string? NameOf(string codecId) => codecId switch
    {
        "A_AAC" or "A_AAC/MPEG2/MAIN" or "A_AAC/MPEG2/LC" or "A_AAC/MPEG2/LC/SBR" or "A_AAC/MPEG2/SSR"
            or "A_AAC/MPEG4/MAIN" or "A_AAC/MPEG4/LC" or "A_AAC/MPEG4/LC/SBR" or "A_AAC/MPEG4/SSR" or "A_AAC/MPEG4/LTP" => "AAC",
        "A_MPEG/L1" => "MPEG audio Layer I",
        "A_MPEG/L2" => "MPEG audio Layer II",
        "A_MPEG/L3" => "MPEG audio Layer III",
        "A_AC3" or "A_AC3/BSID9" or "A_AC3/BSID10" => "AC-3",
        "A_EAC3" => "E-AC-3",
        "A_DTS" => "DTS",
        "A_DTS/EXPRESS" => "DTS Express",
        "A_DTS/LOSSLESS" => "DTS lossless",
        "A_VORBIS" => "Vorbis",
        "A_OPUS" or "A_OPUS/EXPERIMENTAL" => "Opus",
        "A_FLAC" => "FLAC",
        "A_ALAC" => "ALAC",
        "A_TRUEHD" => "Dolby TrueHD",
        "A_MLP" => "MLP",
        "A_TTA1" => "TTA",
        "A_WAVPACK4" => "WavPack",
        "A_PCM/INT/LIT" => "PCM (integer, little-endian)",
        "A_PCM/INT/BIG" => "PCM (integer, big-endian)",
        "A_PCM/FLOAT/IEEE" => "PCM (floating-point)",
        "A_MS/ACM" => "Microsoft ACM audio",
        "A_ATRAC/AT1" => "ATRAC1",
        _ when codecId.StartsWith("A_REAL/", StringComparison.Ordinal) => "RealAudio",
        _ when codecId.StartsWith("A_QUICKTIME", StringComparison.Ordinal) => "QuickTime audio",
        _ => null,
    };

    // The codec IDs WebM allows (WebM Container Guidelines, "Demuxer and Muxer Guidelines"): VP8, VP9 and AV1 video, Vorbis and Opus
    // audio, and WebVTT.
    public static bool IsWebM(string codecId) =>
        codecId is "V_VP8" or "V_VP9" or "V_AV1" or "A_VORBIS" or "A_OPUS" || codecId.StartsWith("D_WEBVTT/", StringComparison.Ordinal);
}
