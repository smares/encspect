namespace Encspect.Formats.Aac;

// The tables of ISO/IEC 14496-3 that the configuration and the ADTS header index.
internal static class AacTables
{
    // 1.6.3.4, Table 1.18: samplingFrequencyIndex 0 to 12; 13 and 14 are reserved, 15 escapes to an
    // explicit frequency.
    private static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    // ISO/IEC 23003-3 6.1.1.2, Table 70 (usacSamplingFrequencyIndex): 0 to 12 as above, 15 to 27
    // the rates USAC adds; 13, 14 and 28 to 30 are reserved, 31 escapes.
    private static readonly int[] UsacSampleRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350, 0, 0,
        57600, 51200, 40000, 38400, 34150, 28800, 25600, 20000, 19200, 17075, 14400, 12800, 9600,
    ];

    public static int? SampleRate(int index) => index < SampleRates.Length ? SampleRates[index] : null;

    public static int? UsacSampleRate(int index) => index < UsacSampleRates.Length && UsacSampleRates[index] > 0 ? UsacSampleRates[index] : null;

    // 1.5.1.1, Table 1.1 (Audio Object Types), the names of the ones files carry.
    public static string ObjectTypeName(int type) => type switch
    {
        0 => "null",
        1 => "AAC Main",
        2 => "AAC LC",
        3 => "AAC SSR",
        4 => "AAC LTP",
        5 => "SBR",
        6 => "AAC Scalable",
        7 => "TwinVQ",
        8 => "CELP",
        9 => "HVXC",
        12 => "TTSI",
        13 => "Main synthetic",
        14 => "Wavetable synthesis",
        15 => "General MIDI",
        16 => "Algorithmic synthesis and audio effects",
        17 => "ER AAC LC",
        19 => "ER AAC LTP",
        20 => "ER AAC Scalable",
        21 => "ER TwinVQ",
        22 => "ER BSAC",
        23 => "ER AAC LD",
        24 => "ER CELP",
        25 => "ER HVXC",
        26 => "ER HILN",
        27 => "ER Parametric",
        28 => "SSC",
        29 => "PS",
        30 => "MPEG Surround",
        32 => "Layer-1",
        33 => "Layer-2",
        34 => "Layer-3",
        35 => "DST",
        36 => "ALS",
        37 => "SLS",
        38 => "SLS non-core",
        39 => "ER AAC ELD",
        40 => "SMR Simple",
        41 => "SMR Main",
        42 => "USAC",
        43 => "SAOC",
        44 => "LD MPEG Surround",
        45 => "SAOC-DE",
        46 => "Audio Sync",
        _ => "reserved",
    };

    // The object types whose access units are GASpecificConfig's raw_data_block (4.4.2.1), which
    // starts with a syntax element ID, and the error resilient ones, whose er_raw_data_block
    // (4.4.2.3) has no IDs and takes its elements in the order of the channel configuration.
    public static bool HasRawDataBlock(int type) => type is 1 or 2 or 3 or 4 or 6 or 7;

    public static bool IsErrorResilient(int type) => type is 17 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 39;

    // 1.6.3.5, Table 1.19 (channelConfiguration), with 11 to 14 as the 2009 edition's amendment 4
    // adds them; 8 to 10 and 15 are reserved.
    public static int? Channels(int configuration) => configuration switch
    {
        >= 1 and <= 6 => configuration,
        7 => 8,
        11 => 7,
        12 => 8,
        13 => 24,
        14 => 8,
        _ => null,
    };

    public static string ChannelLayout(int configuration) => configuration switch
    {
        0 => "defined in a program config element",
        1 => "mono",
        2 => "stereo",
        3 => "3.0: center, left/right",
        4 => "4.0: center, left/right, rear center",
        5 => "5.0: center, left/right, left/right surround",
        6 => "5.1: center, left/right, left/right surround, LFE",
        7 => "7.1: center, left/right, left/right outside front, left/right surround, LFE",
        11 => "6.1: center, left/right, left/right surround, rear center, LFE",
        12 => "7.1: center, left/right, left/right surround, left/right rear, LFE",
        13 => "22.2",
        14 => "7.1: center, left/right, left/right surround, LFE, left/right front height",
        _ => "reserved",
    };

    // The channel mode a stream reports for a channel configuration.
    public static string ChannelMode(int configuration) => configuration switch
    {
        1 => "Mono",
        2 => "Stereo",
        3 => "3.0",
        4 => "4.0",
        5 => "5.0",
        6 => "5.1",
        7 or 12 or 14 => "7.1",
        11 => "6.1",
        13 => "22.2",
        _ => "Reserved",
    };

    // 4.5.2.1 (raw_data_block), Table 4.85: the syntax elements by their 3-bit ID.
    public static string ElementName(int id) => id switch
    {
        0 => "SCE",
        1 => "CPE",
        2 => "CCE",
        3 => "LFE",
        4 => "DSE",
        5 => "PCE",
        6 => "FIL",
        _ => "END",
    };

    public static string ElementDescription(int id) => id switch
    {
        0 => "single channel element",
        1 => "channel pair element",
        2 => "coupling channel element",
        3 => "LFE channel element",
        4 => "data stream element",
        5 => "program config element",
        6 => "fill element",
        _ => "end of the raw data block",
    };

    // 4.5.2.10, Table 4.121: the extension_type of a fill element's extension_payload.
    public static string ExtensionTypeName(int type) => type switch
    {
        0 => "EXT_FILL",
        1 => "EXT_FILL_DATA",
        2 => "EXT_DATA_ELEMENT",
        11 => "EXT_DYNAMIC_RANGE",
        12 => "EXT_SAC_DATA",
        13 => "EXT_SBR_DATA",
        14 => "EXT_SBR_DATA_CRC",
        _ => "reserved",
    };
}
