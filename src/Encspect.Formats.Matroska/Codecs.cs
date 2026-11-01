using Encspect.Core;

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
        "A_ALAC" => "alac",
        "A_TTA1" => "tta",
        _ => null,
    };

    // The kind of stream KnownWriters knows the codec ID's coding as, or null for a coding no parser reads.
    public static string? WriterKind(string codecId) => RouteOf(codecId) switch
    {
        "aac" => KnownWriters.Aac,
        "mpeg-audio" => KnownWriters.MpegAudio,
        "ac-3" or "e-ac-3" => KnownWriters.Ac3,
        "dts" => KnownWriters.Dts,
        "vorbis" => KnownWriters.Vorbis,
        "opus" => KnownWriters.Opus,
        "alac" => KnownWriters.Alac,
        "tta" => KnownWriters.Tta,
        _ => null,
    };

    // The coding's name, for a stream the container reports itself, or null for an ID the mapping does not define.
    public static string? NameOf(string codecId) => codecId switch
    {
        "A_AAC" => "AAC",
        // The profile a legacy ID states, in the AAC parser's names.
        "A_AAC/MPEG2/MAIN" or "A_AAC/MPEG4/MAIN" => "AAC Main",
        "A_AAC/MPEG2/LC" or "A_AAC/MPEG4/LC" => "AAC LC",
        "A_AAC/MPEG2/LC/SBR" or "A_AAC/MPEG4/LC/SBR" => "HE-AAC",
        "A_AAC/MPEG2/SSR" or "A_AAC/MPEG4/SSR" => "AAC SSR",
        "A_AAC/MPEG4/LTP" => "AAC LTP",
        "A_MPEG/L1" => CodecNames.MpegAudioLayerI,
        "A_MPEG/L2" => CodecNames.MpegAudioLayerII,
        "A_MPEG/L3" => CodecNames.MpegAudioLayerIII,
        "A_AC3" or "A_AC3/BSID9" or "A_AC3/BSID10" => "AC-3",
        "A_EAC3" => "E-AC-3",
        "A_DTS" => "DTS",
        "A_DTS/EXPRESS" => "DTS Express",
        "A_DTS/LOSSLESS" => CodecNames.DtsHdMasterAudio,
        "A_VORBIS" => "Vorbis",
        "A_OPUS" or "A_OPUS/EXPERIMENTAL" => "Opus",
        "A_FLAC" => "FLAC",
        "A_ALAC" => CodecNames.AppleLossless,
        "A_TRUEHD" => "Dolby TrueHD",
        "A_MLP" => "MLP",
        "A_TTA1" => "TTA",
        "A_WAVPACK4" => "WavPack",
        "A_PCM/INT/LIT" => CodecNames.Pcm,
        "A_PCM/INT/BIG" => CodecNames.Pcm,
        "A_PCM/FLOAT/IEEE" => CodecNames.PcmFloat,
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

    // A sampling frequency RFC 9559 allows (sections 5.1.4.1.29.1 and 5.1.4.1.29.2: greater than 0): a finite number above 0. Anything else is not one.
    public static bool IsRate(double rate) => rate > 0 && double.IsFinite(rate);

    // The AudioSpecificConfig (ISO/IEC 14496-3, section 1.6.2.1) of a legacy A_AAC/MPEG2|4/* track, from the object type its ID names
    // (MAIN 1, LC 2, SSR 3, LTP 4), the sampling frequency, the channel configuration and, for /SBR, the explicit signaling of
    // SBR with the output sampling frequency (the core's where the track gives none). It follows ISO/IEC 14496-3 where FFmpeg's matroskadec.c
    // differs: a frequency outside the table of thirteen is written in full (index 15 and 24 bits), 8 channels are channel configuration 7,
    // and 7 channels have none. Null where the ID names no object type, the channels have no channel configuration, the sampling frequency is not
    // above 0 or, for /SBR, the output sampling frequency is not (the output of another ID is not used).
    public static byte[]? LegacyAacConfig(string codecId, double? samplingFrequency, double? outputSamplingFrequency, ulong? channels)
    {
        int objectType = codecId.Contains("/MAIN", StringComparison.Ordinal) ? 1
            : codecId.Contains("/LC", StringComparison.Ordinal) ? 2
            : codecId.Contains("/SSR", StringComparison.Ordinal) ? 3
            : codecId.Contains("/LTP", StringComparison.Ordinal) ? 4 : 0;
        int configuration = (channels ?? 1) switch { >= 1 and <= 6 => (int)(channels ?? 1), 8 => 7, _ => -1 };
        if (objectType == 0 || configuration < 0)
        {
            return null;
        }

        if ((samplingFrequency is { } stated && !IsRate(stated)) || (codecId.EndsWith("/SBR", StringComparison.Ordinal) && outputSamplingFrequency is { } output && !IsRate(output)))
        {
            return null;
        }

        double rate = samplingFrequency ?? 8000;
        var bits = new List<bool>();
        void Write(long value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        void WriteRate(double hz)
        {
            int index = Array.IndexOf(AacRates, (int)Math.Round(hz));
            Write(index < 0 ? 15 : index, 4);
            if (index < 0)
            {
                Write(Math.Clamp((long)Math.Round(hz), 0, 0xFFFFFF), 24);
            }
        }

        Write(objectType, 5);
        WriteRate(rate);
        Write(configuration, 4);
        Write(0, 3);
        if (codecId.EndsWith("/SBR", StringComparison.Ordinal))
        {
            Write(0x2B7, 11);
            Write(5, 5);
            Write(1, 1);
            WriteRate(outputSamplingFrequency ?? rate);
        }

        byte[] bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bytes;
    }

    private static readonly int[] AacRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
}
