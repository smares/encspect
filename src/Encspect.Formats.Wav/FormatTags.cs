using Encspect.Core;

namespace Encspect.Formats.Wav;

// What the WAVE format tag (wFormatTag of the fmt chunk) says about the coding: its name, the byte order of its samples, and where the
// data chunk goes. The names are the identifiers of Microsoft's mmreg.h, written out; the tags a codec parser exists for are routed.
internal static class FormatTags
{
    public const int Pcm = 0x0001;
    public const int MicrosoftAdpcm = 0x0002;
    public const int IeeeFloat = 0x0003;
    public const int ALaw = 0x0006;
    public const int MuLaw = 0x0007;
    public const int ImaAdpcm = 0x0011;
    public const int Gsm610 = 0x0031;
    public const int Mpeg = 0x0050;
    public const int MpegLayer3 = 0x0055;
    public const int Extensible = 0xFFFE;
    public const int Ac3 = 0x2000;
    public const int Dts = 0x2001;

    // The base of every sub-format GUID: the format tag in the first 32 bits and these twelve bytes after them (ksmedia.h, and the
    // WAVEFORMATEXTENSIBLE sub-format of EBU Tech 3306 Annex A.2).
    private static readonly byte[] SubFormatBase = [0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    // Whether the GUID is a format tag in a sub-format GUID's clothes, and which tag.
    public static int? TagOfSubFormat(Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);
        return bytes[4..].SequenceEqual(SubFormatBase) ? (int)BitConverter.ToUInt32(bytes[..4]) : null;
    }

    // The name of a coding in the form every container uses for it (CodecNames); null for a tag the table does not list.
    public static string? NameOf(int tag) => tag switch
    {
        0x0000 => "unknown",
        Pcm => CodecNames.Pcm,
        MicrosoftAdpcm => "Microsoft ADPCM",
        IeeeFloat => CodecNames.PcmFloat,
        0x0004 => "VSELP",
        0x0005 => "IBM CVSD",
        ALaw => CodecNames.ALaw,
        MuLaw => CodecNames.MuLaw,
        0x0008 => "DTS (Microsoft)",
        0x0009 => "DRM",
        0x000A or 0x000B => "Windows Media Audio Voice",
        0x0010 => "OKI ADPCM",
        ImaAdpcm => "IMA ADPCM",
        0x0012 => "MediaSpace ADPCM",
        0x0013 => "Sierra ADPCM",
        0x0014 => "G.723 ADPCM",
        0x0015 => "DSP Solutions DIGISTD",
        0x0016 => "DSP Solutions DIGIFIX",
        0x0020 => "Yamaha ADPCM",
        0x0021 => "Sonarc",
        0x0022 => "DSP Group TrueSpeech",
        0x0023 => "Echo Speech EchoSC1",
        0x0024 => "Audiofile AF36",
        0x0025 => "aptX",
        0x0026 => "Audiofile AF10",
        0x0030 => "Dolby AC-2",
        Gsm610 => "GSM 6.10",
        0x0032 => "MSN Audio",
        0x0033 => "Antex ADPCME",
        0x0034 => "Control Resources VQLPC",
        0x0035 => "DSP Solutions DIGIREAL",
        0x0036 => "DSP Solutions DIGIADPCM",
        0x0037 => "Control Resources CR10",
        0x0038 => "Natural MicroSystems VBX ADPCM",
        0x0040 => "G.721 ADPCM",
        0x0041 => "G.728 CELP",
        0x0042 => "Microsoft G.723.1",
        0x0043 => "Intel G.723.1",
        0x0044 => "Intel G.729",
        Mpeg => CodecNames.MpegAudioLayerIAndII,
        MpegLayer3 => CodecNames.MpegAudioLayerIII,
        0x0064 => "G.726 ADPCM",
        0x0065 => "G.722 ADPCM",
        0x0092 => "Dolby AC-3 over S/PDIF",
        0x00FF => "AAC (raw)",
        0x0160 => "Windows Media Audio 1",
        0x0161 => "Windows Media Audio 2",
        0x0162 => "Windows Media Audio Professional",
        0x0163 => "Windows Media Audio Lossless",
        0x0164 => "Windows Media Audio over S/PDIF",
        0x0180 => "MPEG-2 AAC (Fraunhofer)",
        0x0190 => "DTS (DirectShow)",
        0x0200 => "Creative ADPCM",
        0x0272 => "Sony ATRAC3",
        0x1600 => "AAC (ADTS)",
        0x1601 => "AAC (raw)",
        0x1602 => "AAC (LOAS)",
        0x1610 => "HE-AAC",
        Ac3 => "AC-3",
        Dts => "DTS",
        0x4143 => "AAC (Divio MPEG-4)",
        0x5756 => "WavPack",
        0x6C61 => CodecNames.AppleLossless,
        0x674F or 0x6750 or 0x6751 or 0x676F or 0x6770 or 0x6771 => "Vorbis",
        0x704F => "Opus",
        0xA106 => "AAC (MPEG-4)",
        0xF1AC => "FLAC",
        Extensible => "extensible",
        _ => null,
    };

    // The name the stream reports for a coding no codec parser read: the tag's name, or "Other" for one that has none.
    public static string CodecName(int tag) => NameOf(tag) is { } name && name is not ("unknown" or "extensible") ? name : CodecNames.Other;

    public static bool IsPcm(int tag) => tag == Pcm;

    public static bool IsFloat(int tag) => tag == IeeeFloat;

    // Samples that are stored as they are, one after another, so the data chunk's size gives the sample count.
    public static bool IsLinear(int tag) => tag is Pcm or IeeeFloat or ALaw or MuLaw;

    // The codec parser the data chunk goes to, or null where Encspect has none for the coding.
    public static string? CodecId(int tag) => tag switch
    {
        Mpeg or MpegLayer3 => "mpeg-audio",
        Ac3 => "ac-3",
        Dts => "dts",
        _ => null,
    };

    // The WAVE format tags whose WAVEFORMATEX is followed by figures of a structure the specifications give.
    public static bool HasKnownExtra(int tag) => tag is MicrosoftAdpcm or ImaAdpcm or Gsm610 or Mpeg or MpegLayer3;

    public static string Show(int tag) => $"0x{tag:X4} ({NameOf(tag) ?? "unlisted"})";
}
