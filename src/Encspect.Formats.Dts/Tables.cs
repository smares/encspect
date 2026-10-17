using Encspect.Core;

namespace Encspect.Formats.Dts;

// The tables of ETSI TS 102 114 V1.6.1 that the fields are named and counted with, and the places
// where FFmpeg (dcadata.c, dca_syncwords.h) and libdca (tables.h) read a code the specification does
// not list.
internal static class Tables
{
    // Table 5-5: the core sampling frequency by SFREQ; 0 for the invalid codes.
    public static int SampleRate(int sfreq) => sfreq switch
    {
        1 => 8000,
        2 => 16000,
        3 => 32000,
        6 => 11025,
        7 => 22050,
        8 => 44100,
        11 => 12000,
        12 => 24000,
        13 => 48000,
        _ => 0,
    };

    // Table 5-7: the targeted bit rate by RATE in units of 100 bit/s (1,411.2 kbit/s is 14112); 0 for open and every code the table calls invalid.
    public static int RateTenths(int rate) => rate switch
    {
        0 => 320,
        1 => 560,
        2 => 640,
        3 => 960,
        4 => 1120,
        5 => 1280,
        6 => 1920,
        7 => 2240,
        8 => 2560,
        9 => 3200,
        10 => 3840,
        11 => 4480,
        12 => 5120,
        13 => 5760,
        14 => 6400,
        15 => 7680,
        16 => 9600,
        17 => 10240,
        18 => 11520,
        19 => 12800,
        20 => 13440,
        21 => 14080,
        22 => 14112,
        23 => 14720,
        24 => 15360,
        _ => 0,
    };

    // FFmpeg's ff_dca_bit_rates and libdca's dca_bit_rates give 896 kbit/s for code 16 where the specification's
    // Table 5-7 gives 960, and list 1,920, 2,048, 3,072 and 3,840 kbit/s for codes 25 to 28, which the table calls invalid.
    public static int DecodersRateKbps(int rate) => rate switch
    {
        16 => 896,
        25 => 1920,
        26 => 2048,
        27 => 3072,
        28 => 3840,
        _ => 0,
    };

    public const int RateOpen = 29;
    public const int RateVariable = 30;
    public const int RateLossless = 31;

    // Table 5-4: the number of channels each channel arrangement names; 0 for the user defined codes.
    public static int ArrangementChannels(int amode) => amode switch
    {
        0 => 1,
        <= 4 => 2,
        <= 6 => 3,
        <= 8 => 4,
        9 => 5,
        <= 12 => 6,
        13 => 7,
        <= 15 => 8,
        _ => 0,
    };

    // 5.4.3: the core codes at most five channels; AMODE names more where extension channels carry the rest.
    public static int CoreChannels(int amode) => Math.Min(5, ArrangementChannels(amode));

    // Table 5-4: the arrangement as the specification writes it, and in written-out words.
    public static string Arrangement(int amode) => amode switch
    {
        0 => "1/0 (center)",
        1 => "1+1 (channel 1, channel 2)",
        2 => "2/0 (left, right)",
        3 => "2/0 sum/difference ((left + right), (left - right))",
        4 => "2/0 left total, right total",
        5 => "3/0 (center, left, right)",
        6 => "2/1 (left, right, surround)",
        7 => "3/1 (center, left, right, surround)",
        8 => "2/2 (left, right, left surround, right surround)",
        9 => "3/2 (center, left, right, left surround, right surround)",
        10 => "4/2 (center left, center right, left, right, left surround, right surround)",
        11 => "3/2 with overhead (center, left, right, left rear, right rear, overhead)",
        12 => "3/3 (center front, center rear, left front, right front, left rear, right rear)",
        13 => "5/2 (center left, center, center right, left, right, left surround, right surround)",
        14 => "4/4 (center left, center right, left, right, first and second left surround, first and second right surround)",
        15 => "5/3 (center left, center, center right, left, right, left surround, surround, right surround)",
        _ => "user defined",
    };

    public static string ArrangementShort(int amode) => amode switch
    {
        0 => "1/0",
        1 => "1+1",
        2 => "2/0",
        3 => "2/0 sum/difference",
        4 => "2/0 left total, right total",
        5 => "3/0",
        6 => "2/1",
        7 => "3/1",
        8 => "2/2",
        9 => "3/2",
        10 => "4/2",
        11 => "3/2 (overhead)",
        12 => "3/3",
        13 => "5/2",
        14 => "4/4",
        15 => "5/3",
        _ => "user defined",
    };

    // Table 7-10 and Table E-5: how many channels each bit of a speaker activity mask names, lowest bit first.
    private static readonly int[] MaskChannels = [1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 2, 1, 2, 1, 2];

    // 7.5.3.3: the number of channels a speaker mask names, a pair counting for two (NumSpkrTableLookUp over Table 7-10).
    public static int SpeakerCount(int mask)
    {
        int count = 0;
        for (int bit = 0; bit < MaskChannels.Length; bit++)
        {
            if ((mask & (1 << bit)) != 0)
            {
                count += MaskChannels[bit];
            }
        }

        return count;
    }

    // Table 7-9: nuMaxSampleRate.
    public static int ExtensionSampleRate(int code) => code switch
    {
        0 => 8000,
        1 => 16000,
        2 => 32000,
        3 => 64000,
        4 => 128000,
        5 => 22050,
        6 => 44100,
        7 => 88200,
        8 => 176400,
        9 => 352800,
        10 => 12000,
        11 => 24000,
        12 => 48000,
        13 => 96000,
        14 => 192000,
        _ => 384000,
    };

