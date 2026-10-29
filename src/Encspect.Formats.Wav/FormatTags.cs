using Encspect.Core;

namespace Encspect.Formats.Wav;

// What the WAVE format tag (wFormatTag of the fmt chunk) says about the coding: its name, the byte order of its samples, and where the
// data chunk goes. The names are the identifiers of Microsoft's mmreg.h, written out, and for 0x0101 to 0x0103 those of the RIFF specification;
// the tags a codec parser exists for are routed. AC-3 under 0x2000 (mmreg.h calls the tag WAVE_FORMAT_DVM) is the convention of the files
// FFmpeg's muxer writes, not a definition of the header.
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
    public const int DolbyAc3Spdif = 0x0092;
    public const int RawSport = 0x0240;
    public const int EsstAc3 = 0x0241;
    public const int Extensible = 0xFFFE;
    public const int Ac3 = 0x2000;
    public const int Dts = 0x2001;
    public const int DtsMicrosoft = 0x0008;

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
        return bytes[4..].SequenceEqual(SubFormatBase) ? (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) : null;
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

    // The name of a coding in the form every container uses for it (CodecNames); null for a tag the table does not list.
    public static string? NameOf(int tag) => WaveFormatNames.NameOf(tag);

    // The tags that Microsoft gives as synonyms for IEC 61937 data over S/PDIF ("Representing Formats for IEC 61937 Transmissions"): the data holds
    // data-bursts whatever the fmt chunk says of bits and channels.
    public static bool IsIec61937Carrier(int tag) => tag is DolbyAc3Spdif or RawSport or EsstAc3;

    public static bool IsPcm(int tag) => tag == Pcm;

    public static bool IsFloat(int tag) => tag == IeeeFloat;

    // Samples that are stored as they are, one after another, so the data chunk's size gives the sample count.
    public static bool IsLinear(int tag) => tag is Pcm or IeeeFloat or ALaw or MuLaw;

    // The codec parser the data chunk goes to, or null where Encspect has none for the coding.
    public static string? CodecId(int tag) => tag switch
    {
        Mpeg or MpegLayer3 => "mpeg-audio",
        Ac3 => "ac-3",
        Dts or DtsMicrosoft => "dts",
        _ => null,
    };

    public static string Show(int tag) => WaveFormatNames.Show(tag);
}
