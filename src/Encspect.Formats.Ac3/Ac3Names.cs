using Encspect.Core;

namespace Encspect.Formats.Ac3;

// Names of coded values, written out as the report shows them (A/52 Tables 5.6 to 5.13, Annex D
// Tables D2.2 to D2.9, Annex E Tables E2.1 to E2.5).
internal static class Ac3Names
{
    // Table 5.8: the audio coding mode, front/surround channels and the channels in coded order.
    public static string CodingMode(int acmod) => acmod switch
    {
        0 => "1+1 (channel 1, channel 2)",
        1 => "1/0 (center)",
        2 => "2/0 (left, right)",
        3 => "3/0 (left, center, right)",
        4 => "2/1 (left, right, surround)",
        5 => "3/1 (left, center, right, surround)",
        6 => "2/2 (left, right, left surround, right surround)",
        _ => "3/2 (left, center, right, left surround, right surround)",
    };

    public static string CodingModeShort(int acmod) => acmod switch
    {
        0 => "1+1",
        1 => "1/0",
        2 => "2/0",
        3 => "3/0",
        4 => "2/1",
        5 => "3/1",
        6 => "2/2",
        _ => "3/2",
    };

    public static int FullBandwidthChannels(int acmod) => acmod switch
    {
        0 => 2,
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 3,
        5 => 4,
        6 => 4,
        _ => 5,
    };

    // The speakers of the coded channels in coded order, the LFE last (5.4.2.3). A format's only
    // surround pair is the back pair; with a rear pair from a dependent substream it becomes the side
    // pair (plan, "Streams and series"), which Speakers of a program sorts out.
    public static Speaker[] Speakers(int acmod, bool lfeon)
    {
        Speaker[] front = acmod switch
        {
            0 => [Speaker.Discrete, Speaker.Discrete],
            1 => [Speaker.FrontCenter],
            2 => [Speaker.FrontLeft, Speaker.FrontRight],
            3 => [Speaker.FrontLeft, Speaker.FrontCenter, Speaker.FrontRight],
            4 => [Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackCenter],
            5 => [Speaker.FrontLeft, Speaker.FrontCenter, Speaker.FrontRight, Speaker.BackCenter],
            6 => [Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight],
            _ => [Speaker.FrontLeft, Speaker.FrontCenter, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight],
        };
        return lfeon ? [.. front, Speaker.LowFrequency] : front;
    }

    // Annex E Table E2.5: the custom channel map's locations by bit, bit 0 in the field's most
    // significant bit; pairs are two coded channels.
    public static string ChannelMapLocation(int bit) => bit switch
    {
        0 => "left",
        1 => "center",
        2 => "right",
        3 => "left surround",
        4 => "right surround",
        5 => "left/right center",
        6 => "left/right rear surround",
        7 => "center surround",
        8 => "top surround",
        9 => "left/right surround direct",
        10 => "left/right wide",
        11 => "left/right vertical height",
        12 => "center vertical height",
        13 => "left/right top surround",
        14 => "LFE 2",
        _ => "LFE",
    };

    public static bool IsPair(int bit) => bit is 5 or 6 or 9 or 10 or 11 or 13;

    // Table 5.7.
    public static string ServiceType(int bsmod, int acmod) => bsmod switch
    {
        0 => "main audio service: complete main",
        1 => "main audio service: music and effects",
        2 => "associated service: visually impaired",
        3 => "associated service: hearing impaired",
        4 => "associated service: dialogue",
        5 => "associated service: commentary",
        6 => "associated service: emergency",
        _ => acmod switch
        {
            0 => "not defined for 1+1",
            1 => "associated service: voice over",
            _ => "main audio service: karaoke",
        },
    };

    public static string BitStreamId(int bsid) => bsid switch
    {
        <= 5 => "AC-3, an earlier version that bsid 8 decoders play",
        6 => "AC-3 with the alternate bit stream syntax of Annex D",
        7 => "AC-3, an earlier version that bsid 8 decoders play",
        8 => "AC-3",
        9 => "AC-3 at half the sample rate, not defined by A/52",
        10 => "AC-3 at a quarter of the sample rate, not defined by A/52",
        16 => "E-AC-3",
        _ => "E-AC-3, a version that bsid 16 decoders play",
    };

    // Table 5.9 (cmixlev), in tenths of a dB, or null for the reserved code.
    public static int? CenterMixLevel(int code) => code switch { 0 => -30, 1 => -45, 2 => -60, _ => null };

    // Table 5.10 (surmixlev): -3 dB, -6 dB, or 0 (no surround in the mix).
    public static string SurroundMixLevel(int code) => code switch
    {
        0 => "-3.0 dB",
        1 => "-6.0 dB",
        2 => "0 (surround left out of the mix)",
        _ => "3 (reserved)",
    };

    public static NodeValue CenterMix(int code) =>
        CenterMixLevel(code) is { } tenths ? NodeValue.Decimal(tenths / 10.0, 1, "dB") : "3 (reserved)";

    public static NodeValue SurroundMix(int code) => code switch
    {
        0 => NodeValue.Decimal(-3.0, 1, "dB"),
        1 => NodeValue.Decimal(-6.0, 1, "dB"),
        _ => SurroundMixLevel(code),
    };

    // Table 5.11 and Annex D Table D2.8 share their states.
    public static string DolbySurround(int code) => code switch
    {
        0 => "not indicated",
        1 => "not Dolby Surround encoded",
        2 => "Dolby Surround encoded",
        _ => "reserved",
    };

