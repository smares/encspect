using System.Buffers.Binary;

namespace Encspect.Core;

// Which codec parser reads the audio a WAVEFORMATEX describes, by its format tag or, for the extensible format, the tag or named sub-format GUID of its coding.
// WAV, ASF and Matroska's A_MS/ACM route through the same tables; each container hands over what its own layout allows.
public static class WaveFormatRoutes
{
    public const int Pcm = 0x0001;
    public const int IeeeFloat = 0x0003;
    public const int ALaw = 0x0006;
    public const int MuLaw = 0x0007;
    public const int DtsMicrosoft = 0x0008;
    public const int Mpeg = 0x0050;
    public const int MpegLayer3 = 0x0055;
    public const int Ac3 = 0x2000;
    public const int Dts = 0x2001;
    public const int Extensible = 0xFFFE;

    // Sub-format GUIDs of the codings that have no format tag, with the codec parser each goes to. The values are those of Microsoft's headers:
    // MEDIASUBTYPE_DOLBY_AC3 and MEDIASUBTYPE_MPEG2_AUDIO (ksuuids.h, named as SubFormat values by "Multiple channel audio data and WAVE
    // files") and MFAudioFormat_Dolby_DDPlus (mfapi.h, "Audio Subtype GUIDs").
    private static readonly (Guid Id, string Name, string CodecId)[] NamedSubFormats =
    [
        (new Guid("e06d802c-db46-11cf-b4d1-00805f6cbbea"), "AC-3", "ac-3"),
        (new Guid("a7fb87af-2d02-42fb-a4d4-05cd93843bdd"), "E-AC-3", "e-ac-3"),
        (new Guid("e06d802b-db46-11cf-b4d1-00805f6cbbea"), "MPEG-2 audio", "mpeg-audio"),
    ];

    // The base of every sub-format GUID: the format tag in the first 32 bits and these twelve bytes after them (ksmedia.h, and the
    // WAVEFORMATEXTENSIBLE sub-format of EBU Tech 3306-2009 Annex A.2).
    private static readonly byte[] SubFormatBase = [0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    // Whether the GUID is a format tag in a sub-format GUID's clothes, and which tag.
    public static int? TagOfSubFormat(Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);
        return bytes[4..].SequenceEqual(SubFormatBase) ? (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) : null;
    }

    // The name and codec parser of a sub-format GUID that is no format tag but is one Microsoft's headers name, or null.
    public static (string Name, string CodecId)? NamedSubFormat(Guid guid)
    {
        foreach ((Guid id, string name, string codecId) in NamedSubFormats)
        {
            if (id == guid)
            {
                return (name, codecId);
            }
        }

        return null;
    }

    // The codec parser of the audio frames a coding stores as a stream of its own (MPEG audio, AC-3 and DTS), or null: AC-3 under 0x2000 (mmreg.h
    // calls the tag WAVE_FORMAT_DVM) is the convention of the files FFmpeg's muxer writes, not a definition of the header.
    public static string? CodecId(int tag) => tag switch
    {
        Mpeg or MpegLayer3 => "mpeg-audio",
        Ac3 => "ac-3",
        Dts or DtsMicrosoft => "dts",
        _ => null,
    };

    // The codec parser of a Windows Media Audio coding (ASF specification, section 11.1; 0x000A is Windows Media Audio Voice), or null.
    public static string? WindowsMediaCodecId(int tag) => tag switch
    {
        0x0160 or 0x0161 => "wma",
        0x0162 => "wma-pro",
        0x0163 => "wma-lossless",
        0x000A => "wma-voice",
        _ => null,
    };

    // How the samples of a linear coding are laid out, or null where the tag is none of them or the widths do not fit it: PCM (8 bits unsigned, else signed),
    // IEEE floating point (32 or 64 bits, the container's width the stated one), and A-law and mu-law (8 bits). `bits` is the container's width of a sample
    // and `statedBits` the wBitsPerSample the header states.
    public static PcmEncoding? PcmEncodingOf(int tag, int bits, int statedBits) => tag switch
    {
        Pcm => bits == 8 ? PcmEncoding.UnsignedInteger : PcmEncoding.SignedInteger,
        IeeeFloat when bits is 32 or 64 && statedBits == bits => PcmEncoding.Float,
        ALaw when statedBits == 8 => PcmEncoding.ALaw,
        MuLaw when statedBits == 8 => PcmEncoding.MuLaw,
        _ => null,
    };
}
