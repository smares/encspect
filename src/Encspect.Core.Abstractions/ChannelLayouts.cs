namespace Encspect.Core;

// The speaker a channel feeds, named as the WAVE channel mask names its positions
// (WAVEFORMATEXTENSIBLE's SPEAKER_FRONT_LEFT and so on), plus the wide, second LFE, top side and
// bottom positions of ISO/IEC 23091-3 that 22.2 needs. FrontLeft to TopBackRight follow the bits of
// the WAVE channel mask and of Core Audio's channel bitmap in order: bit n is the member of value
// n + 1. Formats name a surround pair in their own terms (ITU-R BS.775 places it at 100 to 120
// degrees); a format's only surround pair is BackLeft and BackRight, as in the WAVE channel mask's
// 5.1, and where the format also has a rear pair, the surround pair is SideLeft and SideRight and
// the rear pair BackLeft and BackRight, as in the 7.1 of ISO/IEC 14496-3 Table 1.19.
public enum Speaker
{
    // A channel that feeds no speaker position: one of two independent programs (MPEG dual channel),
    // a channel of a second stereo program, or a channel whose format states no position.
    Discrete,
    FrontLeft,
    FrontRight,
    FrontCenter,
    LowFrequency,
    BackLeft,
    BackRight,
    FrontLeftOfCenter,
    FrontRightOfCenter,
    BackCenter,
    SideLeft,
    SideRight,
    TopCenter,
    TopFrontLeft,
    TopFrontCenter,
    TopFrontRight,
    TopBackLeft,
    TopBackCenter,
    TopBackRight,
    FrontLeftWide,
    FrontRightWide,
    LowFrequency2,
    TopSideLeft,
    TopSideRight,
    BottomFrontLeft,
    BottomFrontCenter,
    BottomFrontRight,
}

// Names and common layouts for StreamInfo.ChannelLayout. The name of a layout depends only on which
// speakers it holds, not on their order, which stays the format's.
public static class ChannelLayouts
{
    private static readonly Speaker[] FivePointOneBack = [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight];
    private static readonly Speaker[] FivePointOneSide = [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.SideLeft, Speaker.SideRight];
    private static readonly Speaker[] SevenPointOne = [.. FivePointOneBack, Speaker.SideLeft, Speaker.SideRight];
    private static readonly Speaker[] TopFront = [Speaker.TopFrontLeft, Speaker.TopFrontRight];
    private static readonly Speaker[] TopFour = [Speaker.TopFrontLeft, Speaker.TopFrontRight, Speaker.TopBackLeft, Speaker.TopBackRight];

    // The common names, by the speakers a layout holds. The surround pair of 5.0 and 5.1 may be at
    // the back or at the sides, as formats place it.
    private static readonly Dictionary<ulong, string> Names = Build(
        ("mono", [Speaker.FrontCenter]),
        ("stereo", [Speaker.FrontLeft, Speaker.FrontRight]),
        ("2.1", [Speaker.FrontLeft, Speaker.FrontRight, Speaker.LowFrequency]),
        ("3.0", [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter]),
        ("3.1", [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency]),
        ("4.0", [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.BackCenter]),
        ("4.1", [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.BackCenter, Speaker.LowFrequency]),
        ("quadraphonic", [Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight]),
        ("quadraphonic", [Speaker.FrontLeft, Speaker.FrontRight, Speaker.SideLeft, Speaker.SideRight]),
        ("5.0", [.. FivePointOneBack.Where(s => s != Speaker.LowFrequency)]),
        ("5.0", [.. FivePointOneSide.Where(s => s != Speaker.LowFrequency)]),
        ("5.1", FivePointOneBack),
        ("5.1", FivePointOneSide),
        ("6.0", [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.BackCenter, Speaker.SideLeft, Speaker.SideRight]),
        ("6.0", [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.BackLeft, Speaker.BackRight, Speaker.BackCenter]),
        ("6.1", [.. FivePointOneSide, Speaker.BackCenter]),
        ("6.1", [.. FivePointOneBack, Speaker.BackCenter]),
        ("7.0", [.. SevenPointOne.Where(s => s != Speaker.LowFrequency)]),
        ("7.1", SevenPointOne),
        ("7.1 (wide)", [.. FivePointOneBack, Speaker.FrontLeftOfCenter, Speaker.FrontRightOfCenter]),
        ("7.1 (wide)", [.. FivePointOneSide, Speaker.FrontLeftOfCenter, Speaker.FrontRightOfCenter]),
        ("5.1.2", [.. FivePointOneBack, .. TopFront]),
        ("5.1.2", [.. FivePointOneSide, .. TopFront]),
        ("5.1.4", [.. FivePointOneBack, .. TopFour]),
        ("5.1.4", [.. FivePointOneSide, .. TopFour]),
        ("7.1.2", [.. SevenPointOne, .. TopFront]),
        ("7.1.4", [.. SevenPointOne, .. TopFour]),
        ("22.2", [.. SevenPointOne, Speaker.FrontLeftOfCenter, Speaker.FrontRightOfCenter, Speaker.BackCenter, Speaker.TopCenter, Speaker.TopFrontLeft,
            Speaker.TopFrontCenter, Speaker.TopFrontRight, Speaker.TopBackLeft, Speaker.TopBackCenter, Speaker.TopBackRight, Speaker.LowFrequency2,
            Speaker.TopSideLeft, Speaker.TopSideRight, Speaker.BottomFrontLeft, Speaker.BottomFrontCenter, Speaker.BottomFrontRight]));

