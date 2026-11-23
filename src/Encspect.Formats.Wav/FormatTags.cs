using Encspect.Core;

namespace Encspect.Formats.Wav;

// What the WAVE format tag (wFormatTag of the fmt chunk) says about the coding: its name, the byte order of its samples, and where the
// data chunk goes. The names are the identifiers of Microsoft's mmreg.h, written out, and for 0x0101 to 0x0103 those of the RIFF specification;
// the tags a codec parser exists for are routed (WaveFormatRoutes, which ASF and Matroska share).
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

    // Whether the GUID is a format tag in a sub-format GUID's clothes, and which tag.
    public static int? TagOfSubFormat(Guid guid) => WaveFormatRoutes.TagOfSubFormat(guid);

    // The name and codec parser of a sub-format GUID that is no format tag but is one Microsoft's headers name, or null.
    public static (string Name, string CodecId)? NamedSubFormat(Guid guid) => WaveFormatRoutes.NamedSubFormat(guid);

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
    public static string? CodecId(int tag) => WaveFormatRoutes.CodecId(tag);

    public static string Show(int tag) => WaveFormatNames.Show(tag);
}