    // Table 5-17: the source PCM resolution by PCMR and whether the surround channels are DTS-ES mastered; 0 for the invalid codes.
    public static int SourceResolution(int pcmr) => pcmr switch
    {
        0 or 1 => 16,
        2 or 3 => 20,
        5 or 6 => 24,
        _ => 0,
    };

    // Table 5-17: ES is 1 for 0b001, 0b011 and 0b101, 0 for 0b000, 0b010 and 0b110.
    public static bool IsEs(int pcmr) => pcmr is 1 or 3 or 5;

    // Table 5-20: the dialog normalization gain in dB for VERNUM 6 and 7; null for the other revisions, which carry none.
    public static int? DialogNormalization(int vernum, int dialnorm) => vernum switch
    {
        7 => -dialnorm,
        6 => -(16 + dialnorm),
        _ => null,
    };

    // Table 5-11.
    public static string ExtensionAudio(int id) => id switch
    {
        0 => "channel extension (XCh)",
        2 => "frequency extension (X96)",
        6 => "channel extension (XXCH)",
        _ => "reserved",
    };

    // Table 7-8.
    public static string AssetType(int type) => type switch
    {
        0 => "music",
        1 => "effects",
        2 => "dialog",
        3 => "commentary",
        4 => "visually impaired",
        5 => "hearing impaired",
        6 => "isolated music object",
        7 => "music and effects",
        8 => "dialog and commentary",
        9 => "effects and commentary",
        10 => "isolated music object and commentary",
        11 => "isolated music object and effects",
        12 => "karaoke",
        13 => "music, effects and dialog",
        14 => "complete audio presentation",
        _ => "reserved",
    };

    // Table 7-15's coding components by bit, lowest first.
    public static readonly string[] Components =
    [
        "core in the core substream",
        "XXCH in the core substream",
        "X96 in the core substream",
        "XCh in the core substream",
        "core",
        "XBR",
        "XXCH",
        "X96",
        "LBR",
        "XLL",
        "reserved",
        "reserved",
    ];

    public const int ComponentCore = 0x10;
    public const int ComponentXbr = 0x20;
    public const int ComponentXxch = 0x40;
    public const int ComponentX96 = 0x80;
    public const int ComponentLbr = 0x100;
    public const int ComponentXll = 0x200;
    public const int ComponentReserved = 0xC00;

    // Table E-2: the streams that the DTS-in-ISOBMFF box's StreamConstruction names, as the components they hold: bit 0 core, 1 XCh, 2 X96,
    // 3 XXCH of the core substream and bit 4 core, 5 XXCH, 6 X96, 7 XBR, 8 XLL, 9 LBR of the extension substream.
    public const int CoreSubstreamCore = 1;
    public const int CoreSubstreamXch = 2;
    public const int CoreSubstreamX96 = 4;
    public const int CoreSubstreamXxch = 8;
    public const int ExtensionCore = 16;
    public const int ExtensionXxch = 32;
    public const int ExtensionX96 = 64;
    public const int ExtensionXbr = 128;
    public const int ExtensionXll = 256;
    public const int ExtensionLbr = 512;

    // Flags beside those components, in the union that names a stream's profile: the lossless frame of the first asset carries the extension
    // data that FFmpeg reads as DTS:X or as DTS:X IMAX (dca_xll.c). Not bits of Table E-2.
    public const int FlagDtsX = 1 << 16;
    public const int FlagDtsXImax = 1 << 17;

    // The components of StreamConstruction 1 to 21 (index value - 1).
    public static readonly int[] StreamConstructions =
    [
        CoreSubstreamCore,
        CoreSubstreamCore | CoreSubstreamXch,
        CoreSubstreamCore | CoreSubstreamXxch,
        CoreSubstreamCore | CoreSubstreamX96,
        CoreSubstreamCore | ExtensionXxch,
        CoreSubstreamCore | ExtensionXbr,
        CoreSubstreamCore | CoreSubstreamXch | ExtensionXbr,
        CoreSubstreamCore | CoreSubstreamXxch | ExtensionXbr,
        CoreSubstreamCore | ExtensionXxch | ExtensionXbr,
        CoreSubstreamCore | ExtensionX96,
        CoreSubstreamCore | CoreSubstreamXch | ExtensionX96,
        CoreSubstreamCore | CoreSubstreamXxch | ExtensionX96,
        CoreSubstreamCore | ExtensionXxch | ExtensionX96,
        CoreSubstreamCore | ExtensionXll,
        CoreSubstreamCore | CoreSubstreamXch | ExtensionXll,
        CoreSubstreamCore | CoreSubstreamX96 | ExtensionXll,
        ExtensionXll,
        ExtensionLbr,
        ExtensionCore,
        ExtensionCore | ExtensionXxch,
        ExtensionCore | ExtensionXll,
    ];

    // The StreamConstruction of a set of components, or 0 where Table E-2 lists none.
    public static int StreamConstructionOf(int components) => Array.IndexOf(StreamConstructions, components) + 1;

    public static string ComponentNames(int components)
    {
        string[] names = ["core", "XCh", "X96", "XXCH", "extension substream core", "extension substream XXCH", "extension substream X96", "XBR", "XLL", "LBR"];
        var set = new List<string>();
        for (int bit = 0; bit < names.Length; bit++)
        {
            if ((components & (1 << bit)) != 0)
            {
                set.Add(names[bit]);
            }
        }

        return set.Count == 0 ? "nothing" : string.Join(", ", set);
    }
}