    public static IReadOnlyList<Speaker> Mono { get; } = [Speaker.FrontCenter];

    public static IReadOnlyList<Speaker> Stereo { get; } = [Speaker.FrontLeft, Speaker.FrontRight];

    // Channels without speaker positions, such as the two programs of MPEG dual channel.
    public static IReadOnlyList<Speaker> Discrete(int count) => [.. Enumerable.Repeat(Speaker.Discrete, count)];

    // The position written out, as reports show it: "front left", "LFE".
    public static string Name(Speaker speaker) => speaker switch
    {
        Speaker.Discrete => "discrete",
        Speaker.FrontLeft => "front left",
        Speaker.FrontRight => "front right",
        Speaker.FrontCenter => "front center",
        Speaker.LowFrequency => "LFE",
        Speaker.BackLeft => "back left",
        Speaker.BackRight => "back right",
        Speaker.FrontLeftOfCenter => "front left of center",
        Speaker.FrontRightOfCenter => "front right of center",
        Speaker.BackCenter => "back center",
        Speaker.SideLeft => "side left",
        Speaker.SideRight => "side right",
        Speaker.TopCenter => "top center",
        Speaker.TopFrontLeft => "top front left",
        Speaker.TopFrontCenter => "top front center",
        Speaker.TopFrontRight => "top front right",
        Speaker.TopBackLeft => "top back left",
        Speaker.TopBackCenter => "top back center",
        Speaker.TopBackRight => "top back right",
        Speaker.FrontLeftWide => "front left wide",
        Speaker.FrontRightWide => "front right wide",
        Speaker.LowFrequency2 => "LFE 2",
        Speaker.TopSideLeft => "top side left",
        Speaker.TopSideRight => "top side right",
        Speaker.BottomFrontLeft => "bottom front left",
        Speaker.BottomFrontCenter => "bottom front center",
        Speaker.BottomFrontRight => "bottom front right",
        _ => throw new ArgumentOutOfRangeException(nameof(speaker)),
    };

    // The layout's common name, such as "5.1", or null where it has none: a layout with a discrete
    // channel, a speaker twice, or a set of speakers no common layout has, such as MPEG-2's 2/1.
    public static string? CommonName(IReadOnlyList<Speaker> layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Key(layout) is { } key && Names.TryGetValue(key, out string? name) ? name : null;
    }

    // The speakers in the stream's order, written out, after the common name where there is one:
    // "5.1 (front center, front left, front right, back left, back right, LFE)".
    public static string Describe(IReadOnlyList<Speaker> layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        string speakers = Speakers(layout);
        return CommonName(layout) is { } name ? $"{name} ({speakers})" : speakers;
    }

    // The speakers in the stream's order, written out and separated by commas.
    public static string Speakers(IReadOnlyList<Speaker> layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return string.Join(", ", layout.Select(Name));
    }

    // Whether two layouts feed the same speakers, whatever their order, so that a container's
    // statement in its own channel order can be compared with a codec's.
    public static bool HaveSameSpeakers(IReadOnlyList<Speaker> first, IReadOnlyList<Speaker> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return first.Count == second.Count && first.Order().SequenceEqual(second.Order());
    }

    // One bit per speaker, or null for a layout with a discrete channel or a speaker twice.
    private static ulong? Key(IReadOnlyList<Speaker> layout)
    {
        ulong key = 0;
        foreach (Speaker speaker in layout)
        {
            ulong bit = 1UL << (int)speaker;
            if (speaker == Speaker.Discrete || (key & bit) != 0)
            {
                return null;
            }

            key |= bit;
        }

        return key;
    }

    private static Dictionary<ulong, string> Build(params (string Name, Speaker[] Speakers)[] layouts) =>
        layouts.ToDictionary(l => Key(l.Speakers) ?? throw new InvalidOperationException(l.Name), l => l.Name);
}
