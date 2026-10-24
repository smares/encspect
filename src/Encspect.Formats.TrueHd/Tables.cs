using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// The constants of docs/design/truehd-format.md: the sampling rates, the channel tables and the noise table.
internal static class Tables
{
    // Per sampling rate code: the rate (0 for a code that gives none or more than 192 kHz) and the doubling of the base rate.
    public static int RateOf(int code) => code switch
    {
        0 => 48000,
        1 => 96000,
        2 => 192000,
        8 => 44100,
        9 => 88200,
        10 => 176400,
        _ => 0,
    };

    // Samples per channel in one access unit: 40, 80 or 160.
    public static int UnitSamplesOf(int code) => 40 << (code & 7);

    // The size of the noise buffer one unit draws from, a power of two at or above the unit's samples.
    public static int NoiseBufferOf(int code) => 64 << (code & 7);

    public static readonly Speaker[] Stereo = [Speaker.FrontLeft, Speaker.FrontRight];

    // The channels of one TrueHD channel flag, in the order of the table, each pair left before right.
    public static readonly int[] FlagChannels = [2, 1, 1, 2, 2, 2, 2, 1, 1, 2, 2, 1, 1];

    public static readonly string[] FlagNames =
    [
        "left, right", "center", "LFE", "left surround, right surround", "left height, right height", "left center, right center", "left rear surround, right rear surround",
        "center surround", "top surround", "left surround direct, right surround direct", "left wide, right wide", "center height", "second LFE",
    ];

    public static int ChannelsOf(int assignment)
    {
        int count = 0;
        for (int flag = 0; flag < FlagChannels.Length; flag++)
        {
            if ((assignment & (1 << flag)) != 0)
            {
                count += FlagChannels[flag];
            }
        }

        return count;
    }

    // The speakers of a channel assignment, in the order of its flags. A surround pair is the back pair where the layout has no rear pair,
    // else the side pair (Speaker's documentation); a surround direct pair has no position of its own.
    public static Speaker[] SpeakersOf(int assignment)
    {
        bool hasRear = (assignment & (1 << 6)) != 0;
        var list = new List<Speaker>();
        for (int flag = 0; flag < FlagChannels.Length; flag++)
        {
            if ((assignment & (1 << flag)) == 0)
            {
                continue;
            }

            switch (flag)
            {
                case 0: list.AddRange([Speaker.FrontLeft, Speaker.FrontRight]); break;
                case 1: list.Add(Speaker.FrontCenter); break;
                case 2: list.Add(Speaker.LowFrequency); break;
                case 3: list.AddRange(hasRear ? [Speaker.SideLeft, Speaker.SideRight] : [Speaker.BackLeft, Speaker.BackRight]); break;
                case 4: list.AddRange([Speaker.TopFrontLeft, Speaker.TopFrontRight]); break;
                case 5: list.AddRange([Speaker.FrontLeftOfCenter, Speaker.FrontRightOfCenter]); break;
                case 6: list.AddRange([Speaker.BackLeft, Speaker.BackRight]); break;
                case 7: list.Add(Speaker.BackCenter); break;
                case 8: list.Add(Speaker.TopCenter); break;
                case 9: list.AddRange([Speaker.Discrete, Speaker.Discrete]); break;
                case 10: list.AddRange([Speaker.FrontLeftWide, Speaker.FrontRightWide]); break;
                case 11: list.Add(Speaker.TopFrontCenter); break;
                default: list.Add(Speaker.LowFrequency2); break;
            }
        }

        return [.. list];
    }

    // The MLP channel arrangements 0 to 20: the channels of group 1, then those of group 2.
    private static readonly (Speaker[] Group1, Speaker[] Group2)[] Arrangements =
    [
        ([Speaker.FrontCenter], []),
        (Stereo, []),
        (Stereo, [Speaker.BackCenter]),
        (Stereo, [Speaker.BackLeft, Speaker.BackRight]),
        (Stereo, [Speaker.LowFrequency]),
        (Stereo, [Speaker.LowFrequency, Speaker.BackCenter]),
        (Stereo, [Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight]),
        (Stereo, [Speaker.FrontCenter]),
        (Stereo, [Speaker.FrontCenter, Speaker.BackCenter]),
        (Stereo, [Speaker.FrontCenter, Speaker.BackLeft, Speaker.BackRight]),
        (Stereo, [Speaker.FrontCenter, Speaker.LowFrequency]),
        (Stereo, [Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackCenter]),
        (Stereo, [Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight]),
        ([Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter], [Speaker.BackCenter]),
        ([Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter], [Speaker.BackLeft, Speaker.BackRight]),
        ([Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter], [Speaker.LowFrequency]),
        ([Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter], [Speaker.LowFrequency, Speaker.BackCenter]),
        ([Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter], [Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight]),
        ([Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight], [Speaker.LowFrequency]),
        ([Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight], [Speaker.FrontCenter]),
        ([Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight], [Speaker.FrontCenter, Speaker.LowFrequency]),
    ];

    public const int ArrangementCount = 21;

    // The channels of an MLP arrangement, or null for a code the table does not give.
    public static Speaker[]? ArrangementSpeakers(int code) => code is >= 0 and < ArrangementCount ? [.. Arrangements[code].Group1, .. Arrangements[code].Group2] : null;

    // The channels of group 1 of an arrangement.
    public static int ArrangementGroup1(int code) => Arrangements[code].Group1.Length;

    // Entry 0 first, row by row (docs/design/truehd-format.md, "Noise generation").
    public static readonly sbyte[] Noise =
    [
        30, 51, 22, 54, 3, 7, -4, 38, 14, 55, 46, 81, 22, 58, -3, 2,
        52, 31, -7, 51, 15, 44, 74, 30, 85, -17, 10, 33, 18, 80, 28, 62,
        10, 32, 23, 69, 72, 26, 35, 17, 73, 60, 8, 56, 2, 6, -2, -5,
        51, 4, 11, 50, 66, 76, 21, 44, 33, 47, 1, 26, 64, 48, 57, 40,
        38, 16, -10, -28, 92, 22, -18, 29, -10, 5, -13, 49, 19, 24, 70, 34,
        61, 48, 30, 14, -6, 25, 58, 33, 42, 60, 67, 17, 54, 17, 22, 30,
        67, 44, -9, 50, -11, 43, 40, 32, 59, 82, 13, 49, -14, 55, 60, 36,
        48, 49, 31, 47, 15, 12, 4, 65, 1, 23, 29, 39, 45, -2, 84, 69,
        0, 72, 37, 57, 27, 41, -15, -16, 35, 31, 14, 61, 24, 0, 27, 24,
        16, 41, 55, 34, 53, 9, 56, 12, 25, 29, 53, 5, 20, -20, -8, 20,
        13, 28, -3, 78, 38, 16, 11, 62, 46, 29, 21, 24, 46, 65, 43, -23,
        89, 18, 74, 21, 38, -12, 19, 12, -19, 8, 15, 33, 4, 57, 9, -8,
        36, 35, 26, 28, 7, 83, 63, 79, 75, 11, 3, 87, 37, 47, 34, 40,
        39, 19, 20, 42, 27, 34, 39, 77, 13, 42, 59, 64, 45, -1, 32, 37,
        45, -5, 53, -6, 7, 36, 50, 23, 6, 32, 9, -21, 18, 71, 27, 52,
        -25, 31, 35, 42, -1, 68, 63, 52, 26, 43, 66, 37, 41, 25, 40, 70,
    ];
}