    public static string DolbyHeadphone(int code) => code switch
    {
        0 => "not indicated",
        1 => "not Dolby Headphone encoded",
        2 => "Dolby Headphone encoded",
        _ => "reserved",
    };

    // Annex D Table D2.7, defined for 2/2 and 3/2 only; reserved in the other modes.
    public static string DolbySurroundEx(int code, int acmod) => acmod < 6 ? "reserved in this audio coding mode" : code switch
    {
        0 => "not indicated",
        1 => "not Dolby Surround EX, Dolby Pro Logic IIx or Dolby Pro Logic IIz encoded",
        2 => "Dolby Surround EX or Dolby Pro Logic IIx encoded",
        _ => "Dolby Pro Logic IIz encoded",
    };

    // Table 5.12.
    public static string RoomType(int code) => code switch
    {
        0 => "not indicated",
        1 => "large room, X curve monitor",
        2 => "small room, flat monitor",
        _ => "reserved",
    };

    // Annex D Table D2.2.
    public static string PreferredDownmix(int code) => code switch
    {
        0 => "not indicated",
        1 => "Lt/Rt downmix preferred",
        2 => "Lo/Ro downmix preferred",
        _ => "reserved",
    };

    // Annex D Tables D2.3 to D2.6: the Lt/Rt and Lo/Ro mix levels in tenths of a dB; the
    // surround levels 0 to 2 are reserved.
    public static NodeValue DownmixLevel(int code, bool isSurround)
    {
        if (isSurround && code < 3)
        {
            return $"{code} (reserved)";
        }

        return code switch
        {
            0 => NodeValue.Decimal(3.0, 1, "dB", showsSign: true),
            1 => NodeValue.Decimal(1.5, 1, "dB", showsSign: true),
            2 => NodeValue.Decimal(0.0, 1, "dB", showsSign: true),
            3 => NodeValue.Decimal(-1.5, 1, "dB", showsSign: true),
            4 => NodeValue.Decimal(-3.0, 1, "dB", showsSign: true),
            5 => NodeValue.Decimal(-4.5, 1, "dB", showsSign: true),
            6 => NodeValue.Decimal(-6.0, 1, "dB", showsSign: true),
            _ => "7 (-inf dB, left out of the mix)",
        };
    }

    // 5.4.2.8: 1 to 31 is -1 to -31 dB; 0 is reserved and read as -31 dB.
    public static NodeValue DialogueNormalization(int code) =>
        code == 0 ? "0 (reserved, read as -31 dB)" : NodeValue.Integer(-code, "dB");

    // 7.7.1.2 and 7.7.2.2.
    public static NodeValue DynamicRange(int code) => NodeValue.Decimal(Tables.DynamicRangeHundredthsDb[code] / 100.0, 2, "dB", showsSign: true);

    public static NodeValue Compression(int code) => NodeValue.Decimal(Tables.CompressionHundredthsDb[code] / 100.0, 2, "dB", showsSign: true);

    // Table 7.4 and Table 7.5.
    public static string ExponentStrategy(int code) => code switch { 0 => "reuse", 1 => "D15", 2 => "D25", _ => "D45" };

    // Table 5.16.
    public static string DeltaBitAllocation(int code) => code switch
    {
        0 => "reuse previous state",
        1 => "new info follows",
        2 => "perform no delta allocation",
        _ => "reserved",
    };

    // Annex E Table E2.1.
    public static string StreamType(int code) => code switch
    {
        0 => "independent",
        1 => "dependent",
        2 => "independent, converted from AC-3",
        _ => "reserved",
    };

    // Annex E Table E2.9.
    public static string SnrOffsetStrategy(int code) => code switch
    {
        0 => "one offset per frame",
        1 => "one offset per block for all channels",
        2 => "one offset per block for each channel",
        _ => "reserved",
    };

    // Annex E Table E2.6.
    public static string MixDefinition(int code) => code switch
    {
        0 => "mixing option 1, no further bits",
        1 => "mixing option 2, 5 bits",
        2 => "mixing option 3, 12 bits",
        _ => "mixing option 4, a length follows",
    };

    // Annex E Table E2.7, in tenths of a percent where defined.
    public static string PremixScale(int code) => code switch
    {
        0 => "0% (no compression)",
        1 => "16.7%",
        2 => "33.3%",
        3 => "50%",
        4 => "66.7%",
        5 => "83.3%",
        6 => "6 (not listed in Table E2.7)",
        _ => "100% (maximum compression)",
    };

    // Annex E Table E2.8.
    public static NodeValue ExternalChannelScale(int code) => code switch
    {
        15 => "15 (-inf dB, mute)",
        _ => NodeValue.Integer(code switch { <= 5 => -1 - code, 6 => -8, 7 => -10, 8 => -12, 9 => -14, 10 => -16, 11 => -19, 12 => -22, 13 => -25, _ => -28 }, "dB"),
    };

    // Annex E 2.3.1.13: 0 mutes, 1 to 63 is -50 to +12 dB.
    public static NodeValue ProgramScale(int code) => code == 0 ? "0 (mute)" : NodeValue.Integer(code - 51, "dB");

    // 5.4.2.27 and 5.4.2.28.
    public static string TimeCode1(int value) => $"{value >> 9:D2}:{(value >> 3) & 0x3F:D2}:{(value & 7) * 8:D2}";

    public static string TimeCode2(int value) => $"+{value >> 11} s, frame {(value >> 6) & 0x1F}, {value & 0x3F}/64";
}
