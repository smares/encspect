using Encspect.Core;

namespace Encspect.Formats.Dts;

// The loudspeaker positions of Table 6-22, which Table 7-10 and Table 5-4 also name, as the speakers a stream's channels feed.
internal enum Position
{
    Center,
    Left,
    Right,
    LeftSurround,
    RightSurround,
    Lfe,
    CenterSurround,
    LeftRear,
    RightRear,
    LeftSide,
    RightSide,
    LeftCenter,
    RightCenter,
    LeftHeight,
    CenterHeight,
    RightHeight,
    Lfe2,
    LeftWide,
    RightWide,
    Overhead,
    LeftHeightSide,
    RightHeightSide,
    CenterHeightRear,
    LeftHeightRear,
    RightHeightRear,
    CenterLow,
    LeftLow,
    RightLow,
    None,
}

internal static class Layouts
{
    // Table 5-4: the positions of each arrangement in coded order; None for the channels the table names without a position.
    public static List<Position> Arrangement(int amode) => amode switch
    {
        0 => [Position.Center],
        1 => [Position.None, Position.None],
        2 or 3 or 4 => [Position.Left, Position.Right],
        5 => [Position.Center, Position.Left, Position.Right],
        6 => [Position.Left, Position.Right, Position.CenterSurround],
        7 => [Position.Center, Position.Left, Position.Right, Position.CenterSurround],
        8 => [Position.Left, Position.Right, Position.LeftSurround, Position.RightSurround],
        9 => [Position.Center, Position.Left, Position.Right, Position.LeftSurround, Position.RightSurround],
        10 => [Position.LeftCenter, Position.RightCenter, Position.Left, Position.Right, Position.LeftSurround, Position.RightSurround],
        11 => [Position.Center, Position.Left, Position.Right, Position.LeftRear, Position.RightRear, Position.Overhead],
        12 => [Position.Center, Position.CenterSurround, Position.Left, Position.Right, Position.LeftRear, Position.RightRear],
        13 => [Position.LeftCenter, Position.Center, Position.RightCenter, Position.Left, Position.Right, Position.LeftSurround, Position.RightSurround],
        14 => [Position.LeftCenter, Position.RightCenter, Position.Left, Position.Right, Position.None, Position.None, Position.None, Position.None],
        15 => [Position.LeftCenter, Position.Center, Position.RightCenter, Position.Left, Position.Right, Position.LeftSurround, Position.CenterSurround, Position.RightSurround],
        _ => [],
    };

    // Table 7-10: the positions a speaker activity mask names, lowest bit first, a pair as its left and then its right.
    public static List<Position> Mask(int mask)
    {
        Position[][] bits =
        [
            [Position.Center],
            [Position.Left, Position.Right],
            [Position.LeftSurround, Position.RightSurround],
            [Position.Lfe],
            [Position.CenterSurround],
            [Position.LeftHeight, Position.RightHeight],
            [Position.LeftRear, Position.RightRear],
            [Position.CenterHeight],
            [Position.Overhead],
            [Position.LeftCenter, Position.RightCenter],
            [Position.LeftWide, Position.RightWide],
            [Position.LeftSide, Position.RightSide],
            [Position.Lfe2],
            [Position.LeftHeightSide, Position.RightHeightSide],
            [Position.CenterHeightRear],
            [Position.LeftHeightRear, Position.RightHeightRear],
        ];
        var positions = new List<Position>();
        for (int bit = 0; bit < bits.Length; bit++)
        {
            if ((mask & (1 << bit)) != 0)
            {
                positions.AddRange(bits[bit]);
            }
        }

        return positions;
    }

    // The positions of a mask with a bit for each speaker, in the order of Table 6-22, lowest bit first: the channel mask of an XLL channel
    // set, which FFmpeg's dca_xll.c reads in this order (the specification refers to its clause C.8 for it, which is about entropy coding).
    public static List<Position> SpeakerMask(uint mask)
    {
        var positions = new List<Position>();
        for (int bit = 0; bit < (int)Position.None; bit++)
        {
            if ((mask & (1u << bit)) != 0)
            {
                positions.Add((Position)bit);
            }
        }

        return positions;
    }

    // Table 6-22: the positions of XXCH's extension masks, from bit 6 on (the bit of Position.CenterSurround).
    public static List<Position> XxchMask(uint mask)
    {
        var positions = new List<Position>();
        for (int bit = 6; bit < 32; bit++)
        {
            if ((mask & (1u << bit)) != 0 && bit < (int)Position.None)
            {
                positions.Add((Position)bit);
            }
        }

        return positions;
    }

    // The speakers of positions in their order. A format's only surround pair is the back pair; where there is also a rear pair, the
    // surround pair is the side pair (plan, "Streams and series"). A position named twice, or without one, is discrete.
    public static IReadOnlyList<Speaker> Speakers(IReadOnlyList<Position> positions)
    {
        bool rear = positions.Contains(Position.LeftRear);
        var speakers = new List<Speaker>();
        foreach (Position position in positions)
        {
            Speaker speaker = position switch
            {
                Position.Center => Speaker.FrontCenter,
                Position.Left => Speaker.FrontLeft,
                Position.Right => Speaker.FrontRight,
                Position.LeftSurround => rear ? Speaker.SideLeft : Speaker.BackLeft,
                Position.RightSurround => rear ? Speaker.SideRight : Speaker.BackRight,
                Position.Lfe => Speaker.LowFrequency,
                Position.CenterSurround => Speaker.BackCenter,
                Position.LeftRear => Speaker.BackLeft,
                Position.RightRear => Speaker.BackRight,
                Position.LeftSide => Speaker.SideLeft,
                Position.RightSide => Speaker.SideRight,
                Position.LeftCenter => Speaker.FrontLeftOfCenter,
                Position.RightCenter => Speaker.FrontRightOfCenter,
                Position.LeftHeight => Speaker.TopFrontLeft,
                Position.CenterHeight => Speaker.TopFrontCenter,
                Position.RightHeight => Speaker.TopFrontRight,
                Position.Lfe2 => Speaker.LowFrequency2,
                Position.LeftWide => Speaker.FrontLeftWide,
                Position.RightWide => Speaker.FrontRightWide,
                Position.Overhead => Speaker.TopCenter,
                Position.LeftHeightSide => Speaker.TopSideLeft,
                Position.RightHeightSide => Speaker.TopSideRight,
                Position.CenterHeightRear => Speaker.TopBackCenter,
                Position.LeftHeightRear => Speaker.TopBackLeft,
                Position.RightHeightRear => Speaker.TopBackRight,
                Position.CenterLow => Speaker.BottomFrontCenter,
                Position.LeftLow => Speaker.BottomFrontLeft,
                Position.RightLow => Speaker.BottomFrontRight,
                _ => Speaker.Discrete,
            };
            speakers.Add(speaker != Speaker.Discrete && speakers.Contains(speaker) ? Speaker.Discrete : speaker);
        }

        return speakers;
    }
}
